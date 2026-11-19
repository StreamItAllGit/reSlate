// @module: reStation.Decomp.PsxExportAndAssetPipeline
// @purpose: Ghidra C export normalizer with coalescing hints, typed array externs, (PsxCallbackFn)(uintptr_t) casts, and clean-room validation.
// @depends-on: System, System.Collections.Generic, System.Globalization, System.IO, System.Text, System.Text.Json, System.Text.Json.Serialization, System.Text.RegularExpressions, Foundation.Core, reEngine.Core.Decomp, reEngine.Core.Manifests, reStation.Contracts
// @exports: PsxNormalizedFunction, PsxTypedGlobalExtern, PsxNormalizedExportSummary, PsxExportAndAssetPipeline
// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Foundation.Core;
using reEngine.Core.Decomp;
using reEngine.Core.Manifests;
using reStation.Contracts;

namespace reStation.Decomp;

/// <summary>Normalized clean-room function metadata extracted from Ghidra C exports.</summary>
/// <param name="Name">Normalized function identifier.</param>
/// <param name="EntryAddress">PS1 entry virtual address.</param>
/// <param name="ByteSize">Estimated machine code size in bytes.</param>
/// <param name="GteIntrinsics">Detected clean-room GTE/COP2 intrinsic calls.</param>
/// <param name="NormalizedDeclaration">Normalized C prototype line.</param>
/// <param name="CoalescedModule">Logical subsystem or original SDK translation unit path.</param>
public sealed record PsxNormalizedFunction(
    string Name,
    uint EntryAddress,
    uint ByteSize,
    IReadOnlyList<string> GteIntrinsics,
    string NormalizedDeclaration,
    string? CoalescedModule = null);

/// <summary>Typed array global extern descriptor replacing scalar <c>&amp;g_* + N</c> pointer math.</summary>
/// <param name="ElementType">Canonical C element type (e.g., <c>u32</c>, <c>u16</c>, <c>u8</c>, <c>PsxSVector</c>).</param>
/// <param name="SymbolName">Normalized global identifier (e.g., <c>g_cd_toc_entries</c>).</param>
/// <param name="ElementCount">Array element count (<c>0</c> emits unsized <c>[]</c>).</param>
/// <param name="CoalescedModule">Optional owning subsystem translation unit.</param>
public sealed record PsxTypedGlobalExtern(
    string ElementType,
    string SymbolName,
    int ElementCount = 0,
    string? CoalescedModule = null);

/// <summary>Deterministic normalization result across exported Ghidra C files and <c>functions.json</c>.</summary>
/// <param name="Functions">Ordered catalog of normalized functions.</param>
/// <param name="NormalizedCPath">Path to generated <c>normalized-export.c</c>.</param>
/// <param name="FunctionsIndexPath">Path to generated <c>normalized-functions.json</c>.</param>
public sealed record PsxNormalizedExportSummary(
    IReadOnlyList<PsxNormalizedFunction> Functions,
    string NormalizedCPath,
    string FunctionsIndexPath);

