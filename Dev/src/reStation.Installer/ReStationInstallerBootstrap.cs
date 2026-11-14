// @module: reStation.Installer.ReStationInstallerBootstrap
// @purpose: reEngine auto-bootstrap for the reStation installer: sandbox-contained in --test-mode, side-by-side engines\reStation\v<Ver> target via ReEngineBootstrapHelper, zero literal host paths.
// @depends-on: System.Diagnostics, System.Reflection, System.Runtime.CompilerServices, System.Runtime.InteropServices, reEngine.Core.Bootstrap
// @exports: ReStationInstallerBootstrap
// @owner: devv-pkg (Wave 2 Slice 3)

using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using reEngine.Core.Bootstrap;

namespace reStation.Installer;

internal static class ReStationInstallerBootstrap
{
    private const int AttachParentProcess = -1;

    /// <summary>Flags that only print help/version or verify/dry-run without modifying installations.</summary>
    private static readonly string[] InformationalFlags =
    {
        "--help", "-h", "/?", "help", "--version", "-v", "--verify", "--dry-run"
    };

    /// <summary>Flags that never install anything and therefore never bootstrap reEngine.</summary>
    private static readonly string[] SkipFlags =
    {
        "--help", "-h", "/?", "help", "--version", "-v", "--verify", "--dry-run", "--uninstall", "--visible-test"
    };

