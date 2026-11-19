// @module: reStation.Core.CliOptions
// @purpose: Fail-closed reSlate CLI flag parser delegating root resolution/containment to Foundation.Core.AppEnvironment.
// @depends-on: System.Globalization, Foundation.Core, reStation.Contracts
// @exports: CliOptions

using System.Globalization;
using Foundation.Core;
using reStation.Contracts;

namespace reStation.Core;

/// <summary>Parsed reSlate.Player command line.</summary>
public sealed class CliOptions
{
    public const int DeferredBackendExitCode = 6;

    public const string HelpText =
        "reSlate v" + ReStationEngineInfo.Version + " \u2014 PS1 static recompilation engine\n" +
        "Usage: reSlate.exe [--module <module.recomp.json>] [--disc <image> | --preload-media <path>] [options]\n" +
        "  --preload-media <path>      Preload optical image/archive fallback when --disc is omitted\n" +
        "  --wait                      Wait for engine completion when launched synchronously\n" +
        "  --backend opengl|software   --require-backend <id>  --scale 1|2|4|8\n" +
        "  --fps-mode original|enhanced  --target-fps <n|unlocked>  --vsync [on|off]  --no-vsync\n" +
        "  --frames <n>  --execution-tier <mode>  --dump-screenshot <png>  --dump-audio <wav>\n" +
        "  --data-dir <dir>  --config <path>  --log-dir <dir>  --save-dir <dir>  --fixture <path>\n" +
        "  --port <n>  --automation-port <n>  --automation-token <tok>  --jit-bringup  --force-zero-rwx\n" +
        "  --profile <json>  --tray-only  --dry-run  --verify  --open-settings  --enable-logs\n" +
        "  --show-fps  --doctor  --version  --help";

    public IReadOnlyList<string> RawArgs { get; private init; } = Array.Empty<string>();
    public string? ModulePath { get; private init; }
    public string? DiscPath { get; private init; }
    public string? PreloadMediaPath { get; private init; }
    public bool ShouldWait { get; private init; }
    public string? BackendId { get; private init; }
    public string? RequireBackendId { get; private init; }
    public int? ResolutionScale { get; private init; }
    public FrameRateMode? FpsMode { get; private init; }
    public int? TargetFps { get; private init; }
    public bool? Vsync { get; private init; }
    public int? MaxFrames { get; private init; }
    public string? ExecutionTierMode { get; private init; }
    public string? DumpScreenshotPath { get; private init; }
    public string? DumpAudioPath { get; private init; }
    public string? DataDir { get; private init; }
    public string? ConfigPath { get; private init; }
    public string? LogDir { get; private init; }
    public string? SaveDir { get; private init; }
    public string? FixturePath { get; private init; }
    public string? ReEngineDir { get; private init; }
    public string? SuiteDir { get; private init; }
    public int? Port { get; private init; }
    public int? AutomationPort { get; private init; }
    public string? AutomationToken { get; private init; }
    public bool TrayOnly { get; private init; }
    public bool DryRun { get; private init; }
    public bool VerifyOnly { get; private init; }
    public bool OpenSettings { get; private init; }
    public bool EnableLogs { get; private init; }
    public bool ShowFps { get; private init; }
    public bool JitBringup { get; private init; }
    public bool ForceZeroRwx { get; private init; }
    public string? ProfilePath { get; private init; }
    public bool RunDoctor { get; private init; }
    public bool ShowVersion { get; private init; }
    public bool ShowHelp { get; private init; }

    /// <summary>Returns true when <see cref="RequireBackendId"/> targets a v0.3.1-deferred backend (<c>vulkan</c> or <c>parallelCpu</c>).</summary>
    public bool IsDeferredBackendRequired(out string? deferredBackendId)
    {
        if (RequireBackendId is PsxRenderBackendIds.Vulkan or PsxRenderBackendIds.ParallelCpu)
        {
            deferredBackendId = RequireBackendId;
            return true;
        }
        deferredBackendId = null;
        return false;
    }

    /// <summary>Parses <paramref name="args"/>; throws <see cref="ArgumentException"/> on unknown flags or invalid values.</summary>
    public static CliOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? module = null, disc = null, preloadMedia = null, backend = null, requireBackend = null;
        string? dump = null, dumpAudio = null, data = null, config = null, log = null, saveDir = null;
        string? fixture = null, profilePath = null, reEngineDir = null, suiteDir = null;
        string? executionTier = null, automationToken = null;
        int? scale = null, targetFps = null, frames = null, port = null, automationPort = null;
        FrameRateMode? fpsMode = null;
        bool? vsync = null;
        bool trayOnly = false, dryRun = false, verify = false, openSettings = false, enableLogs = false;
        bool showFps = false, jitBringup = false, forceZeroRwx = false, shouldWait = false;
        bool runDoctor = false, showVersion = false, help = false;

