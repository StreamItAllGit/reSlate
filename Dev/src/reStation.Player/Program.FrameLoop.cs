// @module: reStation.Player.Program.FrameLoop
// @purpose: SDL3 runtime loop (3-tier input + --input-script + F1 drawer/mouse capture + F3 HUD + GL/SW present + heartbeats).
// @depends-on: System.Diagnostics, Foundation.Core, Foundation.Retro, reStation.Contracts, reStation.Core, reStation.Core.Audio, reStation.Core.Cpu, reStation.Core.DiscIngestion, reStation.Core.Input
// @exports: Program (FrameLoop partial)
// @owner: devv-player-ui (T6)

using System.Diagnostics;
using Foundation.Core;
using Foundation.Retro;
using reStation.Contracts;
using reStation.Core;
using reStation.Core.Audio;
using reStation.Core.Cpu;
using reStation.Core.DiscIngestion;
using reStation.Core.Gpu;
using reStation.Core.Input;

namespace reStation.Player;

internal static unsafe partial class Program
{
    private sealed class LoopState
    {
        public IntPtr Gamepad = HostInputMapper.OpenFirstGamepad();
        public float MouseDx, MouseDy;
        public bool MouseCaptured, QuitRequested, PrevSave, PrevLoad, UiDirty = true;
        public bool WheelUp, WheelDown, RunHeadless;
        public bool SuppressNextIdleSubTickVBlank;
        public uint PrevCaptureTriggerMask;
        public long HostFrame;
        public string DiscInfo = string.Empty;
        public string? ScriptScreenshot;
        public string? PendingBackendSwitch;
        public readonly HashSet<string> PrevNativeActions = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> ActiveNativeBuffer = new();
        public readonly List<string> PressedNativeBuffer = new();
    }

