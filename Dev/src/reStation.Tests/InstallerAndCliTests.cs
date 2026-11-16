// @module: reStation.Tests.InstallerAndCliTests
// @purpose: Verifies AllowAutoStart=false, --autostart* stripping, ReEngineBootstrapHelper side-by-side APP_ISOLATED_TEST=1 containment, Launcher sync-wait flags, and Zero-PII source invariants.
// @depends-on: Xunit, System.Text.RegularExpressions, Foundation.Core, Foundation.Installer, reStation.Installer
// @exports: InstallerAndCliTests

using System.Text.RegularExpressions;
using Foundation.Core;
using Foundation.Installer;
using reStation.Installer;
using Xunit;

namespace reStation.Tests;

public sealed class InstallerAndCliTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"restation-inst-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true);
    }

    [Fact]
    public void InstallerTheme_EnforcesZeroDaemonAllowAutoStartFalseAndEmbeddedResourceName()
    {
        var descriptor = ReStationInstallerTheme.CreateDescriptor();

        Assert.False(descriptor.AllowAutoStart);
        Assert.Equal("reStation", descriptor.ProductName);
        Assert.Equal(ReStationInstallerTheme.Version, descriptor.Version);
        Assert.Equal("reStation.Installer.Embedded.payload.zip", descriptor.EmbeddedPayloadResourceName);
    }

    [Fact]
    public void InstallerProgram_SanitizeArgs_StripsAllAutostartFlags()
    {
        string[] raw = { "--autostart", "--autostart=1", "--auto-start-on-login", "--silent", "--data-dir", _tempRoot };
        string[] clean = Program.SanitizeArgs(raw);

        Assert.Equal(new[] { "--silent", "--data-dir", _tempRoot }, clean);
    }

    [Theory]
    [InlineData(new[] { "--help" }, null, true)]
    [InlineData(new[] { "-h" }, null, true)]
    [InlineData(new[] { "/?" }, null, true)]
    [InlineData(new[] { "help" }, null, true)]
    [InlineData(new[] { "--version" }, null, true)]
    [InlineData(new[] { "-v" }, null, true)]
    [InlineData(new[] { "--verify" }, null, true)]
    [InlineData(new[] { "--dry-run" }, null, true)]
    [InlineData(new[] { "--uninstall" }, null, true)]
    [InlineData(new[] { "--silent" }, "C:\\temp\\uninstall.exe", true)]
    [InlineData(new[] { "--silent" }, "C:\\temp\\reStation-Setup.exe", false)]
    public void InstallerBootstrap_ShouldSkip_MatchesInformationalAndUninstallInvocations(
        string[] args, string? processPath, bool expectedSkip)
    {
        bool actual = ReStationInstallerBootstrap.ShouldSkip(args, processPath, "uninstall.exe");
        Assert.Equal(expectedSkip, actual);
    }

    [Fact]
    public void InstallerBootstrap_ComputeSideBySideDir_MapsToCanonicalEngineVersionTrack()
    {
        string reEngineRoot = Path.Combine(_tempRoot, "reEngine");
        string sideBySide = ReStationInstallerBootstrap.ComputeSideBySideDir(reEngineRoot);

        string expected = Path.Combine(_tempRoot, "engines", "reStation", "v" + ReStationInstallerTheme.Version);
        Assert.Equal(Path.GetFullPath(expected), Path.GetFullPath(sideBySide));
    }

    [Fact]
    public void InstallerBootstrap_IsolatedTestWithPreSeededReEngine_RewritesContainedDataAndInstallDirs()
    {
        string isolatedRoot = Path.Combine(_tempRoot, "isolated");
        string reEngineApp = Path.Combine(isolatedRoot, "reEngine", "app");
        Directory.CreateDirectory(reEngineApp);
        string reEngineSdkVersion = typeof(reEngine.Core.Bootstrap.ReEngineBootstrapHelper).Assembly.GetName().Version?.ToString(3) ?? "0.2.1";
        File.WriteAllText(Path.Combine(reEngineApp, "version.txt"), reEngineSdkVersion);

        string? prevIsolated = Environment.GetEnvironmentVariable("APP_ISOLATED_TEST");
        Environment.SetEnvironmentVariable("APP_ISOLATED_TEST", "1");
        try
        {
            int rc = ReStationInstallerBootstrap.Prepare(
                new[] { "--silent", "--data-dir", isolatedRoot },
                typeof(Program).Assembly,
                "uninstall.exe",
                out string[] effective);
            Assert.Equal(0, rc);

            int dataIdx = Array.IndexOf(effective, "--data-dir");
            int installIdx = Array.IndexOf(effective, "--install-dir");
            Assert.True(dataIdx >= 0 && dataIdx + 1 < effective.Length);
            Assert.True(installIdx >= 0 && installIdx + 1 < effective.Length);

            string resolvedData = AppEnvironment.AssertPathContained(effective[dataIdx + 1], isolatedRoot);
            string resolvedInstall = AppEnvironment.AssertPathContained(effective[installIdx + 1], isolatedRoot);
            Assert.Equal(Path.GetFullPath(isolatedRoot), resolvedData);
            Assert.EndsWith(
                Path.Combine("engines", "reStation", "v" + ReStationInstallerTheme.Version),
                resolvedInstall,
                StringComparison.OrdinalIgnoreCase);
            Assert.False(
                resolvedInstall.Contains(Path.Combine("reEngine", "engines"), StringComparison.OrdinalIgnoreCase),
                "Installer target must use peer engines\\reStation\\v0.2.6-hf.1 rather than nested reEngine\\engines\\.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("APP_ISOLATED_TEST", prevIsolated);
        }
    }

    [Fact]
    public void IsolationContainment_RejectsPathTraversalOutOfIsolatedRoot()
    {
        string isolated = Path.Combine(_tempRoot, "iso");
        Directory.CreateDirectory(isolated);
        string escaped = Path.GetFullPath(Path.Combine(isolated, "..", "host_escape.txt"));

        Assert.ThrowsAny<Exception>(() => AppEnvironment.AssertPathContained(escaped, isolated));
        Assert.True(InstallerRuntimeContext.IsSafeIsolatedInstallDirectory(isolated));
    }

    [Fact]
    public void LauncherAndInstallerSources_ContainZeroPiiLiteralHostPaths()
    {
        string srcRoot = FindSrcRoot();
        string[] ownedDirs =
        {
            Path.Combine(srcRoot, "reStation.Launcher"),
            Path.Combine(srcRoot, "reStation.Installer")
        };

        string[] forbiddenPatterns =
        {
            string.Concat("C", @":\Users\"),
            string.Concat("D", @":\Projects\"),
            string.Concat("D", @":\Programs\")
        };

        foreach (string dir in ownedDirs)
        {
            Assert.True(Directory.Exists(dir), $"Missing directory: {dir}");
            foreach (string file in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                string content = File.ReadAllText(file);
                foreach (string pattern in forbiddenPatterns)
                {
                    Assert.DoesNotContain(pattern, content, StringComparison.OrdinalIgnoreCase);
                }
                Assert.False(
                    Regex.IsMatch(content, @"\b(Password|ClientSecret|ApiKey)\s*=", RegexOptions.IgnoreCase),
                    $"Suspicious credential literal in {Path.GetFileName(file)}");
            }
        }
    }

    [Fact]
    public void LauncherProgram_PreservesCallerWorkingDirectoryAndGuardsRedirectedConsole()
    {
        string launcherCs = Path.Combine(FindSrcRoot(), "reStation.Launcher", "Program.cs");
        string content = File.ReadAllText(launcherCs);

        Assert.Contains("WorkingDirectory = Environment.CurrentDirectory", content, StringComparison.Ordinal);
        Assert.DoesNotContain("WorkingDirectory = Path.GetDirectoryName(playerExe)", content, StringComparison.Ordinal);
        Assert.Contains("!redirOut && !redirErr", content, StringComparison.Ordinal);
        Assert.Contains("RedirectStandardError = redirErr", content, StringComparison.Ordinal);
        Assert.Contains("\"Foundation.Launcher\"", content, StringComparison.Ordinal);
        Assert.Contains("\"Foundation.Core\"", content, StringComparison.Ordinal);
        Assert.Contains("AttachJobGuard", content, StringComparison.Ordinal);
        Assert.Contains("[WARN] [LAUNCHER]", content, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerAndLauncher_ExposeCurrentMetadataParameterizedAssemblyAndZeroEmptyCatches()
    {
        Assert.Equal("0.2.6-hf.1", ReStationInstallerTheme.Version);
        Assert.Equal("reStation-Setup-v0.2.6-hf.1-win-x64", typeof(Program).Assembly.GetName().Name);

        string srcRoot = FindSrcRoot();
        string installerCsproj = File.ReadAllText(Path.Combine(srcRoot, "reStation.Installer", "reStation.Installer.csproj"));
        Assert.Contains("<AssemblyName>reStation-Setup-v$(Version)-win-x64</AssemblyName>", installerCsproj, StringComparison.Ordinal);
        Assert.DoesNotContain("Embedded.reEngine-Setup", installerCsproj, StringComparison.Ordinal);
        Assert.Contains("LogicalName=\"Foundation.Retro.dll.gz\"", installerCsproj, StringComparison.Ordinal);
        Assert.Contains("LogicalName=\"reEngine.Core.dll.gz\"", installerCsproj, StringComparison.Ordinal);

        string launcherCsproj = File.ReadAllText(Path.Combine(srcRoot, "reStation.Launcher", "reStation.Launcher.csproj"));
        Assert.Contains("LogicalName=\"Foundation.Launcher.dll\"", launcherCsproj, StringComparison.Ordinal);
        Assert.Contains("LogicalName=\"Foundation.Core.dll\"", launcherCsproj, StringComparison.Ordinal);

        string[] checkedFiles =
        {
            Path.Combine(srcRoot, "reStation.Launcher", "Program.cs"),
            Path.Combine(srcRoot, "reStation.Launcher", "LauncherArgsHelper.cs"),
            Path.Combine(srcRoot, "reStation.Installer", "ReStationInstallerTheme.cs"),
            Path.Combine(srcRoot, "reStation.Installer", "ReStationInstallerBootstrap.cs")
        };
        foreach (string file in checkedFiles)
        {
            string text = File.ReadAllText(file);
            Assert.DoesNotContain("0.1.0", text, StringComparison.Ordinal);
            Assert.False(
                Regex.IsMatch(text, @"catch\s*(?:\([^)]*\))?\s*\{\s*\}"),
                $"Empty catch block found in {Path.GetFileName(file)}");
        }
    }

    [Fact]
    public void UninstallBootstrap_NormalizesBaseDirs_DirectLeaf_DefaultIsolated_AndRejectsEscape()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "restation-uninstall-norm-" + Guid.NewGuid().ToString("N"));
        string outsideDir = Path.GetFullPath(Path.Combine(tempRoot, "..", "restation-outside-" + Guid.NewGuid().ToString("N")));
        string? prevIsolated = Environment.GetEnvironmentVariable("APP_ISOLATED_TEST");
        Environment.SetEnvironmentVariable("APP_ISOLATED_TEST", "1");
        try
        {
            string case1Suite = Path.Combine(tempRoot, "case1-suite");
            string reEngineRoot = Path.Combine(case1Suite, "reEngine");
            string leaf1 = ReStationInstallerBootstrap.ComputeSideBySideDir(reEngineRoot);
            Directory.CreateDirectory(leaf1);
            File.WriteAllText(Path.Combine(leaf1, "reStation.exe"), string.Empty);

            string[] normalizedRoot = ReStationInstallerBootstrap.NormalizeUninstallArgs(
                new[] { "--uninstall", "--data-dir", case1Suite, "--install-dir", reEngineRoot });
            Assert.Equal(leaf1, ReStationInstallerBootstrap.ReadInstallDir(normalizedRoot));
            Assert.Contains("--uninstall", normalizedRoot);

            string isolated2 = Path.Combine(tempRoot, "case2-isolated");
            string leaf2 = ReStationInstallerBootstrap.ComputeSideBySideDir(Path.Combine(isolated2, "reEngine"));
            Directory.CreateDirectory(Path.Combine(leaf2, "app"));
            File.WriteAllText(Path.Combine(leaf2, "app", "install-manifest.json"), "{}");

            string[] normalizedIsolatedBase = ReStationInstallerBootstrap.NormalizeUninstallArgs(
                new[] { "--uninstall", "--data-dir", isolated2, "--install-dir", isolated2 });
            Assert.Equal(leaf2, ReStationInstallerBootstrap.ReadInstallDir(normalizedIsolatedBase));
            Assert.Equal(isolated2, ReStationInstallerBootstrap.ReadValue(normalizedIsolatedBase, "--data-dir"));

            string[] directLeafArgs = ReStationInstallerBootstrap.NormalizeUninstallArgs(
                new[] { "--uninstall", "--install-dir", leaf2 });
            Assert.Equal(leaf2, ReStationInstallerBootstrap.ReadInstallDir(directLeafArgs));

            string[] omittedDirArgs = ReStationInstallerBootstrap.NormalizeUninstallArgs(
                new[] { "--uninstall", "--data-dir", isolated2 });
            Assert.Equal(leaf2, ReStationInstallerBootstrap.ReadInstallDir(omittedDirArgs));

            string[] escapeArgs = { "--uninstall", "--data-dir", isolated2, "--install-dir", outsideDir };
            Assert.Throws<InvalidOperationException>(() => ReStationInstallerBootstrap.NormalizeUninstallArgs(escapeArgs));
            int prepareEscapeCode = ReStationInstallerBootstrap.Prepare(
                escapeArgs, typeof(Program).Assembly, "uninstall.exe", out _);
            Assert.Equal(3, prepareEscapeCode);

            int verifySkipCode = ReStationInstallerBootstrap.Prepare(
                new[] { "--verify" }, typeof(Program).Assembly, "uninstall.exe", out string[] verifyEffective);
            Assert.Equal(0, verifySkipCode);
            Assert.Equal(new[] { "--verify" }, verifyEffective);
        }
        finally
        {
            Environment.SetEnvironmentVariable("APP_ISOLATED_TEST", prevIsolated);
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
            if (Directory.Exists(outsideDir)) Directory.Delete(outsideDir, recursive: true);
        }
    }

    private static string FindSrcRoot()
    {
        string? cur = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(cur))
        {
            string candidate = Path.Combine(cur, "Dev", "src");
            if (Directory.Exists(candidate)) return candidate;
            string direct = Path.Combine(cur, "reStation.Launcher");
            if (Directory.Exists(direct)) return cur;
            cur = Path.GetDirectoryName(cur);
        }
        throw new DirectoryNotFoundException("Could not locate Dev\\src root from test base directory.");
    }
}
