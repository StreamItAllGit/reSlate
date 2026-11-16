// @module: reStation.Core.CliOptions
// @purpose: Fail-closed reStation CLI flag parser delegating root resolution/containment to Foundation.Core.AppEnvironment.
// @depends-on: System.Globalization, Foundation.Core, reStation.Contracts
// @exports: CliOptions
// @owner: devv-cpu-cd (T2)

using System.Globalization;
using Foundation.Core;
using reStation.Contracts;

namespace reStation.Core;

/// <summary>Parsed reStation.Player command line.</summary>
public sealed class CliOptions
{
    public const string HelpText =
        "reStation v" + ReStationEngineInfo.Version + " \u2014 PS1 static recompilation engine\n" +
        "Usage: reStation.exe [--module <module.recomp.json>] [--disc <image> | --preload-media <path>] [options]\n" +
        "  --preload-media <path>      Preload optical image/archive fallback when --disc is omitted\n" +
        "  --wait                      Wait for engine completion when launched synchronously\n" +
        "  --backend opengl|software   --scale 1|2|4|8      --fps-mode original|enhanced\n" +
        "  --target-fps <n|unlocked>   --vsync [on|off]     --no-vsync   --frames <n>\n" +
        "  --dump-screenshot <png>     --data-dir <dir>     --config <path>  --log-dir <dir>  --port <n>\n" +
        "  --jit-bringup  --force-zero-rwx  --profile <json>  --tray-only  --dry-run\n" +
        "  --verify  --open-settings  --enable-logs  --show-fps  --version  --help";

    public IReadOnlyList<string> RawArgs { get; private init; } = Array.Empty<string>();
    public string? ModulePath { get; private init; }
    public string? DiscPath { get; private init; }
    public string? PreloadMediaPath { get; private init; }
    public bool ShouldWait { get; private init; }
    public string? BackendId { get; private init; }
    public int? ResolutionScale { get; private init; }
    public FrameRateMode? FpsMode { get; private init; }
    public int? TargetFps { get; private init; }
    public bool? Vsync { get; private init; }
    public int? MaxFrames { get; private init; }
    public string? DumpScreenshotPath { get; private init; }
    public string? DataDir { get; private init; }
    public string? ConfigPath { get; private init; }
    public string? LogDir { get; private init; }
    public int? Port { get; private init; }
    public bool TrayOnly { get; private init; }
    public bool DryRun { get; private init; }
    public bool VerifyOnly { get; private init; }
    public bool OpenSettings { get; private init; }
    public bool EnableLogs { get; private init; }
    public bool ShowFps { get; private init; }
    public bool JitBringup { get; private init; }
    public bool ForceZeroRwx { get; private init; }
    public string? ProfilePath { get; private init; }
    public bool ShowVersion { get; private init; }
    public bool ShowHelp { get; private init; }

