// @module: reStation.Core.Enhancement.MinimalPngDecoder
// @purpose: Clean-room pure net8.0 PNG decoder (IHDR, PLTE, tRNS, IDAT ZLibStream, all 5 scanline filters) with zero GDI+/WinForms dependencies.
// @depends-on: System, System.Buffers.Binary, System.Diagnostics, System.IO, System.IO.Compression, System.Text
// @exports: DecodedPngImage, MinimalPngDecoder

using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace reStation.Core.Enhancement;

/// <summary>Decoded tightly packed 8-bit RGBA PNG pixel buffer.</summary>
public sealed record DecodedPngImage(int Width, int Height, byte[] RgbaPixels);

/// <summary>
/// Strict pure-<c>net8.0</c> clean-room PNG decoder supporting 8-bit Grayscale, RGB, Palette (PLTE + tRNS),
/// Grayscale+Alpha, and RGBA images across all five PNG scanline filter modes without <c>System.Drawing</c>.
/// </summary>
public static class MinimalPngDecoder
{
    private const long MaxDecodedRgbaBytes = 256L * 1024 * 1024;
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] Crc32Table = BuildCrc32Table();

    /// <summary>Decodes PNG bytes into tightly packed RGBA8 pixels and returns image metadata.</summary>
    public static DecodedPngImage Decode(ReadOnlySpan<byte> pngBytes)
    {
        byte[] rgba = Decode(pngBytes, out int width, out int height);
        return new DecodedPngImage(width, height, rgba);
    }

    /// <summary>Decodes PNG bytes into tightly packed RGBA8 pixels.</summary>
    public static byte[] Decode(ReadOnlySpan<byte> pngBytes, out int width, out int height)
    {
        if (pngBytes.Length < PngSignature.Length || !pngBytes[..PngSignature.Length].SequenceEqual(PngSignature))
            throw new InvalidDataException("Invalid PNG file signature.");

        ParsedPngChunks chunks = ParseChunks(pngBytes[PngSignature.Length..]);
        byte[] rawScanlines = DecompressIdat(chunks);
        width = chunks.Width;
        height = chunks.Height;
        return ReconstructAndConvertToRgba(chunks, rawScanlines);
    }

    /// <summary>Decodes a PNG stream into tightly packed RGBA8 pixels.</summary>
    public static DecodedPngImage Decode(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return Decode(ms.GetBuffer().AsSpan(0, checked((int)ms.Length)));
    }

    /// <summary>Decodes a PNG file from disk into tightly packed RGBA8 pixels.</summary>
    public static DecodedPngImage DecodeFile(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        return Decode(File.ReadAllBytes(filePath));
    }

    /// <summary>Attempts to decode PNG bytes without throwing on malformed or unsupported PNG data.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> pngBytes, out byte[] rgbaPixels, out int width, out int height)
    {
        try
        {
            rgbaPixels = Decode(pngBytes, out width, out height);
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or EndOfStreamException or OverflowException)
        {
            Trace.WriteLine($"MinimalPngDecoder.TryDecode failed: {ex.GetType().Name}: {ex.Message}");
            rgbaPixels = Array.Empty<byte>();
            width = 0;
            height = 0;
            return false;
        }
    }

    private sealed class ParsedPngChunks
    {
        public int Width;
        public int Height;
        public byte ColorType;
        public int BytesPerPixel;
        public byte[]? Palette;
        public byte[]? TrnsAlpha;
        public bool HasTrnsColorKey;
        public ushort TrnsKey0, TrnsKey1, TrnsKey2;
        public MemoryStream IdatStream = new();
    }

    private static ParsedPngChunks ParseChunks(ReadOnlySpan<byte> data)
    {
        var state = new ParsedPngChunks();
        bool seenIhdr = false, seenPlte = false, seenTrns = false, seenIdat = false, seenIend = false;
        bool inIdat = false, seenPostIdat = false;
        int offset = 0;

        while (offset < data.Length)
        {
            if (data.Length - offset < 12)
                throw new InvalidDataException("Truncated PNG chunk header.");

            uint len = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
            if (len > (uint)(data.Length - offset - 12))
                throw new InvalidDataException("Truncated PNG chunk payload.");

            ReadOnlySpan<byte> type = data.Slice(offset + 4, 4);
            ReadOnlySpan<byte> payload = data.Slice(offset + 8, (int)len);
            uint expectedCrc = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset + 8 + (int)len, 4));
            if (ComputeChunkCrc(type, payload) != expectedCrc)
                throw new InvalidDataException($"PNG chunk '{Encoding.ASCII.GetString(type)}' CRC32 mismatch.");

            offset += 12 + (int)len;
            string tag = Encoding.ASCII.GetString(type);
            if (!seenIhdr && tag != "IHDR")
                throw new InvalidDataException("PNG image must begin with an IHDR chunk.");

            switch (tag)
            {
                case "IHDR":
                    if (seenIhdr) throw new InvalidDataException("Duplicate PNG IHDR chunk.");
                    ParseIhdr(payload, state);
                    seenIhdr = true;
                    break;
                case "PLTE":
                    if (seenPlte || seenIdat || seenTrns) throw new InvalidDataException("Invalid PNG PLTE chunk ordering.");
                    if (payload.Length == 0 || payload.Length % 3 != 0 || payload.Length > 256 * 3)
                        throw new InvalidDataException("Invalid PNG PLTE chunk length.");
                    state.Palette = payload.ToArray();
                    seenPlte = true;
                    break;
                case "tRNS":
                    if (seenTrns || seenIdat) throw new InvalidDataException("Invalid PNG tRNS chunk ordering.");
                    ParseTrns(payload, state);
                    seenTrns = true;
                    break;
                case "IDAT":
                    if (seenPostIdat) throw new InvalidDataException("Non-contiguous PNG IDAT chunk.");
                    state.IdatStream.Write(payload);
                    seenIdat = true;
                    inIdat = true;
                    break;
                case "IEND":
                    if (payload.Length != 0) throw new InvalidDataException("Non-empty PNG IEND chunk.");
                    seenIend = true;
                    break;
                default:
                    if (inIdat) { inIdat = false; seenPostIdat = true; }
                    if ((type[0] & 0x20) == 0)
                        throw new InvalidDataException($"Unsupported critical PNG chunk '{tag}'.");
                    break;
            }

            if (seenIend) break;
        }

        if (!seenIhdr || !seenIdat || !seenIend)
            throw new InvalidDataException("PNG image is missing required IHDR, IDAT, or IEND chunks.");
        if (state.ColorType == 3 && state.Palette is null)
            throw new InvalidDataException("Indexed PNG image is missing required PLTE chunk.");
        return state;
    }

    private static void ParseIhdr(ReadOnlySpan<byte> payload, ParsedPngChunks state)
    {
        if (payload.Length != 13) throw new InvalidDataException("Invalid PNG IHDR chunk length.");

        int width = BinaryPrimitives.ReadInt32BigEndian(payload[..4]);
        int height = BinaryPrimitives.ReadInt32BigEndian(payload.Slice(4, 4));
        if (width <= 0 || height <= 0) throw new InvalidDataException("PNG dimensions must be positive.");
        if ((long)width * height * 4L > MaxDecodedRgbaBytes)
            throw new InvalidDataException($"PNG dimensions {width}x{height} exceed decoder safety limit.");

        byte bitDepth = payload[8];
        byte colorType = payload[9];
        byte compression = payload[10];
        byte filter = payload[11];
        byte interlace = payload[12];

        if (compression != 0 || filter != 0)
            throw new InvalidDataException("Unsupported PNG compression or filter method.");
        if (interlace == 1)
            throw new NotSupportedException("Interlaced (Adam7) PNG images are not supported.");
        if (interlace != 0)
            throw new InvalidDataException("Invalid PNG interlace method.");

        int bpp = colorType switch
        {
            0 => 1,
            2 => 3,
            3 => 1,
            4 => 2,
            6 => 4,
            _ => throw new InvalidDataException($"Unsupported PNG color type {colorType}.")
        };
        if (bitDepth != 8)
            throw new NotSupportedException($"Unsupported PNG bit depth {bitDepth}; only 8-bit samples are supported.");

        state.Width = width;
        state.Height = height;
        state.ColorType = colorType;
        state.BytesPerPixel = bpp;
    }

    private static void ParseTrns(ReadOnlySpan<byte> payload, ParsedPngChunks state)
    {
        switch (state.ColorType)
        {
            case 3:
                if (state.Palette is null || payload.Length > state.Palette.Length / 3)
                    throw new InvalidDataException("Invalid PNG tRNS palette chunk.");
                state.TrnsAlpha = payload.ToArray();
                break;
            case 0:
                if (payload.Length != 2) throw new InvalidDataException("Invalid PNG tRNS grayscale length.");
                state.HasTrnsColorKey = true;
                state.TrnsKey0 = BinaryPrimitives.ReadUInt16BigEndian(payload);
                break;
            case 2:
                if (payload.Length != 6) throw new InvalidDataException("Invalid PNG tRNS RGB length.");
                state.HasTrnsColorKey = true;
                state.TrnsKey0 = BinaryPrimitives.ReadUInt16BigEndian(payload[0..2]);
                state.TrnsKey1 = BinaryPrimitives.ReadUInt16BigEndian(payload[2..4]);
                state.TrnsKey2 = BinaryPrimitives.ReadUInt16BigEndian(payload[4..6]);
                break;
            default:
                throw new InvalidDataException("PNG tRNS chunk is forbidden for color types with an alpha channel.");
        }
    }

    private static byte[] DecompressIdat(ParsedPngChunks state)
    {
        int stride = checked(state.Width * state.BytesPerPixel);
        int expectedBytes = checked(state.Height * (1 + stride));
        byte[] raw = new byte[expectedBytes];

        state.IdatStream.Position = 0;
        using var zlib = new ZLibStream(state.IdatStream, CompressionMode.Decompress);
        try
        {
            zlib.ReadExactly(raw);
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException("Decompressed PNG IDAT stream ended prematurely.", ex);
        }
        if (zlib.ReadByte() != -1)
            throw new InvalidDataException("PNG IDAT stream contains unexpected trailing scanline bytes.");

        return raw;
    }

    private static byte[] ReconstructAndConvertToRgba(ParsedPngChunks state, byte[] rawScanlines)
    {
        int width = state.Width;
        int height = state.Height;
        int bpp = state.BytesPerPixel;
        int stride = width * bpp;
        byte[] rgba = new byte[checked(width * height * 4)];
        byte[] prevRow = new byte[stride];
        byte[] currRow = new byte[stride];

        for (int y = 0; y < height; y++)
        {
            int srcOffset = y * (1 + stride);
            byte filterType = rawScanlines[srcOffset];
            ReadOnlySpan<byte> rawRow = rawScanlines.AsSpan(srcOffset + 1, stride);
            ReconstructScanline(filterType, rawRow, prevRow, currRow, bpp);
            ConvertRowToRgba(state, currRow, rgba.AsSpan(y * width * 4, width * 4));
            (prevRow, currRow) = (currRow, prevRow);
        }
        return rgba;
    }

    private static void ReconstructScanline(
        byte filterType,
        ReadOnlySpan<byte> raw,
        ReadOnlySpan<byte> prev,
        Span<byte> curr,
        int bpp)
    {
        switch (filterType)
        {
            case 0:
                raw.CopyTo(curr);
                break;
            case 1:
                for (int x = 0; x < raw.Length; x++)
                    curr[x] = unchecked((byte)(raw[x] + (x >= bpp ? curr[x - bpp] : 0)));
                break;
            case 2:
                for (int x = 0; x < raw.Length; x++)
                    curr[x] = unchecked((byte)(raw[x] + prev[x]));
                break;
            case 3:
                for (int x = 0; x < raw.Length; x++)
                {
                    int a = x >= bpp ? curr[x - bpp] : 0;
                    curr[x] = unchecked((byte)(raw[x] + ((a + prev[x]) >> 1)));
                }
                break;
            case 4:
                for (int x = 0; x < raw.Length; x++)
                {
                    int a = x >= bpp ? curr[x - bpp] : 0;
                    int b = prev[x];
                    int c = x >= bpp ? prev[x - bpp] : 0;
                    curr[x] = unchecked((byte)(raw[x] + PaethPredictor(a, b, c)));
                }
                break;
            default:
                throw new InvalidDataException($"Unsupported PNG scanline filter type {filterType}.");
        }
    }

    private static int PaethPredictor(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        return pb <= pc ? b : c;
    }

    private static void ConvertRowToRgba(ParsedPngChunks state, ReadOnlySpan<byte> row, Span<byte> dst)
    {
        int w = state.Width;
        switch (state.ColorType)
        {
            case 6:
                row.CopyTo(dst);
                break;
            case 2:
                for (int x = 0; x < w; x++)
                {
                    byte r = row[x * 3], g = row[x * 3 + 1], b = row[x * 3 + 2];
                    int d = x * 4;
                    dst[d] = r;
                    dst[d + 1] = g;
                    dst[d + 2] = b;
                    dst[d + 3] = state.HasTrnsColorKey && r == state.TrnsKey0 && g == state.TrnsKey1 && b == state.TrnsKey2
                        ? (byte)0
                        : (byte)255;
                }
                break;
            case 0:
                for (int x = 0; x < w; x++)
                {
                    byte v = row[x];
                    int d = x * 4;
                    dst[d] = v;
                    dst[d + 1] = v;
                    dst[d + 2] = v;
                    dst[d + 3] = state.HasTrnsColorKey && v == state.TrnsKey0 ? (byte)0 : (byte)255;
                }
                break;
            case 4:
                for (int x = 0; x < w; x++)
                {
                    byte v = row[x * 2];
                    int d = x * 4;
                    dst[d] = v;
                    dst[d + 1] = v;
                    dst[d + 2] = v;
                    dst[d + 3] = row[x * 2 + 1];
                }
                break;
            case 3:
                byte[] palette = state.Palette!;
                int paletteEntries = palette.Length / 3;
                byte[]? trns = state.TrnsAlpha;
                for (int x = 0; x < w; x++)
                {
                    int idx = row[x];
                    if (idx >= paletteEntries)
                        throw new InvalidDataException($"PNG palette index {idx} out of range ({paletteEntries}).");
                    int d = x * 4;
                    dst[d] = palette[idx * 3];
                    dst[d + 1] = palette[idx * 3 + 1];
                    dst[d + 2] = palette[idx * 3 + 2];
                    dst[d + 3] = trns is not null && idx < trns.Length ? trns[idx] : (byte)255;
                }
                break;
        }
    }

    private static uint ComputeChunkCrc(ReadOnlySpan<byte> chunkType, ReadOnlySpan<byte> payload)
    {
        uint crc = 0xFFFFFFFFu;
        crc = UpdateCrc(crc, chunkType);
        crc = UpdateCrc(crc, payload);
        return crc ^ 0xFFFFFFFFu;
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> data)
    {
        uint c = crc;
        uint[] table = Crc32Table;
        for (int i = 0; i < data.Length; i++)
            c = table[(c ^ data[i]) & 0xFF] ^ (c >> 8);
        return c;
    }

    private static uint[] BuildCrc32Table()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? (0xEDB88320u ^ (c >> 1)) : (c >> 1);
            table[i] = c;
        }
        return table;
    }
}
