// @module: reStation.Decomp.PsxExportAndAssetPipeline
// @purpose: Ghidra C export normalizer, deterministic AotRecipeSpec builder, and clean-room output/package facade.
// @depends-on: System, System.Collections.Generic, System.Globalization, System.IO, System.Text, System.Text.Json, System.Text.RegularExpressions, Foundation.Core, reEngine.Core.Decomp, reEngine.Core.Manifests, reStation.Contracts
// @exports: PsxNormalizedFunction, PsxNormalizedExportSummary, PsxExportAndAssetPipeline
// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using System.Text;
using System.Text.Json;
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
public sealed record PsxNormalizedFunction(
    string Name,
    uint EntryAddress,
    uint ByteSize,
    IReadOnlyList<string> GteIntrinsics,
    string NormalizedDeclaration);

/// <summary>Deterministic normalization result across exported Ghidra C files and <c>functions.json</c>.</summary>
/// <param name="Functions">Ordered catalog of normalized functions.</param>
/// <param name="NormalizedCPath">Path to generated <c>normalized-export.c</c>.</param>
/// <param name="FunctionsIndexPath">Path to generated <c>normalized-functions.json</c>.</param>
public sealed record PsxNormalizedExportSummary(
    IReadOnlyList<PsxNormalizedFunction> Functions,
    string NormalizedCPath,
    string FunctionsIndexPath);

/// <summary>
/// Normalizes Ghidra C exports into deterministic <see cref="AotRecipeSpec"/> files and enforces strict
/// clean-room validation across output directories and <c>.decomp</c>/<c>.recomp</c> packages.
/// </summary>
public static partial class PsxExportAndAssetPipeline
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static readonly (string Pattern, string Replacement)[] GhidraTypeReplacements =
    [
        (@"\bundefined1\b", "u8"), (@"\bundefined2\b", "u16"),
        (@"\bundefined4\b", "u32"), (@"\bundefined8\b", "u64"),
        (@"\bundefined\b", "u8"), (@"\buint\b", "u32"),
        (@"\bushort\b", "u16"), (@"\buchar\b", "u8"),
        (@"\bbyte\b", "u8"), (@"\bulonglong\b", "u64"),
        (@"\blonglong\b", "s64"), (@"\bcode\s*\*", "PsxFnPtr ")
    ];

    private static readonly (string Pattern, string Replacement)[] GteIntrinsicReplacements =
    [
        (@"\bgte_(rtps|rtpt|nclip|avsz3|avsz4|mvmva|op|sqr|dpcs|intpl|ncds|cdp|ncdt|nccs|cc|ncs|nct|dcpl|dpct|gpf|gpl|ncct)\b", "psx_gte_$1"),
        (@"\b(MFC2|CFC2|MTC2|CTC2|LWC2|SWC2)\s*\(", "psx_cop2_$1(")
    ];

    [GeneratedRegex(@"\b(?:FUN|psx_fn)_([0-9A-Fa-f]{8})\b")]
    private static partial Regex FunAddressRegex();

    [GeneratedRegex(@"\bpsx_gte_(?:rtps|rtpt|nclip|avsz3|avsz4|mvmva|op|sqr|dpcs|intpl|ncds|cdp|ncdt|nccs|cc|ncs|nct|dcpl|dpct|gpf|gpl|ncct)\b|\bpsx_cop2_(?:MFC2|CFC2|MTC2|CTC2|LWC2|SWC2)\b")]
    private static partial Regex NormalizedGteRegex();

    /// <summary>Normalizes Ghidra C types and GTE COP2 intrinsics into deterministic clean-room C.</summary>
    public static string NormalizeGhidraC(string rawGhidraC)
    {
        ArgumentNullException.ThrowIfNull(rawGhidraC);
        string text = rawGhidraC.Replace("\r\n", "\n").Replace('\r', '\n');
        foreach ((string pattern, string repl) in GhidraTypeReplacements)
            text = Regex.Replace(text, pattern, repl);
        foreach ((string pattern, string repl) in GteIntrinsicReplacements)
            text = Regex.Replace(text, pattern, repl, RegexOptions.IgnoreCase);
        return text;
    }

    /// <summary>Normalizes exported C and <c>functions.json</c> files inside <paramref name="exportDir"/> into deterministic outputs.</summary>
    public static PsxNormalizedExportSummary NormalizeExportDirectory(string exportDir, string outputDir)
    {
        string fullOut = Path.GetFullPath(outputDir);
        Directory.CreateDirectory(fullOut);

        var fnMap = new Dictionary<uint, PsxNormalizedFunction>();
        string fullExport = Path.GetFullPath(exportDir);
        string cCombined = "/* Normalized clean-room C export (reStation.Decomp MPL-2.0) */\n";

        if (Directory.Exists(fullExport))
        {
            string jsonPath = Path.Combine(fullExport, "functions.json");
            if (File.Exists(jsonPath))
                LoadFunctionsManifest(jsonPath, fnMap);

            var sb = new StringBuilder(cCombined);
            foreach (string cFile in Directory.EnumerateFiles(fullExport, "*.c", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                string norm = NormalizeGhidraC(File.ReadAllText(cFile, Encoding.UTF8));
                sb.AppendLine($"/* source: {Path.GetFileName(cFile)} */").AppendLine(norm);
                ExtractFunctionMentions(norm, fnMap);
            }
            cCombined = sb.ToString();
        }

        List<PsxNormalizedFunction> sorted = fnMap.Values.OrderBy(f => f.EntryAddress).ToList();
        string normCPath = SandboxEnvironment.AssertPathContained(Path.Combine(fullOut, "normalized-export.c"), fullOut);
        string fnOutPath = SandboxEnvironment.AssertPathContained(Path.Combine(fullOut, "normalized-functions.json"), fullOut);
        File.WriteAllText(normCPath, cCombined, new UTF8Encoding(false));
        File.WriteAllText(fnOutPath, JsonSerializer.Serialize(new { schemaVersion = 1, functions = sorted }, JsonOptions), new UTF8Encoding(false));

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
            string recipePath = SandboxEnvironment.AssertPathContained(Path.Combine(fullOut, "aot-recipe.json"), fullOut);
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
            fnMap[addr] = new PsxNormalizedFunction(name, addr, size, Array.Empty<string>(), $"void {name}(void);");
        }
    }

    private static void ExtractFunctionMentions(string normC, Dictionary<uint, PsxNormalizedFunction> fnMap)
    {
        string[] gteOps = NormalizedGteRegex().Matches(normC).Select(m => m.Value).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        foreach (Match m in FunAddressRegex().Matches(normC))
        {
            if (!uint.TryParse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint addr)) continue;
            if (fnMap.TryGetValue(addr, out PsxNormalizedFunction? existing))
                fnMap[addr] = existing with { GteIntrinsics = existing.GteIntrinsics.Concat(gteOps).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray() };
            else
                fnMap[addr] = new PsxNormalizedFunction($"psx_fn_{addr:X8}", addr, 4U, gteOps, $"void psx_fn_{addr:X8}(void);");
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
