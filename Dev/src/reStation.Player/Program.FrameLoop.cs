// @module: reStation.Player.Program.FrameLoop
// @purpose: SDL3 runtime loop (60 Hz hardware VBlank + SPU lock, 30 Hz swap decimation, isolated sub-tick mouse look, F1/F3 overlays, automation bridge).
// @depends-on: System.Diagnostics, Foundation.Core, Foundation.Retro, reStation.Contracts, reStation.Core, reStation.Core.Audio, reStation.Core.Cpu, reStation.Core.DiscIngestion, reStation.Core.Gpu, reStation.Core.Input
// @exports: Program (FrameLoop partial)

using System.Diagnostics;
using Foundation.Core;
using Foundation.Retro;
using reStation.Contracts;
using reStation.Core;
using reStation.Core.Audio;
using reStation.Core.Bringup;
using reStation.Core.Cpu;
using reStation.Core.DiscIngestion;
using reStation.Core.Gpu;
using reStation.Core.Input;

namespace reStation.Player;

internal static unsafe partial class Program
{
    internal sealed class LoopState
    {
        public IntPtr Gamepad = HostInputMapper.OpenFirstGamepad();
        public float MouseDx, MouseDy;
        public bool MouseCaptured, UserReleasedMouse, QuitRequested, PrevSave, PrevLoad, UiDirty = true;
        public bool WheelUp, WheelDown, IsOffscreen;
        public bool SuppressNextIdleSubTickVBlank;
        public uint PrevCaptureTriggerMask, LatchedMouseMask;
        public long HostFrame;
        public double InterVBlankSwapBudget, FastForwardTickBudget;
        public int LastGuestSlices = 1;
        public bool ActiveFastForwardThisFrame;
        public string DiscInfo = string.Empty;
        public string? PendingBackendSwitch;
        public readonly byte[] LatchedKeys = new byte[KeyBindingsConfig.MaxScancode + 1];
        public readonly HashSet<string> PrevNativeActions = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> ActiveNativeBuffer = new();
        public readonly List<string> PressedNativeBuffer = new();
    }

    internal static bool ShouldDecimateDisplayPresentation(
        RecompSettings settings,
        int hostTargetFps,
        long nextHostFrameNumber,
        bool hasPendingShot)
    {
        if (hasPendingShot) return false;
        bool is30HzTarget = hostTargetFps == 30 ||
            (settings.FrameRateMode == FrameRateMode.Enhanced && settings.TargetFps == 30);
        return is30HzTarget && (nextHostFrameNumber & 1L) == 0L;
    }

