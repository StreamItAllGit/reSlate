// @module: reStation.Installer.ReStationInstallerBootstrap
// @purpose: Side-by-side engines\reStation\v0.2.0 target resolution, --reengine-dir/--suite-dir support, and fail-closed --test-mode containment without nested Setup bloat.
// @depends-on: System, System.Collections.Generic, System.IO, System.Linq, System.Reflection, Foundation.Core, Foundation.Installer, reEngine.Core.Bootstrap
// @exports: ReStationInstallerBootstrap

using System.Reflection;
using Foundation.Core;
using Foundation.Installer;
using reEngine.Core.Bootstrap;

namespace reStation.Installer;

internal static class ReStationInstallerBootstrap
{
    private static readonly string[] SkipExactArgs =
        { "--help", "-h", "/?", "help", "--version", "-v", "--verify", "--dry-run", "--uninstall", "--visible-test" };

    internal static bool ShouldSkip(IReadOnlyList<string> args, string? processPath, string uninstallerExeName) =>
        string.Equals(Path.GetFileName(processPath), uninstallerExeName, StringComparison.OrdinalIgnoreCase) ||
        args.Any(a => SkipExactArgs.Any(s => string.Equals(a.Trim(), s, StringComparison.OrdinalIgnoreCase)));

    internal static string? ReadValue(IReadOnlyList<string> args, string flag)
    {
        for (int i = 0; i < args.Count; i++)
        {
            string trimmed = args[i].Trim();
            if (string.Equals(trimmed, flag, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
            {
                string val = args[i + 1].Trim();
                return string.IsNullOrWhiteSpace(val) ? null : val;
            }
            if (trimmed.StartsWith(flag + "=", StringComparison.OrdinalIgnoreCase))
            {
                string val = trimmed[(flag.Length + 1)..].Trim();
                return string.IsNullOrWhiteSpace(val) ? null : val;
            }
        }
        return null;
    }

    internal static string? ReadInstallDir(IReadOnlyList<string> args) =>
        ReadValue(args, "--install-dir") ?? ReadValue(args, "--target-dir");

    internal static string ComputeSideBySideDir(string reEngineInstallDir) =>
        ReEngineBootstrapHelper.BuildEngineVersionInstallDir(
            reEngineInstallDir, ReStationInstallerTheme.EngineId, ReStationInstallerTheme.Version);

    internal static bool HasDirectInstallation(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
        string full = Path.GetFullPath(dir);
        return File.Exists(Path.Combine(full, ReStationInstallerTheme.MainExecutable)) ||
               File.Exists(Path.Combine(full, "app", "reStation.Player.exe")) ||
               File.Exists(Path.Combine(full, "app", "install-manifest.json")) ||
               File.Exists(Path.Combine(full, "install-manifest.json"));
    }

    internal static string? TryResolveSideBySideLeaf(string baseDir)
    {
        if (string.IsNullOrWhiteSpace(baseDir)) return null;
        string fullBase = Path.GetFullPath(baseDir);
        string[] candidates =
        {
            Path.Combine(fullBase, "engines", ReStationInstallerTheme.EngineId, $"v{ReStationInstallerTheme.Version}"),
            ComputeSideBySideDir(fullBase),
            ComputeSideBySideDir(Path.Combine(fullBase, ReStationInstallerTheme.SuiteName))
        };
        return candidates.FirstOrDefault(HasDirectInstallation);
    }

    internal static string? TryResolveSandboxRoot(IReadOnlyList<string> args)
    {
        string? raw = ReadValue(args, "--sandbox-root")
            ?? ReadValue(args, "--data-dir")
            ?? Environment.GetEnvironmentVariable("RESTATION_DATA_DIR")
            ?? Environment.GetEnvironmentVariable("DATA_DIR")
            ?? Environment.GetEnvironmentVariable("APP_HOME");
        if (!string.IsNullOrWhiteSpace(raw)) return Path.GetFullPath(raw.Trim());

        string? explicitInstall = ReadInstallDir(args);
        return !string.IsNullOrWhiteSpace(explicitInstall) && InstallerSandboxContext.IsSafeTestInstallDirectory(explicitInstall)
            ? Path.GetFullPath(explicitInstall)
            : null;
    }

    internal static string[] NormalizeUninstallArgs(IReadOnlyList<string> args) =>
        NormalizeUninstallArgs(args, out _);

    internal static string[] NormalizeUninstallArgs(IReadOnlyList<string> args, out string? resolvedDefaultLeafForEnv)
    {
        resolvedDefaultLeafForEnv = null;
        bool testMode = args.Any(a => string.Equals(a.Trim(), "--test-mode", StringComparison.OrdinalIgnoreCase));
        string? sandboxRoot = testMode ? TryResolveSandboxRoot(args) : null;
        string? rawInstall = ReadInstallDir(args);
        string? rawReEngine = ReadValue(args, "--reengine-dir");

        if (testMode && string.IsNullOrWhiteSpace(sandboxRoot))
            throw new InvalidOperationException("Fail-Closed Sandbox Violation: --test-mode --uninstall requires explicit --data-dir.");

        string targetLeaf;
        if (!string.IsNullOrWhiteSpace(rawInstall))
        {
            string full = Path.GetFullPath(rawInstall);
            targetLeaf = HasDirectInstallation(full) ? full : (TryResolveSideBySideLeaf(full) ?? full);
        }
        else if (!string.IsNullOrWhiteSpace(rawReEngine))
        {
            targetLeaf = ComputeSideBySideDir(Path.GetFullPath(rawReEngine));
        }
        else if (testMode && sandboxRoot is not null)
        {
            string defRe = ReEngineBootstrapHelper.ResolveDefaultReEngineInstallDir(null, sandboxRoot, true);
            targetLeaf = TryResolveSideBySideLeaf(sandboxRoot) ?? ComputeSideBySideDir(defRe);
        }
        else
        {
            targetLeaf = ReStationInstallerTheme.ResolveDefaultVersionInstallDir();
            resolvedDefaultLeafForEnv = targetLeaf;
        }

        if (testMode && sandboxRoot is not null) targetLeaf = SandboxEnvironment.AssertPathContained(targetLeaf, sandboxRoot);

        var updated = new List<string>(args.Count + 2);
        bool replaced = false;
        for (int i = 0; i < args.Count; i++)
        {
            string t = args[i].Trim();
            if ((string.Equals(t, "--install-dir", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(t, "--target-dir", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Count)
            {
                updated.Add("--install-dir");
                updated.Add(targetLeaf);
                i++;
                replaced = true;
            }
            else
            {
                updated.Add(args[i]);
            }
        }

        if (!replaced && (!string.IsNullOrWhiteSpace(rawInstall) || testMode || !string.IsNullOrWhiteSpace(rawReEngine)))
        {
            updated.Add("--install-dir");
            updated.Add(targetLeaf);
        }
        return updated.ToArray();
    }

    internal static string[] RewriteContainedArgs(
        string[] args, bool testMode, string? sandboxRoot, string sideBySideDir, string? explicitInstall)
    {
        var updated = new List<string>(args.Length + 4);
        bool hasData = false;
        for (int i = 0; i < args.Length; i++)
        {
            string t = args[i].Trim();
            if (string.Equals(t, "--data-dir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length && sandboxRoot is not null)
            {
                updated.Add("--data-dir");
                updated.Add(sandboxRoot);
                i++;
                hasData = true;
            }
            else if ((string.Equals(t, "--install-dir", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(t, "--target-dir", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
            {
                string val = Path.GetFullPath(args[i + 1].Trim());
                updated.Add("--install-dir");
                updated.Add(sandboxRoot is not null ? SandboxEnvironment.AssertPathContained(val, sandboxRoot) : val);
                i++;
            }
            else
            {
                updated.Add(args[i]);
            }
        }

        if (testMode && !hasData && sandboxRoot is not null) { updated.Add("--data-dir"); updated.Add(sandboxRoot); }
        if (string.IsNullOrWhiteSpace(explicitInstall))
        {
            updated.Add("--install-dir");
            updated.Add(sandboxRoot is not null ? SandboxEnvironment.AssertPathContained(sideBySideDir, sandboxRoot) : sideBySideDir);
        }
        return updated.ToArray();
    }

    internal static string? ResolveExplicitReEngineDir(IReadOnlyList<string> args)
    {
        string? reEngineDir = ReadValue(args, "--reengine-dir");
        if (!string.IsNullOrWhiteSpace(reEngineDir)) return Path.GetFullPath(reEngineDir);

        string? suiteDir = ReadValue(args, "--suite-dir");
        if (string.IsNullOrWhiteSpace(suiteDir)) return null;
        string fullSuite = Path.GetFullPath(suiteDir);
        return File.Exists(Path.Combine(fullSuite, "reEngine.exe"))
            ? fullSuite
            : Path.Combine(fullSuite, ReStationInstallerTheme.SuiteName);
    }

    private static string? FindSiblingExternalReEngineInstaller(bool testMode, string? sandboxRoot)
    {
        string? selfPath = Environment.ProcessPath;
        string? selfDir = string.IsNullOrWhiteSpace(selfPath) ? null : Path.GetDirectoryName(Path.GetFullPath(selfPath));
        if (string.IsNullOrWhiteSpace(selfDir) || !Directory.Exists(selfDir)) return null;

        string? candidate = Directory.EnumerateFiles(selfDir, "reEngine-Setup-*.exe", SearchOption.TopDirectoryOnly)
            .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (candidate is not null && testMode && sandboxRoot is not null)
        {
            string fullRoot = Path.GetFullPath(sandboxRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(candidate).StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) return null;
        }
        return candidate;
    }

    internal static int Prepare(string[] args, Assembly installerAssembly, string uninstallerExeName, out string[] effectiveArgs)
    {
        _ = installerAssembly;
        effectiveArgs = args;
        bool isUninstall = string.Equals(Path.GetFileName(Environment.ProcessPath), uninstallerExeName, StringComparison.OrdinalIgnoreCase) ||
                           args.Any(a => string.Equals(a.Trim(), "--uninstall", StringComparison.OrdinalIgnoreCase));
        if (isUninstall)
        {
            try
            {
                effectiveArgs = NormalizeUninstallArgs(args, out string? defaultLeaf);
                if (!string.IsNullOrWhiteSpace(defaultLeaf) && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ReStationInstallerTheme.InstallDirEnvironmentVariable)))
                    Environment.SetEnvironmentVariable(ReStationInstallerTheme.InstallDirEnvironmentVariable, defaultLeaf);
                return 0;
            }
            catch (InvalidOperationException ex) { Console.Error.WriteLine(ex.Message); return 3; }
        }

        if (ShouldSkip(args, Environment.ProcessPath, uninstallerExeName)) return 0;

        bool testMode = args.Any(a => string.Equals(a.Trim(), "--test-mode", StringComparison.OrdinalIgnoreCase));
        string? explicitInstall = ReadInstallDir(args);
        try
        {
            string? sandboxRoot = null;
            if (testMode)
            {
                sandboxRoot = TryResolveSandboxRoot(args) ?? throw new InvalidOperationException(
                    "Fail-Closed Sandbox Violation: --test-mode requires explicit --data-dir or contained sandbox path.");
            }
            string? explicitReEngineDir = ResolveExplicitReEngineDir(args);
            if (testMode && sandboxRoot is not null && !string.IsNullOrWhiteSpace(explicitReEngineDir))
                explicitReEngineDir = SandboxEnvironment.AssertPathContained(explicitReEngineDir, sandboxRoot);

            string targetReEngineDir = ReEngineBootstrapHelper.ResolveDefaultReEngineInstallDir(explicitReEngineDir, sandboxRoot, testMode);
            string? siblingInstaller = FindSiblingExternalReEngineInstaller(testMode, sandboxRoot);
            if (!string.IsNullOrWhiteSpace(siblingInstaller))
            {
                ReEngineBootstrapResult res = ReEngineBootstrapHelper
                    .EnsureReEngineInstalledAsync(siblingInstaller, targetReEngineDir, !testMode, testMode, sandboxRoot)
                    .GetAwaiter().GetResult();
                if (!res.Success) { Console.Error.WriteLine(res.DiagnosticMessage); return 1; }
                targetReEngineDir = res.ReEngineInstallDir;
            }

            string sideBySide = ComputeSideBySideDir(targetReEngineDir);
            effectiveArgs = RewriteContainedArgs(args, testMode, sandboxRoot, sideBySide, explicitInstall);
            return 0;
        }
        catch (InvalidOperationException ex) { Console.Error.WriteLine(ex.Message); return 3; }
    }
}
