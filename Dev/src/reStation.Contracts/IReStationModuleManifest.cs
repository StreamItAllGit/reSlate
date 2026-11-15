// @module: reStation.Contracts.IReStationModuleManifest
// @purpose: Locked PS1 .recomp module manifest contract + Standard 18 camelCase JSON DTO (module.recomp.json) compatible with reEngine RecompPackageReader.
// @depends-on: System.Text.Json, Foundation.Retro
// @exports: ReStationEngineInfo, IReStationModuleManifest, ReStationModuleManifest

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Foundation.Retro;

namespace reStation.Contracts;

/// <summary>Canonical reStation engine identity constants shared by Player, Compiler, Installer, and module manifests.</summary>
public static class ReStationEngineInfo
{
    /// <summary>Lowercase Standard 18 application identifier.</summary>
    public const string AppId = "restation";
    /// <summary>reEngine side-by-side engine identifier.</summary>
    public const string EngineId = "reStation";
    /// <summary>Current engine SemVer.</summary>
    public const string Version = "0.2.6";
    /// <summary>Compatible engine track advertised to reEngine.</summary>
    public const string CompatibleEngineTrack = "0.2.x";
    /// <summary>Foundation.Retro platform identifier.</summary>
    public const string PlatformId = "ps1";
    /// <summary>Default gitignored user media preload directory relative to the module root.</summary>
    public const string DefaultPreloadRomDir = "data/rom";
    /// <summary>Default relative AOT recipe file embedded in .recomp packages.</summary>
    public const string DefaultAotRecipePath = "aot-recipe.json";
    /// <summary>Engine tracks supported by this host release (primary track first).</summary>
    public static IReadOnlyList<string> SupportedEngineTracks { get; } = ["0.2.x", "0.1.x"];
}

/// <summary>PS1-specific extension of <see cref="IRecompModuleManifest"/> describing a dynamically loaded .recomp game module.</summary>
public interface IReStationModuleManifest : IRecompModuleManifest
{
    /// <summary>Disc product/serial code (e.g. a SLUS/SCUS identifier) used for memory-card and save naming.</summary>
    string SerialNumber { get; }
    /// <summary>Module assembly file name relative to the manifest directory.</summary>
    string EntryAssembly { get; }
    /// <summary>Boot PS-X EXE path inside extracted gamedata.</summary>
    string DefaultBootExecutable { get; }
    /// <summary>Fully qualified type implementing <see cref="IRecompGameModule"/>.</summary>
    string ModuleTypeName { get; }
    /// <summary>Default host timing mode requested by the module.</summary>
    RecompTimingMode DefaultFramerateMode { get; }
    /// <summary>Minimum compatible engine SemVer.</summary>
    string EngineVersion { get; }
    /// <summary>Compatible engine track selector (e.g. 0.2.x).</summary>
    string CompatibleEngineTrack { get; }
    /// <summary>Relative preload media directory.</summary>
    string PreloadRomDir { get; }
    /// <summary>Deterministic multi-scene save-state fixtures.</summary>
    IReadOnlyList<SaveStateFixtureSpec> SaveStateFixtures { get; }
    /// <summary>Engine tracks accepted by this module (defaults to <see cref="CompatibleEngineTrack"/> for v0.1.5 implementers).</summary>
    IReadOnlyList<string> SupportedEngineTracks => string.IsNullOrWhiteSpace(CompatibleEngineTrack)
        ? [ReStationEngineInfo.CompatibleEngineTrack]
        : [CompatibleEngineTrack];
    /// <summary>Standard 18 <c>executionTierMode</c> selector (defaults to <c>aotWithJitFallback</c>).</summary>
    string ExecutionTierMode => ReStationExecutionTierModes.Default;
    /// <summary>Foundation <see cref="RecompExecutionTierMode"/> corresponding to <see cref="ExecutionTierMode"/>.</summary>
    RecompExecutionTierMode FoundationExecutionTier => ReStationExecutionTierModes.ToFoundationTierMode(ExecutionTierMode);
    /// <summary>Optional relative path to <c>aot-recipe.json</c> inside the module package.</summary>
    string? AotRecipe => null;
    /// <summary>Optional inline AOT recipe metadata carried directly in the manifest.</summary>
    AotRecipeSpec? AotRecipeSpec => null;
}

