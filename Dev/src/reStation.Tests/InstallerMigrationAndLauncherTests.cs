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
        if (Directory.Exists(_tempRoot))
        {
            try { Directory.Delete(_tempRoot, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void InstallerTheme_AndProgramArgs_EnforceAllowAutoStartFalseAndProfiles()
    {
        Assert.False(ReStationInstallerTheme.AllowAutoStart);
        Assert.False(reStation.Installer.Program.AllowAutoStart);
        Assert.Equal("lightweight", ReStationInstallerTheme.DefaultInstallProfile);
        Assert.Equal("StreamItAll\\reEngine\\engines\\reStation\\v0.2.2", ReStationInstallerTheme.UmbrellaSubpath);

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
            out _, out _, out _, out _, out _, out _, out _, out _);

        Assert.Equal("full", profileId);
        Assert.Equal(_tempRoot, reEngineDir);
        Assert.True(migrateLegacy);
        Assert.False(confirmCleanup);
        Assert.Equal(sanitized.Length, forwarded.Length);
    }

    [Fact]
    public void LegacyMigrationHelper_PreservesRomAndSettings_AndGatesStrandedCleanup()
    {
        string sandboxBase = Path.Combine(_tempRoot, "legacy-sbx");
        string legacyData = Path.Combine(sandboxBase, "legacy-programs", "reEngine", "engines", "reStation", "v0.1.1", "data");
        string legacyRom = Path.Combine(legacyData, "rom");
        Directory.CreateDirectory(legacyRom);
        File.WriteAllText(Path.Combine(legacyRom, "GAME.BIN"), "rom-bytes");
        File.WriteAllText(Path.Combine(legacyData, "settings.json"), "{\"resolutionScale\":8}");
        File.WriteAllText(Path.Combine(legacyData, "crash-old.json"), "{\"err\":\"old\"}");

        string targetInstall = Path.Combine(_tempRoot, "v020-install");

        // Opt-in migration without --confirm-legacy-cleanup must preserve ROM + settings and NOT delete stranded artifacts.
        LegacyMigrationResult noClean = ReStationLegacyMigrationHelper.TryMigrateLegacyHostArtifacts(
            targetInstall, testMode: true, sandboxDataDir: sandboxBase, silent: true, headless: true, dryRun: false, confirmCleanupFlag: false);
        Assert.True(noClean.MigratedSettings);
        Assert.Equal(1, noClean.PreservedRomFiles);
        Assert.False(noClean.StrandedArtifactsCleaned);
        Assert.True(File.Exists(Path.Combine(legacyData, "crash-old.json")));
        Assert.True(File.Exists(Path.Combine(targetInstall, "data", "rom", "GAME.BIN")));
        Assert.Contains("\"resolutionScale\":8", File.ReadAllText(Path.Combine(targetInstall, "data", "settings.json")), StringComparison.Ordinal);

        // Explicit confirmation cleans stranded non-ROM artifacts while leaving legacy data\rom\ intact.
        LegacyMigrationResult withClean = ReStationLegacyMigrationHelper.TryMigrateLegacyHostArtifacts(
            targetInstall, testMode: true, sandboxDataDir: sandboxBase, silent: true, headless: true, dryRun: false, confirmCleanupFlag: true);
        Assert.True(withClean.StrandedArtifactsCleaned);
        Assert.False(File.Exists(Path.Combine(legacyData, "crash-old.json")));
        Assert.True(File.Exists(Path.Combine(legacyRom, "GAME.BIN")));

        string fakeReEngineDir = Path.Combine(_tempRoot, "StreamItAll", "reEngine", "reEngine");
        ReStationLegacyMigrationHelper.PersistInstallProfileSettings(targetInstall, "full", Path.GetDirectoryName(fakeReEngineDir), fakeReEngineDir);
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(targetInstall, "data", "settings.json")));
        Assert.Equal("full", doc.RootElement.GetProperty("installProfile").GetString());
        Assert.Equal("D3", doc.RootElement.GetProperty("goalDecompTier").GetString());
        Assert.False(doc.RootElement.TryGetProperty("targetDecompTier", out _));
        Assert.Equal(Path.GetFullPath(fakeReEngineDir), doc.RootElement.GetProperty("reEngineInstallDir").GetString());
    }

    [Fact]
    public void LauncherArgsHelper_ResolvesPeerReEngineAndSuiteDirs_AndZeroPiiDefaultPath()
    {
        string canonical = LauncherArgsHelper.ResolveCanonicalDefaultEngineDir();
        Assert.EndsWith(Path.Combine("Programs", "StreamItAll", "reEngine", "engines", "reStation", "v0.2.2"), canonical, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), canonical, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"D:\Programs", canonical, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%LOCALAPPDATA%", ReStationInstallerTheme.CreateSecondaryPathPicker().Placeholder, StringComparison.Ordinal);

        string peerRouter = Path.Combine(_tempRoot, "suiteA", "reEngine");
        string peerPlayer = Path.Combine(_tempRoot, "suiteA", "engines", "reStation", "v0.2.2", "app", "reStation.Player.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(peerPlayer)!);
        File.WriteAllText(peerPlayer, string.Empty);

        string[] sanitized = LauncherArgsHelper.SanitizeAndPrepareForwardArgs(["--verify", "--reengine-dir", peerRouter, "--suite-dir=ignored", "--headless"], out string? parsedRe, out string? parsedSuite);
        Assert.Equal(["--verify", "--headless"], sanitized);
        Assert.Equal(peerRouter, parsedRe);
        Assert.Equal("ignored", parsedSuite);

        string resolvedPeer = LauncherArgsHelper.ResolveTargetPlayerExecutable(_tempRoot, parsedRe, null);
        Assert.Equal(Path.GetFullPath(peerPlayer), Path.GetFullPath(resolvedPeer));

        string portableSuite = Path.Combine(_tempRoot, "portable");
        string portablePlayer = Path.Combine(portableSuite, "engines", "reStation", "v0.2.2", "app", "reStation.Player.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(portablePlayer)!);
        File.WriteAllText(portablePlayer, string.Empty);

        _ = LauncherArgsHelper.SanitizeAndPrepareForwardArgs(["--verify", $"--suite-dir={portableSuite}"], out string? suiteRe, out string? suiteOnly);
        string resolvedSuite = LauncherArgsHelper.ResolveTargetPlayerExecutable(_tempRoot, suiteRe, suiteOnly);
        Assert.Equal(Path.GetFullPath(portablePlayer), Path.GetFullPath(resolvedSuite));
        Assert.Contains("StreamItAll", ReEngineBootstrapHelper.ResolveDefaultReEngineInstallDir(), StringComparison.OrdinalIgnoreCase);
    }
}
