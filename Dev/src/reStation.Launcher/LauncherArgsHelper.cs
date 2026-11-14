// @module: reStation.Launcher.LauncherArgsHelper
// @purpose: Pure CLI sync-wait detection, relative-path resolution (--preload-media), and NoInlining JobObjectGuard supervision helper.
// @depends-on: System.Diagnostics, System.Runtime.CompilerServices, Foundation.Core
// @exports: LauncherArgsHelper
// @owner: devv-pkg (Wave 2 Slice 3)

using System.Diagnostics;
using System.Runtime.CompilerServices;
using Foundation.Core;

namespace reStation.Launcher;

internal static class LauncherArgsHelper
{
    /// <summary>Flags whose runs must finish synchronously before the launcher returns.</summary>
    internal static readonly string[] SyncWaitFlags =
    {
        "--wait", "--visible-test", "--fixture", "--input-script", "--verify", "--test-mode", "--headless", "--version", "--help", "-h", "/?"
    };

    /// <summary>CLI options followed by a filesystem path resolved relative to the caller working directory.</summary>
    internal static readonly HashSet<string> PathValueFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "--module", "--disc", "--preload-media", "--fixture", "--input-script", "--dump-screenshot", "--data-dir", "--config", "--log-dir"
    };

    /// <summary>True when any argument requests synchronous CLI execution or explicit wait propagation.</summary>
    public static bool RequiresSyncWait(string[]? args) =>
        args is not null && args.Any(a => !string.IsNullOrWhiteSpace(a) && SyncWaitFlags.Contains(a.Trim(), StringComparer.OrdinalIgnoreCase));

    /// <summary>Resolves relative path values for <see cref="PathValueFlags"/> against <paramref name="baseDir"/>.</summary>
    public static string[] ResolveRelativePathArgs(string[]? args, string baseDir)
    {
        if (args is null || args.Length == 0) return Array.Empty<string>();
        string root = string.IsNullOrWhiteSpace(baseDir) ? Environment.CurrentDirectory : Path.GetFullPath(baseDir);
        var resolved = (string[])args.Clone();
        for (int i = 0; i < resolved.Length - 1; i++)
        {
            string flag = resolved[i]?.Trim() ?? string.Empty;
            if (!PathValueFlags.Contains(flag)) continue;
            string val = (resolved[i + 1] ?? string.Empty).Trim().Trim('"');
            if (!string.IsNullOrWhiteSpace(val) && !Path.IsPathRooted(val))
                resolved[i + 1] = Path.GetFullPath(val, root);
            i++;
        }
        return resolved;
    }

    /// <summary>
    /// Attaches a Windows <see cref="JobObjectGuard"/> (<c>KILL_ON_JOB_CLOSE</c>) to <paramref name="process"/>.
    /// Kept <see cref="MethodImplOptions.NoInlining"/> so <c>Foundation.Core</c> loads only after <c>AssemblyResolve</c> registers.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static JobObjectGuard? AttachJobGuard(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        var guard = new JobObjectGuard();
        if (!guard.IsSupported)
        {
            guard.Dispose();
            return null;
        }
        if (!guard.TryAttachProcess(process))
        {
            Trace.WriteLine($"[WARN] [LAUNCHER] JobObjectGuard attachment skipped for PID {process.Id}.");
            guard.Dispose();
            return null;
        }
        return guard;
    }
}
