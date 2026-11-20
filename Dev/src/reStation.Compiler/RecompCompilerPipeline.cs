// @module: reStation.Compiler.RecompCompilerPipeline
// @purpose: Modular .recomp lifter pipeline: CLI parsing, PS-X EXE discovery, R3000A lifting, clean-room aot-recipe emission, and local AOT DLL build.
// @depends-on: reEngine.Core.Aot.AotCacheManager, reStation.Contracts, reStation.Compiler.AotRecipeEmitter, reStation.Compiler.DiscExeLoader, reStation.Compiler.ExeScanHelpers, reStation.Compiler.FunctionChunkEmitter, reStation.Compiler.ModuleManifestEmitter
// @exports: RecompCompilerOptions, RecompCompileResult, RecompCompilerPipeline
// @owner: devv-compiler (T5)

using System.Buffers.Binary;
using System.Text.Json;
using reEngine.Core.Aot;
using reStation.Contracts;

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
    public bool EmitCleanRoomOnly { get; init; }
    public bool CompileGeneratedDll { get; init; }
    public string? AotRecipePath { get; init; }
    public string ExecutionTierMode { get; init; } = ReStationExecutionTierModes.Default;
    public string? OutPackagePath { get; init; }
    public string? DataDir { get; init; }
    public string? ThinShellAssemblyName { get; init; }
}

/// <summary>Compiler outcome with every emitted file path.</summary>
public sealed record RecompCompileResult(bool Success, string ManifestPath, IReadOnlyList<string> EmittedFiles, string Message);

public sealed class RecompCompilerPipeline
{
    public const string UsageText =
        "Usage: reStation.Compiler (--disc <image> | --gamedata-dir <dir> | --exe-path <exe>) " +
        "[--decomp-dir <dir>] [--functions-json <file>] --out-dir <dir> " +
        "--module-id <id> --serial <serialNumber> --title <title> [--version <semver>] " +
        "[--execution-tier <mode>] [--aot-recipe <file>] [--emit-clean-room] [--out-package <file.recomp>] " +
        "[--compile-dll | --compile-generated-dll [--data-dir <dir>]]";