    private static int RunHostLoop(
        HostVideoSurface video, ReStationHost host, PsxCpu cpu, IRecompGameModule? gameModule,
        IReStationModuleManifest? manifest, DiscManifest? discManifest, CliOptions options, SandboxEnvironment sandbox,
        RecompSettings settings, HighResFramePacer pacer, ReStationLogger logger)
    {
        string globalProfilePath = GlobalControllerProfile.ResolvePath(sandbox, AppContext.BaseDirectory);
        GlobalControllerProfile globalProfile = GlobalControllerProfile.LoadOrDefault(globalProfilePath);
        IModuleInputProfileProvider? profileProvider = gameModule as IModuleInputProfileProvider;

        var drawer = new SlideOverSettingsDrawer { IsOpen = options.OpenSettings };
        drawer.AttachInputProfiles(globalProfile, globalProfilePath, profileProvider);
        var overlay = new UiOverlayPresenter();
        var st = new LoopState
        {
            DiscInfo = discManifest is null ? "No disc attached" : $"{discManifest.SerialNumber} ({discManifest.Files.Count} files)"
        };

        InputScriptRunner? inputScript = string.IsNullOrWhiteSpace(options.InputScriptPath)
            ? null
            : InputScriptRunner.Load(options.InputScriptPath, sandbox.TestMode ? CliOptions.ResolveSandboxBoundary(sandbox) : null);

        bool runHeadless = options.Headless && !options.VisibleTest;
        st.RunHeadless = runHeadless;
        var audioSpec = new SdlAudioSpec { Format = Sdl3Native.AudioS16Le, Channels = 2, Freq = PsxSpu.SampleRate };
        IntPtr audioStream = video.SdlAvailable && !runHeadless
            ? Sdl3Native.SDL_OpenAudioDeviceStream(Sdl3Native.DefaultAudioPlaybackDevice, &audioSpec, IntPtr.Zero, IntPtr.Zero)
            : IntPtr.Zero;
        if (audioStream != IntPtr.Zero) _ = Sdl3Native.SDL_ResumeAudioStreamDevice(audioStream);

        short[] audioBuf = new short[ReStationHost.SamplesPerFrame * 2];
        int maxFrames = options.MaxFrames ?? (options.VisibleTest ? 240 : runHeadless && gameModule is null ? 1 : 0);
        long totalFrames = Math.Max(1L, maxFrames > 0 ? maxFrames : 1L);
        string? autoShotPath = !string.IsNullOrWhiteSpace(options.DumpScreenshotPath)
            ? Path.GetFullPath(options.DumpScreenshotPath)
            : options.VisibleTest
                ? Path.Combine(sandbox.LogDirectory, "screenshots", "visible-test.png")
                : null;

        host.AttachRuntimeContext(new HostRuntimeContext(
            IsHeadless: runHeadless,
            HasPendingCapture: () => !string.IsNullOrWhiteSpace(st.ScriptScreenshot)
                || (!string.IsNullOrWhiteSpace(autoShotPath) && (maxFrames > 0 ? st.HostFrame + 1 >= maxFrames : st.HostFrame + 1 == 2)),
            IsModernMouseLookActive: () => !drawer.IsOpen && st.MouseCaptured,
            MouseSensitivity: () => (float)settings.MouseSensitivity,
            PollSubTickLook: () => PollLiveSubTickLook(video, host, cpu, drawer, overlay, settings, sandbox, pacer, logger, st, runHeadless),
            PresentSubTickFrame: () => PresentSubTickLoopFrame(video, host, cpu, drawer, overlay, manifest, settings, pacer, st.DiscInfo, logger.IsEnabled, runHeadless),
            IsEnhancedFrameRateEnabled: () => settings.FrameRateMode == FrameRateMode.Enhanced));

        Span<byte> scriptKeys = stackalloc byte[KeyBindingsConfig.MaxScancode + 1];

        try
        {
            while (!st.QuitRequested && !host.HasFinished)
            {
                long t0 = Stopwatch.GetTimestamp();
                PumpEvents(video, host, cpu, drawer, overlay, settings, sandbox, pacer, logger, st);
                if (st.QuitRequested) break;

                if (st.PendingBackendSwitch is { } nextBackend)
                {
                    st.PendingBackendSwitch = null;
                    ApplyLiveBackendSwitch(video, cpu, drawer, overlay, settings, sandbox, logger, st, nextBackend);
                }

                scriptKeys.Clear();
                uint scriptMouseMask = 0u;
                st.ScriptScreenshot = null;
                if (inputScript is not null)
                {
                    inputScript.ApplyFrame(st.HostFrame, scriptKeys, out scriptMouseMask, out DrawerScriptCommand dCmd, out st.ScriptScreenshot);
                    ApplyScriptDrawerCommand(dCmd, drawer, overlay, st, video);
                }

                HostInputState input = HostInputMapper.BuildLiveInput(
                    cpu, drawer.IsOpen, video.SdlAvailable, settings, globalProfile.KeyBindings, profileProvider,
                    st.Gamepad, ref st.MouseDx, ref st.MouseDy, st.WheelUp, st.WheelDown,
                    gameModule as INativeGameActionHook, st.PrevNativeActions, st.ActiveNativeBuffer, st.PressedNativeBuffer,
                    out bool releaseMouseCapture, scriptKeys, scriptMouseMask);
                st.WheelUp = false;
                st.WheelDown = false;
                if (releaseMouseCapture && st.MouseCaptured)
                {
                    st.MouseCaptured = false;
                    st.MouseDx = 0f;
                    st.MouseDy = 0f;
                    video.SetRelativeMouse(false);
                }
                bool saveEdge = input.QuickSave && !st.PrevSave, loadEdge = input.QuickLoad && !st.PrevLoad;
                st.PrevSave = input.QuickSave; st.PrevLoad = input.QuickLoad;
                if (saveEdge) host.RequestQuickSave(0);
                if (loadEdge) host.RequestQuickLoad(0);
                ModernInputController.ApplyToCpu(cpu, input, settings);

                if (gameModule is not null) host.StepModuleFrameAsync(gameModule, CancellationToken.None).GetAwaiter().GetResult();
                else host.RunFrame();
                inputScript?.EvaluateAssertions(st.HostFrame, cpu, logger);

                bool ranSubTickPresents = host.SubTickPresentsInStep > 0;
                bool skipIdleSecondVBlank = !ranSubTickPresents && st.SuppressNextIdleSubTickVBlank && cpu.Gpu.FrameDrawOps == 0 && !st.UiDirty;
                bool wantAutoShot = !string.IsNullOrWhiteSpace(autoShotPath) && (maxFrames > 0 ? st.HostFrame + 1 >= maxFrames : st.HostFrame + 1 == 2);
                string? frameShot = !string.IsNullOrWhiteSpace(st.ScriptScreenshot) ? st.ScriptScreenshot : wantAutoShot ? autoShotPath : null;

                int samplesRead = cpu.Spu.Output.Read(audioBuf);
                if (audioStream != IntPtr.Zero && samplesRead > 0)
                {
                    fixed (short* ap = audioBuf) _ = Sdl3Native.SDL_PutAudioStreamData(audioStream, ap, samplesRead * sizeof(short));
                }

                long tCpu = Stopwatch.GetTimestamp();
                st.HostFrame++;
                long tGpu;
                long tEnd;
                if (ranSubTickPresents)
                {
                    st.SuppressNextIdleSubTickVBlank = true;
                    if (!string.IsNullOrWhiteSpace(frameShot))
                    {
                        video.SyncViewport(settings.ResolutionScale);
                        cpu.Gpu.PresentFrame(preferLastDrawnOrigin: true);
                        PresentLoopFrame(video, host, drawer, overlay, manifest, settings, cpu.Gpu.EffectiveWidescreen, st.DiscInfo, logger.IsEnabled, runHeadless, frameShot);
                    }
                    st.UiDirty = false;
                    tGpu = Stopwatch.GetTimestamp();
                    pacer.TargetFps = ComputeTargetFps(settings, runHeadless);
                    _ = pacer.WaitForNextFrame();
                    tEnd = Stopwatch.GetTimestamp();
                }
                else if (skipIdleSecondVBlank && string.IsNullOrWhiteSpace(frameShot))
                {
                    st.SuppressNextIdleSubTickVBlank = false;
                    tGpu = tEnd = Stopwatch.GetTimestamp();
                }
                else
                {
                    st.SuppressNextIdleSubTickVBlank = false;
                    video.SyncViewport(settings.ResolutionScale);
                    cpu.Gpu.PresentFrame();
                    PresentLoopFrame(video, host, drawer, overlay, manifest, settings, cpu.Gpu.EffectiveWidescreen, st.DiscInfo, logger.IsEnabled, runHeadless, frameShot);
                    st.UiDirty = false;
                    tGpu = Stopwatch.GetTimestamp();
                    pacer.TargetFps = ComputeTargetFps(settings, runHeadless);
                    _ = pacer.WaitForNextFrame();
                    tEnd = Stopwatch.GetTimestamp();
                }

                host.RecordTimings(ElapsedMs(t0, tCpu), ElapsedMs(tCpu, tGpu), ElapsedMs(tGpu, tEnd));
                PsxTelemetrySnapshot telemetry = host.GetTelemetry();
                _ = logger.TryHeartbeat(st.HostFrame, Math.Max(totalFrames, st.HostFrame), telemetry.ToHeartbeatMetrics(), force: st.HostFrame == 1);

                if (maxFrames > 0 && st.HostFrame >= maxFrames) break;
            }
            logger.Info("RUNTIME", $"Session end frames={st.HostFrame} cpuFrames={cpu.FrameCount} backend={video.Backend.BackendId} xaDrops={cpu.Spu.XaDrops} scriptFails={inputScript?.AssertionsFailed ?? 0}");
            return inputScript is not null && inputScript.HasFailures ? 1 : 0;
        }
        finally
        {
            if (audioStream != IntPtr.Zero) Sdl3Native.SDL_DestroyAudioStream(audioStream);
            if (st.Gamepad != IntPtr.Zero) Sdl3Native.SDL_CloseGamepad(st.Gamepad);
        }
    }

