// @module: reStation.Core.Saves.QuickSaveManager
// @purpose: Module-aware compressed quick-save snapshots ("RST1" magic, schema v1) persisted atomically under <dataDir>\saves\.
// @depends-on: System.IO.Compression, Foundation.Core, reStation.Core.Cpu
// @exports: QuickSaveSlotInfo, QuickSaveManager
// @owner: devv-audio-mdec (T3) — public surface T1-locked (Player F5/F9 + fixtures consume it).

using System.IO.Compression;
using System.Text;
using Foundation.Core;
using reStation.Core.Cpu;

namespace reStation.Core.Saves;

/// <summary>Metadata for one populated quick-save slot.</summary>
public sealed record QuickSaveSlotInfo(int Slot, string FilePath, long ByteLength, DateTime LastWriteUtc);

/// <summary>Outcome of loading an "RST1" (and optional "RSTX" trailer) quick-save or fixture snapshot.</summary>
public sealed record QuickSaveLoadResult(bool Success, string Format, IReadOnlyDictionary<string, byte[]>? RstxChunks = null);

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

        string savesDir = Path.Combine(Path.GetFullPath(dataDir), "saves");
        string safeId = string.Concat(moduleId.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == '.' ? '_' : c));
        if (string.IsNullOrWhiteSpace(safeId)) safeId = "module";
        return SandboxEnvironment.AssertPathContained(Path.Combine(savesDir, $"{safeId}_slot{slot}.rst"), savesDir);
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
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fs.Length < 12) return false;
            using var header = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);
            if (header.ReadUInt32() != Magic || header.ReadInt32() != SchemaVersion) return false;
            if (!string.Equals(header.ReadString(), moduleId, StringComparison.Ordinal)) return false;
            using var gz = new GZipStream(fs, CompressionMode.Decompress, leaveOpen: true);
            using var body = new BinaryReader(gz, Encoding.UTF8, leaveOpen: true);
            cpu.ReadSnapshotState(body);
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            return false;
        }
    }

    /// <summary>Lists all occupied quick-save slots (0..MaxSlots-1) for <paramref name="moduleId"/>.</summary>
    public static IReadOnlyList<QuickSaveSlotInfo> ListSlots(string dataDir, string moduleId)
    {
        var list = new List<QuickSaveSlotInfo>();
        for (int slot = 0; slot < MaxSlots; slot++)
        {
            string path = GetSlotPath(dataDir, moduleId, slot);
            if (!File.Exists(path)) continue;
            var info = new FileInfo(path);
            list.Add(new QuickSaveSlotInfo(slot, path, info.Length, info.LastWriteTimeUtc));
        }
        return list;
    }

    /// <summary>Deletes a quick-save slot if present; returns true if a file was deleted.</summary>
    public static bool Delete(string dataDir, string moduleId, int slot)
    {
        string path = GetSlotPath(dataDir, moduleId, slot);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    /// <summary>Loads an "RST1" (plus optional "RSTX" trailer in Slice 1) snapshot file into <paramref name="cpu"/>.</summary>
    public static QuickSaveLoadResult LoadFromFile(PsxCpu cpu, string path, ReStationLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(cpu);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string full = Path.GetFullPath(path);
        if (!File.Exists(full))
        {
            logger?.Warn("SAVE", $"Snapshot file not found: '{full}'.");
            return new QuickSaveLoadResult(false, "MISSING");
        }

        using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (fs.Length < 12) throw new InvalidDataException($"Snapshot '{Path.GetFileName(full)}' truncated (< 12 bytes).");
        using var hdr = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);
        if (hdr.ReadUInt32() != Magic || hdr.ReadInt32() != SchemaVersion)
            throw new InvalidDataException($"Snapshot '{Path.GetFileName(full)}' has invalid RST1 header.");
        _ = hdr.ReadString();
        using var gz = new GZipStream(fs, CompressionMode.Decompress, leaveOpen: true);
        using var body = new BinaryReader(gz, Encoding.UTF8, leaveOpen: true);
        cpu.ReadSnapshotState(body);
        cpu.Bus.NotifyRamWriteRange(0, PsxMemoryBus.RamSize);
        logger?.Info("SAVE", $"Loaded '{Path.GetFileName(full)}' (format=RST1, frame={cpu.FrameCount}, chunks=none).");
        return new QuickSaveLoadResult(true, "RST1");
    }

    /// <summary>Atomically writes a compressed "RST1" snapshot to <paramref name="path"/> (with optional "RSTX" trailer support in Slice 1).</summary>
    public static void SaveToFileWithTrailer(
        PsxCpu cpu,
        string path,
        string moduleId,
        IReadOnlyDictionary<string, byte[]>? rstxChunks = null,
        ReStationLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(cpu);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        _ = rstxChunks;
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string tmp = $"{full}.{Guid.NewGuid():N}.tmp";
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
            File.Move(tmp, full, overwrite: true);
            logger?.Info("SAVE", $"Saved '{Path.GetFileName(full)}' (format=RST1, frame={cpu.FrameCount}, chunks=none).");
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }
}
