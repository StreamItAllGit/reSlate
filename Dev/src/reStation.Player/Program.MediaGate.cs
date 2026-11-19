// @module: reStation.Player.Program.MediaGate
// @purpose: First-run media gate loop prompting for .recomp module and PS1 disc ingestion when no media is present.
// @depends-on: System.IO, System.Threading, Foundation.Retro, reEngine.Core.Manifests, reStation.Core, reStation.Core.DiscIngestion
// @exports: Program (MediaGate partial)
// @owner: devv-player-ui (T6)

using System.Globalization;
using System.Runtime.CompilerServices;
using Foundation.Core;
using Foundation.Retro;
using reEngine.Core.Manifests;
using reStation.Contracts;
using reStation.Core;
using reStation.Core.Aot;
using reStation.Core.Cpu;
using reStation.Core.DiscIngestion;
using reStation.Core.Input;

namespace reStation.Player;

internal static unsafe partial class Program
{
    private static void AttachDiscFromPath(string discPath, PsxCpu cpu, AppEnvironment env,
        RecompSettings settings, ReStationHost host, LoopState st, ReStationLogger logger)
    {
        DiscIngestResult r = DiscVerifierAndExtractor.IngestAsync(discPath, env.DataDirectory, host.ModuleManifest, logger, AppContext.BaseDirectory).GetAwaiter().GetResult();
        if (!r.Success || r.Manifest is null) return;
        cpu.AttachDisc(r.Manifest);
        st.DiscInfo = $"{r.Manifest.SerialNumber} ({r.Manifest.Files.Count} files)";
        settings.LastDiscPath = Path.GetFileName(discPath);
        settings.Save(env.ConfigFilePath);
    }
    private static bool RunFirstRunMediaGate(
        HostVideoSurface video, AppEnvironment env, RecompSettings settings, ReStationLogger logger,
        ref ReStationModuleManifest? manifest, ref string? moduleDir, ref DiscManifest? discManifest, CliOptions options,
        HostContractBridge? bridge = null
#if DEBUG || RESTATION_TEST_AUTOMATION
        , DebugAutomationHook? automation = null
#endif
        )
    {
        var view = new FirstRunMediaGateView();
        byte* ev = stackalloc byte[128];
        int frames = 0;
        int? gateMaxFrames = options.MaxFrames;
        string? gateShot = !string.IsNullOrWhiteSpace(options.DumpScreenshotPath)
            ? Path.GetFullPath(options.DumpScreenshotPath)
            : null;
        bool exitRequested = false;

        while (true)
        {
            while (video.SdlAvailable && Sdl3Native.SDL_PollEvent(ev) != 0)
            {
                uint type = Sdl3Native.EventType(ev);
                if (type == Sdl3Native.EventQuit) return false;
                if (type == Sdl3Native.EventKeyDown && Sdl3Native.EventKeyRepeat(ev) == 0 && Sdl3Native.EventScancode(ev) == Sdl3Native.ScEsc) return false;

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
                        TryLoadGateModule(mp, ref manifest, ref moduleDir, view, settings, env.ConfigFilePath, logger);
                    }
                    else if (hitDisc && Sdl3Native.ShowOpenFileDialog("Select SlateOne Disc Image", Sdl3Native.DiscImageFilter) is { } dp)
                    {
                        if (TryIngestGateDisc(dp, manifest, env, settings, view, logger, out discManifest)) return true;
                    }
                }
                else if (type == Sdl3Native.EventDropFile && Sdl3Native.EventDropPath(ev) is { } dropped)
                {
                    if (dropped.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || dropped.EndsWith(".recomp", StringComparison.OrdinalIgnoreCase))
                    {
                        TryLoadGateModule(dropped, ref manifest, ref moduleDir, view, settings, env.ConfigFilePath, logger);
                    }
                    else if (TryIngestGateDisc(dropped, manifest, env, settings, view, logger, out discManifest))
                    {
                        return true;
                    }
                }
            }

            if (bridge is not null)
            {
                bridge.DrainCommands(
                    onPointerClick: (_, _) => { },
                    onSyntheticScancode: sc => { if (sc == Sdl3Native.ScEsc) exitRequested = true; });
                if (exitRequested) return false;
            }

            if (manifest is not null && discManifest is null && moduleDir is not null &&
                PreloadRomDetector.Detect(moduleDir, manifest).PrimaryMediaPath is { } preload &&
                TryIngestGateDisc(preload, manifest, env, settings, view, logger, out discManifest))
            {
                return true;
            }

            view.Render(manifest);
            video.PresentRgba(FirstRunMediaGateView.CanvasWidth, FirstRunMediaGateView.CanvasHeight, view.Pixels);
            if (bridge is not null)
            {
                bridge.PublishFramebufferRgba(FirstRunMediaGateView.CanvasWidth, FirstRunMediaGateView.CanvasHeight, view.Pixels);
                bridge.SyncMediaGateWidgets(view, manifest is not null, settings.BackendId, settings.ExecutionTierMode);
#if DEBUG || RESTATION_TEST_AUTOMATION
                automation?.SyncWidgets(bridge.GetOverlayWidgets());
#endif
            }
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

