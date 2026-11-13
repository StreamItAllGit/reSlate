// @module: reStation.Player.Program
// @purpose: reStation.Player entry point: AttachConsole, CLI parse, fail-closed SandboxEnvironment, crash bundles, preload/disc verification, and host launch.
// @depends-on: System.Runtime.InteropServices, Foundation.Core, Foundation.Retro, reStation.Contracts, reStation.Core, reStation.Core.Cpu, reStation.Core.DiscIngestion
// @exports: Program
// @owner: devv-player-ui (T6)

using System.Runtime.InteropServices;
using Foundation.Core;
using Foundation.Retro;
using reStation.Contracts;
using reStation.Core;
using reStation.Core.Cpu;
using reStation.Core.DiscIngestion;

namespace reStation.Player;

internal static partial class Program
{
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && !Console.IsOutputRedirected && !Console.IsErrorRedirected)
            _ = AttachConsole(AttachParentProcess);

        try
        {
            return RunMain(args);
        }
        finally
        {
            Console.Out.Flush();
            Console.Error.Flush();
        }
    }

    private static int RunMain(string[] args)
    {
        CliOptions options;
        try { options = CliOptions.Parse(args); }
        catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 2; }

        if (options.ShowHelp) { Console.WriteLine(CliOptions.HelpText); return 0; }

        SandboxEnvironment sandbox;
        try { sandbox = options.CreateSandbox(); }
        catch (InvalidOperationException ex) { Console.Error.WriteLine(ex.Message); return 3; }

        RecompSettings settings = RecompSettings.Load(sandbox.ConfigFilePath).Value;
        options.ApplyOverrides(settings);
        using var logger = new ReStationLogger(sandbox.LogDirectory, settings.ShouldEnableRuntimeLogs);
        CrashBundleService.RegisterGlobalHandlers(sandbox.LogDirectory, ReStationEngineInfo.AppId, ReStationEngineInfo.Version, logger.GetRecentLines);
        logger.Info("BOOT", $"reStation v{ReStationEngineInfo.Version} testMode={sandbox.TestMode} headless={options.Headless} visibleTest={options.VisibleTest} backend={settings.BackendId} scale={settings.ResolutionScale}x");

        try
        {
            return RunApplication(options, sandbox, settings, logger);
        }
        catch (Exception ex)
        {
            logger.Error("FATAL", $"{ex.GetType().Name}: {ex.Message}");
            CrashBundleService.TryWriteCrashBundle(sandbox.LogDirectory, ReStationEngineInfo.AppId, ReStationEngineInfo.Version, ex, logger.GetRecentLines());
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int RunApplication(CliOptions options, SandboxEnvironment sandbox, RecompSettings settings, ReStationLogger logger)
    {
        ReStationModuleManifest? manifest = null;
        string? moduleDir = null;
        if (!string.IsNullOrWhiteSpace(options.ModulePath))
        {
            string fullModule = Path.GetFullPath(options.ModulePath);
            manifest = ReStationModuleManifest.LoadFromFile(fullModule);
            moduleDir = Path.GetDirectoryName(fullModule)!;
            logger.Info("MODULE", $"Loaded manifest '{manifest.ModuleId}' ({manifest.DisplayName}) serial={manifest.SerialNumber}.");
        }

        if (options.VerifyOnly)
            return RunVerify(options, sandbox, manifest, moduleDir, logger);

        if (!string.IsNullOrWhiteSpace(options.FixturePath))
        {
            string fixtureOut = Path.Combine(sandbox.LogDirectory, "fixtures");
            return FixtureRunnerHost.RunWithEngineAsync(options.FixturePath, fixtureOut, manifest, moduleDir, sandbox.DataDirectory, settings, logger)
                .GetAwaiter().GetResult();
        }

        if (options.VisibleTest)
        {
            string shotPath = string.IsNullOrWhiteSpace(options.DumpScreenshotPath)
                ? Path.Combine(sandbox.LogDirectory, "screenshots", "visible-test.png")
                : Path.GetFullPath(options.DumpScreenshotPath);
            var harness = new VisibleTestHarness(settings, logger);
            bool ok = harness.RunVisibleTestAsync(new VisibleTestShowcaseOptions(3000, true, shotPath)).GetAwaiter().GetResult();
            return ok ? 0 : 1;
        }

        DiscManifest? discManifest = DiscManifest.TryLoad(sandbox.DataDirectory);
        if (manifest is not null)
        {
            string? candidateMedia = !string.IsNullOrWhiteSpace(options.DiscPath)
                ? options.DiscPath
                : discManifest is null ? PreloadRomDetector.Detect(moduleDir!, manifest).PrimaryMediaPath : null;
            if (!string.IsNullOrWhiteSpace(candidateMedia))
            {
                DiscIngestResult ingest = DiscVerifierAndExtractor
                    .IngestAsync(candidateMedia, sandbox.DataDirectory, manifest, logger, AppContext.BaseDirectory)
                    .GetAwaiter().GetResult();
                if (!ingest.Success) { Console.Error.WriteLine(ingest.Message); return 4; }
                discManifest = ingest.Manifest;
                settings.LastDiscPath = Path.GetFileName(candidateMedia);
                settings.Save(sandbox.ConfigFilePath);
            }
        }

        bool needsGate = !options.Headless && (manifest is null || discManifest is null);
        using HostVideoSurface video = options.Headless
            ? HostVideoSurface.Create($"reStation v{ReStationEngineInfo.Version}", settings, headless: true, foreground: false, logger)
            : HostVideoSurface.Create($"reStation v{ReStationEngineInfo.Version} \u2014 F1 Settings | F3 HUD", settings, headless: false, foreground: true, logger);

        if (needsGate)
        {
            if (!RunFirstRunMediaGate(video, sandbox, settings, logger, ref manifest, ref moduleDir, ref discManifest, options))
                return 0;
        }

        if (options.Headless && manifest is not null && discManifest is null)
        {
            logger.Warn("BOOT", "Headless session started without a verified disc image or data\\rom preload; exiting cleanly.");
            logger.TryHeartbeat(PsxTelemetrySnapshot.Empty with { ResolutionScale = settings.ResolutionScale, BackendId = video.Backend.BackendId }, force: true);
            return 0;
        }

        ReStationModuleManifest effectiveManifest = manifest ?? new ReStationModuleManifest
        {
            ModuleId = "restation-idle", Title = "reStation", DisplayName = "reStation"
        };
        var cpu = new PsxCpu(settings, sandbox.DataDirectory, video.Backend, logger);
        if (discManifest is not null) cpu.AttachDisc(discManifest);

        double initialFps = ComputeTargetFps(settings, options.Headless);
        using var pacer = new HighResFramePacer(initialFps);
        var hostOptions = new RecompHostOptions
        {
            TargetFps = initialFps,
            UnlockFps = settings.TargetFps <= 0,
            ScaleFactor = settings.ResolutionScale,
            EnableHudOverlay = settings.IsFpsOverlayEnabled
        };
        using var host = new ReStationHost(cpu, effectiveManifest, sandbox.DataDirectory, hostOptions, pacer, logger)
        {
            CdSectorsPerSecProvider = () => cpu.CdSectorsPerSec
        };

        IRecompGameModule? gameModule = null;
        ModuleAssemblyLoader? moduleAlc = null;
        if (manifest is not null && moduleDir is not null && !string.IsNullOrWhiteSpace(manifest.EntryAssembly))
        {
            gameModule = ModuleAssemblyLoader.LoadGameModule(manifest, moduleDir, out moduleAlc);
            gameModule.InitializeAsync(host, CancellationToken.None).GetAwaiter().GetResult();
        }

        RunHostLoop(video, host, cpu, gameModule, manifest, discManifest, options, sandbox, settings, pacer, logger);
        GC.KeepAlive(moduleAlc);
        return 0;
    }

    private static int RunVerify(CliOptions options, SandboxEnvironment sandbox, ReStationModuleManifest? manifest, string? moduleDir, ReStationLogger logger)
    {
        DiscManifest? disc = DiscManifest.TryLoad(sandbox.DataDirectory);
        string? preload = manifest is not null && moduleDir is not null ? PreloadRomDetector.Detect(moduleDir, manifest).PrimaryMediaPath : null;
        bool ok = disc is not null || (options.ModulePath is not null && preload is not null);
        string msg = disc is not null
            ? $"Verified: {disc.SerialNumber} ({disc.Files.Count} files)"
            : preload is not null
                ? $"Preload ready: {Path.GetFileName(preload)}"
                : "Not verified: missing verified disc gamedata and data\\rom preload.";
        Console.WriteLine(msg);
        logger.Info("VERIFY", msg);
        logger.TryHeartbeat(PsxTelemetrySnapshot.Empty, force: true);
        return ok ? 0 : 1;
    }

    private static double ComputeTargetFps(RecompSettings settings, bool headless) =>
        headless ? 0.0 : settings.FrameRateMode == FrameRateMode.Original ? 60.0 : settings.TargetFps <= 0 ? 0.0 : settings.TargetFps;
}
