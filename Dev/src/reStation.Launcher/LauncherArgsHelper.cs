// @module: reStation.Launcher.LauncherArgsHelper
// @purpose: Pure BCL + Foundation.Core CLI sync-wait, caller-CWD path resolution, AllowAutoStart stripping, --reengine-dir/--profile handling, and JobObject guard for reSlate.
// @depends-on: System, System.Collections.Generic, System.Diagnostics, System.IO, Foundation.Core
// @exports: LauncherArgsHelper

using System.Diagnostics;
using Foundation.Core;

namespace reStation.Launcher;

internal static class LauncherArgsHelper
{
    internal const string DefaultVersion = "0.3.1";
    internal const string CanonicalEngineFolder = "v" + DefaultVersion;
    internal const string PrimaryEngineId = "reSlate";
    internal const string LegacyEngineId = "reStation";
    internal const string PrimaryPlayerExecutable = "reSlate.Player.exe";
    internal const string LegacyPlayerExecutable = "reStation.Player.exe";

    internal static readonly string[] SyncWaitFlags =
    {
        "--wait", "--verify", "--jit-bringup", "--tray-only", "--dry-run", "--doctor",
        "--version", "-v", "--help", "-h", "/?", "help"
    };

    internal static readonly HashSet<string> PathValueFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "--module", "--disc", "--preload-media", "--dump-screenshot", "--data-dir",
        "--config", "--log-dir", "--profile", "--reengine-dir", "--suite-dir",
        "--save-dir", "--fixture", "--dump-audio"
    };

    internal static readonly HashSet<string> NonPathValueFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "--backend", "--require-backend", "--scale", "--fps-mode", "--target-fps",
        "--frames", "--execution-tier", "--port", "--automation-port", "--automation-token", "--vsync"
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
            if ((PathValueFlags.Contains(key) || NonPathValueFlags.Contains(key)) &&
                !hasInline && ConsumesNextToken(key, i, args))
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
            else if (NonPathValueFlags.Contains(key) && ConsumesNextToken(key, i, args))
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
                AddEngineCandidates(candidates, Path.Combine(suiteParent, "engines"));
            }
            AddEngineCandidates(candidates, Path.Combine(fullRe, "engines"));
        }

        if (!string.IsNullOrWhiteSpace(suiteDir))
        {
            string fullSuite = Path.GetFullPath(suiteDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            AddEngineCandidates(candidates, Path.Combine(fullSuite, "engines"));
        }

        string colocated = Path.Combine(normalizedRoot, "app", PrimaryPlayerExecutable);
        candidates.Add(colocated);
        candidates.Add(Path.Combine(normalizedRoot, "app", LegacyPlayerExecutable));

        foreach (string envName in new[] { "RESLATE_INSTALL_DIR", "RESTATION_INSTALL_DIR" })
        {
            string? envDir = Environment.GetEnvironmentVariable(envName);
            if (!string.IsNullOrWhiteSpace(envDir))
            {
                string fullEnv = Path.GetFullPath(envDir.Trim());
                candidates.Add(Path.Combine(fullEnv, "app", PrimaryPlayerExecutable));
                candidates.Add(Path.Combine(fullEnv, "app", LegacyPlayerExecutable));
            }
        }

        string defaultPrimary = ResolveCanonicalDefaultEngineDir();
        candidates.Add(Path.Combine(defaultPrimary, "app", PrimaryPlayerExecutable));
        candidates.Add(Path.Combine(defaultPrimary, "app", LegacyPlayerExecutable));

        string defaultLegacy = ResolveCanonicalEngineDirFor(LegacyEngineId);
        candidates.Add(Path.Combine(defaultLegacy, "app", PrimaryPlayerExecutable));
        candidates.Add(Path.Combine(defaultLegacy, "app", LegacyPlayerExecutable));

        foreach (string c in candidates)
        {
            if (File.Exists(c)) return c;
        }
        return colocated;
    }

    internal static string ResolveCanonicalDefaultEngineDir() => ResolveCanonicalEngineDirFor(PrimaryEngineId);

    private static string ResolveCanonicalEngineDirFor(string engineId)
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
        {
            local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local");
        }
        return Path.Combine(local, "Programs", "StreamItAll", "reEngine", "engines", engineId, CanonicalEngineFolder);
    }

    private static void AddEngineCandidates(List<string> candidates, string enginesBaseDir)
    {
        foreach (string engineId in new[] { PrimaryEngineId, LegacyEngineId })
        {
            string appDir = Path.Combine(enginesBaseDir, engineId, CanonicalEngineFolder, "app");
            candidates.Add(Path.Combine(appDir, PrimaryPlayerExecutable));
            candidates.Add(Path.Combine(appDir, LegacyPlayerExecutable));
        }
    }

    private static bool ConsumesNextToken(string key, int index, string[] args)
    {
        if (index + 1 >= args.Length) return false;
        if (!string.Equals(key, "--vsync", StringComparison.OrdinalIgnoreCase)) return true;
        string next = (args[index + 1] ?? string.Empty).Trim();
        return next.Equals("on", StringComparison.OrdinalIgnoreCase) ||
               next.Equals("off", StringComparison.OrdinalIgnoreCase) ||
               next.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               next.Equals("false", StringComparison.OrdinalIgnoreCase) ||
               next.Equals("1", StringComparison.Ordinal) ||
               next.Equals("0", StringComparison.Ordinal);
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
