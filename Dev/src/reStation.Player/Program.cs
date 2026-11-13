// @module: reStation.Player.Program
// @purpose: reStation.Player entry: AttachConsole, CLI parse, fail-closed SandboxEnvironment, crash bundles, settings, logger, module/fixture dispatch.
// @depends-on: System.Runtime.InteropServices, Foundation.Core, reStation.Contracts, reStation.Core
// @exports: Program
// @owner: devv-player-ui (T6) — T1 skeleton; SDL3/GL window, frame loop, drawer/HUD, and --visible-test land in T6.

using System.Runtime.InteropServices;
using Foundation.Core;
using reStation.Contracts;
using reStation.Core;

namespace reStation.Player;

internal static class Program
{
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0) AttachConsole(AttachParentProcess);

        CliOptions options;
        try
        {
            options = CliOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        if (options.ShowHelp)
        {
            Console.WriteLine(CliOptions.HelpText);
            return 0;
        }

        SandboxEnvironment sandbox;
        try
        {
            sandbox = options.CreateSandbox();
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 3;
        }

        RecompSettings settings = RecompSettings.Load(sandbox.ConfigFilePath).Value;
        options.ApplyOverrides(settings);
        using var logger = new ReStationLogger(sandbox.LogDirectory, settings.ShouldEnableRuntimeLogs);
        CrashBundleService.RegisterGlobalHandlers(sandbox.LogDirectory, ReStationEngineInfo.AppId, ReStationEngineInfo.Version, logger.GetRecentLines);
        logger.Info("BOOT", $"reStation v{ReStationEngineInfo.Version} testMode={sandbox.TestMode} headless={sandbox.Headless} backend={settings.BackendId} scale={settings.ResolutionScale}x");

        if (options.ModulePath is not null)
        {
            ReStationModuleManifest manifest = ReStationModuleManifest.LoadFromFile(options.ModulePath);
            string? preload = PreloadRomDetector.TryGetPrimaryMedia(options.ModulePath);
            logger.Info("MODULE", $"Loaded manifest '{manifest.ModuleId}' ({manifest.DisplayName}); preload={(preload is null ? "none" : Path.GetFileName(preload))}.");
        }

        logger.TryHeartbeat(PsxTelemetrySnapshot.Empty with { ResolutionScale = settings.ResolutionScale, BackendId = settings.BackendId }, force: true);
        return 0;
    }
}
