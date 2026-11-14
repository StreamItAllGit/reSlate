// @module: reStation.Player.Program.FrameLoop
// @purpose: First-run media gate loop, SDL3 runtime loop (input, audio ring drain, F1 drawer, F3 HUD, GL/SW present, heartbeats), and IVisibleTestHarness.
// @depends-on: System.Diagnostics, Foundation.Core, Foundation.Retro, reStation.Contracts, reStation.Core, reStation.Core.Audio, reStation.Core.Cpu, reStation.Core.DiscIngestion, reStation.Core.Input, reStation.Core.Saves
// @exports: Program (FrameLoop partial), VisibleTestHarness
// @owner: devv-player-ui (T6)

using System.Diagnostics;
using Foundation.Core;
using Foundation.Retro;
using reStation.Contracts;
using reStation.Core;
using reStation.Core.Audio;
using reStation.Core.Cpu;
using reStation.Core.DiscIngestion;
using reStation.Core.Input;
using reStation.Core.Saves;

namespace reStation.Player;

internal static unsafe partial class Program
{
    private sealed class LoopState
    {
        public IntPtr Gamepad = HostInputMapper.OpenFirstGamepad();
        public int MouseDx, MouseDy;
        public bool MouseLeft, MouseRight, MouseCaptured, QuitRequested, PrevSave, PrevLoad, UiDirty = true;
        public long HostFrame;
        public string DiscInfo = string.Empty;
    }

    private static bool RunFirstRunMediaGate(
        HostVideoSurface video, SandboxEnvironment sandbox, RecompSettings settings, ReStationLogger logger,
        ref ReStationModuleManifest? manifest, ref string? moduleDir, ref DiscManifest? discManifest, CliOptions options)
    {
        var view = new FirstRunMediaGateView();
        byte* ev = stackalloc byte[128];
        int frames = 0;

        while (true)
        {
            while (video.SdlAvailable && Sdl3Native.SDL_PollEvent(ev) != 0)
            {
                uint type = Sdl3Native.EventType(ev);
                if (type == Sdl3Native.EventQuit) return false;
                if (type == Sdl3Native.EventKeyDown && Sdl3Native.EventScancode(ev) == Sdl3Native.ScEsc) return false;

                if (type is Sdl3Native.EventMouseMotion or Sdl3Native.EventMouseButtonDown)
                {
                    (int winW, int winH) = video.GetWindowSize();
                    (float mx, float my) = Sdl3Native.EventMousePosition(ev);
                    (int ox, int oy) = UiOverlayPresenter.WindowToOverlayCoords(mx, my, winW, winH);
                    bool hitDisc = view.HitTestBrowseDisc(ox, oy, manifest is not null);
                    bool hitMod = view.HitTestBrowseModule(ox, oy, manifest is not null);
                    if (type == Sdl3Native.EventMouseMotion) { view.HoverDisc = hitDisc; view.HoverModule = hitMod; }
                    else if (hitMod && Sdl3Native.ShowOpenFileDialog("Select .recomp Module", Sdl3Native.ModuleFilter) is { } mp)
                    {
                        TryLoadGateModule(mp, ref manifest, ref moduleDir, view, settings, sandbox.ConfigFilePath, logger);
                    }
                    else if (hitDisc && Sdl3Native.ShowOpenFileDialog("Select PS1 Disc Image", Sdl3Native.DiscImageFilter) is { } dp)
                    {
                        if (TryIngestGateDisc(dp, manifest, sandbox, settings, view, logger, out discManifest)) return true;
                    }
                }
                else if (type == Sdl3Native.EventDropFile && Sdl3Native.EventDropPath(ev) is { } dropped)
                {
                    if (dropped.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || dropped.EndsWith(".recomp", StringComparison.OrdinalIgnoreCase))
                    {
                        TryLoadGateModule(dropped, ref manifest, ref moduleDir, view, settings, sandbox.ConfigFilePath, logger);
                    }
                    else if (TryIngestGateDisc(dropped, manifest, sandbox, settings, view, logger, out discManifest))
                    {
                        return true;
                    }
                }
            }

            if (manifest is not null && discManifest is null && moduleDir is not null &&
                PreloadRomDetector.Detect(moduleDir, manifest).PrimaryMediaPath is { } preload &&
                TryIngestGateDisc(preload, manifest, sandbox, settings, view, logger, out discManifest))
            {
                return true;
            }

            view.Render(manifest);
            video.PresentRgba(FirstRunMediaGateView.CanvasWidth, FirstRunMediaGateView.CanvasHeight, view.Pixels);
            frames++;
            if (options.MaxFrames is > 0 && frames >= options.MaxFrames.Value)
            {
                if (!string.IsNullOrWhiteSpace(options.DumpScreenshotPath))
                    RgbaPngWriter.WritePng(options.DumpScreenshotPath, FirstRunMediaGateView.CanvasWidth, FirstRunMediaGateView.CanvasHeight, view.Pixels);
                return false;
            }
            Thread.Sleep(16);
        }
    }

