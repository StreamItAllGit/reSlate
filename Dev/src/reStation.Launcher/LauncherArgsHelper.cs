// @module: reStation.Launcher.LauncherArgsHelper
// @purpose: Pure BCL + Foundation.Core CLI sync-wait, caller-CWD path resolution, AllowAutoStart stripping, --reengine-dir/--profile handling, and JobObject guard.
// @depends-on: System, System.Collections.Generic, System.Diagnostics, System.IO, Foundation.Core
// @exports: LauncherArgsHelper

using System.Diagnostics;
using Foundation.Core;

namespace reStation.Launcher;

internal static class LauncherArgsHelper
{
    internal const string DefaultVersion = "0.2.6";
    internal const string CanonicalEngineFolder = "v" + DefaultVersion;

    internal static readonly string[] SyncWaitFlags =
    {
        "--wait", "--visible-test", "--fixture", "--input-script", "--verify", "--test-mode",
        "--headless", "--jit-bringup", "--version", "-v", "--help", "-h", "/?", "help"
    };

    internal static readonly HashSet<string> PathValueFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "--module", "--disc", "--preload-media", "--fixture", "--input-script",
        "--dump-screenshot", "--data-dir", "--config", "--log-dir",
        "--profile", "--reengine-dir", "--suite-dir", "--sandbox-root"
    };

    private static readonly HashSet<string> NonPathValueFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "--backend", "--scale", "--fps-mode", "--target-fps", "--frames", "--execution-tier"
    };

    internal static bool RequiresSyncWait(string[]? args)
    {
        if (args is null || args.Length == 0) return false;
        for (int i = 0; i < args.Length; i++)
        {
            string trimmed = (args[i] ?? string.Empty).Trim();
            if (trimmed.Length == 0) continue;
            (string key, bool hasInline) = SplitOption(trimmed);
            if (SyncWaitFlags.Any(f => string.Equals(f, key, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
            if ((PathValueFlags.Contains(key) || NonPathValueFlags.Contains(key)) && !hasInline && i + 1 < args.Length)
            {
                i++;
            }
        }
        return false;
    }

    internal static string[] ResolveRelativePathArgs(string[]? args, string callerWorkingDirectory)
    {
        if (args is null || args.Length == 0) return Array.Empty<string>();
        string baseDir = string.IsNullOrWhiteSpace(callerWorkingDirectory)
            ? Environment.CurrentDirectory
            : Path.GetFullPath(callerWorkingDirectory);

        var resolved = new string[args.Length];
        for (int i = 0; i < args.Length; i++)
        {
            string raw = args[i] ?? string.Empty;
            string trimmed = raw.Trim();
            (string key, bool hasInline) = SplitOption(trimmed);

            if (hasInline)
            {
                string val = trimmed[(key.Length + 1)..].Trim().Trim('"');
                resolved[i] = PathValueFlags.Contains(key) && val.Length > 0
                    ? $"{key}={Path.GetFullPath(val, baseDir)}"
                    : raw;
                continue;
            }

            resolved[i] = raw;
            if (PathValueFlags.Contains(key) && i + 1 < args.Length)
            {
                string nextRaw = args[i + 1] ?? string.Empty;
                string unquoted = nextRaw.Trim().Trim('"');
                resolved[i + 1] = unquoted.Length > 0 ? Path.GetFullPath(unquoted, baseDir) : nextRaw;
                i++;
            }
            else if (NonPathValueFlags.Contains(key) && i + 1 < args.Length)
            {
                resolved[i + 1] = args[i + 1] ?? string.Empty;
                i++;
            }
        }
        return resolved;
    }

    internal static string[] SanitizeAndPrepareForwardArgs(
        string[] resolvedArgs,
        out string? reEngineDir,
        out string? suiteDir)
    {
        reEngineDir = null;
        suiteDir = null;
        var forwarded = new List<string>(resolvedArgs.Length);

        for (int i = 0; i < resolvedArgs.Length; i++)
        {
            string trimmed = (resolvedArgs[i] ?? string.Empty).Trim();
            if (trimmed.StartsWith("--autostart", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("--auto-start", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            (string key, bool hasInline) = SplitOption(trimmed);
            if (TryConsumeLocatorOption(resolvedArgs, ref i, key, hasInline, trimmed, ref reEngineDir, ref suiteDir))
            {
                continue;
            }

            forwarded.Add(resolvedArgs[i]);
        }
        return forwarded.ToArray();
    }

    internal static string ResolveTargetPlayerExecutable(string rootDir, string? reEngineDir, string? suiteDir)
    {
        string normalizedRoot = Path.GetFullPath(rootDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(reEngineDir))
        {
            string fullRe = Path.GetFullPath(reEngineDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string? suiteParent = Path.GetDirectoryName(fullRe);
            if (!string.IsNullOrWhiteSpace(suiteParent))
            {
                candidates.Add(Path.Combine(suiteParent, "engines", "reStation", CanonicalEngineFolder, "app", "reStation.Player.exe"));
            }
            candidates.Add(Path.Combine(fullRe, "engines", "reStation", CanonicalEngineFolder, "app", "reStation.Player.exe"));
        }

        if (!string.IsNullOrWhiteSpace(suiteDir))
        {
            string fullSuite = Path.GetFullPath(suiteDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            candidates.Add(Path.Combine(fullSuite, "engines", "reStation", CanonicalEngineFolder, "app", "reStation.Player.exe"));
        }

        string colocated = Path.Combine(normalizedRoot, "app", "reStation.Player.exe");
        candidates.Add(colocated);

        string? envDir = Environment.GetEnvironmentVariable("RESTATION_INSTALL_DIR");
        if (!string.IsNullOrWhiteSpace(envDir))
        {
            candidates.Add(Path.Combine(Path.GetFullPath(envDir.Trim()), "app", "reStation.Player.exe"));
        }

        candidates.Add(Path.Combine(ResolveCanonicalDefaultEngineDir(), "app", "reStation.Player.exe"));

        foreach (string c in candidates)
        {
            if (File.Exists(c)) return c;
        }
        return colocated;
    }

    internal static string ResolveCanonicalDefaultEngineDir()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
        {
            local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local");
        }
        return Path.Combine(local, "Programs", "StreamItAll", "reEngine", "engines", "reStation", CanonicalEngineFolder);
    }

    internal static JobObjectGuard? AttachJobGuard(Process? proc)
    {
        if (proc is null || proc.HasExited) return null;
        var guard = new JobObjectGuard();
        if (!guard.IsSupported || !guard.TryAttachProcess(proc))
        {
            guard.Dispose();
            return null;
        }
        return guard;
    }

    private static bool TryConsumeLocatorOption(
        string[] args,
        ref int index,
        string key,
        bool hasInline,
        string trimmed,
        ref string? reEngineDir,
        ref string? suiteDir)
    {
        if (!string.Equals(key, "--reengine-dir", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(key, "--suite-dir", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string? value = null;
        if (hasInline)
        {
            value = trimmed[(key.Length + 1)..].Trim().Trim('"');
        }
        else if (index + 1 < args.Length)
        {
            value = (args[++index] ?? string.Empty).Trim().Trim('"');
        }

        if (!string.IsNullOrWhiteSpace(value))
        {
            if (string.Equals(key, "--reengine-dir", StringComparison.OrdinalIgnoreCase)) reEngineDir = value;
            else suiteDir = value;
        }
        return true;
    }

    private static (string Key, bool HasInlineValue) SplitOption(string trimmed)
    {
        if (trimmed.StartsWith("--", StringComparison.Ordinal))
        {
            int eq = trimmed.IndexOf('=');
            if (eq > 2) return (trimmed[..eq], true);
        }
        return (trimmed, false);
    }
}
