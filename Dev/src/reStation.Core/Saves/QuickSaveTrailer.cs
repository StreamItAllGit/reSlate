// @module: reStation.Core.Saves.QuickSaveTrailer
// @purpose: EOF-anchored "RSTX" trailer framing and bounded GZip payload isolation over frozen "RST1" schema v1 snapshots.
// @depends-on: System.Buffers.Binary, System.IO.Compression, System.Text, reStation.Core.Cpu
// @exports: QuickSaveTrailer
// @owner: devv-core

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using reStation.Core.Cpu;

namespace reStation.Core.Saves;

/// <summary>Encodes and decodes optional EOF-anchored "RSTX" extension chunks appended after an "RST1" GZip payload.</summary>
public static class QuickSaveTrailer
{
    public const uint TrailerMagic = 0x58545352U; // "RSTX"
    public const ushort TrailerVersion = 1;
    public const string ChunkBios = "BIOS";
    public const string ChunkOverlay = "OVLY";
    private const int MaxChunkCount = 64;
    private const int MaxPayloadBytes = 16 * 1024 * 1024;

    /// <summary>Captures default runtime extension chunks ("BIOS") from <paramref name="cpu"/> for slot saves.</summary>
    public static IReadOnlyDictionary<string, byte[]> CaptureDefaultChunks(PsxCpu cpu)
    {
        ArgumentNullException.ThrowIfNull(cpu);
        return new Dictionary<string, byte[]>(1, StringComparer.Ordinal)
        {
            [ChunkBios] = cpu.Bios.CaptureSnapshot()
        };
    }

    /// <summary>Writes the serialized "RSTX" trailer body, uint32 body length, and trailing "RSTX" magic to <paramref name="output"/>.</summary>
    public static string WriteTrailer(Stream output, IReadOnlyDictionary<string, byte[]> chunks)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(chunks);
        if (chunks.Count > MaxChunkCount)
            throw new ArgumentOutOfRangeException(nameof(chunks), $"Chunk count {chunks.Count} exceeds {MaxChunkCount}.");

        using var ms = new MemoryStream(256);
        using var w = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);
        w.Write(TrailerVersion);
        w.Write((ushort)chunks.Count);

        var tagNames = new List<string>(chunks.Count);
        Span<byte> tagBytes = stackalloc byte[4];
        foreach ((string key, byte[] payload) in chunks)
        {
            if (string.IsNullOrEmpty(key) || key.Length != 4)
                throw new ArgumentException($"RSTX chunk tag '{key}' must be exactly 4 ASCII characters.", nameof(chunks));
            for (int i = 0; i < 4; i++)
            {
                char ch = key[i];
                if (ch is < '\x20' or > '\x7E')
                    throw new ArgumentException($"RSTX chunk tag '{key}' contains non-ASCII byte.", nameof(chunks));
                tagBytes[i] = (byte)ch;
            }
            byte[] data = payload ?? [];
            w.Write(tagBytes);
            w.Write(data.Length);
            w.Write(data);
            tagNames.Add(key);
        }
        w.Flush();

        byte[] body = ms.ToArray();
        output.Write(body, 0, body.Length);
        Span<byte> footer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(footer[..4], (uint)body.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(footer[4..8], TrailerMagic);
        output.Write(footer);
        return string.Join(',', tagNames);
    }

    /// <summary>
    /// Isolates the GZip stream starting at <paramref name="payloadStart"/> from any trailing "RSTX" trailer via a bounded
    /// <see cref="MemoryStream"/> so <see cref="GZipStream"/> read-ahead cannot overrun into trailer bytes.
    /// </summary>
    public static Stream OpenBoundedGzipPayload(Stream fs, long payloadStart, out Dictionary<string, byte[]> chunks, out bool hasRstx)
    {
        ArgumentNullException.ThrowIfNull(fs);
        chunks = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        hasRstx = false;

        long totalLen = fs.Length;
        if (totalLen >= 16 && totalLen - payloadStart >= 8)
        {
            Span<byte> tail = stackalloc byte[8];
            fs.Seek(-8, SeekOrigin.End);
            fs.ReadExactly(tail);
            uint bodyLen = BinaryPrimitives.ReadUInt32LittleEndian(tail[..4]);
            uint eofMagic = BinaryPrimitives.ReadUInt32LittleEndian(tail[4..8]);
            if (eofMagic == TrailerMagic)
            {
                long maxBody = totalLen - 8 - payloadStart;
                if (bodyLen < 4 || bodyLen > maxBody || bodyLen > MaxPayloadBytes)
                    throw new InvalidDataException($"Corrupt RSTX trailer body length {bodyLen} (available={maxBody}).");

                long rstxStart = totalLen - 8 - bodyLen;
                byte[] body = new byte[bodyLen];
                fs.Seek(rstxStart, SeekOrigin.Begin);
                fs.ReadExactly(body);
                ParseTrailerBody(body, chunks);

                long gzLen = rstxStart - payloadStart;
                if (gzLen <= 0 || gzLen > MaxPayloadBytes)
                    throw new InvalidDataException($"Corrupt RST1+RSTX GZip payload length {gzLen}.");
                byte[] gzBytes = new byte[gzLen];
                fs.Seek(payloadStart, SeekOrigin.Begin);
                fs.ReadExactly(gzBytes);
                hasRstx = true;
                return new MemoryStream(gzBytes, writable: false);
            }
        }

        fs.Seek(payloadStart, SeekOrigin.Begin);
        return fs;
    }

    private static void ParseTrailerBody(ReadOnlySpan<byte> body, Dictionary<string, byte[]> chunks)
    {
        ushort ver = BinaryPrimitives.ReadUInt16LittleEndian(body[..2]);
        ushort count = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(2, 2));
        if (ver != TrailerVersion) throw new InvalidDataException($"Unsupported RSTX trailer version {ver}.");
        if (count > MaxChunkCount) throw new InvalidDataException($"RSTX chunk count {count} exceeds limit.");

        int pos = 4;
        for (int i = 0; i < count; i++)
        {
            if (pos + 8 > body.Length) throw new InvalidDataException("Truncated RSTX chunk header.");
            string tag = Encoding.ASCII.GetString(body.Slice(pos, 4));
            foreach (byte b in body.Slice(pos, 4))
                if (b is < 0x20 or > 0x7E) throw new InvalidDataException("Invalid non-ASCII byte in RSTX chunk tag.");
            int len = BinaryPrimitives.ReadInt32LittleEndian(body.Slice(pos + 4, 4));
            pos += 8;
            if (len < 0 || pos + len > body.Length)
                throw new InvalidDataException($"Corrupt RSTX chunk '{tag}' length {len}.");
            chunks[tag] = body.Slice(pos, len).ToArray();
            pos += len;
        }
        if (pos != body.Length)
            throw new InvalidDataException($"RSTX trailer contains {body.Length - pos} trailing byte(s) beyond declared chunks.");
    }
}