    private static void TryLoadGateModule(string path, ref ReStationModuleManifest? manifest, ref string? moduleDir,
        FirstRunMediaGateView view, RecompSettings settings, string configPath, ReStationLogger logger)
    {
        try
        {
            string full = Path.GetFullPath(path);
            manifest = ReStationModuleManifest.LoadFromFile(full);
            moduleDir = Path.GetDirectoryName(full)!;
            settings.LastModulePath = Path.GetFileName(full);
            settings.Save(configPath);
            view.StatusMessage = $"Loaded module '{manifest.DisplayName}'. Select disc image or preload in data\\rom\\.";
            view.StatusIsError = false;
            logger.Info("MODULE", $"Gate loaded '{manifest.ModuleId}'.");
        }
        catch (Exception ex) { view.StatusMessage = ex.Message; view.StatusIsError = true; }
    }

    private static bool TryIngestGateDisc(string discPath, ReStationModuleManifest? manifest, SandboxEnvironment sandbox,
        RecompSettings settings, FirstRunMediaGateView view, ReStationLogger logger, out DiscManifest? discManifest)
    {
        discManifest = null;
        if (manifest is null) { view.StatusMessage = "Load a .recomp module before ingesting a disc image."; view.StatusIsError = true; return false; }
        DiscIngestResult res = view.TryIngestAsync(discPath, sandbox.DataDirectory, manifest, logger, CancellationToken.None).GetAwaiter().GetResult();
        if (!res.Success || res.Manifest is null) return false;
        discManifest = res.Manifest;
        settings.LastDiscPath = Path.GetFileName(discPath);
        settings.Save(sandbox.ConfigFilePath);
        return true;
    }