    private static readonly HashSet<string> ValueFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "--disc", "--gamedata-dir", "--exe-path", "--decomp-dir", "--functions-json",
        "--out-dir", "--module-id", "--serial", "--title", "--version",
        "--aot-recipe", "--execution-tier", "--out-package", "--data-dir", "--thin-shell"
    };

    public RecompCompilerPipeline(RecompCompilerOptions options) => Options = options ?? throw new ArgumentNullException(nameof(options));

    public RecompCompilerOptions Options { get; }

    /// <summary>Parses compiler CLI flags; throws <see cref="ArgumentException"/> on missing or unknown values.</summary>
    public static RecompCompilerOptions ParseArgs(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool compileDll = false;
        bool emitCleanRoom = false;
        bool compileGeneratedDll = false;

        for (int i = 0; i < args.Count; i++)
        {
            string flag = args[i].Trim();
            if (TryParseBoolFlag(flag, "--compile-dll", args, ref i, ref compileDll)) continue;
            if (TryParseBoolFlag(flag, "--emit-clean-room", args, ref i, ref emitCleanRoom)) continue;
            if (TryParseBoolFlag(flag, "--compile-generated-dll", args, ref i, ref compileGeneratedDll)) continue;
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
        map.TryGetValue("--aot-recipe", out string? aotRecipe);
        map.TryGetValue("--execution-tier", out string? executionTier);
        map.TryGetValue("--out-package", out string? outPkg);
        map.TryGetValue("--data-dir", out string? dataDir);
        map.TryGetValue("--thin-shell", out string? thinShell);

        if (executionTier is not null && !ReStationExecutionTierModes.IsValid(executionTier))
            throw new ArgumentException($"Unknown --execution-tier '{executionTier}'.");

        return new RecompCompilerOptions(disc, gamedata, decompDir ?? string.Empty, Require("--out-dir"), Require("--module-id"), Require("--serial"), Require("--title"))
        {
            ExePath = exePath,
            FunctionsJsonPath = functionsJson,
            Version = string.IsNullOrWhiteSpace(version) ? "0.1.0" : version,
            CompileDll = compileDll,
            EmitCleanRoomOnly = emitCleanRoom,
            CompileGeneratedDll = compileGeneratedDll,
            AotRecipePath = aotRecipe,
            ExecutionTierMode = executionTier is null ? ReStationExecutionTierModes.Default : ReStationExecutionTierModes.Normalize(executionTier),
            OutPackagePath = outPkg,
            DataDir = dataDir,
            ThinShellAssemblyName = thinShell
        };
    }

    /// <summary>Discovers PS-X EXEs, lifts MIPS R3000A functions, emits clean-room AOT recipes, and optionally builds DLLs.</summary>
    public RecompCompileResult Run(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (TryValidateInputs(out string? err))
            return new RecompCompileResult(false, string.Empty, Array.Empty<string>(), err!);

        AotRecipeSpec? inputRecipe = LoadOptionalRecipe(Options.AotRecipePath, out string? recipeErr);
        if (recipeErr is not null)
            return new RecompCompileResult(false, string.Empty, Array.Empty<string>(), recipeErr);

        List<DiscoveredExe> exes;
        try
        {
            exes = DiscExeLoader.Discover(Options, inputRecipe);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FileNotFoundException or UnauthorizedAccessException)
        {
            return new RecompCompileResult(false, string.Empty, Array.Empty<string>(), ex.Message);
        }

        string outDir = Path.GetFullPath(Options.OutDir);
        Directory.CreateDirectory(outDir);
        string pascalId = DiscExeLoader.ToPascalIdentifier(Options.ModuleId);
        string rootNamespace = $"{pascalId}.Recomp";
        IReadOnlyCollection<uint>? globalSeeds = inputRecipe?.EntryAddresses;

        var specs = new List<GeneratedExeSpec>(exes.Count);
        var emittedFiles = new List<string>();
        int totalFuncs = 0;
        try
        {
            foreach (DiscoveredExe exe in exes)
            {
                ct.ThrowIfCancellationRequested();
                int codeStartOffset = exe.IsOverlay ? 0 : 0x800;
                uint ReadWord(uint vaddr)
                {
                    if (vaddr < exe.Header.TextAddr || vaddr >= exe.Header.TextEnd) return 0U;
                    int off = codeStartOffset + (int)(vaddr - exe.Header.TextAddr);
                    if (off < 0 || off + 4 > exe.Bytes.Length) return 0U;
                    return BinaryPrimitives.ReadUInt32LittleEndian(exe.Bytes.AsSpan(off, 4));
                }

                string? jsonPath = ExeScanHelpers.ResolveFunctionsJson(Options, exe);
                IReadOnlyCollection<uint>? extraSeeds = exe.SeedEntryAddresses ?? SelectUniqueSeedsForExe(exe, exes, globalSeeds);
                (List<GeneratedFnSpec> funcs, HashSet<uint> rdataTargets) = ExeScanHelpers.DiscoverFunctions(exe, ReadWord, jsonPath, extraSeeds);
                uint verifiedTextStart = exe.Header.TextAddr;
                int verifiedTextSize = 0;
                string verifiedTextSha = string.Empty;
                if (exe.IsOverlay && funcs.Count > 0)
                {
                    uint vStart = Math.Max(exe.Header.TextAddr, funcs.Min(f => f.Addr));
                    uint vEnd = Math.Min(exe.Header.TextEnd, funcs.Max(f => f.EndAddr));
                    if (vEnd > vStart)
                    {
                        int off = checked((int)(vStart - exe.Header.TextAddr));
                        int len = checked((int)(vEnd - vStart));
                        if (off >= 0 && off + len <= exe.Bytes.Length)
                        {
                            verifiedTextStart = vStart;
                            verifiedTextSize = len;
                            verifiedTextSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(exe.Bytes.AsSpan(off, len))).ToLowerInvariant();
                        }
                    }
                }
                var spec = new GeneratedExeSpec(exe.ExeId, exe.ExeName, exe.RelativePath, exe.FolderName, exe.ClassName, exe.Header.TextAddr, exe.Header.TextEnd, funcs)
                {
                    IsOverlay = exe.IsOverlay,
                    LoadRamAddress = exe.Header.TextAddr,
                    ContentSizeBytes = exe.Bytes.Length,
                    ContentSha256Hex = exe.ContentSha256Hex,
                    VerifiedTextStart = verifiedTextStart,
                    VerifiedTextSizeBytes = verifiedTextSize,
                    TextSha256Hex = verifiedTextSha
                };
                if (!Options.EmitCleanRoomOnly)
                    emittedFiles.AddRange(FunctionChunkEmitter.EmitModule(outDir, rootNamespace, spec, ReadWord, rdataTargets));
                specs.Add(spec);
                totalFuncs += funcs.Count;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
        {
            return new RecompCompileResult(false, string.Empty, emittedFiles, ex.Message);
        }

        string generatedCacheAsmName = inputRecipe?.GeneratedCacheAssemblyName is { Length: > 0 } customName
            ? customName
            : $"{Options.ModuleId}.Generated.dll";
        AotRecipeSpec recipe = AotRecipeEmitter.BuildRecipe(Options, exes, specs, generatedCacheAsmName);
        string recipePath = AotRecipeEmitter.WriteRecipeFile(outDir, recipe);
        emittedFiles.Add(recipePath);

        string manifestPath;
        if (Options.EmitCleanRoomOnly)
        {
            string bootExe = specs.Count > 0 ? specs[0].RelativePath : string.Empty;
            string entryAsm = !string.IsNullOrWhiteSpace(Options.ThinShellAssemblyName)
                ? Path.GetFileName(Options.ThinShellAssemblyName)
                : generatedCacheAsmName;
            string moduleTypeName = $"{rootNamespace}.{pascalId}GameModule";
            manifestPath = ModuleManifestEmitter.EmitManifestFile(
                outDir, Options, bootExe, entryAsm, moduleTypeName, ReStationEngineInfo.DefaultAotRecipePath);
            emittedFiles.Add(manifestPath);
            if (!TryEmitCleanRoomPackageIfRequested(outDir, manifestPath, recipePath, out string? pkgErr))
                return new RecompCompileResult(false, manifestPath, emittedFiles, pkgErr!);
            return new RecompCompileResult(true, manifestPath, emittedFiles,
                $"Emitted clean-room recipe ({totalFuncs} entrypoints, {exes.Count} EXE(s)) -> '{manifestPath}'.");
        }

        emittedFiles.AddRange(ModuleManifestEmitter.EmitAll(outDir, Options, rootNamespace, pascalId, specs, out manifestPath, out string csprojPath, generatedCacheAsmName));
        string buildTargetName = Options.CompileGeneratedDll
            ? ModuleManifestEmitter.ResolveGeneratedAssemblySimpleName(generatedCacheAsmName, Options.ModuleId)
            : $"{Options.ModuleId}.recomp";
        if (Options.CompileGeneratedDll || Options.CompileDll)
        {
            (bool ok, string? dllPath, string diag) = ModuleManifestEmitter.CompileModuleAssembly(csprojPath, outDir, buildTargetName, ct);
            if (!ok) return new RecompCompileResult(false, manifestPath, emittedFiles, diag);
            if (dllPath is not null)
            {
                emittedFiles.Add(dllPath);
                if (Options.CompileGeneratedDll && !string.IsNullOrWhiteSpace(Options.DataDir))
                {
                    string romSha = AotRecipeEmitter.ComputeCanonicalRomSha256Hex(exes);
                    string modVer = string.IsNullOrWhiteSpace(Options.Version) ? "0.1.5" : Options.Version.Trim();
                    var key = new AotCacheKey(Options.ModuleId, romSha, ReStationEngineInfo.EngineId, ReStationEngineInfo.Version, modVer);
                    AotCompileResult stored = new AotCacheManager(Options.DataDir).StoreCompiledAssemblyAsync(key, dllPath, ct).GetAwaiter().GetResult();
                    if (!stored.Success)
                        return new RecompCompileResult(false, manifestPath, emittedFiles, stored.ErrorMessage ?? "Failed to populate AOT cache.");
                }
            }
        }

        if (!TryEmitCleanRoomPackageIfRequested(outDir, manifestPath, recipePath, out string? cleanPkgErr))
            return new RecompCompileResult(false, manifestPath, emittedFiles, cleanPkgErr!);

        return new RecompCompileResult(true, manifestPath, emittedFiles,
            $"Lifted {totalFuncs} function(s) across {specs.Count} EXE(s) -> '{manifestPath}'.");
    }

    private static bool TryParseBoolFlag(string flag, string name, IReadOnlyList<string> args, ref int index, ref bool target)
    {
        if (!flag.Equals(name, StringComparison.OrdinalIgnoreCase)) return false;
        target = true;
        if (index + 1 < args.Count && bool.TryParse(args[index + 1], out bool explicitVal))
        {
            target = explicitVal;
            index++;
        }
        return true;
    }

    private static AotRecipeSpec? LoadOptionalRecipe(string? recipePath, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(recipePath)) return null;
        try
        {
            string json = File.ReadAllText(recipePath);
            AotRecipeSpec? parsed = JsonSerializer.Deserialize<AotRecipeSpec>(json, ReStationModuleManifest.JsonOptions);
            if (parsed is null) { error = $"Failed to deserialize AOT recipe '{recipePath}'."; return null; }
            parsed.Validate(recipePath);
            return parsed;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or ArgumentException)
        {
            error = ex.Message;
            return null;
        }
    }

    private bool TryEmitCleanRoomPackageIfRequested(string outDir, string manifestPath, string recipePath, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(Options.OutPackagePath)) return true;
        try
        {
            string? thinDll = string.IsNullOrWhiteSpace(Options.ThinShellAssemblyName) ? null : Options.ThinShellAssemblyName;
            string? thinDeps = thinDll is not null && File.Exists(Path.ChangeExtension(thinDll, ".deps.json"))
                ? Path.ChangeExtension(thinDll, ".deps.json")
                : null;
            AotRecipeEmitter.EmitCleanRoomArchive(outDir, Options.OutPackagePath, manifestPath, recipePath, thinDll, thinDeps);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            error = ex.Message;
            return false;
        }
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
        else if (!string.IsNullOrWhiteSpace(Options.AotRecipePath) && !File.Exists(Options.AotRecipePath))
            error = $"AOT recipe not found: '{Options.AotRecipePath}'.";
        return error is not null;
    }

    private static IReadOnlyCollection<uint>? SelectUniqueSeedsForExe(
        DiscoveredExe target,
        IReadOnlyList<DiscoveredExe> allExes,
        IReadOnlyCollection<uint>? globalSeeds)
    {
        if (globalSeeds is not { Count: > 0 }) return null;
        if (allExes.Count <= 1) return globalSeeds;
        var filtered = new List<uint>();
        foreach (uint raw in globalSeeds)
        {
            uint addr = DiscExeLoader.CanonicalRam(raw);
            if (addr < target.Header.TextAddr || addr >= target.Header.TextEnd) continue;
            bool shared = false;
            foreach (DiscoveredExe other in allExes)
            {
                if (ReferenceEquals(other, target)) continue;
                if (addr >= other.Header.TextAddr && addr < other.Header.TextEnd) { shared = true; break; }
            }
            if (!shared) filtered.Add(addr);
        }
        return filtered.Count > 0 ? filtered : null;
    }
}
