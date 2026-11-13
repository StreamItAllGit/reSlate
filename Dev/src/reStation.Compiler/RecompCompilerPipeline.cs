// @module: reStation.Compiler.RecompCompilerPipeline
// @purpose: Modular .recomp lifter pipeline: CLI option model, input validation, per-EXE IRecompModule + IRecompGameModule emission, module.recomp.json.
// @depends-on: reStation.Contracts
// @exports: RecompCompilerOptions, RecompCompileResult, RecompCompilerPipeline
// @owner: devv-compiler (T5) — T1 skeleton; option/result records + Run signature LOCKED; MIPS lifting/chunk emission lands in T5.

using reStation.Contracts;

namespace reStation.Compiler;

/// <summary>Validated compiler inputs (exactly one of <see cref="DiscPath"/> / <see cref="GameDataDir"/>).</summary>
public sealed record RecompCompilerOptions(
    string? DiscPath,
    string? GameDataDir,
    string DecompDir,
    string OutDir,
    string ModuleId,
    string SerialNumber,
    string Title);

/// <summary>Compiler outcome with every emitted file path.</summary>
public sealed record RecompCompileResult(bool Success, string ManifestPath, IReadOnlyList<string> EmittedFiles, string Message);

public sealed class RecompCompilerPipeline
{
    public const string UsageText =
        "Usage: reStation.Compiler (--disc <image> | --gamedata-dir <dir>) --decomp-dir <dir> --out-dir <dir> " +
        "--module-id <id> --serial <serialNumber> --title <title>";

    public RecompCompilerPipeline(RecompCompilerOptions options) => Options = options ?? throw new ArgumentNullException(nameof(options));

    public RecompCompilerOptions Options { get; }

    /// <summary>Parses compiler CLI flags; throws <see cref="ArgumentException"/> on missing/unknown values.</summary>
    public static RecompCompilerOptions ParseArgs(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string[] known = { "--disc", "--gamedata-dir", "--decomp-dir", "--out-dir", "--module-id", "--serial", "--title" };
        for (int i = 0; i < args.Count; i++)
        {
            string flag = args[i].Trim();
            if (!known.Contains(flag, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException($"Unknown option '{flag}'. {UsageText}");
            if (i + 1 >= args.Count || string.IsNullOrWhiteSpace(args[i + 1])) throw new ArgumentException($"Option '{flag}' requires a value.");
            map[flag] = args[++i].Trim().Trim('"');
        }

        map.TryGetValue("--disc", out string? disc);
        map.TryGetValue("--gamedata-dir", out string? gamedata);
        if ((disc is null) == (gamedata is null)) throw new ArgumentException("Specify exactly one of --disc or --gamedata-dir.");
        string Require(string key) => map.TryGetValue(key, out string? v) ? v : throw new ArgumentException($"Missing required option '{key}'.");
        return new RecompCompilerOptions(disc, gamedata, Require("--decomp-dir"), Require("--out-dir"), Require("--module-id"), Require("--serial"), Require("--title"));
    }

    /// <summary>Validates inputs, emits generated sources + module.recomp.json into <see cref="RecompCompilerOptions.OutDir"/>.</summary>
    public RecompCompileResult Run(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        string? inputRoot = Options.GameDataDir ?? Options.DiscPath;
        if (inputRoot is null || (!Directory.Exists(inputRoot) && !File.Exists(inputRoot)))
            return new RecompCompileResult(false, string.Empty, Array.Empty<string>(), $"Input media not found: '{inputRoot}'.");
        if (!Directory.Exists(Options.DecompDir))
            return new RecompCompileResult(false, string.Empty, Array.Empty<string>(), $"Decomp directory not found: '{Options.DecompDir}'.");

        string outDir = Path.GetFullPath(Options.OutDir);
        Directory.CreateDirectory(outDir);
        string assemblyName = $"{Options.ModuleId}.recomp";
        var manifest = new ReStationModuleManifest
        {
            ModuleId = Options.ModuleId,
            Title = Options.Title,
            DisplayName = Options.Title,
            SerialNumber = Options.SerialNumber,
            EntryAssembly = assemblyName + ".dll",
            ModuleTypeName = $"{assemblyName}.GameModule"
        };
        string manifestPath = Path.Combine(outDir, "module.recomp.json");
        manifest.SaveToFile(manifestPath);
        return new RecompCompileResult(true, manifestPath, new[] { manifestPath }, $"Emitted manifest for module '{Options.ModuleId}'.");
    }
}
