// @module: reStation.Core.Saves.QuickSaveManager
// @purpose: Module-aware compressed quick-save snapshots ("RST1" magic, schema v1) persisted atomically under <dataDir>\saves\.
// @depends-on: System.IO.Compression, reStation.Core.Cpu
// @exports: QuickSaveManager
// @owner: devv-audio-mdec (T3) — public surface T1-locked (Player F5/F9 + fixtures consume it).

using System.IO.Compression;
using System.Text;
using reStation.Core.Cpu;

namespace reStation.Core.Saves;

public static class QuickSaveManager
{
    /// <summary>"RST1" little-endian magic.</summary>
    public const uint Magic = 0x31545352U;
    public const int SchemaVersion = 1;
    public const int MaxSlots = 10;

    public static string GetSlotPath(string dataDir, string moduleId, int slot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        if (slot is < 0 or >= MaxSlots) throw new ArgumentOutOfRangeException(nameof(slot));
        string safeId = string.Concat(moduleId.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(Path.GetFullPath(dataDir), "saves", $"{safeId}_slot{slot}.rst");
    }

    public static void Save(PsxCpu cpu, string dataDir, string moduleId, int slot)
    {
        ArgumentNullException.ThrowIfNull(cpu);
        string path = GetSlotPath(dataDir, moduleId, slot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var header = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: true))
            {
                header.Write(Magic);
                header.Write(SchemaVersion);
                header.Write(moduleId);
                header.Flush();
                using var gz = new GZipStream(fs, CompressionLevel.Fastest, leaveOpen: true);
                using var body = new BinaryWriter(gz, Encoding.UTF8, leaveOpen: true);
                cpu.WriteSnapshotState(body);
            }
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    public static bool TryLoad(PsxCpu cpu, string dataDir, string moduleId, int slot)
    {
        ArgumentNullException.ThrowIfNull(cpu);
        string path = GetSlotPath(dataDir, moduleId, slot);
        if (!File.Exists(path)) return false;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var header = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);
        if (header.ReadUInt32() != Magic || header.ReadInt32() != SchemaVersion) return false;
        if (!string.Equals(header.ReadString(), moduleId, StringComparison.Ordinal)) return false;
        using var gz = new GZipStream(fs, CompressionMode.Decompress, leaveOpen: true);
        using var body = new BinaryReader(gz, Encoding.UTF8, leaveOpen: true);
        cpu.ReadSnapshotState(body);
        return true;
    }
}
