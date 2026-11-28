// @module: reStation.Tests.InstallerMigrationAndLauncherTests
// @purpose: Validates T4 installer profiles, AllowAutoStart=false, --reengine-dir/--migrate-legacy cleanup gating, and LauncherArgsHelper routing.

using System.Text.Json;
using Foundation.Installer;
using reEngine.Core.Bootstrap;
using reStation.Installer;
using reStation.Launcher;
using Xunit;

namespace reStation.Tests;

public sealed class InstallerMigrationAndLauncherTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "restation-inst-t4-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        TestTempCleanup.TryDeleteDirectory(_tempRoot, nameof(InstallerMigrationAndLauncherTests));
    }

    [Fact]
    public void InstallerTheme_AndProgramArgs_EnforceAllowAutoStartFalseAndProfiles()
    {
        Assert.False(ReStationInstallerTheme.AllowAutoStart);
        Assert.False(reStation.Installer.Program.AllowAutoStart);
        Assert.Equal("lightweight", ReStationInstallerTheme.DefaultInstallProfile);
        Assert.Equal("StreamItAll\\reEngine\\engines\\reSlate\\v0.3.3", ReStationInstallerTheme.UmbrellaSubpath);

        InstallerThemeOptions theme = ReStationInstallerTheme.CreateThemeOptions();
        Assert.False(theme.AllowAutoStart);
        Assert.False(theme.BootstrapSharedSuiteDaemon);
        Assert.Equal("--reengine-dir", theme.SecondaryPathPicker!.FlagName);
        Assert.Equal(2, theme.AvailableProfiles!.Count);
        Assert.Contains(theme.AvailableProfiles, p => p.Id == "lightweight" && p.IsDefault);
        Assert.Contains(theme.AvailableProfiles, p => p.Id == "full");

        string[] sanitized = reStation.Installer.Program.SanitizeArgs(
            ["--profile", "full", "--autostart", "--auto-start=true", "--reengine-dir", _tempRoot, "--migrate-legacy"]);
        Assert.DoesNotContain(sanitized, a => a.Contains("autostart", StringComparison.OrdinalIgnoreCase) || a.Contains("auto-start", StringComparison.OrdinalIgnoreCase));

        string[] forwarded = reStation.Installer.Program.ExtractHostArgs(
            sanitized,
            out string profileId,
            out _,
            out string? reEngineDir,
            out bool migrateLegacy,
            out bool confirmCleanup,
            out _, out _, out _, out _, out _, out _);

        Assert.Equal("full", profileId);
        Assert.Equal(_tempRoot, reEngineDir);
        Assert.True(migrateLegacy);
        Assert.False(confirmCleanup);
        Assert.Equal(sanitized.Length, forwarded.Length);
    }

    [Fact]
    public void LegacyMigrationHelper_PreservesRomAndSettings_AndGatesStrandedCleanup()
    {
        string isolatedBase = Path.Combine(_tempRoot, "legacy-isolated");
        string legacyData = Path.Combine(isolatedBase, "legacy-programs", "reEngine", "engines", "reStation", "v0.1.1", "data");
        string legacyRom = Path.Combine(legacyData, "rom");
        Directory.CreateDirectory(legacyRom);
        File.WriteAllText(Path.Combine(legacyRom, "GAME.BIN"), "rom-bytes");
        File.WriteAllText(Path.Combine(legacyData, "settings.json"), "{\"resolutionScale\":8}");
        File.WriteAllText(Path.Combine(legacyData, "crash-old.json"), "{\"err\":\"old\"}");

        string targetInstall = Path.Combine(_tempRoot, "v020-install");

        // Opt-in migration without --confirm-legacy-cleanup must preserve ROM + settings and NOT delete stranded artifacts.
        LegacyMigrationResult noClean = ReStationLegacyMigrationHelper.TryMigrateLegacyHostArtifacts(
            targetInstall, isolated: true, isolatedDataDir: isolatedBase, silent: true, dryRun: false, confirmCleanupFlag: false);
        Assert.True(noClean.MigratedSettings);
        Assert.Equal(1, noClean.PreservedRomFiles);
        Assert.False(noClean.StrandedArtifactsCleaned);
        Assert.True(File.Exists(Path.Combine(legacyData, "crash-old.json")));
        Assert.True(File.Exists(Path.Combine(targetInstall, "data", "rom", "GAME.BIN")));
        string canonicalSettingsPath = Path.Combine(targetInstall, "data", "config", "settings.json");
        Assert.Contains("\"resolutionScale\":8", File.ReadAllText(canonicalSettingsPath), StringComparison.Ordinal);

        // Explicit confirmation cleans stranded non-ROM artifacts while leaving legacy data\rom\ intact.
        LegacyMigrationResult withClean = ReStationLegacyMigrationHelper.TryMigrateLegacyHostArtifacts(
            targetInstall, isolated: true, isolatedDataDir: isolatedBase, silent: true, dryRun: false, confirmCleanupFlag: true);
        Assert.True(withClean.StrandedArtifactsCleaned);
        Assert.False(File.Exists(Path.Combine(legacyData, "crash-old.json")));
        Assert.True(File.Exists(Path.Combine(legacyRom, "GAME.BIN")));

        string fakeReEngineDir = Path.Combine(_tempRoot, "StreamItAll", "reEngine", "reEngine");
        ReStationLegacyMigrationHelper.PersistInstallProfileSettings(targetInstall, "full", Path.GetDirectoryName(fakeReEngineDir), fakeReEngineDir);
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(canonicalSettingsPath));
        Assert.Equal("reslate", doc.RootElement.GetProperty("appId").GetString());
        Assert.Equal("full", doc.RootElement.GetProperty("installProfile").GetString());
        Assert.Equal("D3", doc.RootElement.GetProperty("goalDecompTier").GetString());
        Assert.False(doc.RootElement.TryGetProperty("targetDecompTier", out _));
        Assert.Equal(Path.GetFullPath(fakeReEngineDir), doc.RootElement.GetProperty("reEngineInstallDir").GetString());

        // Non-destructive flat settings.json -> config\settings.json migration inside PersistInstallProfileSettings
        string flatUpgradeDir = Path.Combine(_tempRoot, "flat-upgrade");
        string flatDataDir = Path.Combine(flatUpgradeDir, "data");
        Directory.CreateDirectory(flatDataDir);
        File.WriteAllText(Path.Combine(flatDataDir, "settings.json"), "{\"schemaVersion\":1,\"backendId\":\"opengl\",\"resolutionScale\":4}");
        ReStationLegacyMigrationHelper.PersistInstallProfileSettings(flatUpgradeDir, "lightweight", null, null);
        Assert.True(File.Exists(Path.Combine(flatDataDir, "settings.json")));
        using JsonDocument upgradedDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(flatDataDir, "config", "settings.json")));
        Assert.Equal("opengl", upgradedDoc.RootElement.GetProperty("backendId").GetString());
        Assert.Equal(4, upgradedDoc.RootElement.GetProperty("resolutionScale").GetInt32());
    }

    [Fact]
    public void LauncherArgsHelper_ResolvesPeerReEngineAndSuiteDirs_AndZeroPiiDefaultPath()
    {
        string canonical = LauncherArgsHelper.ResolveCanonicalDefaultEngineDir();
        Assert.EndsWith(Path.Combine("Programs", "StreamItAll", "reEngine", "engines", "reSlate", "v0.3.3"), canonical, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), canonical, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"D:\Programs", canonical, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%LOCALAPPDATA%", ReStationInstallerTheme.CreateSecondaryPathPicker().Placeholder, StringComparison.Ordinal);

        string peerRouter = Path.Combine(_tempRoot, "suiteA", "reEngine");
        string peerPlayer = Path.Combine(_tempRoot, "suiteA", "engines", "reSlate", "v0.3.3", "app", "reSlate.Player.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(peerPlayer)!);
        File.WriteAllText(peerPlayer, string.Empty);

        string[] sanitized = LauncherArgsHelper.SanitizeAndPrepareForwardArgs(["--verify", "--reengine-dir", peerRouter, "--suite-dir=ignored", "--tray-only"], out string? parsedRe, out string? parsedSuite);
        Assert.Equal(["--verify", "--tray-only"], sanitized);
        Assert.Equal(peerRouter, parsedRe);
        Assert.Equal("ignored", parsedSuite);

        string resolvedPeer = LauncherArgsHelper.ResolveTargetPlayerExecutable(_tempRoot, parsedRe, null);
        Assert.Equal(Path.GetFullPath(peerPlayer), Path.GetFullPath(resolvedPeer));

        string portableSuite = Path.Combine(_tempRoot, "portable");
        string portablePlayer = Path.Combine(portableSuite, "engines", "reStation", "v0.3.3", "app", "reStation.Player.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(portablePlayer)!);
        File.WriteAllText(portablePlayer, string.Empty);

        _ = LauncherArgsHelper.SanitizeAndPrepareForwardArgs(["--verify", $"--suite-dir={portableSuite}"], out string? suiteRe, out string? suiteOnly);
        string resolvedSuite = LauncherArgsHelper.ResolveTargetPlayerExecutable(_tempRoot, suiteRe, suiteOnly);
        Assert.Equal(Path.GetFullPath(portablePlayer), Path.GetFullPath(resolvedSuite));
        Assert.Contains("StreamItAll", ReEngineBootstrapHelper.ResolveDefaultReEngineInstallDir(), StringComparison.OrdinalIgnoreCase);

        string envPrimary = Path.Combine(_tempRoot, "env-reslate");
        string envPrimaryExe = Path.Combine(envPrimary, "app", "reSlate.Player.exe");
        string envFallback = Path.Combine(_tempRoot, "env-restation");
        string envFallbackExe = Path.Combine(envFallback, "app", "reStation.Player.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(envPrimaryExe)!);
        Directory.CreateDirectory(Path.GetDirectoryName(envFallbackExe)!);
        File.WriteAllText(envPrimaryExe, string.Empty);
        File.WriteAllText(envFallbackExe, string.Empty);

        string? prevReslate = Environment.GetEnvironmentVariable("RESLATE_INSTALL_DIR");
        string? prevRestation = Environment.GetEnvironmentVariable("RESTATION_INSTALL_DIR");
        try
        {
            Environment.SetEnvironmentVariable("RESLATE_INSTALL_DIR", envPrimary);
            Environment.SetEnvironmentVariable("RESTATION_INSTALL_DIR", envFallback);
            Assert.Equal(Path.GetFullPath(envPrimaryExe), Path.GetFullPath(LauncherArgsHelper.ResolveTargetPlayerExecutable(_tempRoot, null, null)));

            Environment.SetEnvironmentVariable("RESLATE_INSTALL_DIR", null);
            Assert.Equal(Path.GetFullPath(envFallbackExe), Path.GetFullPath(LauncherArgsHelper.ResolveTargetPlayerExecutable(_tempRoot, null, null)));
        }
        finally
        {
            Environment.SetEnvironmentVariable("RESLATE_INSTALL_DIR", prevReslate);
            Environment.SetEnvironmentVariable("RESTATION_INSTALL_DIR", prevRestation);
        }
    }

    [Fact]
    public void ResolveInitialPaths_UsesExistingReEngineAndReStationSettingsProfile()
    {
        string baseDir = Path.Combine(_tempRoot, "stream-games", "StreamItAll");
        string reEngineDir = Path.Combine(baseDir, "reEngine");
        string oldStationDir = Path.Combine(reEngineDir, "engines", "reStation", "v0.2.8");
        Directory.CreateDirectory(Path.Combine(oldStationDir, "data", "config"));
        File.WriteAllText(
            Path.Combine(oldStationDir, "data", "config", "settings.json"),
            """{"installProfile":"full"}""");

        var registry = new FakePlatformRegistry();
        registry.SetString(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\reEngine", "InstallLocation", reEngineDir);
        registry.SetString(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\reStation", "InstallLocation", oldStationDir);
        registry.SetString(@"Software\StreamItAll", "LastInstallBaseDir", baseDir);

        ResolvedWizardInstallPaths resolved = ReStationInstallPathResolver.ResolveInitialPaths(
            registry,
            activeSandbox: null,
            suppressHostMutations: false,
            isIsolatedTestProcess: false,
            isolatedDataDir: null,
            defaultInstallDir: ReStationInstallerTheme.ResolveDefaultVersionInstallDir(),
            defaultProfileId: ReStationInstallerTheme.DefaultInstallProfile);

        Assert.Equal(Path.GetFullPath(reEngineDir), resolved.ReEngineInstallDir);
        Assert.Equal(
            Path.Combine(Path.GetFullPath(reEngineDir), "engines", "reSlate", $"v{ReStationInstallerTheme.Version}"),
            resolved.InstallDir);
        Assert.Equal("full", resolved.ProfileId);
        Assert.Equal(
            Path.GetFullPath(reEngineDir),
            ReStationInstallPathResolver.ResolveSilentReEngineDir(registry, activeSandbox: null));
    }

    [Fact]
    public void ResolveInitialPaths_UsesExistingReSlateUninstallLocation()
    {
        string reEngineDir = Path.Combine(_tempRoot, "custom-slate", "StreamItAll", "reEngine");
        string existingSlateDir = Path.Combine(reEngineDir, "engines", "reSlate", $"v{ReStationInstallerTheme.Version}");
        Directory.CreateDirectory(Path.Combine(existingSlateDir, "data"));
        File.WriteAllText(Path.Combine(existingSlateDir, "data", "settings.json"), """{"installProfile":"full"}""");

        var registry = new FakePlatformRegistry();
        registry.SetString(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\reSlate", "InstallLocation", existingSlateDir);

        ResolvedWizardInstallPaths resolved = ReStationInstallPathResolver.ResolveInitialPaths(
            registry,
            activeSandbox: null,
            suppressHostMutations: false,
            isIsolatedTestProcess: false,
            isolatedDataDir: null,
            defaultInstallDir: ReStationInstallerTheme.ResolveDefaultVersionInstallDir(),
            defaultProfileId: ReStationInstallerTheme.DefaultInstallProfile);

        Assert.Equal(Path.GetFullPath(reEngineDir), resolved.ReEngineInstallDir);
        Assert.Equal(Path.GetFullPath(existingSlateDir), resolved.InstallDir);
        Assert.Equal("full", resolved.ProfileId);
    }

    [Fact]
    public void ResolveInitialPaths_ExtractsReEngineRootFromReStationAlone()
    {
        string reEngineDir = Path.Combine(_tempRoot, "legacy-station-only", "StreamItAll", "reEngine");
        string oldStationDir = Path.Combine(reEngineDir, "engines", "reStation", "v0.2.8");

        var registry = new FakePlatformRegistry();
        registry.SetString(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\reStation", "InstallLocation", oldStationDir);

        ResolvedWizardInstallPaths resolved = ReStationInstallPathResolver.ResolveInitialPaths(
            registry,
            activeSandbox: null,
            suppressHostMutations: false,
            isIsolatedTestProcess: false,
            isolatedDataDir: null,
            defaultInstallDir: ReStationInstallerTheme.ResolveDefaultVersionInstallDir(),
            defaultProfileId: ReStationInstallerTheme.DefaultInstallProfile);

        Assert.Equal(Path.GetFullPath(reEngineDir), resolved.ReEngineInstallDir);
        Assert.Equal(
            Path.Combine(Path.GetFullPath(reEngineDir), "engines", "reSlate", $"v{ReStationInstallerTheme.Version}"),
            resolved.InstallDir);
        Assert.Equal(ReStationInstallerTheme.DefaultInstallProfile, resolved.ProfileId);
    }

    [Fact]
    public void ResolveInitialPaths_UsesLastInstallBaseDirEndingInStreamItAll_WithoutDoubleNesting()
    {
        string baseDir = Path.Combine(_tempRoot, "Games", "Misc", "StreamItAll");
        var registry = new FakePlatformRegistry();
        registry.SetString(@"Software\StreamItAll", "LastInstallBaseDir", baseDir);

        ResolvedWizardInstallPaths resolved = ReStationInstallPathResolver.ResolveInitialPaths(
            registry,
            activeSandbox: null,
            suppressHostMutations: false,
            isIsolatedTestProcess: false,
            isolatedDataDir: null,
            defaultInstallDir: ReStationInstallerTheme.ResolveDefaultVersionInstallDir(),
            defaultProfileId: ReStationInstallerTheme.DefaultInstallProfile);

        string expectedEngine = Path.Combine(Path.GetFullPath(baseDir), "reEngine");
        Assert.Equal(expectedEngine, resolved.ReEngineInstallDir);
        Assert.Equal(
            Path.Combine(expectedEngine, "engines", "reSlate", $"v{ReStationInstallerTheme.Version}"),
            resolved.InstallDir);
        Assert.DoesNotContain($"StreamItAll{Path.DirectorySeparatorChar}StreamItAll", resolved.InstallDir, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveInitialPaths_AppendsStreamItAllToLastInstallBaseDir_WhenLeafDiffers()
    {
        string baseDir = Path.Combine(_tempRoot, "Apps");
        var registry = new FakePlatformRegistry();
        registry.SetString(@"Software\StreamItAll", "LastInstallBaseDir", baseDir);

        ResolvedWizardInstallPaths resolved = ReStationInstallPathResolver.ResolveInitialPaths(
            registry,
            activeSandbox: null,
            suppressHostMutations: false,
            isIsolatedTestProcess: false,
            isolatedDataDir: null,
            defaultInstallDir: ReStationInstallerTheme.ResolveDefaultVersionInstallDir(),
            defaultProfileId: ReStationInstallerTheme.DefaultInstallProfile);

        string expectedEngine = Path.Combine(Path.GetFullPath(baseDir), "StreamItAll", "reEngine");
        Assert.Equal(expectedEngine, resolved.ReEngineInstallDir);
        Assert.Equal(
            Path.Combine(expectedEngine, "engines", "reSlate", $"v{ReStationInstallerTheme.Version}"),
            resolved.InstallDir);
    }

    [Fact]
    public void ResolveInitialPaths_IgnoresRegistry_WhenHostMutationsSuppressed()
    {
        string hostBase = Path.Combine(_tempRoot, "LiveHost", "StreamItAll");
        string isolatedRoot = Path.Combine(_tempRoot, "stage1-isolated");

        var registry = new FakePlatformRegistry();
        registry.SetString(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\reEngine", "InstallLocation", Path.Combine(hostBase, "reEngine"));
        registry.SetString(@"Software\StreamItAll", "LastInstallBaseDir", hostBase);

        ResolvedWizardInstallPaths stage1Suppressed = ReStationInstallPathResolver.ResolveInitialPaths(
            registry,
            activeSandbox: null,
            suppressHostMutations: true,
            isIsolatedTestProcess: false,
            isolatedDataDir: null,
            defaultInstallDir: ReStationInstallerTheme.ResolveDefaultVersionInstallDir(),
            defaultProfileId: ReStationInstallerTheme.DefaultInstallProfile);

        Assert.DoesNotContain("LiveHost", stage1Suppressed.ReEngineInstallDir, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            Path.GetFullPath(ReStationInstallerTheme.ResolveDefaultVersionInstallDir()),
            stage1Suppressed.InstallDir);

        ResolvedWizardInstallPaths isolatedTest = ReStationInstallPathResolver.ResolveInitialPaths(
            registry,
            activeSandbox: null,
            suppressHostMutations: true,
            isIsolatedTestProcess: true,
            isolatedDataDir: isolatedRoot,
            defaultInstallDir: ReStationInstallerTheme.ResolveDefaultVersionInstallDir(),
            defaultProfileId: ReStationInstallerTheme.DefaultInstallProfile);

        Assert.StartsWith(Path.GetFullPath(isolatedRoot), isolatedTest.ReEngineInstallDir, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(Path.GetFullPath(isolatedRoot), isolatedTest.InstallDir, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LiveHost", isolatedTest.InstallDir, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveInitialPaths_AndSilentDir_AcceptRegistryPaths_WhenSandboxIsInactive()
    {
        string baseDir = Path.Combine(_tempRoot, "inactive-sandbox-host", "StreamItAll");
        string reEngineDir = Path.Combine(baseDir, "reEngine");
        string oldStationDir = Path.Combine(reEngineDir, "engines", "reStation", "v0.2.8");
        Directory.CreateDirectory(Path.Combine(oldStationDir, "data", "config"));
        File.WriteAllText(
            Path.Combine(oldStationDir, "data", "config", "settings.json"),
            """{"installProfile":"full"}""");

        var registry = new FakePlatformRegistry();
        registry.SetString(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\reEngine", "InstallLocation", reEngineDir);
        registry.SetString(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\reStation", "InstallLocation", oldStationDir);
        registry.SetString(@"Software\StreamItAll", "LastInstallBaseDir", baseDir);

        ResolvedWizardInstallPaths resolved = ReStationInstallPathResolver.ResolveInitialPaths(
            registry,
            activeSandbox: Foundation.Core.SandboxResolution.Inactive,
            suppressHostMutations: false,
            isIsolatedTestProcess: false,
            isolatedDataDir: null,
            defaultInstallDir: ReStationInstallerTheme.ResolveDefaultVersionInstallDir(),
            defaultProfileId: ReStationInstallerTheme.DefaultInstallProfile);

        Assert.Equal(Path.GetFullPath(reEngineDir), resolved.ReEngineInstallDir);
        Assert.Equal(
            Path.Combine(Path.GetFullPath(reEngineDir), "engines", "reSlate", $"v{ReStationInstallerTheme.Version}"),
            resolved.InstallDir);
        Assert.Equal("full", resolved.ProfileId);

        Assert.Equal(
            Path.GetFullPath(reEngineDir),
            ReStationInstallPathResolver.ResolveSilentReEngineDir(
                registry,
                activeSandbox: Foundation.Core.SandboxResolution.Inactive));

        string suppressedSilent = ReStationInstallPathResolver.ResolveSilentReEngineDir(
            registry,
            activeSandbox: Foundation.Core.SandboxResolution.Inactive,
            suppressHostMutations: true);
        Assert.DoesNotContain("inactive-sandbox-host", suppressedSilent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveInitialPaths_WithRealOnDiskReEngineAndPoisonedReSlate_ResolvesSideBySideEnginesFolder()
    {
        string baseDir = Path.Combine(_tempRoot, "Games", "Misc", "StreamItAll");
        string reEngineDir = Path.Combine(baseDir, "reEngine");
        Directory.CreateDirectory(Path.Combine(reEngineDir, "app"));
        Directory.CreateDirectory(Path.Combine(reEngineDir, "data", "config"));
        File.WriteAllText(Path.Combine(reEngineDir, "reEngine.exe"), string.Empty);
        File.WriteAllText(
            Path.Combine(reEngineDir, "app", "install-manifest.json"),
            "{\"product_name\":\"reEngine\",\"appId\":\"reengine\"}");
        File.WriteAllText(
            Path.Combine(reEngineDir, "data", "config", "settings.json"),
            "{\"installProfile\":\"full\"}");

        string expectedInstallDir = Path.Combine(
            Path.GetFullPath(reEngineDir),
            "engines",
            "reSlate",
            $"v{ReStationInstallerTheme.Version}");

        var poisonedRegistry = new FakePlatformRegistry();
        poisonedRegistry.SetString(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\reEngine", "InstallLocation", reEngineDir);
        poisonedRegistry.SetString(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\reSlate", "InstallLocation", reEngineDir);
        poisonedRegistry.SetString(@"Software\StreamItAll", "LastInstallBaseDir", baseDir);

        ResolvedWizardInstallPaths fromPoisoned = ReStationInstallPathResolver.ResolveInitialPaths(
            poisonedRegistry,
            activeSandbox: null,
            suppressHostMutations: false,
            isIsolatedTestProcess: false,
            isolatedDataDir: null,
            defaultInstallDir: ReStationInstallerTheme.ResolveDefaultVersionInstallDir(),
            defaultProfileId: ReStationInstallerTheme.DefaultInstallProfile);

        Assert.Equal(Path.GetFullPath(reEngineDir), fromPoisoned.ReEngineInstallDir);
        Assert.Equal(expectedInstallDir, fromPoisoned.InstallDir);
        Assert.Equal(ReStationInstallerTheme.DefaultInstallProfile, fromPoisoned.ProfileId);

        var lastBaseRegistry = new FakePlatformRegistry();
        lastBaseRegistry.SetString(@"Software\StreamItAll", "LastInstallBaseDir", baseDir);

        ResolvedWizardInstallPaths fromLastBase = ReStationInstallPathResolver.ResolveInitialPaths(
            lastBaseRegistry,
            activeSandbox: null,
            suppressHostMutations: false,
            isIsolatedTestProcess: false,
            isolatedDataDir: null,
            defaultInstallDir: ReStationInstallerTheme.ResolveDefaultVersionInstallDir(),
            defaultProfileId: ReStationInstallerTheme.DefaultInstallProfile);

        Assert.Equal(Path.GetFullPath(reEngineDir), fromLastBase.ReEngineInstallDir);
        Assert.Equal(expectedInstallDir, fromLastBase.InstallDir);
        Assert.Equal(ReStationInstallerTheme.DefaultInstallProfile, fromLastBase.ProfileId);
    }

    [Fact]
    public void LauncherArgsHelper_V031Flags_ForwardWithoutSwallowingSubsequentOptions()
    {
        string baseDir = Path.Combine(_tempRoot, "caller");
        string[] raw =
        [
            "--fast-forward", "--disc", "mods\\game.cue",
            "--hd-packs=on", "--aspect-mode", "horPlus16By9",
            "--hd-packs", "off", "--preload-media", "rom.bin",
            "--fast-forward=off"
        ];
        string[] resolved = LauncherArgsHelper.ResolveRelativePathArgs(raw, baseDir);

        Assert.Equal("--fast-forward", resolved[0]);
        Assert.Equal("--disc", resolved[1]);
        Assert.Equal(Path.GetFullPath(Path.Combine(baseDir, "mods", "game.cue")), resolved[2]);
        Assert.Equal("--hd-packs=on", resolved[3]);
        Assert.Equal("--aspect-mode", resolved[4]);
        Assert.Equal("horPlus16By9", resolved[5]);
        Assert.Equal("--hd-packs", resolved[6]);
        Assert.Equal("off", resolved[7]);
        Assert.Equal("--preload-media", resolved[8]);
        Assert.Equal(Path.GetFullPath(Path.Combine(baseDir, "rom.bin")), resolved[9]);
        Assert.Equal("--fast-forward=off", resolved[10]);

        Assert.True(LauncherArgsHelper.RequiresSyncWait(["--fast-forward", "--doctor"]));
        Assert.True(LauncherArgsHelper.RequiresSyncWait(["--hd-packs=off", "--wait"]));
        Assert.False(LauncherArgsHelper.RequiresSyncWait(["--hd-packs", "on", "--aspect-mode", "fourByThree", "--disc", "a.cue"]));
    }

    private sealed class FakePlatformRegistry : Foundation.Core.IPlatformRegistry
    {
        private readonly Dictionary<string, Foundation.Core.PlatformRegistryValue> _values =
            new(StringComparer.OrdinalIgnoreCase);

        public void SetString(string subKeyPath, string valueName, string value)
        {
            SetValue(
                Foundation.Core.PlatformRegistryHive.CurrentUser,
                subKeyPath,
                valueName,
                value,
                Foundation.Core.PlatformRegistryValueKind.String);
        }

        public bool SubKeyExists(Foundation.Core.PlatformRegistryHive hive, string subKeyPath)
        {
            string prefix = $"{hive}|{subKeyPath}|";
            return _values.Keys.Any(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        public void CreateSubKey(Foundation.Core.PlatformRegistryHive hive, string subKeyPath) { }

        public bool DeleteSubKeyTree(
            Foundation.Core.PlatformRegistryHive hive,
            string subKeyPath,
            bool throwOnMissingSubKey = false)
        {
            string prefix = $"{hive}|{subKeyPath}|";
            List<string> toRemove = _values.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (string key in toRemove)
                _values.Remove(key);
            return toRemove.Count > 0;
        }

        public Foundation.Core.PlatformRegistryValue? GetValue(
            Foundation.Core.PlatformRegistryHive hive,
            string subKeyPath,
            string valueName)
        {
            return _values.TryGetValue(MakeKey(hive, subKeyPath, valueName), out var val) ? val : null;
        }

        public void SetValue(
            Foundation.Core.PlatformRegistryHive hive,
            string subKeyPath,
            string valueName,
            object? value,
            Foundation.Core.PlatformRegistryValueKind valueKind)
        {
            _values[MakeKey(hive, subKeyPath, valueName)] =
                new Foundation.Core.PlatformRegistryValue(valueKind, value);
        }

        public void SetValues(
            Foundation.Core.PlatformRegistryHive hive,
            string subKeyPath,
            IReadOnlyDictionary<string, Foundation.Core.PlatformRegistryValue> values)
        {
            foreach (var kv in values)
                _values[MakeKey(hive, subKeyPath, kv.Key)] = kv.Value;
        }

        public bool DeleteValue(
            Foundation.Core.PlatformRegistryHive hive,
            string subKeyPath,
            string valueName,
            bool throwOnMissingValue = false)
        {
            return _values.Remove(MakeKey(hive, subKeyPath, valueName));
        }

        public IReadOnlyList<string> GetSubKeyNames(Foundation.Core.PlatformRegistryHive hive, string subKeyPath)
            => Array.Empty<string>();

        public IReadOnlyList<string> GetValueNames(Foundation.Core.PlatformRegistryHive hive, string subKeyPath)
            => Array.Empty<string>();

        public IReadOnlyDictionary<string, Foundation.Core.PlatformRegistryValue> GetValues(
            Foundation.Core.PlatformRegistryHive hive,
            string subKeyPath)
            => new Dictionary<string, Foundation.Core.PlatformRegistryValue>(StringComparer.OrdinalIgnoreCase);

        private static string MakeKey(Foundation.Core.PlatformRegistryHive hive, string subKeyPath, string valueName)
            => $"{hive}|{subKeyPath}|{valueName}";
    }
}
