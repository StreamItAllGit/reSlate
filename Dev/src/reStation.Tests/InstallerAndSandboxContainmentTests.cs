// @module: reStation.Tests.InstallerAndSandboxContainmentTests
// @purpose: Verifies AllowAutoStart=false, --autostart* stripping, ReEngineBootstrapHelper side-by-side --test-mode containment, Launcher sync-wait flags, and Zero-PII source invariants.
// @depends-on: Xunit, System.Text.RegularExpressions, Foundation.Core, reStation.Installer
// @exports: InstallerAndSandboxContainmentTests
// @owner: devv-pkg-test (T8)

using System.Text.RegularExpressions;
using Foundation.Core;
using reStation.Installer;
using Xunit;

namespace reStation.Tests;

public sealed class InstallerAndSandboxContainmentTests : IDisposable
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
        string[] raw = { "--test-mode", "--autostart", "--autostart=1", "--auto-start-on-login", "--silent", "--data-dir", _tempRoot };
        string[] clean = Program.SanitizeArgs(raw);

        Assert.Equal(new[] { "--test-mode", "--silent", "--data-dir", _tempRoot }, clean);
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
    [InlineData(new[] { "--visible-test" }, null, true)]
    [InlineData(new[] { "--silent" }, "C:\\temp\\uninstall.exe", true)]
    [InlineData(new[] { "--test-mode", "--silent" }, "C:\\temp\\reStation-Setup.exe", false)]
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

        string expected = Path.Combine(reEngineRoot, "engines", "reStation", "v" + ReStationInstallerTheme.Version);
        Assert.Equal(Path.GetFullPath(expected), Path.GetFullPath(sideBySide));
    }

    [Fact]
    public void InstallerBootstrap_TestModeWithPreSeededReEngine_RewritesContainedDataAndInstallDirs()
    {
        string sandboxRoot = Path.Combine(_tempRoot, "sandbox");
        string reEngineApp = Path.Combine(sandboxRoot, "reEngine", "app");
        Directory.CreateDirectory(reEngineApp);
        string reEngineSdkVersion = typeof(reEngine.Core.Bootstrap.ReEngineBootstrapHelper).Assembly.GetName().Version?.ToString(3) ?? "0.1.1";
        File.WriteAllText(Path.Combine(reEngineApp, "version.txt"), reEngineSdkVersion);

        int rc = ReStationInstallerBootstrap.Prepare(
            new[] { "--test-mode", "--silent", "--data-dir", sandboxRoot },
            typeof(Program).Assembly,
            "uninstall.exe",
            out string[] effective);
        Assert.Equal(0, rc);

        int dataIdx = Array.IndexOf(effective, "--data-dir");
        int installIdx = Array.IndexOf(effective, "--install-dir");
        Assert.True(dataIdx >= 0 && dataIdx + 1 < effective.Length);
        Assert.True(installIdx >= 0 && installIdx + 1 < effective.Length);

        string resolvedData = SandboxEnvironment.AssertPathContained(effective[dataIdx + 1], sandboxRoot);
        string resolvedInstall = SandboxEnvironment.AssertPathContained(effective[installIdx + 1], sandboxRoot);
        Assert.Equal(Path.GetFullPath(sandboxRoot), resolvedData);
        Assert.EndsWith(
            Path.Combine("reEngine", "engines", "reStation", "v" + ReStationInstallerTheme.Version),
            resolvedInstall,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SandboxContainment_RejectsPathTraversalOutOfSandboxRoot()
    {
        string sandbox = Path.Combine(_tempRoot, "sbx");
        Directory.CreateDirectory(sandbox);
        string escaped = Path.GetFullPath(Path.Combine(sandbox, "..", "host_escape.txt"));

        Assert.ThrowsAny<Exception>(() => SandboxEnvironment.AssertPathContained(escaped, sandbox));
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
    public void InstallerAndLauncher_Expose011MetadataParameterizedAssemblyAndZeroEmptyCatches()
    {
        Assert.Equal("0.1.1", ReStationInstallerTheme.Version);
        Assert.Equal("reStation-Setup-v0.1.1-win-x64", typeof(Program).Assembly.GetName().Name);

        string srcRoot = FindSrcRoot();
        string installerCsproj = File.ReadAllText(Path.Combine(srcRoot, "reStation.Installer", "reStation.Installer.csproj"));
        Assert.Contains("<AssemblyName>reStation-Setup-v$(Version)-win-x64</AssemblyName>", installerCsproj, StringComparison.Ordinal);

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