    private static void RunHostLoop(
        HostVideoSurface video, ReStationHost host, PsxCpu cpu, IRecompGameModule? gameModule,
        IReStationModuleManifest? manifest, DiscManifest? discManifest, CliOptions options, SandboxEnvironment sandbox,
        RecompSettings settings, HighResFramePacer pacer, ReStationLogger logger)
    {
        var drawer = new SlideOverSettingsDrawer { IsOpen = options.OpenSettings };
        var overlay = new UiOverlayPresenter();
        var st = new LoopState
        {
            DiscInfo = discManifest is null ? "No disc attached" : $"{discManifest.SerialNumber} ({discManifest.Files.Count} files)"
        };
        var audioSpec = new SdlAudioSpec { Format = Sdl3Native.AudioS16Le, Channels = 2, Freq = PsxSpu.SampleRate };
        IntPtr audioStream = video.SdlAvailable && !options.Headless
            ? Sdl3Native.SDL_OpenAudioDeviceStream(Sdl3Native.DefaultAudioPlaybackDevice, &audioSpec, IntPtr.Zero, IntPtr.Zero)
            : IntPtr.Zero;
        if (audioStream != IntPtr.Zero) _ = Sdl3Native.SDL_ResumeAudioStreamDevice(audioStream);

        short[] audioBuf = new short[ReStationHost.SamplesPerFrame * 2];
        int maxFrames = options.MaxFrames ?? (options.Headless && gameModule is null ? 1 : 0);
        long totalFrames = Math.Max(1L, maxFrames > 0 ? maxFrames : 1L);
        host.AttachRuntimeContext(new HostRuntimeContext(
            IsHeadless: options.Headless,
            HasPendingCapture: () => !string.IsNullOrWhiteSpace(options.DumpScreenshotPath)
                && (maxFrames > 0 ? st.HostFrame + 1 >= maxFrames : st.HostFrame + 1 == 2),
            IsModernMouseLookActive: () => !drawer.IsOpen && st.MouseCaptured,
            MouseSensitivity: () => (float)settings.MouseSensitivity,
            PollSubTickLook: () => PollLiveSubTickLook(video, host, cpu, drawer, overlay, settings, sandbox, logger, st, options.Headless),
            PresentSubTickFrame: () => PresentSubTickLoopFrame(video, host, cpu, drawer, overlay, manifest, settings, st.DiscInfo, logger.IsEnabled, options.Headless)));

        try
        {
            while (!st.QuitRequested && !host.HasFinished)
            {
                long t0 = Stopwatch.GetTimestamp();
                PumpEvents(video, host, cpu, drawer, overlay, settings, sandbox, logger, st);
                if (st.QuitRequested) break;

                HostInputState input = HostInputMapper.BuildLiveInput(cpu, drawer.IsOpen, settings, st.Gamepad, ref st.MouseDx, ref st.MouseDy, st.MouseLeft, st.MouseRight);
                bool saveEdge = input.QuickSave && !st.PrevSave, loadEdge = input.QuickLoad && !st.PrevLoad;
                st.PrevSave = input.QuickSave; st.PrevLoad = input.QuickLoad;
                if (saveEdge) host.SaveQuickSlot(0);
                if (loadEdge) host.RequestQuickLoad(0);
                ModernInputController.ApplyToCpu(cpu, input, settings);

                if (gameModule is not null) host.StepModuleFrameAsync(gameModule, CancellationToken.None).GetAwaiter().GetResult();
                else host.RunFrame();
                video.SyncViewport(settings.ResolutionScale);
                cpu.Gpu.PresentFrame();
                long tCpu = Stopwatch.GetTimestamp();

                int samplesRead = cpu.Spu.Output.Read(audioBuf);
                if (audioStream != IntPtr.Zero && samplesRead > 0)
                {
                    fixed (short* ap = audioBuf) _ = Sdl3Native.SDL_PutAudioStreamData(audioStream, ap, samplesRead * sizeof(short));
                }

                st.HostFrame++;
                bool wantShot = !string.IsNullOrWhiteSpace(options.DumpScreenshotPath) && (maxFrames > 0 ? st.HostFrame >= maxFrames : st.HostFrame == 2);
                PresentLoopFrame(video, host, drawer, overlay, manifest, settings, st.DiscInfo, logger.IsEnabled, options.Headless, wantShot ? options.DumpScreenshotPath : null);
                st.UiDirty = false;
                long tGpu = Stopwatch.GetTimestamp();

                pacer.TargetFps = ComputeTargetFps(settings, options.Headless);
                _ = pacer.WaitForNextFrame();
                long tEnd = Stopwatch.GetTimestamp();

                host.RecordTimings(ElapsedMs(t0, tCpu), ElapsedMs(tCpu, tGpu), ElapsedMs(tGpu, tEnd));
                PsxTelemetrySnapshot telemetry = host.GetTelemetry();
                _ = logger.TryHeartbeat(st.HostFrame, Math.Max(totalFrames, st.HostFrame), telemetry.ToHeartbeatMetrics(), force: st.HostFrame == 1);

                if (maxFrames > 0 && st.HostFrame >= maxFrames) break;
            }
            logger.Info("RUNTIME", $"Session end frames={st.HostFrame} cpuFrames={cpu.FrameCount} backend={video.Backend.BackendId} xaDrops={cpu.Spu.XaDrops}");
        }
        finally
        {
            if (audioStream != IntPtr.Zero) Sdl3Native.SDL_DestroyAudioStream(audioStream);
            if (st.Gamepad != IntPtr.Zero) Sdl3Native.SDL_CloseGamepad(st.Gamepad);
        }
    }

    private static (float MouseDx, float MouseDy, float StickX, float StickY) PollLiveSubTickLook(
        HostVideoSurface video, ReStationHost host, PsxCpu cpu, SlideOverSettingsDrawer drawer, UiOverlayPresenter overlay,
        RecompSettings settings, SandboxEnvironment sandbox, ReStationLogger logger, LoopState st, bool headless)
    {
        if (!headless && video.SdlAvailable) PumpEvents(video, host, cpu, drawer, overlay, settings, sandbox, logger, st);
        float mdx = !drawer.IsOpen && st.MouseCaptured ? st.MouseDx : 0f;
        float mdy = !drawer.IsOpen && st.MouseCaptured ? st.MouseDy : 0f;
        st.MouseDx = 0; st.MouseDy = 0;
        (float sx, float sy) = HostInputMapper.ReadRightStick(st.Gamepad);
        return (mdx, mdy, sx, sy);
    }