/// <summary>
/// Normalizes Ghidra C exports into deterministic <see cref="AotRecipeSpec"/> files, coalesced subsystem modules,
/// typed array externs, and strict clean-room validation via <see cref="DecompD4StrictnessValidator"/>.
/// </summary>
public static partial class PsxExportAndAssetPipeline
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly (string Pattern, string Replacement)[] GhidraTypeReplacements =
    [
        (@"\bundefined1\b", "u8"), (@"\bundefined2\b", "u16"),
        (@"\bundefined4\b", "u32"), (@"\bundefined8\b", "u64"),
        (@"\bundefined\b", "u8"), (@"\buint\b", "u32"),
        (@"\bushort\b", "u16"), (@"\buchar\b", "u8"),
        (@"\bbyte\b", "u8"), (@"\bulonglong\b", "u64"),
        (@"\blonglong\b", "s64")
    ];

    private static readonly (string Pattern, string Replacement)[] GteIntrinsicReplacements =
    [
        (@"\bgte_(rtps|rtpt|nclip|avsz3|avsz4|mvmva|op|sqr|dpcs|intpl|ncds|cdp|ncdt|nccs|cc|ncs|nct|dcpl|dpct|gpf|gpl|ncct)\b", "psx_gte_$1"),
        (@"\b(MFC2|CFC2|MTC2|CTC2|LWC2|SWC2)\s*\(", "psx_cop2_$1(")
    ];

    [GeneratedRegex(@"\(\s*(?:code\s*\*|PsxCallbackFn)\s*\)(?!\s*\(\s*uintptr_t\s*\))", RegexOptions.CultureInvariant)]
    private static partial Regex CallbackCastRegex();

    [GeneratedRegex(@"\bcode\s*\*\s*", RegexOptions.CultureInvariant)]
    private static partial Regex RawCodePtrTypeRegex();

    [GeneratedRegex(@"\b(?:FUN|psx_fn)_([0-9A-Fa-f]{8})\b")]
    private static partial Regex FunAddressRegex();

    [GeneratedRegex(@"\bpsx_gte_(?:rtps|rtpt|nclip|avsz3|avsz4|mvmva|op|sqr|dpcs|intpl|ncds|cdp|ncdt|nccs|cc|ncs|nct|dcpl|dpct|gpf|gpl|ncct)\b|\bpsx_cop2_(?:MFC2|CFC2|MTC2|CTC2|LWC2|SWC2)\b")]
    private static partial Regex NormalizedGteRegex();

    /// <summary>Normalizes Ghidra C types, <c>(PsxCallbackFn)(uintptr_t)</c> casts, and GTE COP2 intrinsics into clean-room C.</summary>
    public static string NormalizeGhidraC(string rawGhidraC)
    {
        ArgumentNullException.ThrowIfNull(rawGhidraC);
        string text = rawGhidraC.Replace("\r\n", "\n").Replace('\r', '\n');
        text = CallbackCastRegex().Replace(text, "(PsxCallbackFn)(uintptr_t)");
        text = RawCodePtrTypeRegex().Replace(text, m => text.AsSpan(m.Index + m.Length).StartsWith(")") || text.AsSpan(m.Index + m.Length).StartsWith(",") ? "PsxCallbackFn" : "PsxCallbackFn ");
        foreach ((string pattern, string repl) in GhidraTypeReplacements)
            text = Regex.Replace(text, pattern, repl);
        foreach ((string pattern, string repl) in GteIntrinsicReplacements)
            text = Regex.Replace(text, pattern, repl, RegexOptions.IgnoreCase);
        return text;
    }

    /// <summary>Assigns a cohesive logical subsystem or Psy-Q translation unit path instead of <c>_01.._NN.c</c> fragments.</summary>
    public static string ClassifyCoalescedModule(string functionName, uint entryAddress, IReadOnlyList<string>? gteIntrinsics = null, string? sourceHint = null)
    {
        string fn = functionName ?? string.Empty;
        string hint = sourceHint ?? string.Empty;
        if ((gteIntrinsics is { Count: > 0 }) || fn.Contains("gte", StringComparison.OrdinalIgnoreCase) || hint.Contains("libgte", StringComparison.OrdinalIgnoreCase))
            return "sdk/libgte/gte_math.c";
        if (fn.Contains("gpu", StringComparison.OrdinalIgnoreCase) || fn.Contains("DrawOTag", StringComparison.OrdinalIgnoreCase) || fn.Contains("DispEnv", StringComparison.OrdinalIgnoreCase) || hint.Contains("libgpu", StringComparison.OrdinalIgnoreCase))
            return "sdk/libgpu/gpu_primitives.c";
        if (fn.StartsWith("cd_", StringComparison.OrdinalIgnoreCase) || fn.Contains("CdControl", StringComparison.OrdinalIgnoreCase) || fn.Contains("CdRead", StringComparison.OrdinalIgnoreCase) || hint.Contains("libcd", StringComparison.OrdinalIgnoreCase))
            return "sdk/libcd/cd_stream.c";
        if (fn.Contains("spu", StringComparison.OrdinalIgnoreCase) || fn.Contains("vag", StringComparison.OrdinalIgnoreCase) || fn.Contains("vab", StringComparison.OrdinalIgnoreCase) || hint.Contains("libspu", StringComparison.OrdinalIgnoreCase))
            return "sdk/libspu/spu_audio.c";
        if (fn.Contains("pad", StringComparison.OrdinalIgnoreCase) || fn.Contains("sio", StringComparison.OrdinalIgnoreCase) || fn.Contains("VSync", StringComparison.OrdinalIgnoreCase) || hint.Contains("libetc", StringComparison.OrdinalIgnoreCase))
            return "sdk/libetc/sys_io.c";
        if (hint.Contains("ovl", StringComparison.OrdinalIgnoreCase) || fn.StartsWith("ovl", StringComparison.OrdinalIgnoreCase))
            return "overlays/overlay_subsystem.c";
        if (fn.Equals("psx_entry", StringComparison.OrdinalIgnoreCase) || fn.StartsWith("main", StringComparison.OrdinalIgnoreCase) || fn.StartsWith("init_", StringComparison.OrdinalIgnoreCase) || entryAddress == 0x80010000U)
            return "src/core_mainloop/core_mainloop.c";
        return "src/gameplay/gameplay.c";
    }

    /// <summary>Formats a typed C array extern declaration (<c>extern T sym[N];</c> or <c>extern T sym[];</c>).</summary>
    public static string FormatTypedArrayExtern(PsxTypedGlobalExtern global)
    {
        ArgumentNullException.ThrowIfNull(global);
        string dim = global.ElementCount > 0 ? global.ElementCount.ToString(CultureInfo.InvariantCulture) : string.Empty;
        return $"extern {global.ElementType} {global.SymbolName}[{dim}];";
    }

    /// <summary>Generates a clean C header block of typed array extern declarations.</summary>
    public static string GenerateTypedArrayExternsHeader(IEnumerable<PsxTypedGlobalExtern> globals)
    {
        ArgumentNullException.ThrowIfNull(globals);
        var sb = new StringBuilder("/* Typed array extern declarations (reSlate.Decomp MPL-2.0) */\n");
        foreach (PsxTypedGlobalExtern g in globals.OrderBy(x => x.SymbolName, StringComparer.Ordinal))
            sb.AppendLine(FormatTypedArrayExtern(g));
        return sb.ToString();
    }

    /// <summary>Normalizes exported C and <c>functions.json</c> files inside <paramref name="exportDir"/> into deterministic outputs.</summary>
    public static PsxNormalizedExportSummary NormalizeExportDirectory(string exportDir, string outputDir)
    {
        string fullOut = Path.GetFullPath(outputDir);
        Directory.CreateDirectory(fullOut);

        var fnMap = new Dictionary<uint, PsxNormalizedFunction>();
        var externs = new List<PsxTypedGlobalExtern>();
        string fullExport = Path.GetFullPath(exportDir);
        string cCombined = "/* Normalized clean-room C export (reSlate.Decomp MPL-2.0) */\n";

        if (Directory.Exists(fullExport))
        {
            string jsonPath = Path.Combine(fullExport, "functions.json");
            if (File.Exists(jsonPath))
                LoadFunctionsManifest(jsonPath, fnMap);
            LoadTypedGlobals(fullExport, externs);

            string[] cFiles = Directory.EnumerateFiles(fullExport, "*.c", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
            if (cFiles.Length > 0 || externs.Count > 0)
            {
                var sb = new StringBuilder(cCombined).AppendLine(PsxGhidraScriptTemplates.GeneratePsxCleanCompatPreamble());
                if (externs.Count > 0) sb.AppendLine(GenerateTypedArrayExternsHeader(externs));
                foreach (string cFile in cFiles)
                {
                    string norm = NormalizeGhidraC(File.ReadAllText(cFile, Encoding.UTF8));
                    string hint = ClassifyCoalescedModule(Path.GetFileNameWithoutExtension(cFile), 0, null, cFile);
                    sb.AppendLine($"/* @coalesced-module {hint} | source: {Path.GetFileName(cFile)} */").AppendLine(norm);
                    ExtractFunctionMentions(norm, fnMap, cFile);
                }
                cCombined = sb.ToString();
            }
        }

        List<PsxNormalizedFunction> sorted = fnMap.Values
            .Select(f => f.CoalescedModule is null ? f with { CoalescedModule = ClassifyCoalescedModule(f.Name, f.EntryAddress, f.GteIntrinsics) } : f)
            .OrderBy(f => f.EntryAddress).ToList();
        string[] coalescedModules = sorted.Select(f => f.CoalescedModule!).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        string normCPath = AppEnvironment.AssertPathContained(Path.Combine(fullOut, "normalized-export.c"), fullOut);
        string fnOutPath = AppEnvironment.AssertPathContained(Path.Combine(fullOut, "normalized-functions.json"), fullOut);
        File.WriteAllText(normCPath, cCombined, new UTF8Encoding(false));
        File.WriteAllText(fnOutPath, JsonSerializer.Serialize(new { schemaVersion = 1, engineId = "reSlate", corpusTier = PsxD4CraftsmanshipValidator.CanonicalD4Tier, coalescedModules, functions = sorted }, JsonOptions), new UTF8Encoding(false));

        return new PsxNormalizedExportSummary(sorted, normCPath, fnOutPath);
    }

    /// <summary>Synthesizes and validates a deterministic clean-room <see cref="AotRecipeSpec"/>.</summary>
    public static AotRecipeSpec BuildDeterministicAotRecipe(
        DecompPackageManifest manifest,
        PsxLoadedProgramLayout? layout,
        PsxNormalizedExportSummary? exports,
        string? outputDir = null,
        string? executionTierOverride = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var exeSpecs = new List<AotRecipeExecutableSpec>();
        var entrySet = new SortedSet<uint>();
        if (layout is not null)
        {
            foreach (PsxExeImage exe in layout.AllExeImages.OrderBy(e => e.SlotId))
            {
                exeSpecs.Add(new AotRecipeExecutableSpec
                {
                    BootExePath = exe.RelativePath,
                    Sha256 = exe.Sha256Hex,
                    LoadAddress = exe.Header.TextAddr,
                    EntryAddress = exe.Header.Pc0,
                    GlobalPointer = exe.Header.Gp0,
                    TextSizeBytes = (int)exe.Header.TextSize,
                    SleighLanguageId = PsxGteSlaExtension.SleighLanguageId
                });
                entrySet.Add(exe.Header.Pc0);
            }
        }

        if (exports is not null)
            foreach (PsxNormalizedFunction fn in exports.Functions)
                entrySet.Add(fn.EntryAddress);

        var artifacts = new SortedSet<string>(StringComparer.Ordinal) { "aot-recipe.json" };
        if (exports is not null && !string.IsNullOrWhiteSpace(outputDir))
        {
            artifacts.Add(Path.GetRelativePath(outputDir, exports.FunctionsIndexPath).Replace('\\', '/'));
            artifacts.Add(Path.GetRelativePath(outputDir, exports.NormalizedCPath).Replace('\\', '/'));
        }

        var recipe = new AotRecipeSpec
        {
            SchemaVersion = 1,
            PlatformId = "ps1",
            ModuleId = manifest.ModuleId,
            DiscSerial = manifest.ExtensionData is not null && manifest.ExtensionData.TryGetValue("serialNumber", out JsonElement ser)
                ? ser.GetString() ?? string.Empty : string.Empty,
            ExecutionTierMode = ReStationExecutionTierModes.Normalize(executionTierOverride),
            SleighSpecId = PsxGteSlaExtension.SleighLanguageId,
            TargetArchitecture = "R3000A",
            LittleEndian = true,
            PrimaryBootExecutable = layout?.BootExe.RelativePath ?? manifest.EntryExecutablePathFallback(),
            Executables = exeSpecs,
            EntryAddresses = entrySet.ToArray(),
            CleanRoomArtifacts = artifacts.ToArray(),
            GeneratedCacheAssemblyName = $"{manifest.ModuleId}.Generated.dll"
        };
        recipe.Validate("aot-recipe.json");

        if (!string.IsNullOrWhiteSpace(outputDir))
        {
            string fullOut = Path.GetFullPath(outputDir);
            Directory.CreateDirectory(fullOut);
            string recipePath = AppEnvironment.AssertPathContained(Path.Combine(fullOut, "aot-recipe.json"), fullOut);
            File.WriteAllText(recipePath, JsonSerializer.Serialize(recipe, JsonOptions), new UTF8Encoding(false));
        }
        return recipe;
    }

    /// <summary>Validates that <paramref name="outputDirectory"/> contains zero retail media, save states, or generated binaries.</summary>
    public static DecompCleanRoomValidationResult ValidateCleanRoomOutput(string outputDirectory) =>
        PsxCleanRoomValidator.ValidateDirectory(outputDirectory);

    /// <summary>Validates a <c>.decomp</c> or <c>.recomp</c> package archive or directory for strict clean-room compliance.</summary>
    public static DecompCleanRoomValidationResult ValidateCleanRoomPackage(string packageOrDirectory) =>
        PsxCleanRoomValidator.ValidatePackage(packageOrDirectory);

    /// <summary>Audits a workspace via <see cref="DecompD4StrictnessValidator"/> while accepting both <c>D4_CleanSource</c> and <c>D4_FunctionalRewrite</c>.</summary>
    public static D4StrictnessValidationResult ValidateD4WorkspaceStrictness(string rootDirectory)
    {
        D4StrictnessValidationResult core = DecompD4StrictnessValidator.ValidateWorkspace(rootDirectory);
        IReadOnlyList<string> normalizedTiers = PsxD4CraftsmanshipValidator.DiscoverCorpusTiers(rootDirectory);
        return core with { DiscoveredTiers = normalizedTiers };
    }

    private static string EntryExecutablePathFallback(this DecompPackageManifest manifest) =>
        manifest.ExtensionData is not null && manifest.ExtensionData.TryGetValue("defaultBootExecutable", out JsonElement boot)
            ? boot.GetString() ?? string.Empty : string.Empty;

    private static void LoadFunctionsManifest(string jsonPath, Dictionary<uint, PsxNormalizedFunction> fnMap)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(jsonPath, Encoding.UTF8));
        JsonElement arr = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement
            : (doc.RootElement.TryGetProperty("functions", out JsonElement f) ? f : default);
        if (arr.ValueKind != JsonValueKind.Array) return;

        foreach (JsonElement item in arr.EnumerateArray())
        {
            if (!TryParseAddr(item, out uint addr)) continue;
            string name = item.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? $"psx_fn_{addr:X8}" : $"psx_fn_{addr:X8}";
            uint size = item.TryGetProperty("size", out JsonElement s) && s.TryGetUInt32(out uint sz) ? sz : 4U;
            string? mod = item.TryGetProperty("coalescedModule", out JsonElement cm) ? cm.GetString() : null;
            fnMap[addr] = new PsxNormalizedFunction(name, addr, size, Array.Empty<string>(), $"void {name}(void);", mod ?? ClassifyCoalescedModule(name, addr));
        }
    }

    private static void LoadTypedGlobals(string exportDir, List<PsxTypedGlobalExtern> externs)
    {
        foreach (string name in new[] { "globals_map.json", "globals.json" })
        {
            string path = Path.Combine(exportDir, name);
            if (!File.Exists(path)) continue;
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            JsonElement arr = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement
                : (doc.RootElement.TryGetProperty("globals", out JsonElement g) ? g : default);
            if (arr.ValueKind != JsonValueKind.Array) continue;
            foreach (JsonElement item in arr.EnumerateArray())
            {
                string sym = item.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? string.Empty : string.Empty;
                if (string.IsNullOrWhiteSpace(sym)) continue;
                string ty = item.TryGetProperty("elementType", out JsonElement et) ? et.GetString() ?? "u32"
                    : (item.TryGetProperty("cType", out JsonElement ct) ? ct.GetString() ?? "u32" : "u32");
                int cnt = item.TryGetProperty("elementCount", out JsonElement ec) && ec.TryGetInt32(out int c) ? c : 0;
                externs.Add(new PsxTypedGlobalExtern(ty, sym, cnt));
            }
            break;
        }
    }

    private static void ExtractFunctionMentions(string normC, Dictionary<uint, PsxNormalizedFunction> fnMap, string? sourceHint = null)
    {
        string[] gteOps = NormalizedGteRegex().Matches(normC).Select(m => m.Value).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        foreach (Match m in FunAddressRegex().Matches(normC))
        {
            if (!uint.TryParse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint addr)) continue;
            if (fnMap.TryGetValue(addr, out PsxNormalizedFunction? existing))
            {
                string[] mergedOps = existing.GteIntrinsics.Concat(gteOps).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
                fnMap[addr] = existing with { GteIntrinsics = mergedOps, CoalescedModule = existing.CoalescedModule ?? ClassifyCoalescedModule(existing.Name, addr, mergedOps, sourceHint) };
            }
            else
            {
                string fnName = $"psx_fn_{addr:X8}";
                fnMap[addr] = new PsxNormalizedFunction(fnName, addr, 4U, gteOps, $"void {fnName}(void);", ClassifyCoalescedModule(fnName, addr, gteOps, sourceHint));
            }
        }
    }

    private static bool TryParseAddr(JsonElement item, out uint addr)
    {
        addr = 0;
        if (!item.TryGetProperty("address", out JsonElement prop) && !item.TryGetProperty("addr", out prop)) return false;
        if (prop.ValueKind == JsonValueKind.Number) return prop.TryGetUInt32(out addr);
        string raw = (prop.GetString() ?? string.Empty).Trim();
        return raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.TryParse(raw.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out addr)
            : uint.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out addr);
    }
}