    private static void ApplyScriptDrawerCommand(
        DrawerScriptCommand dCmd, SlideOverSettingsDrawer drawer, UiOverlayPresenter overlay, LoopState st, HostVideoSurface video)
    {
        if (dCmd.Open && !drawer.IsOpen)
        {
            drawer.IsOpen = true;
            st.MouseCaptured = false;
            video.SetRelativeMouse(false);
            overlay.MarkDirty();
            st.UiDirty = true;
        }
        else if (dCmd.Close && drawer.IsOpen)
        {
            drawer.IsOpen = false;
            drawer.KeybindsTab.ClearListening();
            overlay.MarkDirty();
            st.UiDirty = true;
        }
        if (dCmd.Tab is { } tabIdx && drawer.ActiveTab != tabIdx)
        {
            drawer.ActiveTab = tabIdx;
            overlay.MarkDirty();
            st.UiDirty = true;
        }
    }

    private static void ApplyLiveBackendSwitch(
        HostVideoSurface video, PsxCpu cpu, SlideOverSettingsDrawer drawer, UiOverlayPresenter overlay,
        RecompSettings settings, SandboxEnvironment sandbox, ReStationLogger logger, LoopState st, string nextBackend)
    {
        if (video.TrySwitchBackend(nextBackend, settings, logger, out string? switchError))
        {
            cpu.Gpu.SetBackend(video.Backend);
            drawer.VideoWarningBanner = null;
            settings.BackendId = video.Backend.BackendId;
        }
        else
        {
            settings.BackendId = video.Backend.BackendId;
            drawer.VideoWarningBanner = switchError ?? "OpenGL 3.3 unavailable; kept Software CPU.";
            logger.Warn("VIDEO", $"Live backend hot-swap rejected: {drawer.VideoWarningBanner}");
        }
        settings.Save(sandbox.ConfigFilePath);
        overlay.MarkDirty();
        st.UiDirty = true;
    }