    private static int RunHostLoop(
        HostVideoSurface video, ReStationHost host, PsxCpu cpu, IRecompGameModule? gameModule,
        IReStationModuleManifest? manifest, DiscManifest? discManifest, CliOptions options, AppEnvironment env,
        RecompSettings settings, HighResFramePacer pacer, ReStationLogger logger, string executionTierMode,
        HostContractBridge bridge
#if DEBUG || RESTATION_TEST_AUTOMATION
        , DebugAutomationHook? automation = null
#endif
        )
    {
        string globalProfilePath = GlobalControllerProfile.ResolvePath(env, AppContext.BaseDirectory, !string.IsNullOrWhiteSpace(options.DataDir));
        GlobalControllerProfile globalProfile = GlobalControllerProfile.LoadOrDefault(globalProfilePath);
        IModuleInputProfileProvider? profileProvider = gameModule as IModuleInputProfileProvider;

        var drawer = new SlideOverSettingsDrawer { IsOpen = options.OpenSettings, ActiveBackendOverride = video.Backend.BackendId };
        drawer.AttachInputProfiles(globalProfile, globalProfilePath, profileProvider);
        var overlay = new UiOverlayPresenter();
        var st = new LoopState
        {
            DiscInfo = discManifest is null ? "No disc attached" : $"{discManifest.SerialNumber} ({discManifest.Files.Count} files)"
        };

        bool isOffscreen = !video.HasWindow;
        st.IsOffscreen = isOffscreen;
        bool captureBridgeFrames = isOffscreen;
#if DEBUG || RESTATION_TEST_AUTOMATION
        captureBridgeFrames = captureBridgeFrames || automation is not null;
#endif

        var audioSpec = new SdlAudioSpec { Format = Sdl3Native.AudioS16Le, Channels = 2, Freq = PsxSpu.SampleRate };
        IntPtr audioStream = video.SdlAvailable && !isOffscreen
            ? Sdl3Native.SDL_OpenAudioDeviceStream(Sdl3Native.DefaultAudioPlaybackDevice, &audioSpec, IntPtr.Zero, IntPtr.Zero)
            : IntPtr.Zero;
        if (audioStream != IntPtr.Zero) _ = Sdl3Native.SDL_ResumeAudioStreamDevice(audioStream);

        short[] audioBuf = new short[(PsxSpu.SampleRate / 10) * 2];
        int maxFrames = options.MaxFrames ?? (options.JitBringup ? 60 : isOffscreen && gameModule is null ? 1 : 0);
        long totalFrames = Math.Max(1L, maxFrames > 0 ? maxFrames : 1L);
        string? autoShotPath = !string.IsNullOrWhiteSpace(options.DumpScreenshotPath)
            ? Path.GetFullPath(options.DumpScreenshotPath)
            : null;

        host.AttachRuntimeContext(new HostRuntimeContext(
            IsOffscreen: isOffscreen,
            HasPendingCapture: () => !string.IsNullOrWhiteSpace(autoShotPath)
                && (maxFrames > 0 ? st.HostFrame + 1 >= maxFrames : st.HostFrame + 1 == 2),
            IsModernMouseLookActive: () => !drawer.IsOpen && st.MouseCaptured,
            MouseSensitivity: () => (float)settings.MouseSensitivity,
            PollSubTickLook: () => PollLiveSubTickLook(video, drawer, st, isOffscreen),
            PresentSubTickFrame: () => PresentSubTickLoopFrame(video, host, cpu, drawer, overlay, manifest, settings, pacer, st.DiscInfo, logger.IsEnabled, isOffscreen),
            IsEnhancedFrameRateEnabled: () => settings.FrameRateMode == FrameRateMode.Enhanced,
            IsFastForwardActive: () => st.ActiveFastForwardThisFrame,
            FastForwardMultiplier: () => settings.FastForwardMultiplier));

        try
        {
            while (!st.QuitRequested && !host.HasFinished)
            {
                long t0 = Stopwatch.GetTimestamp();
                PumpEvents(video, host, cpu, drawer, overlay, settings, env, pacer, logger, st);
                bridge.DrainCommands(
                    onPointerClick: (ox, oy, btn) => HandleOverlayPointerDown(ox, oy, btn, video, host, cpu, drawer, overlay, settings, env, pacer, logger, st),
                    onSyntheticScancode: sc => HandleKeyDownScancode(sc, video, host, drawer, overlay, settings, env, st));
#if DEBUG || RESTATION_TEST_AUTOMATION
                _ = bridge.Diagnostics?.ProcessFrameCommands(drawer, overlay, settings, env.ConfigFilePath, () => { overlay.MarkDirty(); st.UiDirty = true; });
#endif
                if (st.QuitRequested) break;
                SyncMouseCaptureState(video, drawer, settings, st);

                if (st.PendingBackendSwitch is { } nextBackend)
                {
                    st.PendingBackendSwitch = null;
                    ApplyLiveBackendSwitch(video, cpu, drawer, overlay, settings, env, logger, st, nextBackend);
                }

                HostInputState input = HostInputMapper.BuildLiveInput(
                    cpu, drawer.IsOpen, video.SdlAvailable, settings, globalProfile.KeyBindings, profileProvider,
                    st.Gamepad, ref st.MouseDx, ref st.MouseDy, st.WheelUp, st.WheelDown,
                    st.LatchedKeys, st.LatchedMouseMask,
                    gameModule as INativeGameActionHook, st.PrevNativeActions, st.ActiveNativeBuffer, st.PressedNativeBuffer,
                    out bool releaseMouseCapture
#if DEBUG || RESTATION_TEST_AUTOMATION
                    , bridge.Diagnostics
#endif
                    );
                st.ActiveFastForwardThisFrame = ResolveEffectiveFastForward(video, drawer, settings, globalProfile.KeyBindings, profileProvider, st, bridge);
                st.WheelUp = false; st.WheelDown = false;
                Array.Clear(st.LatchedKeys);
                st.LatchedMouseMask = 0u;
                if (releaseMouseCapture && st.MouseCaptured)
                {
                    st.MouseCaptured = false; st.MouseDx = 0f; st.MouseDy = 0f;
                    video.SetRelativeMouse(false);
                }
                bool saveEdge = input.QuickSave && !st.PrevSave, loadEdge = input.QuickLoad && !st.PrevLoad;
                st.PrevSave = input.QuickSave; st.PrevLoad = input.QuickLoad;
                if (saveEdge) host.RequestQuickSave(0);
                if (loadEdge) host.RequestQuickLoad(0);

                StepGuestSlicesWithQoL(host, cpu, gameModule, st, st.ActiveFastForwardThisFrame, settings.FastForwardMultiplier, audioBuf);

                bool ranSubTickPresents = host.SubTickPresentsInStep > 0;
                bool skipIdleSecondVBlank = !ranSubTickPresents && st.SuppressNextIdleSubTickVBlank && cpu.Gpu.FrameDrawOps == 0 && !st.UiDirty;
                bool wantAutoShot = !string.IsNullOrWhiteSpace(autoShotPath) && (maxFrames > 0 ? st.HostFrame + 1 >= maxFrames : st.HostFrame + 1 == 2);
                string? frameShot = wantAutoShot ? autoShotPath : null;

#if DEBUG || RESTATION_TEST_AUTOMATION
                int drainedAudio = DrainAudioWithFastForwardDecimation(cpu.Spu.Output, audioStream, audioBuf, st.LastGuestSlices, bridge.Diagnostics);
                if (drainedAudio == 0) bridge.Diagnostics?.RecordAudioSamples(ReadOnlySpan<short>.Empty, 0);
#else
                _ = DrainAudioWithFastForwardDecimation(cpu.Spu.Output, audioStream, audioBuf, st.LastGuestSlices);
#endif

                long tCpu = Stopwatch.GetTimestamp();
                st.HostFrame++;
                bool decimateSwap = !ranSubTickPresents && ShouldDecimateDisplayPresentation(settings, host.TargetFps, st.HostFrame, !string.IsNullOrWhiteSpace(frameShot));
                long tGpu;
                long tEnd;
                if (ranSubTickPresents)
                {
                    st.SuppressNextIdleSubTickVBlank = true;
                    if (!string.IsNullOrWhiteSpace(frameShot) || captureBridgeFrames)
                    {
                        video.SyncViewport(settings.ResolutionScale);
                        cpu.Gpu.PresentFrame(preferLastDrawnOrigin: true);
                        PresentAndRecordFrame(video, host, drawer, overlay, manifest, settings, cpu.Gpu.EffectiveWidescreen, st.DiscInfo, logger.IsEnabled, isOffscreen, frameShot, bridge, captureBridgeFrames);
                    }
                    st.UiDirty = false;
                    tGpu = Stopwatch.GetTimestamp();
                    pacer.TargetFps = 0.0;
                    _ = pacer.WaitForNextFrame();
                    pacer.TargetFps = ComputeTargetFps(settings, isOffscreen);
                    tEnd = Stopwatch.GetTimestamp();
                }
                else if ((skipIdleSecondVBlank || decimateSwap) && string.IsNullOrWhiteSpace(frameShot))
                {
                    if (!decimateSwap) st.SuppressNextIdleSubTickVBlank = false;
                    tGpu = Stopwatch.GetTimestamp();
                    if (decimateSwap)
                    {
                        pacer.TargetFps = ComputeTargetFps(settings, isOffscreen);
                        _ = pacer.WaitForNextFrame();
                    }
                    tEnd = Stopwatch.GetTimestamp();
                }
                else
                {
                    st.SuppressNextIdleSubTickVBlank = false;
                    video.SyncViewport(settings.ResolutionScale);
                    cpu.Gpu.PresentFrame();
                    PresentAndRecordFrame(video, host, drawer, overlay, manifest, settings, cpu.Gpu.EffectiveWidescreen, st.DiscInfo, logger.IsEnabled, isOffscreen, frameShot, bridge, captureBridgeFrames);
                    st.UiDirty = false;
                    tGpu = Stopwatch.GetTimestamp();
                    RunInterVBlankPresentationPacing(t0, video, host, cpu, drawer, overlay, manifest, settings, env, pacer, logger, st, bridge);
                    tEnd = Stopwatch.GetTimestamp();
                }

                host.RecordTimings(ElapsedMs(t0, tCpu), ElapsedMs(tCpu, tGpu), ElapsedMs(tGpu, tEnd));
                PsxTelemetrySnapshot telemetry = host.GetTelemetry();
#if DEBUG || RESTATION_TEST_AUTOMATION
                bridge.Diagnostics?.UpdateTelemetry(host, cpu, in input, st.HostFrame);
#endif
                bridge.SyncWidgets(drawer, settings, overlay, telemetry, executionTierMode);
#if DEBUG || RESTATION_TEST_AUTOMATION
                automation?.SyncWidgets(bridge.GetOverlayWidgets());
#endif
                _ = logger.TryHeartbeat(st.HostFrame, Math.Max(totalFrames, st.HostFrame), telemetry.ToHeartbeatMetrics(), force: st.HostFrame == 1);

                if (maxFrames > 0 && st.HostFrame >= maxFrames) break;
            }
            int? preservedExit = null;
            if (cpu.BringupRecorder is { } bringup)
            {
                string written = bringup.WriteProfileAtomic(compiledDispatchCount: cpu.CompiledDispatchCount, interpreterFallbackCount: cpu.InterpreterFallbackCount);
                logger.Info("BRINGUP", $"Saved bringup profile '{Path.GetFileName(written)}' functions={bringup.DiscoveredFunctionCount} spinTripped={bringup.IsSpinWatchdogTripped}.");
                if (bringup.IsSpinWatchdogTripped || (options.JitBringup && isOffscreen))
                    preservedExit = JitBringupRecorder.SpinWatchdogExitCode;
            }
            logger.Info("RUNTIME", $"Session end frames={st.HostFrame} cpuFrames={cpu.FrameCount} backend={video.Backend.BackendId} xaDrops={cpu.Spu.XaDrops}");
            return ResolveLoopExitCode(preservedExit);
        }
        finally
        {
            if (audioStream != IntPtr.Zero) Sdl3Native.SDL_DestroyAudioStream(audioStream);
            if (st.Gamepad != IntPtr.Zero) Sdl3Native.SDL_CloseGamepad(st.Gamepad);
        }
    }

