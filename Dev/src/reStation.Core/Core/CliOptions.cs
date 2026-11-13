// @module: reStation.Core.CliOptions
// @purpose: Fail-closed reStation CLI flag parser delegating root resolution/containment to Foundation.Core.SandboxEnvironment.
// @depends-on: System.Globalization, Foundation.Core, reStation.Contracts
// @exports: CliOptions
// @owner: devv-cpu-cd (T2) — public surface T1-locked; extend additively only.

using System.Globalization;
using Foundation.Core;
using reStation.Contracts;

namespace reStation.Core;

/// <summary>Parsed reStation.Player command line.</summary>
public sealed class CliOptions
{
    public const string HelpText =
        "reStation v" + ReStationEngineInfo.Version + " — PS1 static recompilation engine\n" +
        "Usage: reStation.exe [--module <module.recomp.json>] [--disc <image>] [options]\n" +
        "  --backend opengl|software   --scale 1|2|4|8      --fps-mode original|enhanced\n" +
        "  --target-fps <n|0>          --vsync | --no-vsync --frames <n>   --fixture <spec.json>\n" +
        "  --data-dir <dir>  --config <path>  --log-dir <dir>  --test-mode  --headless  --visible-test\n" +
        "  --verify  --open-settings  --enable-logs  --show-fps  --dump-screenshot <png>  --help";

    public IReadOnlyList<string> RawArgs { get; private init; } = Array.Empty<string>();
    public string? ModulePath { get; private init; }
    public string? DiscPath { get; private init; }
    public string? BackendId { get; private init; }
    public int? ResolutionScale { get; private init; }
    public FrameRateMode? FpsMode { get; private init; }
    public int? TargetFps { get; private init; }
    public bool? Vsync { get; private init; }
    public int? MaxFrames { get; private init; }
    public string? FixturePath { get; private init; }
    public string? DumpScreenshotPath { get; private init; }
    public string? DataDir { get; private init; }
    public string? ConfigPath { get; private init; }
    public string? LogDir { get; private init; }
    public bool TestMode { get; private init; }
    public bool Headless { get; private init; }
    public bool VisibleTest { get; private init; }
    public bool VerifyOnly { get; private init; }
    public bool OpenSettings { get; private init; }
    public bool EnableLogs { get; private init; }
    public bool ShowFps { get; private init; }
    public bool ShowHelp { get; private init; }

    /// <summary>Parses <paramref name="args"/>; throws <see cref="ArgumentException"/> on unknown flags or invalid values.</summary>
    public static CliOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? module = null, disc = null, backend = null, fixture = null, dump = null, data = null, config = null, log = null;
        int? scale = null, targetFps = null, frames = null;
        FrameRateMode? fpsMode = null;
        bool? vsync = null;
        bool test = false, headless = false, visible = false, verify = false, openSettings = false, enableLogs = false, showFps = false, help = false;

        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i].Trim();
            switch (arg.ToLowerInvariant())
            {
                case "--module": module = Next(args, ref i, arg); break;
                case "--disc": disc = Next(args, ref i, arg); break;
                case "--backend":
                    backend = Next(args, ref i, arg).ToLowerInvariant();
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
                case "--target-fps": targetFps = Math.Max(0, ParseInt(Next(args, ref i, arg), arg)); break;
                case "--vsync": vsync = true; break;
                case "--no-vsync": vsync = false; break;
                case "--frames": frames = Math.Max(1, ParseInt(Next(args, ref i, arg), arg)); break;
                case "--fixture": fixture = Next(args, ref i, arg); break;
                case "--dump-screenshot": dump = Next(args, ref i, arg); break;
                case "--data-dir": data = Next(args, ref i, arg); break;
                case "--config": config = Next(args, ref i, arg); break;
                case "--log-dir": log = Next(args, ref i, arg); break;
                case "--test-mode": test = true; break;
                case "--headless": headless = true; break;
                case "--visible-test": visible = true; break;
                case "--verify": verify = true; break;
                case "--open-settings": openSettings = true; break;
                case "--enable-logs": enableLogs = true; break;
                case "--show-fps": showFps = true; break;
                case "--help" or "-h" or "/?": help = true; break;
                default: throw new ArgumentException($"Unknown option '{arg}'. Pass --help for usage.");
            }
        }

        return new CliOptions
        {
            RawArgs = args.ToArray(), ModulePath = module, DiscPath = disc, BackendId = backend, ResolutionScale = scale,
            FpsMode = fpsMode, TargetFps = targetFps, Vsync = vsync, MaxFrames = frames, FixturePath = fixture,
            DumpScreenshotPath = dump, DataDir = data, ConfigPath = config, LogDir = log, TestMode = test, Headless = headless,
            VisibleTest = visible, VerifyOnly = verify, OpenSettings = openSettings, EnableLogs = enableLogs, ShowFps = showFps, ShowHelp = help
        };
    }

    /// <summary>Resolves data/config/log roots via SandboxEnvironment; in --test-mode asserts config/logs stay inside the data root.</summary>
    public SandboxEnvironment CreateSandbox(string? baseDirectoryOverride = null)
    {
        var sandbox = new SandboxEnvironment(ReStationEngineInfo.AppId, RawArgs, baseDirectoryOverride, "settings.json");
        if (sandbox.TestMode && string.IsNullOrWhiteSpace(DataDir))
            throw new InvalidOperationException("Fail-Closed Sandbox Violation: --test-mode requires an explicit --data-dir.");
        if (sandbox.TestMode && string.IsNullOrWhiteSpace(ConfigPath)) sandbox.AssertSandboxPathContained(sandbox.ConfigFilePath);
        return sandbox;
    }

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
        if (EnableLogs || TestMode) settings.ShouldEnableRuntimeLogs = true;
        settings.Clamp();
    }

    private static string Next(IReadOnlyList<string> args, ref int i, string flag)
    {
        if (i + 1 >= args.Count || string.IsNullOrWhiteSpace(args[i + 1]))
            throw new ArgumentException($"Option '{flag}' requires a value.");
        return args[++i].Trim().Trim('"');
    }

    private static int ParseInt(string value, string flag) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : throw new ArgumentException($"Option '{flag}' expects an integer, got '{value}'.");
}