    private static void PresentSubTickLoopFrame(HostVideoSurface video, ReStationHost host, PsxCpu cpu, SlideOverSettingsDrawer drawer,
        UiOverlayPresenter overlay, IReStationModuleManifest? manifest, RecompSettings settings, string discInfo, bool loggingActive, bool headless)
    {
        if (headless) return;
        video.SyncViewport(settings.ResolutionScale);
        cpu.Gpu.PresentFrame();
        PresentLoopFrame(video, host, drawer, overlay, manifest, settings, discInfo, loggingActive, headless: false, shotPath: null);
    }

    private static void PumpEvents(HostVideoSurface video, ReStationHost host, PsxCpu cpu, SlideOverSettingsDrawer drawer,
        UiOverlayPresenter overlay, RecompSettings settings, SandboxEnvironment sandbox, ReStationLogger logger, LoopState st)
    {
        if (!video.SdlAvailable) return;
        byte* ev = stackalloc byte[128];
        while (Sdl3Native.SDL_PollEvent(ev) != 0)
        {
            uint type = Sdl3Native.EventType(ev);
            if (type == Sdl3Native.EventQuit) { st.QuitRequested = true; return; }
            if (type == Sdl3Native.EventGamepadAdded && st.Gamepad == IntPtr.Zero) st.Gamepad = HostInputMapper.OpenFirstGamepad();
            if (type == Sdl3Native.EventMouseMotion && !drawer.IsOpen)
            {
                (float dx, float dy) = Sdl3Native.EventMouseRelative(ev);
                st.MouseDx += (int)dx; st.MouseDy += (int)dy;
            }
            else if (type == Sdl3Native.EventKeyDown)
            {
                int sc = Sdl3Native.EventScancode(ev);
                if (drawer.IsOpen && drawer.TryHandleKeyCapture(sc, settings, sandbox.ConfigFilePath)) { overlay.MarkDirty(); st.UiDirty = true; continue; }
                if (sc == Sdl3Native.ScF1)
                {
                    drawer.IsOpen = !drawer.IsOpen;
                    if (!drawer.IsOpen) drawer.KeybindsTab.ListeningAction = null;
                    st.MouseCaptured = !drawer.IsOpen;
                    if (drawer.IsOpen) { st.MouseLeft = st.MouseRight = false; }
                    video.SetRelativeMouse(st.MouseCaptured);
                    overlay.MarkDirty(); st.UiDirty = true;
                }
                else if (sc == Sdl3Native.ScF3) { settings.IsFpsOverlayEnabled = !settings.IsFpsOverlayEnabled; settings.Save(sandbox.ConfigFilePath); overlay.MarkDirty(); }
                else if (sc == Sdl3Native.ScEsc && st.MouseCaptured) { st.MouseCaptured = false; video.SetRelativeMouse(false); }
                else if (sc == Sdl3Native.ScF5) host.SaveQuickSlot(0);
                else if (sc == Sdl3Native.ScF9) host.RequestQuickLoad(0);
            }
            else if (type == Sdl3Native.EventMouseButtonDown)
            {
                byte btn = Sdl3Native.EventMouseButton(ev);
                bool wasOpen = drawer.IsOpen;
                (int winW, int winH) = video.GetWindowSize();
                (float mx, float my) = Sdl3Native.EventMousePosition(ev);
                (int ox, int oy) = UiOverlayPresenter.WindowToOverlayCoords(mx, my, winW, winH);
                DrawerAction act = drawer.HandleClick(ox, oy, settings, sandbox.ConfigFilePath);
                if (act != DrawerAction.None)
                {
                    video.Backend.ScaleFactor = settings.ResolutionScale;
                    cpu.Pgxp.IsEnabled = settings.IsPgxpEnabled;
                    overlay.MarkDirty();
                    st.UiDirty = true;
                }
                if (act == DrawerAction.ToggleFullscreen) video.SetFullscreen(settings.IsFullscreen);
                else if (act == DrawerAction.ToggleVsync) video.SetVsync(settings.IsVsyncEnabled);
                else if (act == DrawerAction.ToggleBackend) logger.Info("VIDEO", $"Saved backend preference '{settings.BackendId}' (applies on next launch).");
                else if (act == DrawerAction.ToggleRuntimeLogging) logger.SetEnabled(settings.ShouldEnableRuntimeLogs || sandbox.TestMode);
                else if (act == DrawerAction.QuickSave) host.SaveQuickSlot(0);
                else if (act == DrawerAction.QuickLoad) host.RequestQuickLoad(0);
                else if (act == DrawerAction.ChangeDisc && Sdl3Native.ShowOpenFileDialog("Select PS1 Disc Image", Sdl3Native.DiscImageFilter) is { } dp)
                {
                    DiscIngestResult r = DiscVerifierAndExtractor.IngestAsync(dp, sandbox.DataDirectory, host.ModuleManifest, logger, AppContext.BaseDirectory).GetAwaiter().GetResult();
                    if (r.Success && r.Manifest is not null)
                    {
                        cpu.AttachDisc(r.Manifest);
                        st.DiscInfo = $"{r.Manifest.SerialNumber} ({r.Manifest.Files.Count} files)";
                        settings.LastDiscPath = Path.GetFileName(dp);
                        settings.Save(sandbox.ConfigFilePath);
                    }
                }
                else if (act == DrawerAction.ChangeModule && Sdl3Native.ShowOpenFileDialog("Select .recomp Module", Sdl3Native.ModuleFilter) is { } mp)
                {
                    settings.LastModulePath = Path.GetFileName(mp);
                    settings.Save(sandbox.ConfigFilePath);
                    logger.Info("MODULE", $"Selected next-launch module '{Path.GetFileName(mp)}'.");
                }
                else if (act == DrawerAction.None && !wasOpen && !drawer.IsOpen)
                {
                    if (!st.MouseCaptured) { st.MouseCaptured = true; video.SetRelativeMouse(true); }
                    if (btn == 1) st.MouseLeft = true; else if (btn == 3) st.MouseRight = true;
                }
                if (drawer.IsOpen) { st.MouseCaptured = st.MouseLeft = st.MouseRight = false; video.SetRelativeMouse(false); }
            }
            else if (type == Sdl3Native.EventMouseButtonUp)
            {
                byte btn = Sdl3Native.EventMouseButton(ev);
                if (btn == 1) st.MouseLeft = false; else if (btn == 3) st.MouseRight = false;
            }
            else if (type == Sdl3Native.EventDropFile && Sdl3Native.EventDropPath(ev) is { } dropped)
            {
                DiscIngestResult r = DiscVerifierAndExtractor.IngestAsync(dropped, sandbox.DataDirectory, host.ModuleManifest, logger, AppContext.BaseDirectory).GetAwaiter().GetResult();
                if (r.Success && r.Manifest is not null)
                {
                    cpu.AttachDisc(r.Manifest);
                    st.DiscInfo = $"{r.Manifest.SerialNumber} ({r.Manifest.Files.Count} files)";
                    settings.LastDiscPath = Path.GetFileName(dropped);
                    settings.Save(sandbox.ConfigFilePath);
                }
            }
        }
    }