    internal static int ResolveLoopExitCode(int? preservedExitCode = null)
    {
        int processExitCode = preservedExitCode ?? Environment.ExitCode;
        return processExitCode != 0 ? processExitCode : 0;
    }

    private static void ApplyLiveBackendSwitch(
        HostVideoSurface video, PsxCpu cpu, SlideOverSettingsDrawer drawer, UiOverlayPresenter overlay,
        RecompSettings settings, AppEnvironment env, ReStationLogger logger, LoopState st, string nextBackend)
    {
        _ = cpu.Gpu.SyncBackendToVramMirror();
        if (video.TrySwitchBackend(nextBackend, settings, logger, out string? switchError))
        {
            cpu.Gpu.SetBackend(video.Backend);
            SyncBackendHdTexturePack(cpu, env, drawer, settings, logger);
            drawer.VideoWarningBanner = null;
            drawer.ActiveBackendOverride = video.Backend.BackendId;
            settings.BackendId = video.Backend.BackendId;
        }
        else
        {
            drawer.ActiveBackendOverride = video.Backend.BackendId;
            settings.BackendId = video.Backend.BackendId;
            drawer.VideoWarningBanner = switchError ?? $"{nextBackend} unavailable; kept {video.Backend.BackendId}.";
            logger.Warn("VIDEO", $"Live backend hot-swap rejected: {drawer.VideoWarningBanner}");
        }
        settings.Save(env.ConfigFilePath);
        overlay.MarkDirty();
        st.UiDirty = true;
    }