        for (int i = 0; i < args.Count; i++)
        {
            string raw = args[i].Trim();
            (string key, string? inline) = SplitOption(raw);
            switch (key)
            {
                case "--module": module = NextValue(args, ref i, key, inline); break;
                case "--disc": disc = NextValue(args, ref i, key, inline); break;
                case "--preload-media": preloadMedia = NextValidatedPath(args, ref i, key, inline); break;
                case "--backend":
                    string rawBackend = NextValue(args, ref i, key, inline).ToLowerInvariant();
                    backend = rawBackend == "gl" ? PsxRenderBackendIds.OpenGl : rawBackend;
                    if (backend is not (PsxRenderBackendIds.OpenGl or PsxRenderBackendIds.Software))
                        throw new ArgumentException($"--backend must be '{PsxRenderBackendIds.OpenGl}' or '{PsxRenderBackendIds.Software}'.");
                    break;
                case "--require-backend":
                    requireBackend = NormalizeRequireBackend(NextValue(args, ref i, key, inline));
                    break;
                case "--scale":
                    scale = ParseInt(NextValue(args, ref i, key, inline), key);
                    if (scale is not (1 or 2 or 4 or 8)) throw new ArgumentException("--scale must be 1, 2, 4, or 8.");
                    break;
                case "--fps-mode":
                    fpsMode = NextValue(args, ref i, key, inline).ToLowerInvariant() switch
                    {
                        "original" => FrameRateMode.Original,
                        "enhanced" => FrameRateMode.Enhanced,
                        _ => throw new ArgumentException("--fps-mode must be 'original' or 'enhanced'.")
                    };
                    break;
                case "--target-fps":
                    string fpsVal = NextValue(args, ref i, key, inline);
                    if (string.Equals(fpsVal, "unlocked", StringComparison.OrdinalIgnoreCase))
                        targetFps = 0;
                    else
                    {
                        int parsedFps = ParseInt(fpsVal, key);
                        if (parsedFps != 0 && parsedFps is < 30 or > 500)
                            throw new ArgumentException("--target-fps must be 0/'unlocked' or 30..500.");
                        targetFps = parsedFps;
                    }
                    break;
                case "--vsync":
                    if (inline is not null)
                    {
                        if (!TryParseOnOff(inline, out bool inlineVsync))
                            throw new ArgumentException($"Option '--vsync' expects on/off, got '{inline}'.");
                        vsync = inlineVsync;
                    }
                    else if (i + 1 < args.Count && TryParseOnOff(args[i + 1], out bool parsedVsync))
                    {
                        vsync = parsedVsync;
                        i++;
                    }
                    else vsync = true;
                    break;
                case "--no-vsync": vsync = false; break;
                case "--frames": frames = Math.Max(1, ParseInt(NextValue(args, ref i, key, inline), key)); break;
                case "--execution-tier":
                    string rawTier = NextValue(args, ref i, key, inline);
                    if (!ReStationExecutionTierModes.TryNormalize(rawTier, out string normTier))
                        throw new ArgumentException($"Unknown --execution-tier '{rawTier}'.");
                    executionTier = normTier;
                    break;
                case "--dump-screenshot": dump = NextValue(args, ref i, key, inline); break;
                case "--dump-audio": dumpAudio = NextValidatedPath(args, ref i, key, inline); break;
                case "--data-dir": data = NextValue(args, ref i, key, inline); break;
                case "--config": config = NextValue(args, ref i, key, inline); break;
                case "--log-dir": log = NextValue(args, ref i, key, inline); break;
                case "--save-dir": saveDir = NextValidatedPath(args, ref i, key, inline); break;
                case "--fixture": fixture = NextValidatedPath(args, ref i, key, inline); break;
                case "--reengine-dir": reEngineDir = NextValidatedPath(args, ref i, key, inline); break;
                case "--suite-dir": suiteDir = NextValidatedPath(args, ref i, key, inline); break;
                case "--port":
                    int parsedPort = ParseInt(NextValue(args, ref i, key, inline), key);
                    if (parsedPort is < 1 or > 65535) throw new ArgumentException("--port must be 1..65535.");
                    port = parsedPort;
                    break;
                case "--automation-port":
                    int parsedAutoPort = ParseInt(NextValue(args, ref i, key, inline), key);
                    if (parsedAutoPort is < 0 or > 65535) throw new ArgumentException("--automation-port must be 0..65535.");
                    automationPort = parsedAutoPort;
                    break;
                case "--automation-token": automationToken = NextValue(args, ref i, key, inline); break;
                case "--tray-only": trayOnly = true; break;
                case "--dry-run": dryRun = true; break;
                case "--verify": verify = true; break;
                case "--open-settings": openSettings = true; break;
                case "--enable-logs": enableLogs = true; break;
                case "--show-fps": showFps = true; break;
                case "--jit-bringup": jitBringup = true; break;
                case "--force-zero-rwx": forceZeroRwx = true; break;
                case "--profile": profilePath = NextValidatedPath(args, ref i, key, inline); break;
                case "--doctor": runDoctor = true; break;
                case "--wait": shouldWait = true; break;
                case "--version": showVersion = true; break;
                case "--help" or "-h" or "/?": help = true; break;
                default: throw new ArgumentException($"Unknown option '{raw}'. Pass --help for usage.");
            }
        }