    private static void PresentLoopFrame(HostVideoSurface video, ReStationHost host, SlideOverSettingsDrawer drawer, UiOverlayPresenter overlay,
        IReStationModuleManifest? manifest, RecompSettings settings, string discInfo, bool loggingActive, bool headless, string? shotPath)
    {
        if (headless && string.IsNullOrWhiteSpace(shotPath)) return;
        bool overlayActive = drawer.IsOpen || settings.IsFpsOverlayEnabled;
        if (!headless && video.UsesOpenGl && !overlayActive && string.IsNullOrWhiteSpace(shotPath))
        {
            video.SwapGlOnly();
            return;
        }

        byte[] gameRgba = video.Backend.CaptureRgba(out int gw, out int gh);
        _ = overlay.EnsureRendered(drawer, settings, manifest, discInfo, loggingActive, host.GetTelemetry());
        overlay.Composite(gameRgba, gw, gh, settings);
        if (!headless && video.CanPresent)
            video.PresentRgba(UiOverlayPresenter.OverlayWidth, UiOverlayPresenter.OverlayHeight, overlay.CompositePixels);
        if (!string.IsNullOrWhiteSpace(shotPath))
            overlay.SaveCompositePng(shotPath);
    }

    private static double ElapsedMs(long a, long b) => (b - a) * 1000.0 / Stopwatch.Frequency;
}