    private static (float MouseDx, float MouseDy, float StickX, float StickY) PollLiveSubTickLook(
        HostVideoSurface video, ReStationHost host, PsxCpu cpu, SlideOverSettingsDrawer drawer, UiOverlayPresenter overlay,
        RecompSettings settings, SandboxEnvironment sandbox, HighResFramePacer pacer, ReStationLogger logger, LoopState st, bool headless)
    {
        if (!headless && video.SdlAvailable) PumpEvents(video, host, cpu, drawer, overlay, settings, sandbox, pacer, logger, st);
        float mdx = !drawer.IsOpen && st.MouseCaptured ? st.MouseDx : 0f;
        float mdy = !drawer.IsOpen && st.MouseCaptured ? st.MouseDy : 0f;
        st.MouseDx = 0f; st.MouseDy = 0f;
        (float sx, float sy) = video.SdlAvailable ? HostInputMapper.ReadRightStick(st.Gamepad) : (0f, 0f);
        return (mdx, mdy, sx, sy);
    }

    private static void PresentSubTickLoopFrame(HostVideoSurface video, ReStationHost host, PsxCpu cpu, SlideOverSettingsDrawer drawer,
        UiOverlayPresenter overlay, IReStationModuleManifest? manifest, RecompSettings settings, HighResFramePacer pacer,
        string discInfo, bool loggingActive, bool headless)
    {
        if (headless) return;
        video.SyncViewport(settings.ResolutionScale);
        cpu.Gpu.PresentFrame(preferLastDrawnOrigin: true);
        PresentLoopFrame(video, host, drawer, overlay, manifest, settings, cpu.Gpu.EffectiveWidescreen, discInfo, loggingActive, headless: false, shotPath: null);
        double savedTarget = pacer.TargetFps;
        pacer.TargetFps = 0.0;
        _ = pacer.WaitForNextFrame();
        pacer.TargetFps = savedTarget;
    }

    private static void PresentLoopFrame(HostVideoSurface video, ReStationHost host, SlideOverSettingsDrawer drawer, UiOverlayPresenter overlay,
        IReStationModuleManifest? manifest, RecompSettings settings, bool effectiveWidescreen, string discInfo, bool loggingActive, bool headless, string? shotPath)
    {
        if (headless && string.IsNullOrWhiteSpace(shotPath)) return;
        bool overlayActive = drawer.IsOpen || settings.IsFpsOverlayEnabled;
        if (!headless && video.UsesOpenGl && string.IsNullOrWhiteSpace(shotPath))
        {
            if (!overlayActive) { video.SwapGlOnly(); return; }
            if (video.Backend is IPsxGpuBackendHooks hooks)
            {
                bool changed = overlay.EnsureRendered(drawer, settings, manifest, discInfo, loggingActive, host.GetTelemetry());
                hooks.RenderUiOverlay(
                    overlay.ActiveOverlayPixels, overlay.ActiveOverlayWidth, overlay.ActiveOverlayHeight,
                    overlay.ActiveOverlayDstX, overlay.ActiveOverlayDstY,
                    UiOverlayPresenter.OverlayWidth, UiOverlayPresenter.OverlayHeight, changed);
                video.SwapGlOnly();
                return;
            }
        }

        byte[] gameRgba = video.Backend.CaptureRgba(out int gw, out int gh);
        bool uiChanged = overlayActive && overlay.EnsureRendered(drawer, settings, manifest, discInfo, loggingActive, host.GetTelemetry());
        if (!headless && video.Renderer != IntPtr.Zero && string.IsNullOrWhiteSpace(shotPath))
        {
            video.PresentLayeredSoftware(
                gw, gh, gameRgba, effectiveWidescreen,
                overlayActive, overlay.ActiveOverlayWidth, overlay.ActiveOverlayHeight,
                overlay.ActiveOverlayPixels, overlay.ActiveOverlayDstX, overlay.ActiveOverlayDstY, uiChanged);
            return;
        }

        if (!overlayActive) _ = overlay.EnsureRendered(drawer, settings, manifest, discInfo, loggingActive, host.GetTelemetry());
        overlay.Composite(gameRgba, gw, gh, settings, effectiveWidescreen);
        if (!headless && video.CanPresent)
            video.PresentRgba(UiOverlayPresenter.OverlayWidth, UiOverlayPresenter.OverlayHeight, overlay.CompositePixels);
        if (!string.IsNullOrWhiteSpace(shotPath))
            overlay.SaveCompositePng(shotPath);
    }

    private static double ElapsedMs(long a, long b) => (b - a) * 1000.0 / Stopwatch.Frequency;
}
