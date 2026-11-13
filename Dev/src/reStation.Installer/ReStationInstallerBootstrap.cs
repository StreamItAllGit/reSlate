// @module: reStation.Installer.ReStationInstallerBootstrap
// @purpose: reEngine auto-bootstrap for the reStation installer: sandbox-contained in --test-mode, side-by-side engines\reStation\v<Ver> target via ReEngineBootstrapHelper, zero literal host paths.
// @depends-on: System.Reflection, System.Runtime.InteropServices, reEngine.Core.Bootstrap
// @exports: ReStationInstallerBootstrap
// @owner: devv-pkg-test (T7)

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using reEngine.Core.Bootstrap;

namespace reStation.Installer;

internal static class ReStationInstallerBootstrap
{
    private const int AttachParentProcess = -1;

    /// <summary>Flags that never install anything and therefore never bootstrap reEngine.</summary>
    private static readonly string[] SkipFlags =
    {
        "--help", "-h", "/?", "help", "--version", "-v", "--verify", "--dry-run", "--uninstall", "--visible-test"
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

    /// <summary>Side-by-side <c>&lt;reEngineDir&gt;\engines\reStation\v0.1.0</c> target computed strictly via the reEngine SDK.</summary>
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
    internal static int Prepare(string[] args, Assembly payloadAssembly, string uninstallerExeName, out string[] effectiveArgs)
    {
        effectiveArgs = args;
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
                try { AttachConsole(AttachParentProcess); } catch (DllNotFoundException) { }
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
            try { if (Directory.Exists(bootstrapDir)) Directory.Delete(bootstrapDir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