/// <summary>Serializable module.recomp.json DTO (Standard 18 camelCase keys; superset of reEngine RecompPackageManifest).</summary>
public sealed class ReStationModuleManifest : IReStationModuleManifest
{
    /// <summary>Shared camelCase JSON options (case-insensitive read, indented write, camelCase enums).</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>Manifest schema version.</summary>
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    /// <inheritdoc/>
    [JsonPropertyName("moduleId")] public string ModuleId { get; set; } = string.Empty;
    /// <summary>reEngine-facing title.</summary>
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
    /// <inheritdoc/>
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = string.Empty;
    /// <inheritdoc/>
    [JsonPropertyName("platformId")] public string PlatformId { get; set; } = ReStationEngineInfo.PlatformId;
    /// <inheritdoc/>
    [JsonPropertyName("version")] public string Version { get; set; } = "0.1.0";
    /// <summary>Target engine identifier (always reStation).</summary>
    [JsonPropertyName("engineId")] public string EngineId { get; set; } = ReStationEngineInfo.EngineId;
    /// <inheritdoc/>
    [JsonPropertyName("engineVersion")] public string EngineVersion { get; set; } = ReStationEngineInfo.Version;
    /// <inheritdoc/>
    [JsonPropertyName("compatibleEngineTrack")] public string CompatibleEngineTrack { get; set; } = ReStationEngineInfo.CompatibleEngineTrack;
    /// <summary>Optional multi-track engine compatibility list (for modules supporting both <c>0.1.x</c> and <c>0.2.x</c>).</summary>
    [JsonPropertyName("supportedEngineTracks")] public IReadOnlyList<string>? SupportedEngineTracks { get; set; }
    /// <inheritdoc/>
    [JsonPropertyName("preloadRomDir")] public string PreloadRomDir { get; set; } = ReStationEngineInfo.DefaultPreloadRomDir;
    /// <summary>Standard 18 <c>executionTierMode</c> selector (defaults to <c>aotWithJitFallback</c>).</summary>
    [JsonPropertyName("executionTierMode")] public string ExecutionTierMode { get; set; } = ReStationExecutionTierModes.Default;
    /// <inheritdoc/>
    [JsonPropertyName("aotRecipe")] public string? AotRecipe { get; set; }
    /// <inheritdoc/>
    [JsonPropertyName("aotRecipeSpec")] public AotRecipeSpec? AotRecipeSpec { get; set; }
    /// <summary>Optional reEngine entry executable override.</summary>
    [JsonPropertyName("entryExecutable")] public string? EntryExecutable { get; set; }
    /// <inheritdoc/>
    [JsonPropertyName("serialNumber")] public string SerialNumber { get; set; } = string.Empty;
    /// <inheritdoc/>
    [JsonPropertyName("entryAssembly")] public string EntryAssembly { get; set; } = string.Empty;
    /// <inheritdoc/>
    [JsonPropertyName("defaultBootExecutable")] public string DefaultBootExecutable { get; set; } = string.Empty;
    /// <inheritdoc/>
    [JsonPropertyName("moduleTypeName")] public string ModuleTypeName { get; set; } = string.Empty;
    /// <inheritdoc/>
    [JsonPropertyName("defaultFramerateMode")] public RecompTimingMode DefaultFramerateMode { get; set; } = RecompTimingMode.LockedCap;
    /// <inheritdoc/>
    [JsonPropertyName("requiredDiscChecksums")] public IReadOnlyList<DiscChecksumEntry> RequiredDiscChecksums { get; set; } = Array.Empty<DiscChecksumEntry>();
    /// <inheritdoc/>
    [JsonPropertyName("saveStateFixtures")] public IReadOnlyList<SaveStateFixtureSpec> SaveStateFixtures { get; set; } = Array.Empty<SaveStateFixtureSpec>();
    /// <summary>Forward-compatible unknown keys preserved across round-trips.</summary>
    [JsonExtensionData] public IDictionary<string, JsonElement>? ExtensionData { get; set; }
    /// <summary>Foundation <see cref="RecompExecutionTierMode"/> corresponding to <see cref="ExecutionTierMode"/>.</summary>
    [JsonIgnore] public RecompExecutionTierMode FoundationExecutionTier => ReStationExecutionTierModes.ToFoundationTierMode(ExecutionTierMode);
    /// <inheritdoc/>
    IReadOnlyList<string> IReStationModuleManifest.SupportedEngineTracks =>
        SupportedEngineTracks is { Count: > 0 }
            ? SupportedEngineTracks
            : (string.IsNullOrWhiteSpace(CompatibleEngineTrack) ? [ReStationEngineInfo.CompatibleEngineTrack] : [CompatibleEngineTrack]);
    /// <summary>Full path to the ZIP <c>.recomp</c> cartridge when loaded from an archive; <see langword="null"/> for loose JSON files.</summary>
    [JsonIgnore] public string? ResolvedPackagePath { get; set; }