    internal static (float MouseDx, float MouseDy, float StickX, float StickY) PollLiveSubTickLook(
        HostVideoSurface video, SlideOverSettingsDrawer drawer, LoopState st, bool isOffscreen)
    {
        if (!isOffscreen && video.SdlAvailable) PumpSubTickMouseOnly(video, drawer, st);
        float mdx = !drawer.IsOpen && st.MouseCaptured ? st.MouseDx : 0f;
        float mdy = !drawer.IsOpen && st.MouseCaptured ? st.MouseDy : 0f;
        st.MouseDx = 0f; st.MouseDy = 0f;
        (float sx, float sy) = video.SdlAvailable ? HostInputMapper.ReadRightStick(st.Gamepad) : (0f, 0f);
        return (mdx, mdy, sx, sy);
    }

    private static void PresentSubTickLoopFrame(HostVideoSurface video, ReStationHost host, PsxCpu cpu, SlideOverSettingsDrawer drawer,
        UiOverlayPresenter overlay, IReStationModuleManifest? manifest, RecompSettings settings, HighResFramePacer pacer,
        string discInfo, bool loggingActive, bool isOffscreen)
    {
        if (isOffscreen) return;
        video.SyncViewport(settings.ResolutionScale);
        cpu.Gpu.PresentFrame(preferLastDrawnOrigin: true);
        PresentAndRecordFrame(video, host, drawer, overlay, manifest, settings, cpu.Gpu.EffectiveWidescreen, discInfo, loggingActive, isOffscreen: false, shotPath: null);
        double savedTarget = pacer.TargetFps;
        pacer.TargetFps = 0.0;
        _ = pacer.WaitForNextFrame();
        pacer.TargetFps = savedTarget;
    }

