// @module: reStation.Core.Bios.MemCardMcrStore
// @purpose: Standard 128 KB PS1 Slot 1+2 .mcr Memory Card store under <dataDir>\memcards\ with legacy migration and VBlank-debounced atomic flush.
// @depends-on: System.Buffers.Binary, System.IO.Compression, System.Text, Foundation.Core
// @exports: MemCardMcrStore

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Foundation.Core;

namespace reStation.Core.Bios;

public sealed class MemCardMcrStore
{
    public const int CardSize = 131_072; // 128 KB: 16 blocks x 64 frames x 128 bytes
    public const int BlockSize = 8192;
    public const int SectorSize = 128;
    public const int SectorCount = 1024;
    public const int SlotCount = 2;
    public const byte FlagNewCard = 0x08;

    private readonly string _dataDir;
    private readonly string _memcardsDir;
    private readonly byte[][] _slots = [new byte[CardSize], new byte[CardSize]];
    private readonly string[] _slotPaths = [string.Empty, string.Empty];
    private readonly byte[] _slotFlags = [FlagNewCard, FlagNewCard];
    private readonly bool[] _dirty = new bool[SlotCount];
    private long _lastFlushedVBlank = -1;

    public MemCardMcrStore(string dataDir, string? cardIdentity = null, string? serialFallback = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDir);
        _dataDir = Path.GetFullPath(dataDir);
        _memcardsDir = AppEnvironment.AssertPathContained(Path.Combine(_dataDir, "memcards"), _dataDir);
        Directory.CreateDirectory(_memcardsDir);
        MigrateLegacySavesNonDestructive();
        SetCardIdentity(cardIdentity, serialFallback);
    }

    public string MemcardsDirectory => _memcardsDir;
    public string ActiveIdentity { get; private set; } = string.Empty;
    public byte[] CardData => _slots[0];
    public string McrPath => _slotPaths[0];
    public string Slot1Path => _slotPaths[0];
    public string Slot2Path => _slotPaths[1];
    public bool HasDirtySlots => _dirty[0] || _dirty[1];
    public int DiskFlushCount { get; private set; }

    public byte[] GetSlotData(int slot) => _slots[ValidateSlot(slot)];
    public string GetMcrPath(int slot) => _slotPaths[ValidateSlot(slot)];
    public byte GetSlotFlag(int slot) => _slotFlags[ValidateSlot(slot)];
    public void SetSlotFlag(int slot, byte flag) => _slotFlags[ValidateSlot(slot)] = flag;
    public bool IsSlotNewCard(int slot) => (_slotFlags[ValidateSlot(slot)] & FlagNewCard) != 0;
    public void ClearSlotNewCardFlag(int slot) => _slotFlags[ValidateSlot(slot)] &= unchecked((byte)~FlagNewCard);
    public void AcknowledgeNewCard() { _slotFlags[0] &= unchecked((byte)~FlagNewCard); _slotFlags[1] &= unchecked((byte)~FlagNewCard); }
    public bool IsDirty(int slot) => _dirty[ValidateSlot(slot)];
    public void MarkDirty(int slot = 0) => _dirty[ValidateSlot(slot)] = true;

    /// <summary>Switches active Slot 1+2 backing .mcr images, prioritizing <paramref name="cardIdentity"/> (moduleId) over <paramref name="serialFallback"/>.</summary>
    public void SetCardIdentity(string? cardIdentity, string? serialFallback = null)
    {
        if (HasDirtySlots) FlushIfDirty();
        MigrateLegacySavesNonDestructive();
        string safePrimary = SanitizeIdentity(cardIdentity);
        string safe = !string.IsNullOrEmpty(safePrimary) ? safePrimary : SanitizeIdentity(serialFallback);
        ActiveIdentity = safe;
        for (int s = 0; s < SlotCount; s++)
        {
            string name = string.IsNullOrEmpty(safe) ? $"memcard_slot{s + 1}.mcr" : $"memcard_{safe}_slot{s + 1}.mcr";
            string path = AppEnvironment.AssertPathContained(Path.Combine(_memcardsDir, name), _memcardsDir);
            _slotPaths[s] = path;
            _dirty[s] = false;
            _slotFlags[s] = FlagNewCard;
            if (TryReadExistingCard(path, _slots[s])) continue;
            Format(s);
        }
    }

    public void Format() => Format(0);

    public void Format(int slot)
    {
        byte[] card = _slots[ValidateSlot(slot)];
        Array.Fill(card, (byte)0x00);
        card[0] = (byte)'M';
        card[1] = (byte)'C';
        WriteXor(card, 0);
        for (int i = 1; i <= 15; i++)
        {
            int off = i * SectorSize;
            BinaryPrimitives.WriteUInt32LittleEndian(card.AsSpan(off, 4), 0x000000A0U);
            BinaryPrimitives.WriteUInt16LittleEndian(card.AsSpan(off + 8, 2), 0xFFFF);
            WriteXor(card, off);
        }
        for (int i = 16; i <= 35; i++)
        {
            int off = i * SectorSize;
            BinaryPrimitives.WriteUInt32LittleEndian(card.AsSpan(off, 4), 0xFFFFFFFFU);
            BinaryPrimitives.WriteUInt16LittleEndian(card.AsSpan(off + 8, 2), 0xFFFF);
            WriteXor(card, off);
        }
        FlushSlot(slot);
    }

    public bool ReadSector(int slot, int sector, Span<byte> destination)
    {
        byte[] card = _slots[ValidateSlot(slot)];
        if (sector is < 0 or >= SectorCount || destination.Length < SectorSize) return false;
        card.AsSpan(sector * SectorSize, SectorSize).CopyTo(destination);
        return true;
    }

    public bool WriteSector(int slot, int sector, ReadOnlySpan<byte> source)
    {
        byte[] card = _slots[ValidateSlot(slot)];
        if (sector is < 0 or >= SectorCount || source.Length < SectorSize) return false;
        source[..SectorSize].CopyTo(card.AsSpan(sector * SectorSize, SectorSize));
        MarkDirty(slot);
        ClearSlotNewCardFlag(slot);
        return true;
    }

    public List<(string Name, int SizeBytes, int FirstBlock)> ListFiles(string pattern)
    {
        int slot = ResolveSlot(pattern, out string cleanPat);
        byte[] card = _slots[slot];
        var results = new List<(string, int, int)>();
        for (int block = 1; block <= 15; block++)
        {
            int off = block * SectorSize;
            if (BinaryPrimitives.ReadUInt32LittleEndian(card.AsSpan(off, 4)) != 0x00000051U) continue;
            string fname = ReadAsciiZ(card.AsSpan(off + 10, 21));
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(card.AsSpan(off + 4, 4));
            if (MatchesWildcard(fname, cleanPat)) results.Add((fname, size, block));
        }
        return results;
    }

    public byte[]? ReadFile(string rawName)
    {
        int slot = ResolveSlot(rawName, out string target);
        byte[] card = _slots[slot];
        foreach (var f in ListFiles(slot == 1 ? "bu10:*" : "*"))
        {
            if (!string.Equals(f.Name, target, StringComparison.OrdinalIgnoreCase)) continue;
            int count = Math.Clamp((f.SizeBytes + BlockSize - 1) / BlockSize, 1, 15);
            byte[] buf = new byte[f.SizeBytes];
            int copied = 0, blk = f.FirstBlock;
            for (int i = 0; i < count && blk is >= 1 and <= 15; i++)
            {
                int take = Math.Min(BlockSize, buf.Length - copied);
                Array.Copy(card, blk * BlockSize, buf, copied, take);
                copied += take;
                ushort next = BinaryPrimitives.ReadUInt16LittleEndian(card.AsSpan(blk * SectorSize + 8, 2));
                blk = next == 0xFFFF ? -1 : next + 1;
            }
            return buf;
        }
        return null;
    }

    public bool WriteFile(string rawName, ReadOnlySpan<byte> content)
    {
        int slot = ResolveSlot(rawName, out string name);
        byte[] card = _slots[slot];
        Erase(rawName);
        int blocksNeeded = Math.Max(1, (content.Length + BlockSize - 1) / BlockSize);
        var free = new List<int>();
        for (int b = 1; b <= 15; b++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(card.AsSpan(b * SectorSize, 4)) == 0x000000A0U)
            {
                free.Add(b);
                if (free.Count == blocksNeeded) break;
            }
        }
        if (free.Count < blocksNeeded) return false;
        int written = 0;
        for (int i = 0; i < blocksNeeded; i++)
        {
            int blk = free[i], dir = blk * SectorSize;
            Array.Fill(card, (byte)0, dir, SectorSize);
            BinaryPrimitives.WriteUInt32LittleEndian(card.AsSpan(dir, 4), i == 0 ? 0x51U : (i == blocksNeeded - 1 ? 0x53U : 0x52U));
            BinaryPrimitives.WriteUInt32LittleEndian(card.AsSpan(dir + 4, 4), (uint)(i == 0 ? blocksNeeded * BlockSize : 0));
            BinaryPrimitives.WriteUInt16LittleEndian(card.AsSpan(dir + 8, 2), i + 1 < blocksNeeded ? (ushort)(free[i + 1] - 1) : (ushort)0xFFFF);
            if (i == 0)
            {
                byte[] ascii = Encoding.ASCII.GetBytes(name);
                ascii.AsSpan(0, Math.Min(ascii.Length, 20)).CopyTo(card.AsSpan(dir + 10));
            }
            WriteXor(card, dir);
            int take = Math.Min(BlockSize, content.Length - written);
            Array.Fill(card, (byte)0, blk * BlockSize, BlockSize);
            if (take > 0) { content.Slice(written, take).CopyTo(card.AsSpan(blk * BlockSize, take)); written += take; }
        }
        ClearSlotNewCardFlag(slot);
        FlushSlot(slot);
        return true;
    }

    public bool Erase(string rawName)
    {
        int slot = ResolveSlot(rawName, out string target);
        byte[] card = _slots[slot];
        bool erased = false;
        for (int b = 1; b <= 15; b++)
        {
            int dir = b * SectorSize;
            if (BinaryPrimitives.ReadUInt32LittleEndian(card.AsSpan(dir, 4)) != 0x51U) continue;
            if (!string.Equals(ReadAsciiZ(card.AsSpan(dir + 10, 21)), target, StringComparison.OrdinalIgnoreCase)) continue;
            int cur = b;
            while (cur is >= 1 and <= 15)
            {
                int cdir = cur * SectorSize;
                ushort next = BinaryPrimitives.ReadUInt16LittleEndian(card.AsSpan(cdir + 8, 2));
                Array.Fill(card, (byte)0, cdir, SectorSize);
                BinaryPrimitives.WriteUInt32LittleEndian(card.AsSpan(cdir, 4), 0xA0U);
                BinaryPrimitives.WriteUInt16LittleEndian(card.AsSpan(cdir + 8, 2), 0xFFFF);
                WriteXor(card, cdir);
                cur = next == 0xFFFF ? -1 : next + 1;
            }
            erased = true;
        }
        if (erased) { ClearSlotNewCardFlag(slot); FlushSlot(slot); }
        return erased;
    }

    public void FlushIfDirty(long vblankSequence)
    {
        if (!HasDirtySlots || vblankSequence == _lastFlushedVBlank) return;
        _lastFlushedVBlank = vblankSequence;
        FlushIfDirty();
    }

    public void FlushIfDirty()
    {
        for (int s = 0; s < SlotCount; s++)
            if (_dirty[s]) FlushSlot(s);
    }

    public void Flush()
    {
        FlushSlot(0);
        if (_dirty[1] || !File.Exists(_slotPaths[1])) FlushSlot(1);
    }

    internal void WriteSnapshotState(BinaryWriter w)
    {
        w.Write(ActiveIdentity);
        for (int s = 0; s < SlotCount; s++)
        {
            w.Write(_slotFlags[s]);
            w.Write(_dirty[s]);
            w.Write(_dirty[s]);
            if (_dirty[s])
            {
                using var gzOut = new MemoryStream();
                using (var gz = new GZipStream(gzOut, CompressionLevel.Fastest, leaveOpen: true)) gz.Write(_slots[s], 0, CardSize);
                byte[] compressed = gzOut.ToArray();
                w.Write(compressed.Length);
                w.Write(compressed);
            }
        }
    }

    internal void ReadSnapshotState(BinaryReader r)
    {
        string savedIdentity = r.ReadString();
        if (!string.IsNullOrEmpty(savedIdentity) && string.IsNullOrEmpty(ActiveIdentity))
            SetCardIdentity(savedIdentity);
        for (int s = 0; s < SlotCount; s++)
        {
            _slotFlags[s] = r.ReadByte();
            _dirty[s] = r.ReadBoolean();
            bool hasImage = r.ReadBoolean();
            if (hasImage)
            {
                int compLen = r.ReadInt32();
                if (compLen is <= 0 or > CardSize * 2) throw new InvalidDataException($"Invalid MCD2 compressed length {compLen}.");
                byte[] comp = r.ReadBytes(compLen);
                if (comp.Length != compLen) throw new EndOfStreamException("Truncated MCD2 slot payload.");
                using var ms = new MemoryStream(comp, writable: false);
                using var gz = new GZipStream(ms, CompressionMode.Decompress);
                gz.ReadExactly(_slots[s]);
            }
        }
        _lastFlushedVBlank = -1;
    }

    private void MigrateLegacySavesNonDestructive()
    {
        string legacySavesDir = Path.Combine(_dataDir, "saves");
        if (!Directory.Exists(legacySavesDir)) return;
        foreach (string src in Directory.EnumerateFiles(legacySavesDir, "memcard_*.mcr", SearchOption.TopDirectoryOnly))
        {
            string safeSrc = AppEnvironment.AssertPathContained(src, legacySavesDir);
            string dest = AppEnvironment.AssertPathContained(Path.Combine(_memcardsDir, Path.GetFileName(safeSrc)), _memcardsDir);
            if (!File.Exists(dest)) File.Copy(safeSrc, dest, overwrite: false);
        }
    }

    private static bool TryReadExistingCard(string path, byte[] dst)
    {
        for (int attempt = 0; attempt < 6; attempt++)
        {
            if (!File.Exists(path)) return false;
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (fs.Length != CardSize) return false;
                fs.ReadExactly(dst, 0, CardSize);
                return true;
            }
            catch (Exception ex) when (attempt < 5 && ex is IOException or UnauthorizedAccessException) { Thread.Sleep(15); }
        }
        return false;
    }

    private void FlushSlot(int slot)
    {
        string path = _slotPaths[slot];
        if (string.IsNullOrEmpty(path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(tmp, _slots[slot]);
            for (int attempt = 0; attempt < 6; attempt++)
            {
                try { File.Move(tmp, path, overwrite: true); break; }
                catch (Exception ex) when (attempt < 5 && ex is IOException or UnauthorizedAccessException) { Thread.Sleep(15); }
            }
            _dirty[slot] = false;
            DiskFlushCount++;
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    private static int ValidateSlot(int slot) => slot is >= 0 and < SlotCount ? slot : throw new ArgumentOutOfRangeException(nameof(slot));
    private static int ResolveSlot(string raw, out string cleanName)
    {
        int colon = raw.IndexOf(':');
        if (colon >= 0)
        {
            string prefix = raw[..colon].Trim();
            cleanName = raw[(colon + 1)..];
            return prefix.Equals("bu10", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        }
        cleanName = raw;
        return 0;
    }

    private static void WriteXor(byte[] card, int frameOff)
    {
        byte x = 0;
        for (int i = 0; i < 127; i++) x ^= card[frameOff + i];
        card[frameOff + 127] = x;
    }

    private static string SanitizeIdentity(string? identity)
    {
        if (string.IsNullOrWhiteSpace(identity)) return string.Empty;
        char[] bad = Path.GetInvalidFileNameChars();
        return string.Concat(identity.Trim().Select(c => bad.Contains(c) || c == ' ' ? '_' : c));
    }

    private static string ReadAsciiZ(ReadOnlySpan<byte> span) { int idx = span.IndexOf((byte)0); return Encoding.ASCII.GetString(idx >= 0 ? span[..idx] : span); }
    private static bool MatchesWildcard(string text, string pattern) =>
        string.IsNullOrEmpty(pattern) || pattern == "*" || (pattern.EndsWith('*')
            ? text.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
            : string.Equals(text, pattern, StringComparison.OrdinalIgnoreCase));
}
