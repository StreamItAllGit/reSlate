// @module: reStation.Core.DiscIngestion.Iso9660RawReader
// @purpose: Pure-C# ISO-9660 reader for 2048-byte .iso and raw 2352-byte Mode1/Mode2 .bin images (XA attribute + subheader Form2 detection) plus a minimal CUE sheet parser.
// @depends-on: System, System.Buffers, System.Buffers.Binary, System.Text
// @exports: Iso9660Entry, Iso9660RawReader, CueTrack, CueSheetReader
// @owner: devv-audio-mdec (T3)

using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using reStation.Core.CdRom;

namespace reStation.Core.DiscIngestion;

/// <summary>One file in the ISO-9660 tree (paths use '/' separators, version suffix stripped).</summary>
public sealed record Iso9660Entry(string FullPath, uint Lba, uint ByteLength, bool IsForm2Stream);

public sealed class Iso9660RawReader : IDisposable
{
    public const int RawSectorSize = 2352;
    public const int UserSectorSize = 2048;
    private const int MaxDirectoryDepth = 32;
    private static readonly byte[] SyncPattern = { 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00 };

    private readonly FileStream _stream;
    private readonly List<Iso9660Entry> _entries = new();
    private readonly long _sectorCount;

    private Iso9660RawReader(FileStream stream, int stride, int userOffset, string volumeId)
    {
        _stream = stream;
        SectorStride = stride;
        UserDataOffset = userOffset;
        VolumeId = volumeId;
        _sectorCount = stream.Length / stride;
    }

    /// <summary>2352 for raw .bin images, 2048 for cooked .iso images.</summary>
    public int SectorStride { get; }
    /// <summary>Offset of the 2048-byte user data inside each sector (24 = Mode2, 16 = Mode1, 0 = cooked).</summary>
    public int UserDataOffset { get; }
    public string VolumeId { get; }
    public IReadOnlyList<Iso9660Entry> Entries => _entries;
    public bool IsRaw => SectorStride == RawSectorSize;

