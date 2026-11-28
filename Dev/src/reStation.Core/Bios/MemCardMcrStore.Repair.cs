// @module: reStation.Core.Bios.MemCardMcrStore.Repair
// @purpose: Safe .mcr mount scan, atomic contained .bak-<timestamp> backup, and orphaned-nameless-block reclaim with slot block summaries.
// @depends-on: System.Buffers.Binary, System.Globalization, Foundation.Core
// @exports: MemCardSlotSummary, MemCardRepairResult, MemCardMcrStore (Repair partial)

using System.Buffers.Binary;
using System.Globalization;
using Foundation.Core;

namespace reStation.Core.Bios;

public sealed record MemCardSlotSummary(
    int TotalBlocks,
    int OccupiedBlocks,
    int FreeBlocks,
    int OrphanedBlocks,
    IReadOnlyList<string> NamedEntries);

public sealed record MemCardRepairResult(
    int ReclaimedBlockCount,
    string? BackupPath,
    MemCardSlotSummary Summary);

public sealed partial class MemCardMcrStore
{
    public const int UserBlockCount = 15;

    /// <summary>Summarizes block allocation, free capacity, safely reclaimable orphans, and named entries for <paramref name="slot"/>.</summary>
    public MemCardSlotSummary GetSlotBlockSummary(int slot)
    {
        byte[] card = _slots[ValidateSlot(slot)];
        int free = 0;
        var named = new List<string>();
        for (int b = 1; b <= UserBlockCount; b++)
        {
            uint alloc = ReadAlloc(card, b);
            if (alloc == 0x000000A0U)
            {
                free++;
                continue;
            }
            if (alloc == 0x00000051U)
            {
                string name = ReadBlockName(card, b);
                if (!string.IsNullOrEmpty(name)) named.Add(name);
            }
        }
        int orphaned = HasValidHeader(card) ? CollectOrphanedNamelessBlocks(card).Count : 0;
        return new MemCardSlotSummary(UserBlockCount, UserBlockCount - free, free, orphaned, named);
    }

    /// <summary>
    /// Safely reclaims orphaned nameless/zero-magic blocks (0x51..0x53 chains with empty ASCII name and zeroed 0x0000 frame magic)
    /// after creating an atomic contained <c>.bak-&lt;timestamp&gt;</c> backup inside <see cref="MemcardsDirectory"/>.
    /// </summary>
    public MemCardRepairResult ReclaimOrphanedNamelessBlocks(int slot)
    {
        byte[] card = _slots[ValidateSlot(slot)];
        if (!HasValidHeader(card))
            return new MemCardRepairResult(0, null, GetSlotBlockSummary(slot));

        List<int> toReclaim = CollectOrphanedNamelessBlocks(card);
        if (toReclaim.Count == 0)
            return new MemCardRepairResult(0, null, GetSlotBlockSummary(slot));

        string slotPath = _slotPaths[slot];
        string? backupPath = CreateAtomicContainedBackup(slotPath);
        foreach (int blk in toReclaim)
        {
            int dir = blk * SectorSize;
            Array.Fill(card, (byte)0, dir, SectorSize);
            BinaryPrimitives.WriteUInt32LittleEndian(card.AsSpan(dir, 4), 0x000000A0U);
            BinaryPrimitives.WriteUInt16LittleEndian(card.AsSpan(dir + 8, 2), 0xFFFF);
            WriteXor(card, dir);
        }

        ClearSlotNewCardFlag(slot);
        FlushSlot(slot);

        string slotLabel = string.IsNullOrEmpty(slotPath) ? $"slot{slot + 1}" : Path.GetFileName(slotPath);
        string bakLabel = backupPath is not null ? Path.GetFileName(backupPath) : "none";
        _warnLogger?.Invoke($"[MEMCARD] Reclaimed {toReclaim.Count} orphaned nameless block(s) [{string.Join(",", toReclaim)}] on slot {slot + 1} ({slotLabel}); backup={bakLabel}");
        return new MemCardRepairResult(toReclaim.Count, backupPath, GetSlotBlockSummary(slot));
    }

    private static bool HasValidHeader(byte[] card) =>
        card.Length >= CardSize && card[0] == (byte)'M' && card[1] == (byte)'C';