    /// <summary>Parses <paramref name="args"/>; throws <see cref="ArgumentException"/> on unknown flags or invalid values.</summary>
    public static CliOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? module = null, disc = null, preloadMedia = null, backend = null, dump = null, data = null, config = null, log = null, profilePath = null;
        int? scale = null, targetFps = null, frames = null, port = null;
        FrameRateMode? fpsMode = null;
        bool? vsync = null;
        bool trayOnly = false, dryRun = false, verify = false, openSettings = false, enableLogs = false, showFps = false, jitBringup = false, forceZeroRwx = false, shouldWait = false, showVersion = false, help = false;

        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i].Trim();
            switch (arg.ToLowerInvariant())
            {
                case "--module": module = Next(args, ref i, arg); break;
                case "--disc": disc = Next(args, ref i, arg); break;
                case "--preload-media": preloadMedia = NextValidatedPath(args, ref i, arg); break;
                case "--backend":
                    backend = Next(args, ref i, arg).ToLowerInvariant();
                    if (backend == "gl") backend = PsxRenderBackendIds.OpenGl;
                    if (backend is not (PsxRenderBackendIds.OpenGl or PsxRenderBackendIds.Software))
                        throw new ArgumentException($"--backend must be '{PsxRenderBackendIds.OpenGl}' or '{PsxRenderBackendIds.Software}'.");
                    break;
                case "--scale":
                    scale = ParseInt(Next(args, ref i, arg), arg);
                    if (scale is not (1 or 2 or 4 or 8)) throw new ArgumentException("--scale must be 1, 2, 4, or 8.");
                    break;
                case "--fps-mode":
                    fpsMode = Next(args, ref i, arg).ToLowerInvariant() switch
                    {
                        "original" => FrameRateMode.Original,
                        "enhanced" => FrameRateMode.Enhanced,
                        _ => throw new ArgumentException("--fps-mode must be 'original' or 'enhanced'.")
                    };
                    break;
                case "--target-fps":
                    string fpsVal = Next(args, ref i, arg);
                    if (string.Equals(fpsVal, "unlocked", StringComparison.OrdinalIgnoreCase))
                    {
                        targetFps = 0;
                    }
                    else
                    {
                        int parsedFps = ParseInt(fpsVal, arg);
                        if (parsedFps != 0 && parsedFps is < 30 or > 500)
                        {
                            throw new ArgumentException("--target-fps must be 0/'unlocked' or 30..500.");
                        }
                        targetFps = parsedFps;
                    }
                    break;
                case "--vsync":
                    if (i + 1 < args.Count && TryParseOnOff(args[i + 1], out bool parsedVsync))
                    {
                        vsync = parsedVsync;
                        i++;
                    }
                    else
                    {
                        vsync = true;
                    }
                    break;
                case "--no-vsync": vsync = false; break;
                case "--frames": frames = Math.Max(1, ParseInt(Next(args, ref i, arg), arg)); break;
                case "--dump-screenshot": dump = Next(args, ref i, arg); break;
                case "--data-dir": data = Next(args, ref i, arg); break;
                case "--config": config = Next(args, ref i, arg); break;
                case "--log-dir": log = Next(args, ref i, arg); break;
                case "--port":
                    int parsedPort = ParseInt(Next(args, ref i, arg), arg);
                    if (parsedPort is < 1 or > 65535) throw new ArgumentException("--port must be 1..65535.");
                    port = parsedPort;
                    break;
                case "--tray-only": trayOnly = true; break;
                case "--dry-run": dryRun = true; break;
                case "--verify": verify = true; break;
                case "--open-settings": openSettings = true; break;
                case "--enable-logs": enableLogs = true; break;
                case "--show-fps": showFps = true; break;
                case "--jit-bringup": jitBringup = true; break;
                case "--force-zero-rwx": forceZeroRwx = true; break;
                case "--profile": profilePath = NextValidatedPath(args, ref i, arg); break;
                case "--wait": shouldWait = true; break;
                case "--version": showVersion = true; break;
                case "--help" or "-h" or "/?": help = true; break;
                default: throw new ArgumentException($"Unknown option '{arg}'. Pass --help for usage.");
            }
        }

        return new CliOptions
        {
            RawArgs = args.ToArray(), ModulePath = module, DiscPath = disc, PreloadMediaPath = preloadMedia,
            ShouldWait = shouldWait, BackendId = backend, ResolutionScale = scale,
            FpsMode = fpsMode, TargetFps = targetFps, Vsync = vsync, MaxFrames = frames,
            DumpScreenshotPath = dump, DataDir = data, ConfigPath = config, LogDir = log, Port = port,
            TrayOnly = trayOnly, DryRun = dryRun, VerifyOnly = verify, OpenSettings = openSettings,
            EnableLogs = enableLogs, ShowFps = showFps, JitBringup = jitBringup, ForceZeroRwx = forceZeroRwx,
            ProfilePath = profilePath, ShowVersion = showVersion, ShowHelp = help
        };
    }

    /// <summary>Resolves data/config/log roots via <see cref="AppEnvironment"/>.</summary>
    public AppEnvironment CreateEnvironment(string? baseDirectoryOverride = null) =>
        new(ReStationEngineInfo.AppId, RawArgs, baseDirectoryOverride, "settings.json");

    /// <summary>Applies CLI overrides onto loaded settings (in-memory only; never persisted implicitly).</summary>
    public void ApplyOverrides(RecompSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (BackendId is not null) settings.BackendId = BackendId;
        if (ResolutionScale is { } scale) settings.ResolutionScale = scale;
        if (FpsMode is { } mode) settings.FrameRateMode = mode;
        if (TargetFps is { } fps) settings.TargetFps = fps;
        if (Vsync is { } vs) settings.IsVsyncEnabled = vs;
        if (ShowFps) settings.IsFpsOverlayEnabled = true;
        if (EnableLogs || !string.IsNullOrWhiteSpace(LogDir)) settings.ShouldEnableRuntimeLogs = true;
        settings.Clamp();
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

    private static string Next(IReadOnlyList<string> args, ref int i, string flag)
    {
        if (i + 1 >= args.Count || string.IsNullOrWhiteSpace(args[i + 1]))
            throw new ArgumentException($"Option '{flag}' requires a value.");
        string val = args[++i].Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(val))
            throw new ArgumentException($"Option '{flag}' requires a non-empty value.");
        return val;
    }

    private static string NextValidatedPath(IReadOnlyList<string> args, ref int i, string flag)
    {
        string value = Next(args, ref i, flag);
        if (value.StartsWith("--", StringComparison.Ordinal) || value is "-h" or "/?" || value.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || value.Contains('\0'))
            throw new ArgumentException($"Option '{flag}' requires a valid filesystem path, got '{value}'.");
        return value;
    }

    private static int ParseInt(string value, string flag) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : throw new ArgumentException($"Option '{flag}' expects an integer, got '{value}'.");
}
