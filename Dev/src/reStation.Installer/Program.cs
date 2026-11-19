// @module: reStation.Installer.Program
// @purpose: Zero-daemon 4-pillar single-EXE setup host wiring interactive InstallerWizardForm, InstallerManifestStore, .ready marker emission, and 3-mode sandbox containment.
// @depends-on: System, System.Collections.Concurrent, System.Collections.Generic, System.IO, System.IO.Compression, System.Linq, System.Reflection, System.Runtime.CompilerServices, System.Runtime.Loader, System.Text, System.Text.Json, System.Windows.Forms, Foundation.Core, Foundation.Installer, reEngine.Core.Bootstrap
// @exports: Program

using System.Collections.Concurrent;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using Foundation.Core;
using Foundation.Installer;
using reEngine.Core.Bootstrap;

namespace reStation.Installer;

internal static class Program
{
    public const string ProductName = ReStationInstallerTheme.ProductName;
    public const string Version = ReStationInstallerTheme.Version;
    public const string BrandAccentHex = ReStationInstallerTheme.BrandAccentHex;
    public const string PayloadResourceName = ReStationInstallerTheme.PayloadResourceName;
    public const bool AllowAutoStart = ReStationInstallerTheme.AllowAutoStart;

    private static readonly string[] EmbeddedAssemblies =
        { "Foundation.Core", "Foundation.Installer", "Foundation.Retro", "reEngine.Core" };
    private static readonly ConcurrentDictionary<string, Assembly> ResolvedAssemblies = new(StringComparer.OrdinalIgnoreCase);

    static Program()
    {
        AssemblyLoadContext.Default.Resolving += static (_, assemblyName) =>
        {
            string? shortName = assemblyName.Name;
            if (string.IsNullOrWhiteSpace(shortName) ||
                !EmbeddedAssemblies.Contains(shortName, StringComparer.OrdinalIgnoreCase))
            {
                return null;
            }

            return ResolvedAssemblies.GetOrAdd(shortName, static name =>
            {
                using Stream? gzStream = typeof(Program).Assembly.GetManifestResourceStream($"{name}.dll.gz")
                    ?? throw new FileNotFoundException($"Missing embedded installer dependency '{name}.dll.gz'.");
                using var gzip = new GZipStream(gzStream, CompressionMode.Decompress);
                using var ms = new MemoryStream();
                gzip.CopyTo(ms);
                ms.Position = 0;
                return AssemblyLoadContext.Default.LoadFromStream(ms);
            });
        };
    }

    [STAThread]
    private static int Main(string[] args) => RunInstallerCore(args ?? Array.Empty<string>());

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunInstallerCore(string[] args)
    {
        InstallerThemeOptions theme = ReStationInstallerTheme.CreateThemeOptions();
        InstallerAppDescriptor descriptor = ReStationInstallerTheme.CreateDescriptor(theme);
        string[] cleanArgs = SanitizeArgs(args);

        string profileId;
        string[] hostArgs;
        string? suiteDir, reEngineDir, dataDir;
        bool migrateLegacy, confirmLegacyCleanup, silent, dryRun, verifyOnly, uninstall;
        string? installDir;
        try
        {
            hostArgs = ExtractHostArgs(cleanArgs, out profileId, out suiteDir, out reEngineDir,
                out migrateLegacy, out confirmLegacyCleanup, out silent,
                out dryRun, out verifyOnly, out uninstall, out installDir, out dataDir);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"Setup argument error: {ex.Message}");
            return 2;
        }

        InstallerCliOptions cli;
        try
        {
            cli = InstallerEngine.ParseCliArgs(hostArgs, descriptor);
        }
        catch (ArgumentException)
        {
            return StandardInstallerHost.Run(descriptor, hostArgs, typeof(Program).Assembly);
        }

