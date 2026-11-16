// @module: reStation.Player.Program.FrameLoop
// @purpose: SDL3 runtime loop (3-tier input + F1 drawer/mouse capture + F3 HUD + GL/SW present + heartbeats).
// @depends-on: System.Diagnostics, Foundation.Core, Foundation.Retro, reStation.Contracts, reStation.Core, reStation.Core.Audio, reStation.Core.Cpu, reStation.Core.DiscIngestion, reStation.Core.Input
// @exports: Program (FrameLoop partial)
// @owner: devv-player-ui (T6)

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
    private sealed class LoopState
    {
        public IntPtr Gamepad = HostInputMapper.OpenFirstGamepad();
        public float MouseDx, MouseDy;
        public bool MouseCaptured, UserReleasedMouse, QuitRequested, PrevSave, PrevLoad, UiDirty = true;
        public bool WheelUp, WheelDown, IsOffscreen;
        public bool SuppressNextIdleSubTickVBlank;
        public uint PrevCaptureTriggerMask, LatchedMouseMask;
        public long HostFrame;
        public string DiscInfo = string.Empty;
        public string? PendingBackendSwitch;
        public readonly byte[] LatchedKeys = new byte[KeyBindingsConfig.MaxScancode + 1];
        public readonly HashSet<string> PrevNativeActions = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> ActiveNativeBuffer = new();
        public readonly List<string> PressedNativeBuffer = new();
    }

    private static int RunHostLoop(
        HostVideoSurface video, ReStationHost host, PsxCpu cpu, IRecompGameModule? gameModule,
        IReStationModuleManifest? manifest, DiscManifest? discManifest, CliOptions options, AppEnvironment env,
        RecompSettings settings, HighResFramePacer pacer, ReStationLogger logger)
    {
        string globalProfilePath = GlobalControllerProfile.ResolvePath(env, AppContext.BaseDirectory, !string.IsNullOrWhiteSpace(options.DataDir));
        GlobalControllerProfile globalProfile = GlobalControllerProfile.LoadOrDefault(globalProfilePath);
        IModuleInputProfileProvider? profileProvider = gameModule as IModuleInputProfileProvider;

        var drawer = new SlideOverSettingsDrawer { IsOpen = options.OpenSettings };
        drawer.AttachInputProfiles(globalProfile, globalProfilePath, profileProvider);
        var overlay = new UiOverlayPresenter();
        var st = new LoopState
        {
            DiscInfo = discManifest is null ? "No disc attached" : $"{discManifest.SerialNumber} ({discManifest.Files.Count} files)"
        };

        bool isOffscreen = !video.HasWindow;
        st.IsOffscreen = isOffscreen;
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
            PollSubTickLook: () => PollLiveSubTickLook(video, host, cpu, drawer, overlay, settings, env, pacer, logger, st, isOffscreen),
            PresentSubTickFrame: () => PresentSubTickLoopFrame(video, host, cpu, drawer, overlay, manifest, settings, pacer, st.DiscInfo, logger.IsEnabled, isOffscreen),
            IsEnhancedFrameRateEnabled: () => settings.FrameRateMode == FrameRateMode.Enhanced));

        try
        {
            while (!st.QuitRequested && !host.HasFinished)
            {
                long t0 = Stopwatch.GetTimestamp();
                PumpEvents(video, host, cpu, drawer, overlay, settings, env, pacer, logger, st);
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
                    out bool releaseMouseCapture);
                st.WheelUp = false;
                st.WheelDown = false;
                Array.Clear(st.LatchedKeys);
                st.LatchedMouseMask = 0u;
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

                if (gameModule is not null) host.StepModuleFrameAsync(gameModule, CancellationToken.None).GetAwaiter().GetResult();
                else host.RunFrame();

                bool ranSubTickPresents = host.SubTickPresentsInStep > 0;
                bool skipIdleSecondVBlank = !ranSubTickPresents && st.SuppressNextIdleSubTickVBlank && cpu.Gpu.FrameDrawOps == 0 && !st.UiDirty;
                bool wantAutoShot = !string.IsNullOrWhiteSpace(autoShotPath) && (maxFrames > 0 ? st.HostFrame + 1 >= maxFrames : st.HostFrame + 1 == 2);
                string? frameShot = wantAutoShot ? autoShotPath : null;

                DrainSpuAudioToStream(cpu.Spu.Output, audioStream, audioBuf);

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
                        PresentLoopFrame(video, host, drawer, overlay, manifest, settings, cpu.Gpu.EffectiveWidescreen, st.DiscInfo, logger.IsEnabled, isOffscreen, frameShot);
                    }
                    st.UiDirty = false;
                    tGpu = Stopwatch.GetTimestamp();
                    pacer.TargetFps = 0.0;
                    _ = pacer.WaitForNextFrame();
                    pacer.TargetFps = ComputeTargetFps(settings, isOffscreen);
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
                    PresentLoopFrame(video, host, drawer, overlay, manifest, settings, cpu.Gpu.EffectiveWidescreen, st.DiscInfo, logger.IsEnabled, isOffscreen, frameShot);
                    st.UiDirty = false;
                    tGpu = Stopwatch.GetTimestamp();
                    pacer.TargetFps = ComputeTargetFps(settings, isOffscreen);
                    _ = pacer.WaitForNextFrame();
                    tEnd = Stopwatch.GetTimestamp();
                }

                host.RecordTimings(ElapsedMs(t0, tCpu), ElapsedMs(tCpu, tGpu), ElapsedMs(tGpu, tEnd));
                PsxTelemetrySnapshot telemetry = host.GetTelemetry();
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
        settings.Save(env.ConfigFilePath);
        overlay.MarkDirty();
        st.UiDirty = true;
    }

    private static (float MouseDx, float MouseDy, float StickX, float StickY) PollLiveSubTickLook(
        HostVideoSurface video, ReStationHost host, PsxCpu cpu, SlideOverSettingsDrawer drawer, UiOverlayPresenter overlay,
        RecompSettings settings, AppEnvironment env, HighResFramePacer pacer, ReStationLogger logger, LoopState st, bool isOffscreen)
    {
        if (!isOffscreen && video.SdlAvailable) PumpEvents(video, host, cpu, drawer, overlay, settings, env, pacer, logger, st);
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
        PresentLoopFrame(video, host, drawer, overlay, manifest, settings, cpu.Gpu.EffectiveWidescreen, discInfo, loggingActive, isOffscreen: false, shotPath: null);
        double savedTarget = pacer.TargetFps;
        pacer.TargetFps = 0.0;
        _ = pacer.WaitForNextFrame();
        pacer.TargetFps = savedTarget;
    }

    private static void PresentLoopFrame(HostVideoSurface video, ReStationHost host, SlideOverSettingsDrawer drawer, UiOverlayPresenter overlay,
        IReStationModuleManifest? manifest, RecompSettings settings, bool effectiveWidescreen, string discInfo, bool loggingActive, bool isOffscreen, string? shotPath)
    {
        if (isOffscreen && string.IsNullOrWhiteSpace(shotPath)) return;
        bool overlayActive = drawer.IsOpen || settings.IsFpsOverlayEnabled;
        if (!isOffscreen && video.UsesOpenGl && string.IsNullOrWhiteSpace(shotPath))
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
        if (!isOffscreen && video.Renderer != IntPtr.Zero && string.IsNullOrWhiteSpace(shotPath))
        {
            video.PresentLayeredSoftware(
                gw, gh, gameRgba, effectiveWidescreen,
                overlayActive, overlay.ActiveOverlayWidth, overlay.ActiveOverlayHeight,
                overlay.ActiveOverlayPixels, overlay.ActiveOverlayDstX, overlay.ActiveOverlayDstY, uiChanged);
            return;
        }

        if (!overlayActive) _ = overlay.EnsureRendered(drawer, settings, manifest, discInfo, loggingActive, host.GetTelemetry());
        overlay.Composite(gameRgba, gw, gh, settings, effectiveWidescreen);
        if (!isOffscreen && video.CanPresent)
            video.PresentRgba(UiOverlayPresenter.OverlayWidth, UiOverlayPresenter.OverlayHeight, overlay.CompositePixels);
        if (!string.IsNullOrWhiteSpace(shotPath))
            overlay.SaveCompositePng(shotPath);
    }

    internal static int DrainSpuAudioToStream(AudioRingBuffer output, IntPtr audioStream, short[] chunkBuf)
    {
        const int maxQueueBytes = (PsxSpu.SampleRate * 2 * sizeof(short)) / 10; // 100 ms target cap
        int totalSamples = 0;
        if (audioStream != IntPtr.Zero && Sdl3Native.SDL_GetAudioStreamQueued(audioStream) > maxQueueBytes * 2)
            _ = Sdl3Native.SDL_ClearAudioStream(audioStream);

        int maxRingSamples = (PsxSpu.SampleRate * 2) / 10;
        if (output.BufferedSamples > maxRingSamples * 2)
        {
            int trim = (output.BufferedSamples - maxRingSamples) & ~1;
            while (trim > 0)
            {
                int step = Math.Min(chunkBuf.Length & ~1, trim);
                int dropped = output.Read(chunkBuf.AsSpan(0, step));
                if (dropped <= 0) break;
                trim -= dropped;
            }
        }

        while (true)
        {
            int availEven = output.BufferedSamples & ~1;
            if (availEven <= 0) break;
            int toRead = Math.Min(chunkBuf.Length & ~1, availEven);
            if (toRead <= 0) break;
            int got = output.Read(chunkBuf.AsSpan(0, toRead));
            if (got <= 0) break;
            totalSamples += got;
            if (audioStream != IntPtr.Zero)
            {
                fixed (short* ap = chunkBuf) _ = Sdl3Native.SDL_PutAudioStreamData(audioStream, ap, got * sizeof(short));
            }
        }
        return totalSamples;
    }

    private static void SyncMouseCaptureState(HostVideoSurface video, SlideOverSettingsDrawer drawer, RecompSettings settings, LoopState st)
    {
        if (st.IsOffscreen || !video.HasWindow || drawer.IsOpen || settings.ControlMode != ControlMode.ModernWasdMouse)
        {
            if (st.MouseCaptured)
            {
                st.MouseCaptured = false;
                st.MouseDx = 0f;
                st.MouseDy = 0f;
                video.SetRelativeMouse(false);
            }
            return;
        }

        bool focused = !video.SdlAvailable || (Sdl3Native.SDL_GetWindowFlags(video.Window) & Sdl3Native.WindowInputFocus) != 0UL;
        if (!focused)
        {
            if (st.MouseCaptured)
            {
                st.MouseCaptured = false;
                st.MouseDx = 0f;
                st.MouseDy = 0f;
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