    private static bool TryIngestGateDisc(string discPath, ReStationModuleManifest? manifest, AppEnvironment env,
        RecompSettings settings, FirstRunMediaGateView view, ReStationLogger logger, out DiscManifest? discManifest)
    {
        discManifest = null;
        if (manifest is null) { view.StatusMessage = "Load a .recomp module before ingesting a disc image."; view.StatusIsError = true; return false; }
        DiscIngestResult res = view.TryIngestAsync(discPath, env.DataDirectory, manifest, logger, CancellationToken.None).GetAwaiter().GetResult();
        if (!res.Success || res.Manifest is null) return false;
        discManifest = res.Manifest;
        settings.LastDiscPath = Path.GetFileName(discPath);
        settings.Save(env.ConfigFilePath);
        return true;
    }

    private static IRecompGameModule LoadGameModuleWithUiPump(
        ReStationModuleManifest manifest,
        string moduleDir,
        LocalAotLoadOptions aotOptions,
        HostVideoSurface video,
        bool isOffscreen,
        out ModuleAssemblyLoader? moduleAlc,
        out bool quitRequested)
    {
        quitRequested = false;
        if (isOffscreen || !video.HasWindow)
            return ModuleAssemblyLoader.LoadGameModule(manifest, moduleDir, aotOptions, out moduleAlc);

        ModuleAssemblyLoader? loadedAlc = null;
        Task<IRecompGameModule> loadTask = Task.Run(() =>
        {
            IRecompGameModule mod = ModuleAssemblyLoader.LoadGameModule(manifest, moduleDir, aotOptions, out ModuleAssemblyLoader alc);
            loadedAlc = alc;
            return mod;
        });

        var statusView = new FirstRunMediaGateView();
        byte* ev = stackalloc byte[128];
        int tick = 0;
        while (!loadTask.IsCompleted)
        {
            while (video.SdlAvailable && Sdl3Native.SDL_PollEvent(ev) != 0)
            {
                if (Sdl3Native.EventType(ev) == Sdl3Native.EventQuit)
                    quitRequested = true;
            }
            string dots = new string('.', (tick++ / 12) % 4);
            statusView.RenderStatus(manifest, "Preparing Native Module", $"Verifying media and local AOT cache{dots}");
            video.PresentRgba(FirstRunMediaGateView.CanvasWidth, FirstRunMediaGateView.CanvasHeight, statusView.Pixels);
            Thread.Sleep(25);
        }

        IRecompGameModule result = loadTask.GetAwaiter().GetResult();
        moduleAlc = loadedAlc;
        return result;
    }

    private static int RunDoctor(CliOptions options, AppEnvironment env, RecompSettings settings, ReStationLogger logger)
    {
        SandboxResolution sandbox = env.Sandbox;
        bool isolatedTest = string.Equals(Environment.GetEnvironmentVariable(SandboxEnvironmentSeam.EnvIsolatedTest), "1", StringComparison.Ordinal);
        string baseDir = AppContext.BaseDirectory;
        bool hasSdl = File.Exists(Path.Combine(baseDir, "SDL3.dll")) || File.Exists(Path.Combine(baseDir, "runtimes", "win-x64", "native", "SDL3.dll"));
        bool has7z = File.Exists(Path.Combine(baseDir, "7z.exe"));
        bool hasCompiler = File.Exists(Path.Combine(baseDir, "reSlate.Compiler.exe")) || File.Exists(Path.Combine(baseDir, "reStation.Compiler.exe"));
        bool automationCompiled =
#if DEBUG || RESTATION_TEST_AUTOMATION
            true;
#else
            false;
#endif
        string[] lines =
        [
            $"reSlate Doctor v{ReStationEngineInfo.Version} (appId={ReStationEngineInfo.AppId}, track={ReStationEngineInfo.CompatibleEngineTrack})",
            $"Containment: isolatedTest={isolatedTest} sandboxActive={sandbox.IsSandboxActive} mode={env.PresentationMode}",
            $"Paths: dataDir={env.DataDirectory} config={env.ConfigFilePath} logDir={env.LogDirectory}",
            $"Execution: dynamicCode={RuntimeFeature.IsDynamicCodeSupported} tier={settings.ExecutionTierMode} compilerPresent={hasCompiler}",
            $"Host: backend={settings.BackendId} required={options.RequireBackendId ?? "none"} sdl3={hasSdl} sevenZip={has7z} automationCompiled={automationCompiled}",
            $"DeferredBackends (v0.3.1): {PsxRenderBackendIds.Vulkan}, {PsxRenderBackendIds.ParallelCpu}"
        ];
        foreach (string line in lines)
        {
            Console.WriteLine(line);
            logger.Info("DOCTOR", line);
        }
        PsxTelemetrySnapshot docSnap = PsxTelemetrySnapshot.Empty with { FrameNumber = 1, ResolutionScale = settings.ResolutionScale, BackendId = settings.BackendId };
        _ = logger.TryHeartbeat(1, 1, docSnap.ToHeartbeatMetrics(), force: true);
        return 0;
    }
}