    /// <summary>Returns true if <paramref name="imagePath"/> contains a valid ISO-9660 CD001 primary volume descriptor.</summary>
    public static bool HasIso9660Header(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath)) return false;
        try
        {
            using var fs = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fs.Length < 17L * UserSectorSize) return false;
            Span<byte> head = stackalloc byte[16];
            fs.ReadExactly(head);
            bool raw = head[..12].SequenceEqual(SyncPattern);
            int stride = raw ? RawSectorSize : UserSectorSize;
            int userOffset = raw ? (head[15] == 2 ? 24 : 16) : 0;
            if (fs.Length < 17L * stride) return false;
            Span<byte> pvd = stackalloc byte[8];
            fs.Seek(16L * stride + userOffset, SeekOrigin.Begin);
            fs.ReadExactly(pvd);
            return pvd[0] == 1 && Encoding.ASCII.GetString(pvd.Slice(1, 5)) == "CD001";
        }
        catch (IOException)
        {
            return false;
        }
    }

    public static Iso9660RawReader Open(string imagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        var fs = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (fs.Length < 17L * UserSectorSize) throw new InvalidDataException($"'{Path.GetFileName(imagePath)}' is too small to be an ISO-9660 image.");
            Span<byte> head = stackalloc byte[16];
            fs.ReadExactly(head);
            bool raw = head[..12].SequenceEqual(SyncPattern);
            int stride = raw ? RawSectorSize : UserSectorSize;
            int userOffset = raw ? (head[15] == 2 ? 24 : 16) : 0;

            byte[] pvd = new byte[UserSectorSize];
            ReadUser(fs, stride, userOffset, 16, pvd);
            if (pvd[0] != 1 || Encoding.ASCII.GetString(pvd, 1, 5) != "CD001")
                throw new InvalidDataException($"'{Path.GetFileName(imagePath)}' is not a valid ISO-9660 CD image.");

            var reader = new Iso9660RawReader(fs, stride, userOffset, Encoding.ASCII.GetString(pvd, 40, 32).Trim('\0', ' '));
            uint rootLba = BinaryPrimitives.ReadUInt32LittleEndian(pvd.AsSpan(158, 4));
            uint rootLen = BinaryPrimitives.ReadUInt32LittleEndian(pvd.AsSpan(166, 4));
            reader.WalkDirectory(string.Empty, rootLba, rootLen, 0);
            return reader;
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    /// <summary>Finds an entry by path, ignoring case, leading separators, and ";1" suffixes.</summary>
    public Iso9660Entry? FindEntry(string path)
    {
        string wanted = path.Replace('\\', '/').TrimStart('/');
        int semi = wanted.IndexOf(';');
        if (semi >= 0) wanted = wanted[..semi];
        return _entries.FirstOrDefault(e => string.Equals(e.FullPath, wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Reads <paramref name="byteLength"/> bytes of Form1 user data starting at <paramref name="lba"/>.</summary>
    public byte[] ReadForm1Bytes(uint lba, uint byteLength)
    {
        byte[] output = new byte[byteLength];
        Span<byte> sec = stackalloc byte[UserSectorSize];
        uint offset = 0, cur = lba;
        while (offset < byteLength)
        {
            int take = (int)Math.Min(UserSectorSize, byteLength - offset);
            if (take == UserSectorSize)
            {
                ReadUser(_stream, SectorStride, UserDataOffset, cur, output.AsSpan((int)offset, UserSectorSize));
            }
            else
            {
                ReadUser(_stream, SectorStride, UserDataOffset, cur, sec);
                sec[..take].CopyTo(output.AsSpan((int)offset, take));
            }
            offset += (uint)take;
            cur++;
        }
        return output;
    }

    /// <summary>Reads one full 2352-byte raw sector (raw images only).</summary>
    public void ReadRawSector(uint lba, Span<byte> destination)
    {
        if (!IsRaw) throw new InvalidOperationException("Raw sector reads require a 2352-byte .bin image.");
        if (destination.Length < RawSectorSize) throw new ArgumentException("Destination must hold 2352 bytes.", nameof(destination));
        _stream.Seek((long)lba * RawSectorSize, SeekOrigin.Begin);
        _stream.ReadExactly(destination[..RawSectorSize]);
    }

    /// <summary>Streams an entry (Form1 2048-byte sectors, or 2336-byte Mode2 raw sectors when <see cref="Iso9660Entry.IsForm2Stream"/>) to <paramref name="destination"/>.</summary>
    public void CopyEntryTo(Iso9660Entry entry, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(destination);
        if (entry.IsForm2Stream && IsRaw)
        {
            uint sectors = Math.Max(1U, (entry.ByteLength + UserSectorSize - 1U) / UserSectorSize);
            byte[] raw = ArrayPool<byte>.Shared.Rent(RawSectorSize);
            try
            {
                for (uint s = 0; s < sectors && entry.Lba + s < _sectorCount; s++)
                {
                    ReadRawSector(entry.Lba + s, raw.AsSpan(0, RawSectorSize));
                    destination.Write(raw, 16, RawSectorSize - 16);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(raw);
            }
            return;
        }
        byte[] sec = ArrayPool<byte>.Shared.Rent(UserSectorSize);
        try
        {
            uint remaining = entry.ByteLength, cur = entry.Lba;
            while (remaining > 0)
            {
                ReadUser(_stream, SectorStride, UserDataOffset, cur++, sec.AsSpan(0, UserSectorSize));
                int take = (int)Math.Min(UserSectorSize, remaining);
                destination.Write(sec, 0, take);
                remaining -= (uint)take;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(sec);
        }
    }

    public void Dispose() => _stream.Dispose();

    private void WalkDirectory(string prefix, uint lba, uint byteLength, int depth)
    {
        if (depth > MaxDirectoryDepth) throw new InvalidDataException("ISO-9660 directory tree is too deep (possible loop).");
        uint padded = Math.Max(UserSectorSize, (byteLength + UserSectorSize - 1) / UserSectorSize * UserSectorSize);
        byte[] dir = ReadForm1Bytes(lba, padded);
        int pos = 0;
        while (pos < (int)byteLength && pos < dir.Length)
        {
            byte recLen = dir[pos];
            if (recLen == 0)
            {
                pos = (pos / UserSectorSize + 1) * UserSectorSize;
                continue;
            }
            if (pos + recLen > dir.Length || recLen < 34) break;

            uint childLba = BinaryPrimitives.ReadUInt32LittleEndian(dir.AsSpan(pos + 2, 4));
            uint childLen = BinaryPrimitives.ReadUInt32LittleEndian(dir.AsSpan(pos + 10, 4));
            byte flags = dir[pos + 25];
            int nameLen = dir[pos + 32];
            ReadOnlySpan<byte> record = dir.AsSpan(pos, recLen);
            pos += recLen;
            if (nameLen == 0 || 33 + nameLen > record.Length) continue;
            if (nameLen == 1 && record[33] <= 1) continue; // "." and ".."

            string rawName = Encoding.ASCII.GetString(record.Slice(33, nameLen));
            int semi = rawName.IndexOf(';');
            string name = (semi >= 0 ? rawName[..semi] : rawName).TrimEnd('.');
            if (name.Length == 0 || name == ".." || name.IndexOfAny(new[] { '/', '\\', '\0' }) >= 0) continue;
            string full = prefix.Length == 0 ? name : $"{prefix}/{name}";

            if ((flags & 0x02) != 0)
            {
                if (childLba != lba) WalkDirectory(full, childLba, childLen, depth + 1);
                continue;
            }
            _entries.Add(new Iso9660Entry(full, childLba, childLen, IsForm2(record, nameLen, childLba)));
        }
    }

    /// <summary>Form2/streaming detection: CD-XA attributes (Form2 0x1000 / interleaved 0x2000) or a raw subheader with audio/video/Form2 submode bits.</summary>
    private bool IsForm2(ReadOnlySpan<byte> record, int nameLen, uint lba)
    {
        int su = 33 + nameLen + ((nameLen & 1) == 0 ? 1 : 0);
        if (su + 14 <= record.Length && record[su + 6] == (byte)'X' && record[su + 7] == (byte)'A')
        {
            ushort attr = BinaryPrimitives.ReadUInt16BigEndian(record.Slice(su + 4, 2));
            if ((attr & 0x3000) != 0) return true;
        }
        if (!IsRaw || UserDataOffset != 24 || lba >= _sectorCount) return false;
        Span<byte> sub = stackalloc byte[4];
        _stream.Seek((long)lba * RawSectorSize + 16, SeekOrigin.Begin);
        _stream.ReadExactly(sub);
        return PsxCdRomTiming.HasStreamingSubmode(sub[2]);
    }

    private static void ReadUser(FileStream fs, int stride, int userOffset, uint lba, Span<byte> dest)
    {
        fs.Seek((long)lba * stride + userOffset, SeekOrigin.Begin);
        fs.ReadExactly(dest);
    }
}

/// <summary>One TRACK of a CUE sheet with the FILE it belongs to.</summary>
public sealed record CueTrack(string FileName, int TrackNumber, string Mode);

public static class CueSheetReader
{
    /// <summary>Parses FILE/TRACK lines (quoted or bare file names).</summary>
    public static IReadOnlyList<CueTrack> Parse(string cueText)
    {
        ArgumentNullException.ThrowIfNull(cueText);
        var tracks = new List<CueTrack>();
        string? currentFile = null;
        foreach (string rawLine in cueText.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.StartsWith("FILE ", StringComparison.OrdinalIgnoreCase))
            {
                string rest = line[5..].Trim();
                if (rest.StartsWith('"'))
                {
                    int close = rest.IndexOf('"', 1);
                    currentFile = close > 1 ? rest[1..close] : rest.Trim('"');
                }
                else
                {
                    int sp = rest.IndexOf(' ');
                    currentFile = sp > 0 ? rest[..sp] : rest;
                }
            }
            else if (line.StartsWith("TRACK ", StringComparison.OrdinalIgnoreCase) && currentFile is not null)
            {
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3 && int.TryParse(parts[1], out int number))
                    tracks.Add(new CueTrack(currentFile, number, parts[2].ToUpperInvariant()));
            }
        }
        return tracks;
    }
}