        return new CliOptions
        {
            RawArgs = args.ToArray(), ModulePath = module, DiscPath = disc, PreloadMediaPath = preloadMedia,
            ShouldWait = shouldWait, BackendId = backend, RequireBackendId = requireBackend,
            ResolutionScale = scale, FpsMode = fpsMode, TargetFps = targetFps, Vsync = vsync, MaxFrames = frames,
            ExecutionTierMode = executionTier, DumpScreenshotPath = dump, DumpAudioPath = dumpAudio,
            DataDir = data, ConfigPath = config, LogDir = log, SaveDir = saveDir, FixturePath = fixture,
            ReEngineDir = reEngineDir, SuiteDir = suiteDir, Port = port,
            AutomationPort = automationPort, AutomationToken = automationToken,
            TrayOnly = trayOnly, DryRun = dryRun, VerifyOnly = verify, OpenSettings = openSettings,
            EnableLogs = enableLogs, ShowFps = showFps, JitBringup = jitBringup, ForceZeroRwx = forceZeroRwx,
            ProfilePath = profilePath, RunDoctor = runDoctor, ShowVersion = showVersion, ShowHelp = help
        };
    }

    /// <summary>Resolves data/config/log roots via <see cref="AppEnvironment"/> with canonical <c>config/settings.json</c>.</summary>
    public AppEnvironment CreateEnvironment(string? baseDirectoryOverride = null) =>
        new(ReStationEngineInfo.AppId, RawArgs, baseDirectoryOverride, SettingsMigrationHelper.CanonicalRelativePath);

    /// <summary>Applies CLI overrides onto loaded settings (in-memory only; never persisted implicitly).</summary>
    public void ApplyOverrides(RecompSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (BackendId is not null) settings.BackendId = BackendId;
        else if (RequireBackendId is PsxRenderBackendIds.OpenGl or PsxRenderBackendIds.Software)
            settings.BackendId = RequireBackendId;
        if (ResolutionScale is { } scale) settings.ResolutionScale = scale;
        if (FpsMode is { } mode) settings.FrameRateMode = mode;
        if (TargetFps is { } fps) settings.TargetFps = fps;
        if (Vsync is { } vs) settings.IsVsyncEnabled = vs;
        if (ExecutionTierMode is not null) settings.ExecutionTierMode = ExecutionTierMode;
        else if (JitBringup) settings.ExecutionTierMode = ReStationExecutionTierModes.JitBringup;
        if (ForceZeroRwx) settings.ForceZeroRwx = true;
        if (ShowFps) settings.IsFpsOverlayEnabled = true;
        if (EnableLogs || !string.IsNullOrWhiteSpace(LogDir)) settings.ShouldEnableRuntimeLogs = true;
        settings.Clamp();
    }

    private static string NormalizeRequireBackend(string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "gl" or PsxRenderBackendIds.OpenGl => PsxRenderBackendIds.OpenGl,
        PsxRenderBackendIds.Software => PsxRenderBackendIds.Software,
        PsxRenderBackendIds.Vulkan => PsxRenderBackendIds.Vulkan,
        "parallelcpu" or "parallel-cpu" or "parallel_cpu" => PsxRenderBackendIds.ParallelCpu,
        _ => throw new ArgumentException($"Unknown --require-backend '{raw}'; expected opengl, software, vulkan, or parallelCpu.")
    };

    private static (string Key, string? InlineValue) SplitOption(string raw)
    {
        if (raw.StartsWith("--", StringComparison.Ordinal))
        {
            int eq = raw.IndexOf('=');
            if (eq > 2) return (raw[..eq].ToLowerInvariant(), raw[(eq + 1)..]);
        }
        return (raw.ToLowerInvariant(), null);
    }

    private static bool TryParseOnOff(string raw, out bool enabled)
    {
        switch (raw.Trim().ToLowerInvariant())
        {
            case "on" or "true" or "1": enabled = true; return true;
            case "off" or "false" or "0": enabled = false; return true;
            default: enabled = false; return false;
        }
    }

    private static string NextValue(IReadOnlyList<string> args, ref int i, string flag, string? inlineValue)
    {
        string raw = inlineValue ?? (i + 1 < args.Count ? args[++i] : throw new ArgumentException($"Option '{flag}' requires a value."));
        string val = raw.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(val))
            throw new ArgumentException($"Option '{flag}' requires a non-empty value.");
        return val;
    }

    private static string NextValidatedPath(IReadOnlyList<string> args, ref int i, string flag, string? inlineValue)
    {
        string value = NextValue(args, ref i, flag, inlineValue);
        if (value.StartsWith("--", StringComparison.Ordinal) || value is "-h" or "/?" || value.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || value.Contains('\0'))
            throw new ArgumentException($"Option '{flag}' requires a valid filesystem path, got '{value}'.");
        return value;
    }

    private static int ParseInt(string value, string flag) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : throw new ArgumentException($"Option '{flag}' expects an integer, got '{value}'.");
}
