// @module: reStation.Tests.InstallerWizardAutoSortTests
// @purpose: Verifies Setup Wizard live folder auto-sorting into engines\reStation\v<Ver> / <parent>\reEngine and suite resolution.
// @depends-on: System, System.IO, System.Reflection, System.Threading, System.Windows.Forms, Foundation.Core, Foundation.Installer, reStation.Installer, Xunit
// @exports: InstallerWizardAutoSortTests

using System.Reflection;
using System.Windows.Forms;
using Foundation.Core;
using Foundation.Installer;
using reStation.Installer;
using Xunit;

namespace reStation.Tests;

public sealed class InstallerWizardAutoSortTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"restation-autosort-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Fact]
    public void NormalizeWizardStationInstallDir_SortsParentReEngineEnginesAndStationFolders()
    {
        string versionFolder = $"v{ReStationInstallerTheme.Version}";

        string genericMisc = Path.Combine(_tempRoot, "Misc");
        Assert.Equal(
            Path.Combine(genericMisc, "reEngine", "engines", "reStation", versionFolder),
            ReStationWizardPathNormalizer.NormalizeWizardStationInstallDir(genericMisc));

        string selectedReEngine = Path.Combine(genericMisc, "reEngine");
        Assert.Equal(
            Path.Combine(selectedReEngine, "engines", "reStation", versionFolder),
            ReStationWizardPathNormalizer.NormalizeWizardStationInstallDir(selectedReEngine));

        string selectedEngines = Path.Combine(selectedReEngine, "engines");
        Assert.Equal(
            Path.Combine(selectedEngines, "reStation", versionFolder),
            ReStationWizardPathNormalizer.NormalizeWizardStationInstallDir(selectedEngines));

        string selectedStation = Path.Combine(selectedEngines, "reStation");
        Assert.Equal(
            Path.Combine(selectedStation, versionFolder),
            ReStationWizardPathNormalizer.NormalizeWizardStationInstallDir(selectedStation));

        string exactVersionDir = Path.Combine(selectedStation, versionFolder);
        Assert.Equal(
            exactVersionDir,
            ReStationWizardPathNormalizer.NormalizeWizardStationInstallDir(exactVersionDir));
    }

    [Fact]
    public void NormalizeWizardReEngineDir_AndResolvePreferredSuiteInstallDir_PreserveCompanionLayout()
    {
        string genericMisc = Path.Combine(_tempRoot, "Misc");
        string normalizedReEngine = ReStationWizardPathNormalizer.NormalizeWizardReEngineDir(genericMisc);
        Assert.Equal(Path.Combine(genericMisc, "reEngine"), normalizedReEngine);

        Assert.Equal(
            normalizedReEngine,
            ReStationWizardPathNormalizer.ResolvePreferredSuiteInstallDir(normalizedReEngine));
        Assert.Equal(
            Path.Combine(normalizedReEngine, "engines"),
            ReStationWizardPathNormalizer.ResolvePreferredEnginesRootDir(normalizedReEngine));

        string umbrellaRoot = Path.Combine(_tempRoot, "StreamItAll", "reEngine");
        string umbrellaReEngine = Path.Combine(umbrellaRoot, "reEngine");
        Directory.CreateDirectory(umbrellaReEngine);
        Assert.Equal(
            umbrellaRoot,
            ReStationWizardPathNormalizer.ResolvePreferredSuiteInstallDir(umbrellaReEngine));
    }

    [Fact]
    public void WireWizardAutoSortHandlers_LiveSortsTypedPathsOnWizardControls()
    {
        Exception? uiError = null;
        string actualInstall = string.Empty;
        string actualSecondary = string.Empty;
        string genericMisc = Path.Combine(_tempRoot, "Misc");
        string versionFolder = $"v{ReStationInstallerTheme.Version}";

        var thread = new Thread(() =>
        {
            try
            {
                InstallerThemeOptions theme = ReStationInstallerTheme.CreateThemeOptions();
                InstallerAppDescriptor descriptor = ReStationInstallerTheme.CreateDescriptor(theme);
                var ctx = new InstallerRuntimeContext(
                    cliDataDir: Path.Combine(_tempRoot, "data"),
                    cliConfigPath: null,
                    cliLogDir: Path.Combine(_tempRoot, "logs"),
                    dryRun: true,
                    envPrefix: descriptor.EnvPrefix);
                var engine = new InstallerEngine(descriptor, ctx, typeof(Program).Assembly);

                using var wizard = new InstallerWizardForm(
                    engine: engine,
                    ctx: ctx,
                    initialInstallDir: Path.Combine(_tempRoot, "initial", "engines", "reStation", versionFolder),
                    externalPayloadPath: null,
                    isUninstallMode: false,
                    initialCreateShortcuts: false,
                    initialAutoStart: false,
                    initialLaunchAfter: false,
                    initialPurgeData: false,
                    initialProfileId: theme.DefaultProfileId,
                    initialSecondaryDir: Path.Combine(_tempRoot, "initial"),
                    secondaryFlagName: "--reengine-dir",
                    initialMigrateLegacy: false);

                ReStationWizardPathNormalizer.WireWizardAutoSortHandlers(wizard);
                wizard.SecondaryDirTextBox.Text = genericMisc;
                typeof(Control).GetMethod("OnLeave", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(wizard.SecondaryDirTextBox, [EventArgs.Empty]);

                actualSecondary = wizard.SecondaryDirTextBox.Text;
                actualInstall = wizard.InstallDirTextBox.Text;
            }
            catch (Exception ex)
            {
                uiError = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        Assert.Null(uiError);

        Assert.Equal(Path.Combine(genericMisc, "reEngine"), actualSecondary);
        Assert.Equal(Path.Combine(genericMisc, "reEngine", "engines", "reStation", versionFolder), actualInstall);
    }
}
