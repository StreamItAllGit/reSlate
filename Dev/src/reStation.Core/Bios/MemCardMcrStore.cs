// @module: reStation.Core.Bios.MemCardMcrStore
// @purpose: Standard 128 KB PS1 .mcr Memory Card image store parameterized by moduleId/serialNumber for bu00:* BIOS file I/O.
// @depends-on: System.Buffers.Binary, System.Text
// @exports: MemCardMcrStore
// @owner: devv-cpu-cd (T2)

using System.Buffers.Binary;
using System.Text;

namespace reStation.Core.Bios;

public sealed class MemCardMcrStore
{
    public const int CardSize = 131_072; // 128 KB: 16 blocks x 64 frames x 128 bytes
    public const int BlockSize = 8192;

    private readonly string _savesDir;
    public byte[] CardData { get; } = new byte[CardSize];
    public string McrPath { get; private set; } = string.Empty;

    public MemCardMcrStore(string dataDir, string? cardIdentity = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDir);
        _savesDir = Path.Combine(Path.GetFullPath(dataDir), "saves");
        Directory.CreateDirectory(_savesDir);
        SetCardIdentity(cardIdentity);
    }

    /// <summary>Switches the active backing .mcr image to match <paramref name="cardIdentity"/> (e.g. serialNumber or moduleId).</summary>
    public void SetCardIdentity(string? cardIdentity)
    {
        string safe = SanitizeIdentity(cardIdentity);
        McrPath = string.IsNullOrEmpty(safe)
            ? Path.Combine(_savesDir, "memcard_slot1.mcr")
            : Path.Combine(_savesDir, $"memcard_{safe}_slot1.mcr");

        if (File.Exists(McrPath))
        {
            byte[] existing = File.ReadAllBytes(McrPath);
            if (existing.Length == CardSize)
            {
                Array.Copy(existing, CardData, CardSize);
                return;
            }
        }
        Format();
    }

    public void Format()
    {
        Array.Fill(CardData, (byte)0x00);
        CardData[0] = (byte)'M';
        CardData[1] = (byte)'C';
        WriteXor(0);

        for (int slot = 1; slot <= 15; slot++)
        {
            int off = slot * 128;
            BinaryPrimitives.WriteUInt32LittleEndian(CardData.AsSpan(off, 4), 0x000000A0U); // free block
            BinaryPrimitives.WriteUInt16LittleEndian(CardData.AsSpan(off + 8, 2), 0xFFFF);
            WriteXor(off);
        }
        for (int slot = 16; slot <= 35; slot++)
        {
            int off = slot * 128;
            BinaryPrimitives.WriteUInt32LittleEndian(CardData.AsSpan(off, 4), 0xFFFFFFFFU);
            BinaryPrimitives.WriteUInt16LittleEndian(CardData.AsSpan(off + 8, 2), 0xFFFF);
            WriteXor(off);
        }
        Flush();
    }

    public List<(string Name, int SizeBytes, int FirstBlock)> ListFiles(string pattern)
    {
        var results = new List<(string, int, int)>();
        string cleanPat = NormalizeName(pattern);
        for (int block = 1; block <= 15; block++)
        {
            int off = block * 128;
            if (BinaryPrimitives.ReadUInt32LittleEndian(CardData.AsSpan(off, 4)) != 0x00000051U) continue;
            string fname = ReadAsciiZ(CardData.AsSpan(off + 10, 21));
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(CardData.AsSpan(off + 4, 4));
            if (MatchesWildcard(fname, cleanPat)) results.Add((fname, size, block));
        }
        return results;
    }

    public byte[]? ReadFile(string rawName)
    {
        string target = NormalizeName(rawName);
        foreach (var f in ListFiles("*"))
        {
            if (!string.Equals(f.Name, target, StringComparison.OrdinalIgnoreCase)) continue;
            int count = Math.Clamp((f.SizeBytes + BlockSize - 1) / BlockSize, 1, 15);
            byte[] buf = new byte[f.SizeBytes];
            int copied = 0, blk = f.FirstBlock;
            for (int i = 0; i < count && blk is >= 1 and <= 15; i++)
            {
                int take = Math.Min(BlockSize, buf.Length - copied);
                Array.Copy(CardData, blk * BlockSize, buf, copied, take);
                copied += take;
                ushort next = BinaryPrimitives.ReadUInt16LittleEndian(CardData.AsSpan(blk * 128 + 8, 2));
                blk = next == 0xFFFF ? -1 : next + 1;
            }
            return buf;
        }
        return null;
    }

    public bool WriteFile(string rawName, ReadOnlySpan<byte> content)
    {
        string name = NormalizeName(rawName);
        Erase(name);
        int blocksNeeded = Math.Max(1, (content.Length + BlockSize - 1) / BlockSize);
        var free = new List<int>();
        for (int b = 1; b <= 15; b++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(CardData.AsSpan(b * 128, 4)) == 0x000000A0U)
            {
                free.Add(b);
                if (free.Count == blocksNeeded) break;
            }
        }
        if (free.Count < blocksNeeded) return false;

        int written = 0;
        for (int i = 0; i < blocksNeeded; i++)
        {
            int blk = free[i], dir = blk * 128;
            Array.Fill(CardData, (byte)0, dir, 128);
            uint allocState = i == 0 ? 0x51U : (i == blocksNeeded - 1 ? 0x53U : 0x52U);
            BinaryPrimitives.WriteUInt32LittleEndian(CardData.AsSpan(dir, 4), allocState);
            BinaryPrimitives.WriteUInt32LittleEndian(CardData.AsSpan(dir + 4, 4), (uint)(i == 0 ? blocksNeeded * BlockSize : 0));
            ushort nextBlk = i + 1 < blocksNeeded ? (ushort)(free[i + 1] - 1) : (ushort)0xFFFF;
            BinaryPrimitives.WriteUInt16LittleEndian(CardData.AsSpan(dir + 8, 2), nextBlk);
            if (i == 0)
            {
                byte[] ascii = Encoding.ASCII.GetBytes(name);
                ascii.AsSpan(0, Math.Min(ascii.Length, 20)).CopyTo(CardData.AsSpan(dir + 10));
            }
            WriteXor(dir);

            int take = Math.Min(BlockSize, content.Length - written);
            Array.Fill(CardData, (byte)0, blk * BlockSize, BlockSize);
            if (take > 0)
            {
                content.Slice(written, take).CopyTo(CardData.AsSpan(blk * BlockSize, take));
                written += take;
            }
        }
        Flush();
        return true;
    }

    public bool Erase(string rawName)
    {
        string target = NormalizeName(rawName);
        bool erased = false;
        for (int b = 1; b <= 15; b++)
        {
            int dir = b * 128;
            if (BinaryPrimitives.ReadUInt32LittleEndian(CardData.AsSpan(dir, 4)) != 0x51U) continue;
            if (!string.Equals(ReadAsciiZ(CardData.AsSpan(dir + 10, 21)), target, StringComparison.OrdinalIgnoreCase)) continue;
            int cur = b;
            while (cur is >= 1 and <= 15)
            {
                int cdir = cur * 128;
                ushort next = BinaryPrimitives.ReadUInt16LittleEndian(CardData.AsSpan(cdir + 8, 2));
                Array.Fill(CardData, (byte)0, cdir, 128);
                BinaryPrimitives.WriteUInt32LittleEndian(CardData.AsSpan(cdir, 4), 0xA0U);
                BinaryPrimitives.WriteUInt16LittleEndian(CardData.AsSpan(cdir + 8, 2), 0xFFFF);
                WriteXor(cdir);
                cur = next == 0xFFFF ? -1 : next + 1;
            }
            erased = true;
        }
        if (erased) Flush();
        return erased;
    }

    public void Flush()
    {
        string tmp = $"{McrPath}.{Guid.NewGuid():N}.tmp";
        File.WriteAllBytes(tmp, CardData);
        File.Move(tmp, McrPath, overwrite: true);
    }

    private void WriteXor(int frameOff)
    {
        byte x = 0;
        for (int i = 0; i < 127; i++) x ^= CardData[frameOff + i];
        CardData[frameOff + 127] = x;
    }

    private static string SanitizeIdentity(string? identity)
    {
        if (string.IsNullOrWhiteSpace(identity)) return string.Empty;
        char[] bad = Path.GetInvalidFileNameChars();
        return string.Concat(identity.Trim().Select(c => bad.Contains(c) || c == ' ' ? '_' : c));
    }

    private static string NormalizeName(string raw)
    {
        int colon = raw.IndexOf(':');
        return colon >= 0 ? raw[(colon + 1)..] : raw;
    }

    private static string ReadAsciiZ(ReadOnlySpan<byte> span)
    {
        int idx = span.IndexOf((byte)0);
        return Encoding.ASCII.GetString(idx >= 0 ? span[..idx] : span);
    }

    private static bool MatchesWildcard(string text, string pattern)
    {
        if (string.IsNullOrEmpty(pattern) || pattern == "*") return true;
        return pattern.EndsWith('*')
            ? text.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
            : string.Equals(text, pattern, StringComparison.OrdinalIgnoreCase);
    }
}