    /// <summary>Loads, normalizes (title/displayName fallback), and validates a loose <c>module.recomp.json</c> or packaged ZIP <c>.recomp</c> file.</summary>
    public static ReStationModuleManifest LoadFromFile(string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        string full = Path.GetFullPath(manifestPath);
        string jsonText;
        string? packagePath = null;
        if (IsZipArchiveFile(full))
        {
            using ZipArchive zip = ZipFile.OpenRead(full);
            ZipArchiveEntry entry = zip.Entries.FirstOrDefault(e => string.Equals(e.FullName, "module.recomp.json", StringComparison.OrdinalIgnoreCase))
                ?? zip.Entries.FirstOrDefault(e => string.Equals(e.Name, "module.recomp.json", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException($"Package archive '{full}' does not contain 'module.recomp.json'.");
            using StreamReader reader = new(entry.Open(), Encoding.UTF8);
            jsonText = reader.ReadToEnd();
            packagePath = full;
        }
        else
        {
            jsonText = File.ReadAllText(full, Encoding.UTF8);
        }

        ReStationModuleManifest manifest = JsonSerializer.Deserialize<ReStationModuleManifest>(jsonText, JsonOptions)
            ?? throw new InvalidDataException($"Module manifest '{full}' deserialized to null.");
        manifest.ResolvedPackagePath = packagePath;
        if (string.IsNullOrWhiteSpace(manifest.DisplayName)) manifest.DisplayName = manifest.Title;
        if (string.IsNullOrWhiteSpace(manifest.Title)) manifest.Title = manifest.DisplayName;
        if (string.IsNullOrWhiteSpace(manifest.PreloadRomDir)) manifest.PreloadRomDir = ReStationEngineInfo.DefaultPreloadRomDir;
        if (string.IsNullOrWhiteSpace(manifest.ExecutionTierMode))
            manifest.ExecutionTierMode = ReStationExecutionTierModes.Default;
        else if (ReStationExecutionTierModes.TryNormalize(manifest.ExecutionTierMode, out string normalizedTier))
            manifest.ExecutionTierMode = normalizedTier;
        manifest.Validate(full);
        return manifest;
    }

    private static bool IsZipArchiveFile(string fullPath)
    {
        if (!File.Exists(fullPath)) return false;
        using FileStream fs = File.OpenRead(fullPath);
        if (fs.Length < 4) return false;
        Span<byte> hdr = stackalloc byte[4];
        return fs.Read(hdr) == 4 && hdr[0] == (byte)'P' && hdr[1] == (byte)'K' && hdr[2] == 3 && hdr[3] == 4;
    }

    /// <summary>Atomically writes the manifest as UTF-8 (no BOM) camelCase JSON via .tmp + move.</summary>
    public void SaveToFile(string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        string full = Path.GetFullPath(manifestPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string tmp = $"{full}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions), new UTF8Encoding(false));
        File.Move(tmp, full, overwrite: true);
    }

    /// <summary>Fail-closed validation of required routing keys; throws <see cref="InvalidDataException"/>.</summary>
    public void Validate(string sourceLabel = "module.recomp.json")
    {
        if (SchemaVersion < 1) throw new InvalidDataException($"'{sourceLabel}' has invalid schemaVersion ({SchemaVersion}).");
        foreach ((string key, string value) in new[] { ("moduleId", ModuleId), ("engineId", EngineId), ("engineVersion", EngineVersion), ("compatibleEngineTrack", CompatibleEngineTrack) })
            if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException($"'{sourceLabel}' is missing required '{key}'.");
        if (!string.Equals(EngineId, ReStationEngineInfo.EngineId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"'{sourceLabel}' targets engine '{EngineId}', expected '{ReStationEngineInfo.EngineId}'.");
        if (Path.IsPathRooted(PreloadRomDir)) throw new InvalidDataException($"'{sourceLabel}' preloadRomDir must be relative.");
        if (!ReStationExecutionTierModes.TryNormalize(ExecutionTierMode, out _))
            throw new InvalidDataException($"'{sourceLabel}' has unsupported executionTierMode '{ExecutionTierMode}'.");
        if (!string.IsNullOrWhiteSpace(AotRecipe) && Path.IsPathRooted(AotRecipe))
            throw new InvalidDataException($"'{sourceLabel}' aotRecipe must be relative.");
        AotRecipeSpec?.Validate($"{sourceLabel}:aotRecipeSpec");
    }
}
