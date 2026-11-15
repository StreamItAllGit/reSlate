// @module: reStation.Installer.Program
// @purpose: Zero-daemon 4-pillar single-EXE setup host wiring interactive InstallerWizardForm (--profile, --reengine-dir, opt-in --migrate-legacy) and CLI containment.
// @depends-on: System, System.Collections.Generic, System.IO, System.IO.Compression, System.Linq, System.Reflection, System.Runtime.CompilerServices, System.Windows.Forms, Foundation.Core, Foundation.Installer, reEngine.Core.Bootstrap
// @exports: Program

using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
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

    static Program()
    {
        AppDomain.CurrentDomain.AssemblyResolve += static (_, e) =>
        {
            string? shortName = new AssemblyName(e.Name).Name;
            if (!EmbeddedAssemblies.Contains(shortName, StringComparer.OrdinalIgnoreCase)) return null;
            using Stream? gzStream = typeof(Program).Assembly.GetManifestResourceStream($"{shortName}.dll.gz");
            if (gzStream is null) return null;
            using var gzip = new GZipStream(gzStream, CompressionMode.Decompress);
            using var ms = new MemoryStream();
            gzip.CopyTo(ms);
            return Assembly.Load(ms.ToArray());
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
        bool migrateLegacy, confirmLegacyCleanup, testMode, silent, headless, dryRun, verifyOnly, uninstall;
        string? installDir;
        try
        {
            hostArgs = ExtractHostArgs(cleanArgs, out profileId, out suiteDir, out reEngineDir,
                out migrateLegacy, out confirmLegacyCleanup, out testMode, out silent,
                out headless, out dryRun, out verifyOnly, out uninstall, out installDir, out dataDir);
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

        bool showShowcase = cli.VisibleTest && !cli.Silent && !cli.Headless && OperatingSystem.IsWindows();
        bool isInteractiveGui = !cli.ShowVersion && !cli.ShowHelp && !cli.Silent && !cli.Headless &&
                                !cli.TestMode && !cli.DryRun && !cli.VerifyOnly &&
                                string.IsNullOrWhiteSpace(cli.InstallDir) && OperatingSystem.IsWindows();

        if (showShowcase || isInteractiveGui)
        {
            return RunInteractiveWizard(descriptor, theme, cli, suiteDir, reEngineDir,
                migrateLegacy, confirmLegacyCleanup, showShowcase ? 3000 : 0);
        }

        int prepared = ReStationInstallerBootstrap.Prepare(hostArgs, typeof(Program).Assembly, descriptor.UninstallerExeName, out string[] effectiveArgs);
        if (prepared != 0) return prepared;

        int exitCode = StandardInstallerHost.Run(descriptor, effectiveArgs, typeof(Program).Assembly);
        if (exitCode == 0 && !verifyOnly && !uninstall && !dryRun && !cli.VisibleTest)
        {
            string? resolvedInstall = ReStationInstallerBootstrap.ReadInstallDir(effectiveArgs) ?? installDir;
            string effectiveInstall = !string.IsNullOrWhiteSpace(resolvedInstall)
                ? Path.GetFullPath(resolvedInstall)
                : theme.DefaultInstallDir;
            string? sandboxRoot = testMode ? ReStationInstallerBootstrap.TryResolveSandboxRoot(effectiveArgs) : null;
            string effectiveReEngineDir = ResolveEffectiveReEngineDir(reEngineDir, suiteDir, sandboxRoot, testMode);
            string? effectiveSuiteDir = ResolveEffectiveSuiteDir(suiteDir, effectiveReEngineDir);
            ApplyPostInstallActions(effectiveInstall, profileId, effectiveSuiteDir, effectiveReEngineDir,
                migrateLegacy, confirmLegacyCleanup, testMode, sandboxRoot ?? dataDir, silent, headless);
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
        bool confirmLegacyCleanup,
        int showcaseAutoCloseMs)
    {
        var ctx = new InstallerSandboxContext(
            cliDataDir: cli.DataDir, cliConfigPath: cli.ConfigPath, cliLogDir: cli.LogDir,
            testMode: cli.TestMode, dryRun: cli.DryRun, headless: cli.Headless,
            envPrefix: descriptor.EnvPrefix, fallbackTestInstallDir: cli.InstallDir,
            visibleTest: cli.VisibleTest, cliSandboxRoot: cli.SandboxRoot);

        var guiEngine = new InstallerEngine(descriptor, ctx, typeof(Program).Assembly);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        string initialProfile = !string.IsNullOrWhiteSpace(cli.ProfileId)
            ? ReStationLegacyMigrationHelper.NormalizeProfileId(cli.ProfileId)
            : theme.DefaultProfileId;
        string initialReEngineDir = ResolveEffectiveReEngineDir(reEngineDir, suiteDir, ctx.DataDirectory, cli.TestMode);
        string initialInstallDir = !string.IsNullOrWhiteSpace(cli.InstallDir)
            ? Path.GetFullPath(cli.InstallDir)
            : (cli.TestMode
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
            showShowcaseAutoCloseMs: showcaseAutoCloseMs,
            initialProfileId: initialProfile,
            initialSecondaryDir: initialReEngineDir,
            secondaryFlagName: "--reengine-dir",
            initialMigrateLegacy: !cli.Uninstall);

        if (!cli.Uninstall)
        {
            wizard.MigrateLegacyCheckBox.Checked = migrateLegacy || (ctx.ConfigOverride?.MigrateLegacy ?? false);
        }

        Application.Run(wizard);
        if (wizard.ExitCode == 0 && !cli.Uninstall && showcaseAutoCloseMs == 0)
        {
            string chosenInstallDir = string.IsNullOrWhiteSpace(wizard.InstallDirTextBox.Text)
                ? initialInstallDir
                : Path.GetFullPath(wizard.InstallDirTextBox.Text.Trim());
            string chosenProfile = ReStationLegacyMigrationHelper.NormalizeProfileId(wizard.SelectedProfileId);
            string chosenReEngineDir = !string.IsNullOrWhiteSpace(wizard.SecondaryInstallDir)
                ? Path.GetFullPath(wizard.SecondaryInstallDir.Trim())
                : initialReEngineDir;
            string? chosenSuiteDir = ResolveEffectiveSuiteDir(suiteDir, chosenReEngineDir);

            ApplyPostInstallActions(chosenInstallDir, chosenProfile, chosenSuiteDir, chosenReEngineDir,
                wizard.MigrateLegacyChecked, confirmLegacyCleanup, testMode: false, dataDir: cli.DataDir,
                silent: false, headless: false);
        }

        return wizard.ExitCode;
    }

    private static void ApplyPostInstallActions(
        string installDir, string profileId, string? suiteDir, string? reEngineDir,
        bool migrateLegacy, bool confirmLegacyCleanup, bool testMode, string? dataDir, bool silent, bool headless)
    {
        if (!Directory.Exists(installDir)) return;

        if (migrateLegacy)
        {
            LegacyMigrationResult migration = ReStationLegacyMigrationHelper.TryMigrateLegacyHostArtifacts(
                installDir, testMode, dataDir, silent, headless, dryRun: false, confirmLegacyCleanup);
            Console.WriteLine(migration.Summary);
        }

        ReStationLegacyMigrationHelper.PersistInstallProfileSettings(installDir, profileId, suiteDir, reEngineDir);
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
        out bool migrateLegacy, out bool confirmLegacyCleanup, out bool testMode, out bool silent,
        out bool headless, out bool dryRun, out bool verifyOnly, out bool uninstall,
        out string? installDir, out string? dataDir)
    {
        profileId = ReStationInstallerTheme.DefaultInstallProfile;
        suiteDir = null; reEngineDir = null; installDir = null; dataDir = null;
        migrateLegacy = false; confirmLegacyCleanup = false; testMode = false;
        silent = false; headless = false; dryRun = false; verifyOnly = false; uninstall = false;

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
                case "--test-mode": testMode = true; break;
                case "--silent" or "--quiet": silent = true; break;
                case "--headless": headless = true; break;
                case "--dry-run": dryRun = true; break;
                case "--verify": verifyOnly = true; break;
                case "--uninstall": uninstall = true; break;
            }
        }
        return forwarded.ToArray();
    }

    private static string ResolveEffectiveReEngineDir(
        string? reEngineDir, string? suiteDir, string? sandboxRoot, bool testMode)
    {
        string? explicitDir = !string.IsNullOrWhiteSpace(reEngineDir)
            ? Path.GetFullPath(reEngineDir.Trim())
            : (!string.IsNullOrWhiteSpace(suiteDir)
                ? Path.Combine(Path.GetFullPath(suiteDir.Trim()), ReStationInstallerTheme.SuiteName)
                : null);
        return ReEngineBootstrapHelper.ResolveDefaultReEngineInstallDir(explicitDir, sandboxRoot, testMode);
    }

    private static string? ResolveEffectiveSuiteDir(string? explicitSuiteDir, string reEngineInstallDir)
    {
        if (!string.IsNullOrWhiteSpace(explicitSuiteDir)) return Path.GetFullPath(explicitSuiteDir.Trim());
        string? parent = Path.GetDirectoryName(Path.GetFullPath(reEngineInstallDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return !string.IsNullOrWhiteSpace(parent) && !string.Equals(parent, Path.GetPathRoot(parent), StringComparison.OrdinalIgnoreCase)
            ? parent
            : null;
    }
}