    private static List<int> CollectOrphanedNamelessBlocks(byte[] card)
    {
        bool[] protectedBlocks = new bool[UserBlockCount + 1];

        // Pass 1: Protect every 0x51 save head with a non-empty ASCII name or valid 'SC' frame magic, plus its linked chain.
        for (int b = 1; b <= UserBlockCount; b++)
        {
            if (ReadAlloc(card, b) != 0x00000051U) continue;
            if (string.IsNullOrEmpty(ReadBlockName(card, b)) && !HasScFrameMagic(card, b)) continue;

            int cur = b;
            for (int step = 0; step < UserBlockCount && cur is >= 1 and <= UserBlockCount; step++)
            {
                if (protectedBlocks[cur]) break;
                uint curAlloc = ReadAlloc(card, cur);
                if (step > 0 && curAlloc is not (0x00000052U or 0x00000053U)) break;
                protectedBlocks[cur] = true;
                if (curAlloc == 0x00000053U) break;
                cur = ReadNextBlock(card, cur);
            }
        }

        // Pass 2: Mark unprotected nameless + zero-magic 0x51 chains and unreachable nameless + zero-magic 0x52/0x53 blocks.
        bool[] reclaimMask = new bool[UserBlockCount + 1];
        for (int b = 1; b <= UserBlockCount; b++)
        {
            if (protectedBlocks[b] || reclaimMask[b] || ReadAlloc(card, b) != 0x00000051U) continue;
            if (!IsEmptyNameAndZeroMagic(card, b)) continue;

            int cur = b;
            for (int step = 0; step < UserBlockCount && cur is >= 1 and <= UserBlockCount; step++)
            {
                if (protectedBlocks[cur] || reclaimMask[cur]) break;
                uint curAlloc = ReadAlloc(card, cur);
                if (step == 0 ? curAlloc != 0x00000051U : curAlloc is not (0x00000052U or 0x00000053U)) break;
                if (!IsEmptyNameAndZeroMagic(card, cur)) break;
                reclaimMask[cur] = true;
                if (curAlloc == 0x00000053U) break;
                cur = ReadNextBlock(card, cur);
            }
        }

        for (int b = 1; b <= UserBlockCount; b++)
        {
            if (protectedBlocks[b] || reclaimMask[b]) continue;
            uint alloc = ReadAlloc(card, b);
            if ((alloc == 0x00000052U || alloc == 0x00000053U) && IsEmptyNameAndZeroMagic(card, b))
                reclaimMask[b] = true;
        }

        var result = new List<int>();
        for (int b = 1; b <= UserBlockCount; b++)
            if (reclaimMask[b]) result.Add(b);
        return result;
    }

    private string? CreateAtomicContainedBackup(string slotPath)
    {
        if (string.IsNullOrEmpty(slotPath) || !File.Exists(slotPath)) return null;
        string safeSrc = AppEnvironment.AssertPathContained(slotPath, _memcardsDir);
        string stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture);
        string backupPath = AppEnvironment.AssertPathContained($"{safeSrc}.bak-{stamp}", _memcardsDir);
        if (File.Exists(backupPath))
            backupPath = AppEnvironment.AssertPathContained($"{safeSrc}.bak-{stamp}-{Guid.NewGuid().ToString("N")[..6]}", _memcardsDir);

        string tmpPath = AppEnvironment.AssertPathContained($"{backupPath}.{Guid.NewGuid():N}.tmp", _memcardsDir);
        try
        {
            File.Copy(safeSrc, tmpPath, overwrite: true);
            File.Move(tmpPath, backupPath, overwrite: false);
            return backupPath;
        }
        finally
        {
            if (File.Exists(tmpPath)) File.Delete(tmpPath);
        }
    }

    private static uint ReadAlloc(byte[] card, int blk) =>
        BinaryPrimitives.ReadUInt32LittleEndian(card.AsSpan(blk * SectorSize, 4));

    private static int ReadNextBlock(byte[] card, int blk)
    {
        ushort next = BinaryPrimitives.ReadUInt16LittleEndian(card.AsSpan((blk * SectorSize) + 8, 2));
        return next == 0xFFFF ? -1 : next + 1;
    }

    private static string ReadBlockName(byte[] card, int blk) =>
        ReadAsciiZ(card.AsSpan((blk * SectorSize) + 10, 21));

    private static bool HasScFrameMagic(byte[] card, int blk)
    {
        int dataOff = blk * BlockSize;
        return card[dataOff] == (byte)'S' && card[dataOff + 1] == (byte)'C';
    }

    private static bool HasZeroedFrameMagic(byte[] card, int blk)
    {
        int dataOff = blk * BlockSize;
        return card[dataOff] == 0x00 && card[dataOff + 1] == 0x00;
    }

    private static bool IsEmptyNameAndZeroMagic(byte[] card, int blk) =>
        string.IsNullOrEmpty(ReadBlockName(card, blk)) && HasZeroedFrameMagic(card, blk);
}