        bool isolatedTest = ReStationInstallerBootstrap.IsIsolatedTestEnabled();
        InstallerRuntimeContext? runtimeCtx = null;
        try
        {
            runtimeCtx = new InstallerRuntimeContext(
                cliDataDir: cli.DataDir, cliConfigPath: cli.ConfigPath, cliLogDir: cli.LogDir,
                dryRun: cli.DryRun, envPrefix: descriptor.EnvPrefix);
        }
        catch (Exception ex) when (SandboxEnvironmentSeam.IsContainmentViolation(ex))
        {
            Console.Error.WriteLine(ex.Message);
            return SandboxEnvironmentSeam.ContainmentExitCode;
        }

        bool isInteractiveGui = !cli.ShowVersion && !cli.ShowHelp && !cli.Silent &&
                                !isolatedTest && !cli.DryRun && !cli.VerifyOnly &&
                                string.IsNullOrWhiteSpace(cli.InstallDir) && OperatingSystem.IsWindows();

        if (isInteractiveGui)
        {
            return RunInteractiveWizard(descriptor, theme, cli, suiteDir, reEngineDir,
                migrateLegacy, confirmLegacyCleanup);
        }

        int prepared = ReStationInstallerBootstrap.Prepare(hostArgs, typeof(Program).Assembly, descriptor.UninstallerExeName, out string[] effectiveArgs);
        if (prepared != 0) return prepared;

        int exitCode = StandardInstallerHost.Run(descriptor, effectiveArgs, typeof(Program).Assembly);
        string? resolvedInstall = ReStationInstallerBootstrap.ReadInstallDir(effectiveArgs) ?? installDir;
        string effectiveInstall = !string.IsNullOrWhiteSpace(resolvedInstall)
            ? Path.GetFullPath(resolvedInstall)
            : theme.DefaultInstallDir;

        if (exitCode == 0 && uninstall && !cli.ShowVersion && !cli.ShowHelp && !verifyOnly && !dryRun)
        {
            RemoveReadyMarkerIfPresent(effectiveInstall);
        }
        else if (exitCode == 0 && !cli.ShowVersion && !cli.ShowHelp && !verifyOnly && !uninstall && !dryRun)
        {
            string? isolatedRoot = isolatedTest ? ReStationInstallerBootstrap.TryResolveIsolatedDataRoot(effectiveArgs) : null;
            string effectiveReEngineDir = ResolveEffectiveReEngineDir(reEngineDir, suiteDir, isolatedRoot, isolatedTest);
            string? effectiveSuiteDir = ResolveEffectiveSuiteDir(suiteDir, effectiveReEngineDir);
            ApplyPostInstallActions(effectiveInstall, profileId, effectiveSuiteDir, effectiveReEngineDir,
                migrateLegacy, confirmLegacyCleanup, isolatedTest, isolatedRoot ?? dataDir, silent,
                runtimeCtx.ShouldSuppressLiveHostMutations, runtimeCtx.Sandbox);
        }