    private static void PresentLoopFrame(HostVideoSurface video, ReStationHost host, SlideOverSettingsDrawer drawer, UiOverlayPresenter overlay,
        IReStationModuleManifest? manifest, RecompSettings settings, bool effectiveWidescreen, string discInfo, bool loggingActive,
        bool isOffscreen, string? shotPath, HostContractBridge? bridge = null, bool captureCompositeForBridge = false, bool reblitGlDisplay = false)
    {
        if (isOffscreen && string.IsNullOrWhiteSpace(shotPath) && !captureCompositeForBridge) return;
        bool overlayActive = drawer.IsOpen || settings.IsFpsOverlayEnabled;
        if (!isOffscreen && video.UsesOpenGl && string.IsNullOrWhiteSpace(shotPath) && !captureCompositeForBridge)
        {
            if (video.Backend is IPsxGpuBackendHooks hooks)
            {
                if (reblitGlDisplay) hooks.ReblitPresentedDisplay();
                if (overlayActive)
                {
                    bool changed = overlay.EnsureRendered(drawer, settings, manifest, discInfo, loggingActive, host.GetTelemetry());
                    hooks.RenderUiOverlay(
                        overlay.ActiveOverlayPixels, overlay.ActiveOverlayWidth, overlay.ActiveOverlayHeight,
                        overlay.ActiveOverlayDstX, overlay.ActiveOverlayDstY,
                        UiOverlayPresenter.OverlayWidth, UiOverlayPresenter.OverlayHeight, changed);
                }
            }
            video.SwapGlOnly();
            return;
        }

        if (!isOffscreen && video.UsesPresenter)
        {
            if (reblitGlDisplay && !overlay.EnsureRendered(drawer, settings, manifest, discInfo, loggingActive, host.GetTelemetry())
                && string.IsNullOrWhiteSpace(shotPath) && !captureCompositeForBridge)
            {
                video.PresentRgba(UiOverlayPresenter.OverlayWidth, UiOverlayPresenter.OverlayHeight, overlay.CompositePixels);
                return;
            }
            byte[] presRgba = video.Backend.CaptureRgba(out int pw, out int ph);
            _ = overlay.EnsureRendered(drawer, settings, manifest, discInfo, loggingActive, host.GetTelemetry());
            overlay.Composite(presRgba, pw, ph, settings, effectiveWidescreen);
            bridge?.PublishFramebufferRgba(UiOverlayPresenter.OverlayWidth, UiOverlayPresenter.OverlayHeight, overlay.CompositePixels);
            video.PresentRgba(UiOverlayPresenter.OverlayWidth, UiOverlayPresenter.OverlayHeight, overlay.CompositePixels);
            if (!string.IsNullOrWhiteSpace(shotPath)) overlay.SaveCompositePng(shotPath);
            return;
        }

        byte[] gameRgba = video.Backend.CaptureRgba(out int gw, out int gh);
        bool uiChanged = overlayActive && overlay.EnsureRendered(drawer, settings, manifest, discInfo, loggingActive, host.GetTelemetry());
        if (!isOffscreen && video.Renderer != IntPtr.Zero && string.IsNullOrWhiteSpace(shotPath) && !captureCompositeForBridge)
        {
            video.PresentLayeredSoftware(
                gw, gh, gameRgba, settings, effectiveWidescreen,
                overlayActive, overlay.ActiveOverlayWidth, overlay.ActiveOverlayHeight,
                overlay.ActiveOverlayPixels, overlay.ActiveOverlayDstX, overlay.ActiveOverlayDstY, uiChanged);
            return;
        }

        if (!overlayActive) _ = overlay.EnsureRendered(drawer, settings, manifest, discInfo, loggingActive, host.GetTelemetry());
        overlay.Composite(gameRgba, gw, gh, settings, effectiveWidescreen);
        bridge?.PublishFramebufferRgba(UiOverlayPresenter.OverlayWidth, UiOverlayPresenter.OverlayHeight, overlay.CompositePixels);
        if (!isOffscreen && video.CanPresent)
            video.PresentRgba(UiOverlayPresenter.OverlayWidth, UiOverlayPresenter.OverlayHeight, overlay.CompositePixels);
        if (!string.IsNullOrWhiteSpace(shotPath))
            overlay.SaveCompositePng(shotPath);
    }

