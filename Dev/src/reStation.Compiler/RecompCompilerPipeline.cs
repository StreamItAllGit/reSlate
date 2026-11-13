// @module: reStation.Compiler.RecompCompilerPipeline
// @purpose: Modular .recomp lifter pipeline: CLI option parsing, PS-X EXE discovery, R3000A lifting, chunk emission, manifest + optional DLL build.
// @depends-on: reStation.Contracts, reStation.Compiler.DiscExeLoader, reStation.Compiler.ExeScanHelpers, reStation.Compiler.FunctionChunkEmitter, reStation.Compiler.ModuleManifestEmitter
// @exports: RecompCompilerOptions, RecompCompileResult, RecompCompilerPipeline
// @owner: devv-compiler (T5)

using System.Buffers.Binary;

namespace reStation.Compiler;

/// <summary>Validated compiler inputs (at least one of <see cref="DiscPath"/>, <see cref="GameDataDir"/>, or <see cref="ExePath"/>).</summary>
public sealed record RecompCompilerOptions(
    string? DiscPath,
    string? GameDataDir,
    string DecompDir,
    string OutDir,
    string ModuleId,
    string SerialNumber,
    string Title)
{
    public string? ExePath { get; init; }
    public string? FunctionsJsonPath { get; init; }
    public string Version { get; init; } = "0.1.0";
    public bool CompileDll { get; init; }
}

/// <summary>Compiler outcome with every emitted file path.</summary>
public sealed record RecompCompileResult(bool Success, string ManifestPath, IReadOnlyList<string> EmittedFiles, string Message);

public sealed class RecompCompilerPipeline
{
    public const string UsageText =
        "Usage: reStation.Compiler (--disc <image> | --gamedata-dir <dir> | --exe-path <exe>) " +
        "[--decomp-dir <dir>] [--functions-json <file>] --out-dir <dir> " +
        "--module-id <id> --serial <serialNumber> --title <title> [--version <semver>] [--compile-dll]";

