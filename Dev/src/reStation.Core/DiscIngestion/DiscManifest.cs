// @module: reStation.Core.DiscIngestion.DiscManifest
// @purpose: Verified disc manifest (gamedata\disc_manifest.json) mapping ISO9660 files to LBAs for CD-ROM streaming and EXE loading.
// @depends-on: System.Text.Json
// @exports: DiscFileEntry, DiscManifest, DiscIngestResult
// @owner: devv-audio-mdec (T3) — public surface T1-locked (PsxCpu/PsxCdRom/PsxBiosHle consume it).

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace reStation.Core.DiscIngestion;

/// <summary>One ISO9660 file on the verified disc.</summary>
public sealed class DiscFileEntry
{
    [JsonPropertyName("relativeFile")] public string RelativeFile { get; set; } = string.Empty;
    [JsonPropertyName("lba")] public uint Lba { get; set; }
    [JsonPropertyName("byteLength")] public uint ByteLength { get; set; }
    [JsonPropertyName("sectorCount")] public uint SectorCount { get; set; }
    /// <summary>True for Form2/XA/raw MDEC streams that must be read sector-by-sector from the image.</summary>
    [JsonPropertyName("isForm2Raw")] public bool IsForm2Raw { get; set; }
    [JsonPropertyName("sha256Hex")] public string Sha256 { get; set; } = string.Empty;
}

/// <summary>Module-aware disc manifest persisted under <c>&lt;dataDir&gt;\gamedata\</c>.</summary>
public sealed class DiscManifest
{
    public const string FileName = "disc_manifest.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("moduleId")] public string ModuleId { get; set; } = string.Empty;
    [JsonPropertyName("serialNumber")] public string SerialNumber { get; set; } = string.Empty;
    [JsonPropertyName("volumeId")] public string VolumeId { get; set; } = string.Empty;
    /// <summary>Absolute path of the verified primary raw image (.bin/.iso) used for raw sector reads.</summary>
    [JsonPropertyName("primaryImagePath")] public string PrimaryImagePath { get; set; } = string.Empty;
    [JsonPropertyName("cueSheetPath")] public string? CueSheetPath { get; set; }
    [JsonPropertyName("verifiedAtUtc")] public string VerifiedAtUtc { get; set; } = string.Empty;
    [JsonPropertyName("files")] public List<DiscFileEntry> Files { get; set; } = new();

    public static string GetGameDataDir(string dataDir) => Path.Combine(Path.GetFullPath(dataDir), "gamedata");
    public static string GetManifestPath(string dataDir) => Path.Combine(GetGameDataDir(dataDir), FileName);

    public static DiscManifest? TryLoad(string dataDir)
    {
        string path = GetManifestPath(dataDir);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<DiscManifest>(File.ReadAllText(path, Encoding.UTF8), JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Save(string dataDir)
    {
        string path = GetManifestPath(dataDir);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Finds the file whose sector range contains <paramref name="lba"/>.</summary>
    public DiscFileEntry? FindByLba(uint lba) =>
        Files.FirstOrDefault(f => lba >= f.Lba && lba < f.Lba + Math.Max(1U, f.SectorCount));

    /// <summary>Finds a file by name, ignoring case, leading separators, and ISO9660 ";1" version suffixes.</summary>
    public DiscFileEntry? FindByName(string name)
    {
        string wanted = Normalize(name);
        return Files.FirstOrDefault(f => string.Equals(Normalize(f.RelativeFile), wanted, StringComparison.OrdinalIgnoreCase));
    }

    private static string Normalize(string name)
    {
        string n = name.Replace('\\', '/').TrimStart('/');
        int semi = n.IndexOf(';');
        return semi >= 0 ? n[..semi] : n;
    }
}

/// <summary>Outcome of verifying/extracting user-supplied disc media.</summary>
public sealed record DiscIngestResult(bool Success, string Message, DiscManifest? Manifest);