    private static void SyncMouseCaptureState(HostVideoSurface video, SlideOverSettingsDrawer drawer, RecompSettings settings, LoopState st)
    {
        if (st.IsOffscreen || !video.HasWindow || drawer.IsOpen || settings.ControlMode != ControlMode.ModernWasdMouse)
        {
            if (st.MouseCaptured)
            {
                st.MouseCaptured = false; st.MouseDx = 0f; st.MouseDy = 0f;
                video.SetRelativeMouse(false);
            }
            return;
        }

        bool focused = !video.SdlAvailable || (Sdl3Native.SDL_GetWindowFlags(video.Window) & Sdl3Native.WindowInputFocus) != 0UL;
        if (!focused)
        {
            if (st.MouseCaptured)
            {
                st.MouseCaptured = false; st.MouseDx = 0f; st.MouseDy = 0f;
                video.SetRelativeMouse(false);
            }
            return;
        }

        if (!st.UserReleasedMouse)
        {
            st.MouseCaptured = true;
            if (video.SdlAvailable && Sdl3Native.SDL_GetWindowRelativeMouseMode(video.Window) == 0)
                video.SetRelativeMouse(true);
        }
    }

    private static double ElapsedMs(long a, long b) => (b - a) * 1000.0 / Stopwatch.Frequency;
}
