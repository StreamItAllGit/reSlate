// @module: reStation.Player.Program
// @purpose: reStation.Player entry point: AttachConsole, CLI parse, fail-closed SandboxEnvironment, crash bundles, preload/disc verification, and host launch.
// @depends-on: System.Runtime.InteropServices, Foundation.Core, Foundation.Retro, reStation.Contracts, reStation.Core, reStation.Core.Cpu, reStation.Core.DiscIngestion
// @exports: Program
// @owner: devv-player-ui (T6)

using System.Runtime.InteropServices;
using Foundation.Core;
using Foundation.Retro;
using reEngine.Core.Manifests;
using reStation.Contracts;
using reStation.Core;
using reStation.Core.Aot;
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

    internal static int RunMain(string[] args)
    {
        CliOptions options;
        try { options = CliOptions.Parse(args); }
        catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 2; }

        if (options.ShowHelp) { Console.WriteLine(CliOptions.HelpText); return 0; }
        if (options.ShowVersion) { Console.WriteLine($"reStation v{ReStationEngineInfo.Version}"); return 0; }

        SandboxEnvironment sandbox;
        try { sandbox = options.CreateSandbox(); }
        catch (InvalidOperationException ex) { Console.Error.WriteLine(ex.Message); return 3; }

        RecompSettings settings = RecompSettings.Load(sandbox.ConfigFilePath).Value;
        options.ApplyOverrides(settings);
        using var logger = new ReStationLogger(sandbox.LogDirectory, settings.ShouldEnableRuntimeLogs);
        CrashBundleService.RegisterGlobalHandlers(sandbox.LogDirectory, ReStationEngineInfo.AppId, ReStationEngineInfo.Version, logger.GetRecentLines);
        logger.Info("BOOT", $"reStation v{ReStationEngineInfo.Version} testMode={sandbox.TestMode} headless={options.Headless} visibleTest={options.VisibleTest} wait={options.ShouldWait} backend={settings.BackendId} scale={settings.ResolutionScale}x");

        try
        {
            ValidateSandboxContainment(options, sandbox);
            return RunApplication(options, sandbox, settings, logger);
        }
        catch (InvalidOperationException ex) when (IsSandboxViolation(ex.Message))
        {
            logger.Error("SANDBOX", ex.Message);
            Console.Error.WriteLine(ex.Message);
            return 3;
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
            moduleDir = RecompPackageReader.ResolveModuleRootDirectory(fullModule);
            logger.Info("MODULE", $"Loaded manifest '{manifest.ModuleId}' ({manifest.DisplayName}) serial={manifest.SerialNumber}.");
        }

        if (options.VerifyOnly)
            return RunVerify(options, sandbox, settings, manifest, moduleDir, logger);

        if (!string.IsNullOrWhiteSpace(options.FixturePath))
        {
            string fixtureOut = Path.Combine(sandbox.LogDirectory, "fixtures");
            bool fixtureHeadless = options.Headless && !options.VisibleTest;
            return FixtureRunnerHost.RunWithEngineAsync(options.FixturePath, fixtureOut, manifest, moduleDir, sandbox.DataDirectory, settings, logger, fixtureHeadless)
                .GetAwaiter().GetResult();
        }

        DiscManifest? discManifest = DiscManifest.TryLoad(sandbox.DataDirectory);
        int mediaExit = TryResolveAndIngestMedia(options, sandbox, settings, manifest, moduleDir, logger, ref discManifest);
        if (mediaExit != 0) return mediaExit;

        bool hasUiScriptOrSettings = options.OpenSettings || !string.IsNullOrWhiteSpace(options.InputScriptPath);
        if (options.VisibleTest && manifest is null && discManifest is null && !hasUiScriptOrSettings)
        {
            string shotPath = string.IsNullOrWhiteSpace(options.DumpScreenshotPath)
                ? Path.Combine(sandbox.LogDirectory, "screenshots", "visible-test.png")
                : Path.GetFullPath(options.DumpScreenshotPath);
            var harness = new VisibleTestHarness(settings, logger);
            bool ok = harness.RunVisibleTestAsync(new VisibleTestShowcaseOptions(3000, true, shotPath)).GetAwaiter().GetResult();
            return ok ? 0 : 1;
        }

        bool runHeadless = options.Headless && !options.VisibleTest;
        bool needsGate = !runHeadless && !(options.VisibleTest && hasUiScriptOrSettings) && (manifest is null || discManifest is null);
        string winTitle = options.VisibleTest
            ? $"reStation v{ReStationEngineInfo.Version} \u2014 Visible Live Test | F1 Settings | F3 HUD"
            : $"reStation v{ReStationEngineInfo.Version} \u2014 F1 Settings | F3 HUD";
        using HostVideoSurface video = runHeadless
            ? HostVideoSurface.Create($"reStation v{ReStationEngineInfo.Version}", settings, headless: true, foreground: false, logger)
            : HostVideoSurface.Create(winTitle, settings, headless: false, foreground: true, logger, alwaysOnTop: options.VisibleTest);

        if (needsGate && !RunFirstRunMediaGate(video, sandbox, settings, logger, ref manifest, ref moduleDir, ref discManifest, options))
            return 0;

        if (runHeadless && manifest is not null && discManifest is null)
        {
            logger.Warn("BOOT", "Headless session started without a verified disc image or data\\rom preload; exiting cleanly.");
            PsxTelemetrySnapshot idleSnap = PsxTelemetrySnapshot.Empty with { FrameNumber = 1, ResolutionScale = settings.ResolutionScale, BackendId = video.Backend.BackendId };
            logger.TryHeartbeat(1, 1, idleSnap.ToHeartbeatMetrics(), force: true);
            return 0;
        }

        ReStationModuleManifest effectiveManifest = manifest ?? new ReStationModuleManifest
        {
            ModuleId = "restation-idle", Title = "reStation", DisplayName = "reStation"
        };
        var cpu = new PsxCpu(settings, sandbox.DataDirectory, video.Backend, logger);
        if (discManifest is not null) cpu.AttachDisc(discManifest);

        double initialFps = ComputeTargetFps(settings, runHeadless);
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
            LocalAotLoadOptions aotOptions = BuildLocalAotLoadOptions(options, sandbox, settings);
            gameModule = ModuleAssemblyLoader.LoadGameModule(manifest, moduleDir, aotOptions, out moduleAlc);
            cpu.WidescreenProvider = gameModule as IRecompWidescreenProvider;
            logger.Info("MODULE", $"Loaded '{manifest.ModuleId}' tier={moduleAlc.ResolvedExecutionTierMode} localAot={moduleAlc.LoadedFromLocalAotCache} cacheHit={moduleAlc.AotCacheHit} zeroRwx={moduleAlc.ForceZeroRwx} bringup={moduleAlc.JitBringupEnabled}.");
            gameModule.InitializeAsync(host, CancellationToken.None).GetAwaiter().GetResult();
        }

        int loopExit = RunHostLoop(video, host, cpu, gameModule, manifest, discManifest, options, sandbox, settings, pacer, logger);
        GC.KeepAlive(moduleAlc);
        return loopExit;
    }

    private static LocalAotLoadOptions BuildLocalAotLoadOptions(
        CliOptions options, SandboxEnvironment sandbox, RecompSettings settings)
    {
        string? preloadDir = null;
        if (!string.IsNullOrWhiteSpace(options.PreloadMediaPath))
        {
            string fullPreload = Path.GetFullPath(options.PreloadMediaPath);
            preloadDir = Directory.Exists(fullPreload) ? fullPreload : Path.GetDirectoryName(fullPreload);
        }
        string? tierOverride = options.JitBringup
            ? ReStationExecutionTierModes.JitBringup
            : !string.Equals(settings.ExecutionTierMode, ReStationExecutionTierModes.Default, StringComparison.OrdinalIgnoreCase)
                ? settings.ExecutionTierMode
                : null;
        string? profilePath = string.IsNullOrWhiteSpace(options.ProfilePath)
            ? null
            : Path.GetFullPath(options.ProfilePath);
        return new LocalAotLoadOptions(
            DataDir: sandbox.DataDirectory,
            PreloadRomDir: preloadDir,
            JitBringup: options.JitBringup,
            ForceZeroRwx: options.ForceZeroRwx,
            ExecutionTierModeOverride: tierOverride,
            BringupProfilePath: profilePath);
    }

    internal static string? ResolveMediaCandidate(CliOptions options, ReStationModuleManifest? manifest, string? moduleDir, DiscManifest? existingDisc)
    {
        if (!string.IsNullOrWhiteSpace(options.DiscPath)) return options.DiscPath;
        if (!string.IsNullOrWhiteSpace(options.PreloadMediaPath)) return options.PreloadMediaPath;
        if (existingDisc is null && manifest is not null && !string.IsNullOrWhiteSpace(moduleDir))
            return PreloadRomDetector.Detect(moduleDir, manifest).PrimaryMediaPath;
        return null;
    }

    internal static void ValidateSandboxContainment(CliOptions options, SandboxEnvironment sandbox)
    {
        if (!sandbox.TestMode) return;
        string boundary = CliOptions.ResolveSandboxBoundary(sandbox);
        foreach (string? raw in new[] { options.DiscPath, options.PreloadMediaPath, options.FixturePath, options.DumpScreenshotPath, options.InputScriptPath, options.ProfilePath })
        {
            if (!string.IsNullOrWhiteSpace(raw))
                _ = SandboxEnvironment.AssertPathContained(Path.GetFullPath(raw), boundary);
        }
    }

    private static bool IsSandboxViolation(string? message) =>
        !string.IsNullOrWhiteSpace(message) &&
        (message.Contains("Sandbox Violation", StringComparison.OrdinalIgnoreCase) ||
         message.Contains("Containment Violation", StringComparison.OrdinalIgnoreCase));

    private static int TryResolveAndIngestMedia(CliOptions options, SandboxEnvironment sandbox, RecompSettings settings,
        ReStationModuleManifest? manifest, string? moduleDir, ReStationLogger logger, ref DiscManifest? discManifest)
    {
        string? candidateMedia = ResolveMediaCandidate(options, manifest, moduleDir, discManifest);
        if (string.IsNullOrWhiteSpace(candidateMedia)) return 0;

        string fullMedia = Path.GetFullPath(candidateMedia);
        if (!File.Exists(fullMedia))
        {
            string missingMsg = $"Media file not found: '{candidateMedia}'.";
            logger.Error("DISC", missingMsg);
            Console.Error.WriteLine(missingMsg);
            return 4;
        }

        if (manifest is null)
        {
            var opts = new MediaExtractionOptions(AppContext.BaseDirectory, CopyDirectMediaToDestination: false, ComputeSha256: false);
            MediaExtractionResult verify = MediaContainerExtractor.ExtractOrVerifyAsync(fullMedia, sandbox.DataDirectory, opts).GetAwaiter().GetResult();
            if (!verify.Success)
            {
                bool containment = sandbox.TestMode && IsSandboxViolation(verify.DiagnosticMessage);
                logger.Error(containment ? "SANDBOX" : "DISC", verify.DiagnosticMessage);
                Console.Error.WriteLine(verify.DiagnosticMessage);
                return containment ? 3 : 4;
            }
            logger.Info("DISC", $"Validated CLI media '{Path.GetFileName(fullMedia)}' (module deferred).");
            return 0;
        }

        DiscIngestResult ingest = DiscVerifierAndExtractor
            .IngestAsync(fullMedia, sandbox.DataDirectory, manifest, logger, AppContext.BaseDirectory)
            .GetAwaiter().GetResult();
        if (!ingest.Success)
        {
            bool containment = sandbox.TestMode && IsSandboxViolation(ingest.Message);
            logger.Error(containment ? "SANDBOX" : "DISC", ingest.Message);
            Console.Error.WriteLine(ingest.Message);
            return containment ? 3 : 4;
        }

        discManifest = ingest.Manifest;
        settings.LastDiscPath = Path.GetFileName(fullMedia);
        settings.Save(sandbox.ConfigFilePath);
        return 0;
    }

    private static int RunVerify(CliOptions options, SandboxEnvironment sandbox, RecompSettings settings,
        ReStationModuleManifest? manifest, string? moduleDir, ReStationLogger logger)
    {
        DiscManifest? disc = DiscManifest.TryLoad(sandbox.DataDirectory);
        string? candidate = ResolveMediaCandidate(options, manifest, moduleDir, disc);
        bool cliMediaExists = !string.IsNullOrWhiteSpace(candidate) && File.Exists(Path.GetFullPath(candidate));
        bool ok = disc is not null || cliMediaExists;
        string msg = disc is not null
            ? $"Verified: {disc.SerialNumber} ({disc.Files.Count} files)"
            : cliMediaExists
                ? $"Preload ready: {Path.GetFileName(candidate!)}"
                : "Not verified: missing verified disc gamedata, --preload-media, and data\\rom preload.";
        Console.WriteLine(msg);
        logger.Info("VERIFY", msg);
        PsxTelemetrySnapshot verifySnap = PsxTelemetrySnapshot.Empty with
        {
            FrameNumber = 1, ResolutionScale = settings.ResolutionScale, BackendId = settings.BackendId
        };
        logger.TryHeartbeat(1, 1, verifySnap.ToHeartbeatMetrics(), force: true);
        return ok ? 0 : 1;
    }

    private static double ComputeTargetFps(RecompSettings settings, bool headless) =>
        headless ? 0.0 : settings.FrameRateMode == FrameRateMode.Original ? 60.0 : settings.TargetFps <= 0 ? 0.0 : settings.TargetFps;
}
