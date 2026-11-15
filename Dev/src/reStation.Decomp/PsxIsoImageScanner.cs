// @module: reStation.Decomp.PsxIsoImageScanner
// @purpose: Clean-room ISO-9660 (2048B .iso / 2352B raw .bin) directory reader and SYSTEM.CNF boot executable parser for PS1 decomp intake.
// @depends-on: System, System.Buffers.Binary, System.Collections.Generic, System.IO, System.Text
// @exports: PsxDiscFileSlice, PsxIsoImageScanner
// SPDX-License-Identifier: MPL-2.0

using System.Buffers.Binary;
using System.Text;

namespace reStation.Decomp;

/// <summary>Represents a file read from a PS1 disc image or directory.</summary>
/// <param name="RelativePath">Normalized slash-separated relative path inside the disc root.</param>
/// <param name="Bytes">Raw binary contents read from the image or file.</param>
internal sealed record PsxDiscFileSlice(string RelativePath, byte[] Bytes);

/// <summary>Clean-room ISO-9660 and <c>SYSTEM.CNF</c> inspector for PS1 optical media.</summary>
internal static class PsxIsoImageScanner
{
    private const int UserSectorBytes = 2048;
    private const int RawSectorBytes = 2352;
    private const int MaxDirectoryDepth = 32;
    private static readonly byte[] RawSyncHeader = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];
    private static readonly byte[] PsxMagic = "PS-X EXE"u8.ToArray();

    public static string? ParseSystemCnfBootPath(string systemCnfText)
    {
        ArgumentNullException.ThrowIfNull(systemCnfText);
        foreach (string rawLine in systemCnfText.Split('\n'))
        {
            int bootIdx = rawLine.IndexOf("BOOT", StringComparison.OrdinalIgnoreCase);
            if (bootIdx < 0) continue;
            int eq = rawLine.IndexOf('=', bootIdx);
            if (eq < 0) continue;
            string rhs = rawLine[(eq + 1)..].Trim();
            int semi = rhs.IndexOf(';');
            if (semi >= 0) rhs = rhs[..semi];
            int colon = rhs.IndexOf(':');
            if (colon >= 0) rhs = rhs[(colon + 1)..];
            string normalized = rhs.Replace('\\', '/').Trim().TrimStart('/');
            if (!string.IsNullOrWhiteSpace(normalized))
                return normalized;
        }
        return null;
    }

    public static bool HasIso9660PrimaryDescriptor(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            return false;
        try
        {
            using var fs = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return TryProbeGeometry(fs, out int stride, out int userOffset) &&
                   HasCd001AtLba16(fs, stride, userOffset);
        }
        catch (IOException)
        {
            return false;
        }
    }

    public static (string? BootRelativePath, List<PsxDiscFileSlice> ExeFiles) ScanIsoExecutables(
        string imagePath, long maxExeBytes)
    {
        using var fs = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!TryProbeGeometry(fs, out int stride, out int userOffset) || !HasCd001AtLba16(fs, stride, userOffset))
            throw new InvalidDataException($"'{Path.GetFileName(imagePath)}' is not a valid PS1 ISO-9660 image.");

        byte[] pvd = new byte[UserSectorBytes];
        ReadSectorUser(fs, stride, userOffset, 16, pvd);
        uint rootLba = BinaryPrimitives.ReadUInt32LittleEndian(pvd.AsSpan(158, 4));
        uint rootLen = BinaryPrimitives.ReadUInt32LittleEndian(pvd.AsSpan(166, 4));

        var entries = new List<(string Rel, uint Lba, uint Len)>();
        WalkDirectory(fs, stride, userOffset, string.Empty, rootLba, rootLen, 0, entries);

        string? bootPath = null;
        var sysCnf = entries.FirstOrDefault(e => string.Equals(e.Rel, "SYSTEM.CNF", StringComparison.OrdinalIgnoreCase));
        if (sysCnf.Len is > 0 and <= 4096)
        {
            byte[] cnfBytes = ReadExtent(fs, stride, userOffset, sysCnf.Lba, sysCnf.Len);
            bootPath = ParseSystemCnfBootPath(Encoding.ASCII.GetString(cnfBytes));
        }

        var exes = new List<PsxDiscFileSlice>();
        foreach (var entry in entries.OrderBy(e => e.Rel, StringComparer.OrdinalIgnoreCase))
        {
            if (entry.Len < 0x804 || entry.Len > maxExeBytes) continue;
            byte[] head = ReadExtent(fs, stride, userOffset, entry.Lba, 8);
            if (!head.AsSpan().SequenceEqual(PsxMagic)) continue;
            byte[] full = ReadExtent(fs, stride, userOffset, entry.Lba, entry.Len);
            exes.Add(new PsxDiscFileSlice(entry.Rel, full));
        }
        return (bootPath, exes);
    }

    private static bool TryProbeGeometry(FileStream fs, out int stride, out int userOffset)
    {
        stride = UserSectorBytes;
        userOffset = 0;
        if (fs.Length < 17L * UserSectorBytes)
            return false;

        Span<byte> head = stackalloc byte[16];
        fs.ReadExactly(head);
        if (head[..12].SequenceEqual(RawSyncHeader))
        {
            stride = RawSectorBytes;
            userOffset = head[15] == 2 ? 24 : 16;
        }
        return fs.Length >= 17L * stride;
    }

    private static bool HasCd001AtLba16(FileStream fs, int stride, int userOffset)
    {
        Span<byte> pvd = stackalloc byte[8];
        fs.Seek(16L * stride + userOffset, SeekOrigin.Begin);
        fs.ReadExactly(pvd);
        return pvd[0] == 1 && Encoding.ASCII.GetString(pvd.Slice(1, 5)) == "CD001";
    }

    private static void WalkDirectory(
        FileStream fs, int stride, int userOffset, string prefix, uint lba, uint byteLen, int depth,
        List<(string Rel, uint Lba, uint Len)> entries)
    {
        if (depth > MaxDirectoryDepth)
            throw new InvalidDataException("ISO-9660 directory tree exceeded maximum depth.");

        uint padded = Math.Max((uint)UserSectorBytes, (byteLen + UserSectorBytes - 1U) / UserSectorBytes * UserSectorBytes);
        byte[] dir = ReadExtent(fs, stride, userOffset, lba, padded);
        int pos = 0;
        while (pos < (int)byteLen && pos < dir.Length)
        {
            byte recLen = dir[pos];
            if (recLen == 0)
            {
                pos = (pos / UserSectorBytes + 1) * UserSectorBytes;
                continue;
            }
            if (pos + recLen > dir.Length || recLen < 34) break;

            uint childLba = BinaryPrimitives.ReadUInt32LittleEndian(dir.AsSpan(pos + 2, 4));
            uint childLen = BinaryPrimitives.ReadUInt32LittleEndian(dir.AsSpan(pos + 10, 4));
            byte flags = dir[pos + 25];
            int nameLen = dir[pos + 32];
            ReadOnlySpan<byte> rec = dir.AsSpan(pos, recLen);
            pos += recLen;
            if (nameLen == 0 || 33 + nameLen > rec.Length) continue;
            if (nameLen == 1 && rec[33] <= 1) continue;

            string rawName = Encoding.ASCII.GetString(rec.Slice(33, nameLen));
            int semi = rawName.IndexOf(';');
            string cleanName = (semi >= 0 ? rawName[..semi] : rawName).TrimEnd('.');
            if (cleanName.Length == 0 || cleanName == ".." || cleanName.IndexOfAny(['/', '\\', '\0']) >= 0)
                continue;

            string fullRel = prefix.Length == 0 ? cleanName : $"{prefix}/{cleanName}";
            if ((flags & 0x02) != 0)
            {
                if (childLba != lba) WalkDirectory(fs, stride, userOffset, fullRel, childLba, childLen, depth + 1, entries);
            }
            else
            {
                entries.Add((fullRel, childLba, childLen));
            }
        }
    }

    private static byte[] ReadExtent(FileStream fs, int stride, int userOffset, uint lba, uint byteLength)
    {
        byte[] output = new byte[byteLength];
        Span<byte> sector = stackalloc byte[UserSectorBytes];
        uint copied = 0;
        uint curLba = lba;
        while (copied < byteLength)
        {
            ReadSectorUser(fs, stride, userOffset, curLba++, sector);
            int take = (int)Math.Min(UserSectorBytes, byteLength - copied);
            sector[..take].CopyTo(output.AsSpan((int)copied, take));
            copied += (uint)take;
        }
        return output;
    }

    private static void ReadSectorUser(FileStream fs, int stride, int userOffset, uint lba, Span<byte> dest)
    {
        fs.Seek((long)lba * stride + userOffset, SeekOrigin.Begin);
        fs.ReadExactly(dest);
    }
}