    private static readonly HashSet<string> ValueFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "--disc", "--gamedata-dir", "--exe-path", "--decomp-dir", "--functions-json",
        "--out-dir", "--module-id", "--serial", "--title", "--version"
    };

    public RecompCompilerPipeline(RecompCompilerOptions options) => Options = options ?? throw new ArgumentNullException(nameof(options));

    public RecompCompilerOptions Options { get; }

    /// <summary>Parses compiler CLI flags; throws <see cref="ArgumentException"/> on missing or unknown values.</summary>
    public static RecompCompilerOptions ParseArgs(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool compileDll = false;

        for (int i = 0; i < args.Count; i++)
        {
            string flag = args[i].Trim();
            if (flag.Equals("--compile-dll", StringComparison.OrdinalIgnoreCase))
            {
                compileDll = true;
                if (i + 1 < args.Count && bool.TryParse(args[i + 1], out bool explicitVal))
                {
                    compileDll = explicitVal;
                    i++;
                }
                continue;
            }
            if (!ValueFlags.Contains(flag)) throw new ArgumentException($"Unknown option '{flag}'. {UsageText}");
            if (i + 1 >= args.Count || string.IsNullOrWhiteSpace(args[i + 1])) throw new ArgumentException($"Option '{flag}' requires a value.");
            map[flag] = args[++i].Trim().Trim('"');
        }

        map.TryGetValue("--disc", out string? disc);
        map.TryGetValue("--gamedata-dir", out string? gamedata);
        map.TryGetValue("--exe-path", out string? exePath);
        if (disc is not null && gamedata is not null)
            throw new ArgumentException("Specify either --disc or --gamedata-dir, not both.");
        if (disc is null && gamedata is null && exePath is null)
            throw new ArgumentException("Specify one of --disc, --gamedata-dir, or --exe-path.");

        string Require(string key) => map.TryGetValue(key, out string? v) ? v : throw new ArgumentException($"Missing required option '{key}'.");
        map.TryGetValue("--decomp-dir", out string? decompDir);
        map.TryGetValue("--functions-json", out string? functionsJson);
        map.TryGetValue("--version", out string? version);

        return new RecompCompilerOptions(disc, gamedata, decompDir ?? string.Empty, Require("--out-dir"), Require("--module-id"), Require("--serial"), Require("--title"))
        {
            ExePath = exePath,
            FunctionsJsonPath = functionsJson,
            Version = string.IsNullOrWhiteSpace(version) ? "0.1.0" : version,
            CompileDll = compileDll
        };
    }

    /// <summary>Discovers PS-X EXEs, lifts MIPS R3000A functions, emits module files, and optionally builds the DLL.</summary>
    public RecompCompileResult Run(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (TryValidateInputs(out string? err))
            return new RecompCompileResult(false, string.Empty, Array.Empty<string>(), err!);

        List<DiscoveredExe> exes;
        try
        {
            exes = DiscExeLoader.Discover(Options);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FileNotFoundException or UnauthorizedAccessException)
        {
            return new RecompCompileResult(false, string.Empty, Array.Empty<string>(), ex.Message);
        }

        string outDir = Path.GetFullPath(Options.OutDir);
        Directory.CreateDirectory(outDir);
        string pascalId = DiscExeLoader.ToPascalIdentifier(Options.ModuleId);
        string rootNamespace = $"{pascalId}.Recomp";

        var specs = new List<GeneratedExeSpec>(exes.Count);
        var emittedFiles = new List<string>();
        int totalFuncs = 0;
        try
        {
            foreach (DiscoveredExe exe in exes)
            {
                ct.ThrowIfCancellationRequested();
                uint ReadWord(uint vaddr)
                {
                    if (vaddr < exe.Header.TextAddr || vaddr >= exe.Header.TextEnd) return 0U;
                    int off = 0x800 + (int)(vaddr - exe.Header.TextAddr);
                    if (off < 0 || off + 4 > exe.Bytes.Length) return 0U;
                    return BinaryPrimitives.ReadUInt32LittleEndian(exe.Bytes.AsSpan(off, 4));
                }

                string? jsonPath = ExeScanHelpers.ResolveFunctionsJson(Options, exe);
                (List<GeneratedFnSpec> funcs, HashSet<uint> rdataTargets) = ExeScanHelpers.DiscoverFunctions(exe, ReadWord, jsonPath);
                var spec = new GeneratedExeSpec(exe.ExeId, exe.ExeName, exe.RelativePath, exe.FolderName, exe.ClassName, exe.Header.TextAddr, exe.Header.TextEnd, funcs);
                emittedFiles.AddRange(FunctionChunkEmitter.EmitModule(outDir, rootNamespace, spec, ReadWord, rdataTargets));
                specs.Add(spec);
                totalFuncs += funcs.Count;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            return new RecompCompileResult(false, string.Empty, emittedFiles, ex.Message);
        }

        emittedFiles.AddRange(ModuleManifestEmitter.EmitAll(outDir, Options, rootNamespace, pascalId, specs, out string manifestPath, out string csprojPath));
        if (Options.CompileDll)
        {
            (bool ok, string? dllPath, string diag) = ModuleManifestEmitter.CompileModuleAssembly(csprojPath, outDir, $"{Options.ModuleId}.recomp", ct);
            if (!ok) return new RecompCompileResult(false, manifestPath, emittedFiles, diag);
            if (dllPath is not null) emittedFiles.Add(dllPath);
        }

        return new RecompCompileResult(true, manifestPath, emittedFiles,
            $"Lifted {totalFuncs} function(s) across {specs.Count} EXE(s) -> '{manifestPath}'.");
    }

    private bool TryValidateInputs(out string? error)
    {
        error = null;
        if (!string.IsNullOrWhiteSpace(Options.DiscPath) && !File.Exists(Options.DiscPath))
            error = $"Disc image not found: '{Options.DiscPath}'.";
        else if (!string.IsNullOrWhiteSpace(Options.GameDataDir) && !Directory.Exists(Options.GameDataDir))
            error = $"Gamedata directory not found: '{Options.GameDataDir}'.";
        else if (string.IsNullOrWhiteSpace(Options.DiscPath) && string.IsNullOrWhiteSpace(Options.GameDataDir) && string.IsNullOrWhiteSpace(Options.ExePath))
            error = "Input media not specified (--disc, --gamedata-dir, or --exe-path).";
        else if (!string.IsNullOrWhiteSpace(Options.DecompDir) && !Directory.Exists(Options.DecompDir))
            error = $"Decomp directory not found: '{Options.DecompDir}'.";
        else if (!string.IsNullOrWhiteSpace(Options.FunctionsJsonPath) && !File.Exists(Options.FunctionsJsonPath))
            error = $"Functions manifest not found: '{Options.FunctionsJsonPath}'.";
        return error is not null;
    }
}