    private static readonly string[] DirectInstallMarkers =
    {
        "reStation.exe",
        @"app\reStation.exe",
        "reStation.Player.exe",
        @"app\reStation.Player.exe",
        "install-manifest.json",
        @"app\install-manifest.json",
        ".install_manifest.json",
        @"app\.install_manifest.json",
        "uninstall.exe",
        "reStation-Uninstall.exe"
    };

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    /// <summary>True when the run is informational/verification-only or the installed uninstaller copy is executing.</summary>
    internal static bool ShouldSkip(IReadOnlyList<string> args, string? processPath = null, string? uninstallerExeName = null)
    {
        if (args.Any(a => SkipFlags.Contains(a.Trim(), StringComparer.OrdinalIgnoreCase))) return true;
        return !string.IsNullOrWhiteSpace(processPath) && !string.IsNullOrWhiteSpace(uninstallerExeName) &&
               string.Equals(Path.GetFileName(processPath), uninstallerExeName, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool HasFlag(IReadOnlyList<string> args, string flag) =>
        args.Any(a => string.Equals(a.Trim(), flag, StringComparison.OrdinalIgnoreCase));

    /// <summary>Reads <c>--flag value</c> or <c>--flag=value</c>; null when absent or valueless.</summary>
    internal static string? ReadValue(IReadOnlyList<string> args, string flag)
    {
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i].Trim();
            if (a.StartsWith(flag + "=", StringComparison.OrdinalIgnoreCase)) return a[(flag.Length + 1)..].Trim().Trim('"');
            if (string.Equals(a, flag, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count && !string.IsNullOrWhiteSpace(args[i + 1]))
                return args[i + 1].Trim().Trim('"');
        }
        return null;
    }

    internal static string? ReadInstallDir(IReadOnlyList<string> args) =>
        ReadValue(args, "--install-dir") ?? ReadValue(args, "--target-dir");

    /// <summary>--test-mode containment root: explicit --data-dir, else explicit --install-dir, else <paramref name="tempDir"/>.</summary>
    internal static string ResolveSandboxRoot(IReadOnlyList<string> args, string tempDir) =>
        Path.GetFullPath(ReadValue(args, "--data-dir") ?? ReadInstallDir(args) ?? tempDir);

    /// <summary>Drops any existing data/install-dir options and appends the contained <c>--data-dir</c> + <c>--install-dir</c> pair.</summary>
    internal static string[] RewriteContainedArgs(IReadOnlyList<string> args, string sandboxRoot, string sideBySideDir)
    {
        var result = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i].Trim();
            bool pair = a.Equals("--data-dir", StringComparison.OrdinalIgnoreCase) ||
                        a.Equals("--install-dir", StringComparison.OrdinalIgnoreCase) ||
                        a.Equals("--target-dir", StringComparison.OrdinalIgnoreCase);
            bool joined = a.StartsWith("--data-dir=", StringComparison.OrdinalIgnoreCase) ||
                          a.StartsWith("--install-dir=", StringComparison.OrdinalIgnoreCase) ||
                          a.StartsWith("--target-dir=", StringComparison.OrdinalIgnoreCase);
            if (pair) { i++; continue; }
            if (joined) continue;
            result.Add(args[i]);
        }
        result.Add("--data-dir");
        result.Add(sandboxRoot);
        result.Add("--install-dir");
        result.Add(sideBySideDir);
        return result.ToArray();
    }

    /// <summary>Extracts the embedded reEngine setup EXE into <paramref name="destDir"/>; null when this build does not embed it.</summary>
    internal static string? ExtractBootstrapExe(Assembly assembly, string destDir)
    {
        using Stream? stream = assembly.GetManifestResourceStream(ReStationInstallerTheme.ReEngineBootstrapResourceName);
        if (stream is null) return null;
        Directory.CreateDirectory(destDir);
        string exePath = Path.Combine(destDir, "reEngine-Setup.exe");
        using var fs = new FileStream(exePath, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.CopyTo(fs);
        return exePath;
    }

    /// <summary>Side-by-side <c>&lt;reEngineDir&gt;\engines\reStation\v&lt;Version&gt;</c> (<c>v0.1.x</c> track) target computed strictly via the reEngine SDK.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static string ComputeSideBySideDir(string reEngineInstallDir) =>
        ReEngineBootstrapHelper.BuildEngineVersionInstallDir(reEngineInstallDir, ReStationInstallerTheme.EngineId, ReStationInstallerTheme.Version);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(bool Success, string ReEngineDir, string Message)> EnsureReEngineAsync(string? bundledExe, bool testMode, string? sandboxRoot)
    {
        ReEngineBootstrapResult result = await ReEngineBootstrapHelper
            .EnsureReEngineInstalledAsync(bundledExe, null, true, testMode, sandboxRoot).ConfigureAwait(false);
        string dir = result.Success ? result.ReEngineInstallDir : ReEngineBootstrapHelper.ResolveDefaultReEngineInstallDir(null, sandboxRoot, testMode);
        return (result.Success, dir, result.DiagnosticMessage);
    }

    /// <summary>
    /// Ensures reEngine is installed and computes the side-by-side target. Returns 0 to continue; <paramref name="effectiveArgs"/> carries
    /// the rewritten contained arguments in --test-mode, while live mode seeds the default install directory via the process-local variable.
    /// </summary>
    /// <summary>True when <paramref name="dir"/> is already a direct leaf installation directory.</summary>
    internal static bool HasDirectInstallation(string dir) =>
        !string.IsNullOrWhiteSpace(dir) &&
        Directory.Exists(dir) &&
        DirectInstallMarkers.Any(marker => File.Exists(Path.Combine(dir, marker)));

    /// <summary>True when the current invocation is an uninstall run (<c>--uninstall</c> or uninstaller binary).</summary>
    internal static bool IsUninstallInvocation(IReadOnlyList<string> args, string? processPath = null, string? uninstallerExeName = null) =>
        HasFlag(args, "--uninstall") ||
        (!string.IsNullOrWhiteSpace(processPath) && !string.IsNullOrWhiteSpace(uninstallerExeName) &&
         string.Equals(Path.GetFileName(processPath), uninstallerExeName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Probes <c>&lt;base&gt;\engines\reStation\v&lt;Version&gt;</c> then <c>&lt;base&gt;\reEngine\engines\reStation\v&lt;Version&gt;</c>.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static string? TryResolveSideBySideLeaf(string baseDir)
    {
        if (string.IsNullOrWhiteSpace(baseDir)) return null;
        string fullBase = Path.GetFullPath(baseDir);
        string[] candidates =
        {
            ComputeSideBySideDir(fullBase),
            ComputeSideBySideDir(Path.Combine(fullBase, "reEngine"))
        };
        return candidates.FirstOrDefault(HasDirectInstallation)
            ?? candidates.FirstOrDefault(Directory.Exists);
    }

    /// <summary>Replaces any <c>--install-dir</c> or <c>--target-dir</c> option with <paramref name="resolvedLeaf"/>.</summary>
    internal static string[] RewriteInstallDirArg(IReadOnlyList<string> args, string resolvedLeaf)
    {
        var result = new List<string>(args.Count + 2);
        for (int i = 0; i < args.Count; i++)
        {
            string raw = args[i];
            string trimmed = raw.Trim();
            if (trimmed.Equals("--install-dir", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("--target-dir", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Count && !args[i + 1].TrimStart().StartsWith("--", StringComparison.Ordinal)) i++;
                continue;
            }
            if (trimmed.StartsWith("--install-dir=", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("--target-dir=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            result.Add(raw);
        }
        result.Add("--install-dir");
        result.Add(resolvedLeaf);
        return result.ToArray();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string ResolveDefaultSideBySideDir(string? sandboxRoot, bool testMode) =>
        ComputeSideBySideDir(ReEngineBootstrapHelper.ResolveDefaultReEngineInstallDir(null, sandboxRoot, testMode));

    /// <summary>Normalizes base-directory <c>--install-dir</c> arguments during <c>--uninstall</c> to the side-by-side leaf directory.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static string[] NormalizeUninstallArgs(IReadOnlyList<string> args) =>
        NormalizeUninstallArgs(args, out _);

    /// <summary>Normalizes base-directory <c>--install-dir</c> arguments during <c>--uninstall</c> and computes any fallback default leaf for environment wiring.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static string[] NormalizeUninstallArgs(IReadOnlyList<string> args, out string? resolvedDefaultLeafForEnv)
    {
        resolvedDefaultLeafForEnv = null;
        string[] original = args as string[] ?? args.ToArray();
        bool testMode = HasFlag(args, "--test-mode");
        string? sandboxRoot = testMode
            ? ResolveSandboxRoot(args, Path.Combine(Path.GetTempPath(), "restation-setup-sandbox"))
            : null;
        string? rawInstall = ReadInstallDir(args);

        if (!string.IsNullOrWhiteSpace(rawInstall))
        {
            string fullRaw = Path.GetFullPath(rawInstall);
            if (sandboxRoot is not null) global::Foundation.Core.SandboxEnvironment.AssertPathContained(fullRaw, sandboxRoot);
            if (HasDirectInstallation(fullRaw)) return original;

            string? leaf = TryResolveSideBySideLeaf(fullRaw);
            if (leaf is null) return original;
            if (sandboxRoot is not null) global::Foundation.Core.SandboxEnvironment.AssertPathContained(leaf, sandboxRoot);
            return RewriteInstallDirArg(original, leaf);
        }

        if (sandboxRoot is not null)
        {
            string candidate = ResolveDefaultSideBySideDir(sandboxRoot, testMode: true);
            if (Directory.Exists(candidate))
            {
                global::Foundation.Core.SandboxEnvironment.AssertPathContained(candidate, sandboxRoot);
                return RewriteInstallDirArg(original, candidate);
            }
            return original;
        }

        string defaultLeaf = ResolveDefaultSideBySideDir(sandboxRoot: null, testMode: false);
        if (Directory.Exists(defaultLeaf)) resolvedDefaultLeafForEnv = defaultLeaf;
        return original;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int TryPrepareUninstall(string[] args, out string[] effectiveArgs)
    {
        effectiveArgs = args;
        try
        {
            effectiveArgs = NormalizeUninstallArgs(args, out string? defaultLeaf);
            string? processDir = Path.GetDirectoryName(Environment.ProcessPath);
            bool runningInsideInstall = !string.IsNullOrWhiteSpace(processDir) && HasDirectInstallation(processDir);
            if (!runningInsideInstall &&
                !string.IsNullOrWhiteSpace(defaultLeaf) &&
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ReStationInstallerTheme.InstallDirEnvironmentVariable)))
            {
                Environment.SetEnvironmentVariable(ReStationInstallerTheme.InstallDirEnvironmentVariable, defaultLeaf);
            }
            return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[reStation Installer] FAIL: {ex.Message}");
            return 3;
        }
    }

    internal static int Prepare(string[] args, Assembly payloadAssembly, string uninstallerExeName, out string[] effectiveArgs)
    {
        effectiveArgs = args;
        if (args.Any(a => InformationalFlags.Contains(a.Trim(), StringComparer.OrdinalIgnoreCase))) return 0;
        if (IsUninstallInvocation(args, Environment.ProcessPath, uninstallerExeName))
        {
            return TryPrepareUninstall(args, out effectiveArgs);
        }
        if (ShouldSkip(args, Environment.ProcessPath, uninstallerExeName)) return 0;

        bool testMode = HasFlag(args, "--test-mode");
        string? sandboxRoot = testMode ? ResolveSandboxRoot(args, Path.Combine(Path.GetTempPath(), "restation-setup-sandbox")) : null;
        string bootstrapDir = testMode
            ? Path.Combine(sandboxRoot!, "_bootstrap")
            : Path.Combine(Path.GetTempPath(), "restation-bootstrap-" + Guid.NewGuid().ToString("N"));
        try
        {
            string? bundledExe = ExtractBootstrapExe(payloadAssembly, bootstrapDir);
            (bool success, string reEngineDir, string message) = EnsureReEngineAsync(bundledExe, testMode, sandboxRoot).GetAwaiter().GetResult();
            if (!success)
            {
                try
                {
                    AttachConsole(AttachParentProcess);
                }
                catch (DllNotFoundException ex)
                {
                    Trace.WriteLine($"[WARN] [INSTALLER] AttachConsole unavailable: {ex.Message}");
                }
                Console.Error.WriteLine($"Setup error: reEngine bootstrap failed: {message}");
                return 1;
            }

            string sideBySide = ComputeSideBySideDir(reEngineDir);
            if (testMode)
            {
                effectiveArgs = RewriteContainedArgs(args, sandboxRoot!, sideBySide);
            }
            else if (ReadInstallDir(args) is null &&
                     string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ReStationInstallerTheme.InstallDirEnvironmentVariable)))
            {
                Environment.SetEnvironmentVariable(ReStationInstallerTheme.InstallDirEnvironmentVariable, sideBySide);
            }
            return 0;
        }
        finally
        {
            try
            {
                if (Directory.Exists(bootstrapDir)) Directory.Delete(bootstrapDir, recursive: true);
            }
            catch (IOException ex)
            {
                Trace.WriteLine($"[WARN] [INSTALLER] Bootstrap temp cleanup skipped (IO): {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                Trace.WriteLine($"[WARN] [INSTALLER] Bootstrap temp cleanup skipped (Access): {ex.Message}");
            }
        }
    }
}