        return exitCode;
    }

    private static int RunInteractiveWizard(
        InstallerAppDescriptor descriptor,
        InstallerThemeOptions theme,
        InstallerCliOptions cli,
        string? suiteDir,
        string? reEngineDir,
        bool migrateLegacy,
        bool confirmLegacyCleanup)
    {
        var ctx = new InstallerRuntimeContext(
            cliDataDir: cli.DataDir, cliConfigPath: cli.ConfigPath, cliLogDir: cli.LogDir,
            dryRun: cli.DryRun, envPrefix: descriptor.EnvPrefix);

        var guiEngine = new InstallerEngine(descriptor, ctx, typeof(Program).Assembly);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        string initialProfile = !string.IsNullOrWhiteSpace(cli.ProfileId)
            ? ReStationLegacyMigrationHelper.NormalizeProfileId(cli.ProfileId)
            : theme.DefaultProfileId;
        string initialReEngineDir = ResolveEffectiveReEngineDir(reEngineDir, suiteDir, ctx.DataDirectory, ctx.IsIsolatedTestProcess);
        string initialInstallDir = !string.IsNullOrWhiteSpace(cli.InstallDir)
            ? Path.GetFullPath(cli.InstallDir)
            : (ctx.IsIsolatedTestProcess
                ? ReStationInstallerBootstrap.ComputeSideBySideDir(initialReEngineDir)
                : theme.DefaultInstallDir);

        using var wizard = new InstallerWizardForm(
            engine: guiEngine,
            ctx: ctx,
            initialInstallDir: initialInstallDir,
            externalPayloadPath: cli.PayloadPath,
            isUninstallMode: cli.Uninstall,
            initialCreateShortcuts: !cli.NoShortcuts,
            initialAutoStart: false,
            initialLaunchAfter: !cli.NoLaunch,
            initialPurgeData: cli.PurgeData,
            initialProfileId: initialProfile,
            initialSecondaryDir: initialReEngineDir,
            secondaryFlagName: "--reengine-dir",
            initialMigrateLegacy: !cli.Uninstall);

        ReStationWizardPathNormalizer.WireWizardAutoSortHandlers(wizard);
        if (!cli.Uninstall)
        {
            wizard.MigrateLegacyCheckBox.Checked = migrateLegacy || (ctx.ConfigOverride?.MigrateLegacy ?? false);
        }

        Application.Run(wizard);
        if (wizard.ExitCode == 0 && !cli.Uninstall)
        {
            string rawInstallDir = string.IsNullOrWhiteSpace(wizard.InstallDirTextBox.Text)
                ? initialInstallDir
                : wizard.InstallDirTextBox.Text;
            string chosenInstallDir = ReStationWizardPathNormalizer.NormalizeWizardStationInstallDir(rawInstallDir);
            string chosenProfile = ReStationLegacyMigrationHelper.NormalizeProfileId(wizard.SelectedProfileId);
            string chosenReEngineDir = !string.IsNullOrWhiteSpace(wizard.SecondaryInstallDir)
                ? ReStationWizardPathNormalizer.NormalizeWizardReEngineDir(wizard.SecondaryInstallDir)
                : initialReEngineDir;
            string? chosenSuiteDir = ResolveEffectiveSuiteDir(suiteDir, chosenReEngineDir);

            ApplyPostInstallActions(chosenInstallDir, chosenProfile, chosenSuiteDir, chosenReEngineDir,
                wizard.MigrateLegacyChecked, confirmLegacyCleanup, ctx.IsIsolatedTestProcess, cli.DataDir,
                silent: false, ctx.ShouldSuppressLiveHostMutations, ctx.Sandbox);
        }

        return wizard.ExitCode;
    }

    internal static void ApplyPostInstallActions(
        string installDir, string profileId, string? suiteDir, string? reEngineDir,
        bool migrateLegacy, bool confirmLegacyCleanup, bool isolatedTest, string? dataDir, bool silent,
        bool suppressLiveHostMutations = false, SandboxResolution? sandbox = null)
    {
        if (!Directory.Exists(installDir)) return;

        if (migrateLegacy)
        {
            LegacyMigrationResult migration = ReStationLegacyMigrationHelper.TryMigrateLegacyHostArtifacts(
                installDir, isolatedTest, dataDir, silent, dryRun: false, confirmLegacyCleanup,
                sandbox, suppressLiveHostMutations);
            Console.WriteLine(migration.Summary);
        }

        ReStationLegacyMigrationHelper.PersistInstallProfileSettings(installDir, profileId, suiteDir, reEngineDir);
        WriteReadyMarker(installDir);
    }

    internal static void WriteReadyMarker(string installDir)
    {
        string fullInstall = Path.GetFullPath(installDir);
        Directory.CreateDirectory(fullInstall);
        string readyPath = Path.Combine(fullInstall, ".ready");
        string tmpPath = readyPath + ".tmp";
        string payload = JsonSerializer.Serialize(new
        {
            engineId = ReStationInstallerTheme.EngineId,
            appId = ReStationInstallerTheme.AppId,
            version = ReStationInstallerTheme.Version,
            completedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
        }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(tmpPath, payload + Environment.NewLine, new UTF8Encoding(false));
        File.Move(tmpPath, readyPath, overwrite: true);
    }

    internal static void RemoveReadyMarkerIfPresent(string installDir)
    {
        if (string.IsNullOrWhiteSpace(installDir)) return;
        string readyPath = Path.Combine(Path.GetFullPath(installDir), ".ready");
        if (File.Exists(readyPath)) File.Delete(readyPath);
        if (Directory.Exists(installDir) && !Directory.EnumerateFileSystemEntries(installDir).Any())
        {
            Directory.Delete(installDir, recursive: false);
        }
    }

    internal static InstallerAppDescriptor CreateDescriptor() => ReStationInstallerTheme.CreateDescriptor();

    /// <summary>Strips every <c>--autostart*</c> / <c>--auto-start*</c> argument (zero Startup registration invariant).</summary>
    internal static string[] SanitizeArgs(string[]? args) =>
        args is null
            ? Array.Empty<string>()
            : args.Where(a => !string.IsNullOrWhiteSpace(a) &&
                              !a.Trim().StartsWith("--autostart", StringComparison.OrdinalIgnoreCase) &&
                              !a.Trim().StartsWith("--auto-start", StringComparison.OrdinalIgnoreCase))
                  .ToArray();

    internal static string[] ExtractHostArgs(
        string[] clean, out string profileId, out string? suiteDir, out string? reEngineDir,
        out bool migrateLegacy, out bool confirmLegacyCleanup, out bool silent,
        out bool dryRun, out bool verifyOnly, out bool uninstall,
        out string? installDir, out string? dataDir)
    {
        profileId = ReStationInstallerTheme.DefaultInstallProfile;
        suiteDir = null; reEngineDir = null; installDir = null; dataDir = null;
        migrateLegacy = false; confirmLegacyCleanup = false;
        silent = false; dryRun = false; verifyOnly = false; uninstall = false;

        var forwarded = new List<string>(clean.Length);
        for (int i = 0; i < clean.Length; i++)
        {
            string arg = clean[i].Trim();
            if (string.Equals(arg, "--confirm-legacy-cleanup", StringComparison.OrdinalIgnoreCase))
            {
                confirmLegacyCleanup = true;
                continue;
            }

            forwarded.Add(clean[i]);
            switch (arg.ToLowerInvariant())
            {
                case "--profile" when i + 1 < clean.Length:
                    profileId = ReStationLegacyMigrationHelper.NormalizeProfileId(clean[i + 1]);
                    break;
                case "--suite-dir" when i + 1 < clean.Length: suiteDir = clean[i + 1]; break;
                case "--reengine-dir" when i + 1 < clean.Length: reEngineDir = clean[i + 1]; break;
                case "--install-dir" or "--target-dir" when i + 1 < clean.Length: installDir = clean[i + 1]; break;
                case "--data-dir" when i + 1 < clean.Length: dataDir = clean[i + 1]; break;
                case "--migrate-legacy": migrateLegacy = true; break;
                case "--silent" or "--quiet": silent = true; break;
                case "--dry-run": dryRun = true; break;
                case "--verify": verifyOnly = true; break;
                case "--uninstall": uninstall = true; break;
            }
        }
        return forwarded.ToArray();
    }

    private static string ResolveEffectiveReEngineDir(
        string? reEngineDir, string? suiteDir, string? isolatedRoot, bool isolatedTest)
    {
        string? explicitDir = !string.IsNullOrWhiteSpace(reEngineDir)
            ? Path.GetFullPath(reEngineDir.Trim())
            : (!string.IsNullOrWhiteSpace(suiteDir)
                ? Path.Combine(Path.GetFullPath(suiteDir.Trim()), ReStationInstallerTheme.SuiteName)
                : null);
        return ReEngineBootstrapHelper.ResolveDefaultReEngineInstallDir(explicitDir, isolatedRoot, isolatedTest);
    }

    private static string? ResolveEffectiveSuiteDir(string? explicitSuiteDir, string reEngineInstallDir) =>
        ReStationWizardPathNormalizer.ResolvePreferredSuiteInstallDir(reEngineInstallDir, explicitSuiteDir);
}
