// @module: reStation.Player.Program.MediaGate
// @purpose: First-run media gate loop prompting for .recomp module and PS1 disc ingestion when no media is present.
// @depends-on: System.IO, System.Threading, Foundation.Retro, reEngine.Core.Manifests, reStation.Core, reStation.Core.DiscIngestion
// @exports: Program (MediaGate partial)
// @owner: devv-player-ui (T6)

using Foundation.Core;
using Foundation.Retro;
using reEngine.Core.Manifests;
using reStation.Contracts;
using reStation.Core;
using reStation.Core.Cpu;
using reStation.Core.DiscIngestion;

namespace reStation.Player;

internal static unsafe partial class Program
{
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
                    if (!drawer.IsOpen) drawer.KeybindsTab.ClearListening();
                    st.MouseCaptured = !drawer.IsOpen;
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
                if (drawer.IsOpen && drawer.ActiveTab == 2 && drawer.KeybindsTab.IsListening)
                {
                    int captureCode = Sdl3Native.SdlButtonToBindingCode(btn);
                    if (captureCode != 0 && drawer.TryHandleMouseCapture(captureCode, settings, sandbox.ConfigFilePath))
                    {
                        overlay.MarkDirty(); st.UiDirty = true; continue;
                    }
                }
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
                    AttachDiscFromPath(dp, cpu, sandbox, settings, host, st, logger);
                else if (act == DrawerAction.ChangeModule && Sdl3Native.ShowOpenFileDialog("Select .recomp Module", Sdl3Native.ModuleFilter) is { } mp)
                {
                    settings.LastModulePath = Path.GetFileName(mp);
                    settings.Save(sandbox.ConfigFilePath);
                    logger.Info("MODULE", $"Selected next-launch module '{Path.GetFileName(mp)}'.");
                }
                else if (act == DrawerAction.None && !wasOpen && !drawer.IsOpen && !st.MouseCaptured)
                {
                    st.MouseCaptured = true;
                    video.SetRelativeMouse(true);
                }
                if (drawer.IsOpen) { st.MouseCaptured = false; video.SetRelativeMouse(false); }
            }
            else if (type == Sdl3Native.EventDropFile && Sdl3Native.EventDropPath(ev) is { } dropped)
            {
                AttachDiscFromPath(dropped, cpu, sandbox, settings, host, st, logger);
            }
        }
    }

    private static void AttachDiscFromPath(string discPath, PsxCpu cpu, SandboxEnvironment sandbox,
        RecompSettings settings, ReStationHost host, LoopState st, ReStationLogger logger)
    {
        DiscIngestResult r = DiscVerifierAndExtractor.IngestAsync(discPath, sandbox.DataDirectory, host.ModuleManifest, logger, AppContext.BaseDirectory).GetAwaiter().GetResult();
        if (!r.Success || r.Manifest is null) return;
        cpu.AttachDisc(r.Manifest);
        st.DiscInfo = $"{r.Manifest.SerialNumber} ({r.Manifest.Files.Count} files)";
        settings.LastDiscPath = Path.GetFileName(discPath);
        settings.Save(sandbox.ConfigFilePath);
    }
    private static bool RunFirstRunMediaGate(
        HostVideoSurface video, SandboxEnvironment sandbox, RecompSettings settings, ReStationLogger logger,
        ref ReStationModuleManifest? manifest, ref string? moduleDir, ref DiscManifest? discManifest, CliOptions options)
    {
        var view = new FirstRunMediaGateView();
        byte* ev = stackalloc byte[128];
        int frames = 0;
        int? gateMaxFrames = options.MaxFrames ?? (options.VisibleTest ? 120 : null);
        string? gateShot = !string.IsNullOrWhiteSpace(options.DumpScreenshotPath)
            ? Path.GetFullPath(options.DumpScreenshotPath)
            : options.VisibleTest
                ? Path.Combine(sandbox.LogDirectory, "screenshots", "visible-test.png")
                : null;

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
            if (gateMaxFrames is > 0 && frames >= gateMaxFrames.Value)
            {
                if (!string.IsNullOrWhiteSpace(gateShot))
                    RgbaPngWriter.WritePng(gateShot, FirstRunMediaGateView.CanvasWidth, FirstRunMediaGateView.CanvasHeight, view.Pixels);
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
            moduleDir = RecompPackageReader.ResolveModuleRootDirectory(full);
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
}
