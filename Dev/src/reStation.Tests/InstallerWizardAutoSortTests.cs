// @module: reStation.Tests.InstallerWizardAutoSortTests
// @purpose: Verifies Setup Wizard live folder auto-sorting into engines\reSlate\v<Ver> / <parent>\reEngine and suite resolution.
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
            Path.Combine(genericMisc, "reEngine", "engines", "reSlate", versionFolder),
            ReStationWizardPathNormalizer.NormalizeWizardStationInstallDir(genericMisc));

        string selectedReEngine = Path.Combine(genericMisc, "reEngine");
        Assert.Equal(
            Path.Combine(selectedReEngine, "engines", "reSlate", versionFolder),
            ReStationWizardPathNormalizer.NormalizeWizardStationInstallDir(selectedReEngine));

        string selectedEngines = Path.Combine(selectedReEngine, "engines");
        Assert.Equal(
            Path.Combine(selectedEngines, "reSlate", versionFolder),
            ReStationWizardPathNormalizer.NormalizeWizardStationInstallDir(selectedEngines));

        string selectedStation = Path.Combine(selectedEngines, "reSlate");
        Assert.Equal(
            Path.Combine(selectedStation, versionFolder),
            ReStationWizardPathNormalizer.NormalizeWizardStationInstallDir(selectedStation));

        string exactVersionDir = Path.Combine(selectedStation, versionFolder);
        Assert.Equal(
            exactVersionDir,
            ReStationWizardPathNormalizer.NormalizeWizardStationInstallDir(exactVersionDir));

        string legacyInstalledDir = Path.Combine(selectedEngines, "reStation", "v0.2.8");
        Directory.CreateDirectory(legacyInstalledDir);
        File.WriteAllText(Path.Combine(legacyInstalledDir, "reStation.exe"), string.Empty);
        Assert.Equal(
            legacyInstalledDir,
            ReStationWizardPathNormalizer.NormalizeWizardStationInstallDir(legacyInstalledDir));
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
                    initialInstallDir: Path.Combine(_tempRoot, "initial", "engines", "reSlate", versionFolder),
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
        Assert.Equal(Path.Combine(genericMisc, "reEngine", "engines", "reSlate", versionFolder), actualInstall);
    }

    [Fact]
    public void NormalizeWizardStationInstallDir_DoesNotTreatPopulatedReEngineDirAsOwnInstall()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"reslate_reengine_guard_{Guid.NewGuid():N}");
        try
        {
            string reEngineDir = Path.Combine(tempRoot, "Misc", "StreamItAll", "reEngine");
            Directory.CreateDirectory(Path.Combine(reEngineDir, "app"));
            Directory.CreateDirectory(Path.Combine(reEngineDir, "engines", "reStation", "v0.2.8"));
            File.WriteAllText(Path.Combine(reEngineDir, "reEngine.exe"), string.Empty);
            File.WriteAllText(Path.Combine(reEngineDir, "app", "reEngine.exe"), string.Empty);
            File.WriteAllText(Path.Combine(reEngineDir, "app", "reEngine-Uninstall.exe"), string.Empty);
            File.WriteAllText(
                Path.Combine(reEngineDir, "app", "install-manifest.json"),
                "{\"product_name\":\"reEngine\",\"appId\":\"reengine\"}");
            File.WriteAllText(Path.Combine(reEngineDir, "engines", "reStation", "v0.2.8", "reStation.exe"), string.Empty);

            Assert.True(reStation.Installer.ReStationWizardPathNormalizer.IsReEngineDirectory(reEngineDir));
            Assert.False(reStation.Installer.ReStationInstallerBootstrap.HasDirectInstallation(reEngineDir));

            string expectedSideBySide = Path.Combine(
                reEngineDir,
                "engines",
                "reSlate",
                $"v{reStation.Installer.ReStationInstallerTheme.Version}");
            Assert.Equal(
                expectedSideBySide,
                reStation.Installer.ReStationWizardPathNormalizer.NormalizeWizardStationInstallDir(reEngineDir));

            string ownInstallDir = Path.Combine(tempRoot, "CustomInstall", "reSlate");
            Directory.CreateDirectory(Path.Combine(ownInstallDir, "app"));
            File.WriteAllText(
                Path.Combine(ownInstallDir, "app", "install-manifest.json"),
                "{\"product_name\":\"reSlate\",\"appId\":\"reslate\"}");
            Assert.False(reStation.Installer.ReStationWizardPathNormalizer.IsReEngineDirectory(ownInstallDir));
            Assert.True(reStation.Installer.ReStationInstallerBootstrap.HasDirectInstallation(ownInstallDir));
            Assert.Equal(
                ownInstallDir,
                reStation.Installer.ReStationWizardPathNormalizer.NormalizeWizardStationInstallDir(ownInstallDir));
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
            catch
            {
                // Ignore transient file locks during temp cleanup.
            }
        }
    }
}
