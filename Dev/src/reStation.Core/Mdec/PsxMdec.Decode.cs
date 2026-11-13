// @module: reStation.Core.Mdec.PsxMdec.Decode
// @purpose: MDEC RLE block parse + dequantization (zig-zag, q-scale), 2-pass fixed-point IDCT, and YUV -> mono4/mono8/RGB24/RGB15 macroblock output.
// @depends-on: System
// @exports: PsxMdec (partial)
// @owner: devv-audio-mdec (T3)

namespace reStation.Core.Mdec;

public sealed partial class PsxMdec
{
    /// <summary>Zig-zag position k -> raster index (row * 8 + col).</summary>
    private static readonly int[] ZagZig =
    {
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5,
        12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63
    };

    private static readonly short[] DefaultScaleTable = BuildDefaultScaleTable();

    private readonly int[] _coef = new int[64];
    private readonly int[] _idctTmp = new int[64];
    private readonly int[] _cr = new int[64], _cb = new int[64];
    private readonly int[][] _y = { new int[64], new int[64], new int[64], new int[64] };
    private readonly byte[] _rgb = new byte[16 * 16 * 3];
    private int _blockIndex;

    /// <summary>Luminance quant table (zig-zag order), uploaded by MDEC(2); defaults to unit quant.</summary>
    internal byte[] LumQuant { get; } = BuildDefaultQuantTable();
    /// <summary>Chrominance quant table (zig-zag order), uploaded by MDEC(2) with bit 0 set; defaults to unit quant.</summary>
    internal byte[] ChrQuant { get; } = BuildDefaultQuantTable();
    /// <summary>IDCT scale table (1.15 fixed point), uploaded by MDEC(3); all-zero means the standard table.</summary>
    internal short[] ScaleTable { get; } = new short[64];

    private uint Depth => (_cmdWord >> 27) & 3U;
    private bool IsMono => Depth <= 1;
    private bool IsSignedOutput => (_cmdWord & (1U << 26)) != 0;
    private bool SetBit15 => (_cmdWord & (1U << 25)) != 0;

    /// <summary>Status bits 18-16: 4 = Cr, 5 = Cb, 0..3 = Y1..Y4 (mono mode always reports Y).</summary>
    private int CurrentBlockStatus => IsMono && _command == 1 ? 0 : _blockIndex switch { 0 => 4, 1 => 5, _ => _blockIndex - 2 };

    private void ResetMacroblockState() => _blockIndex = 0;

    private bool TryDecodeNextBlock()
    {
        bool mono = IsMono;
        bool chroma = !mono && _blockIndex < 2;
        if (!TryParseBlock(chroma ? ChrQuant : LumQuant)) return false;

        int[] dst = mono ? _y[0] : _blockIndex switch { 0 => _cr, 1 => _cb, _ => _y[_blockIndex - 2] };
        RunIdct(_coef, dst);

        if (mono)
        {
            EmitMono(dst);
            OnMacroblockDecoded();
            return true;
        }

        if (++_blockIndex < 6) return true;
        _blockIndex = 0;
        EmitColorMacroblock();
        OnMacroblockDecoded();
        return true;
    }

    /// <summary>Parses one RLE block starting at <c>_parse</c>; leaves <c>_parse</c> untouched when data is still incomplete.</summary>
    private bool TryParseBlock(byte[] qt)
    {
        int i = _parse, count = _in.Count;
        while (i < count && _in[i] == 0xFE00) i++;
        if (i >= count) return false;

        Array.Clear(_coef);
        ushort n = _in[i++];
        int qScale = (n >> 10) & 0x3F;
        int k = 0;
        int val = Signed10(n) * qt[0];
        while (true)
        {
            if (qScale == 0) val = Signed10(n) * 2;
            val = Math.Clamp(val, -0x400, 0x3FF);
            _coef[qScale > 0 ? ZagZig[k] : k] = val;
            if (k == 63) break;
            if (i >= count) return false;
            n = _in[i++];
            k += ((n >> 10) & 0x3F) + 1; // 0xFE00 carries run 63, so it always terminates the block
            if (k > 63) break;
            val = (Signed10(n) * qt[k] * qScale + 4) / 8;
        }
        _parse = i;
        return true;
    }

    private static int Signed10(ushort n) => ((n & 0x3FF) << 22) >> 22;

    /// <summary>Two-pass separable IDCT: each pass is dst[x + y*8] = Σz src[y + z*8] * (scale[x + z*8] / 8) / 0x2000.</summary>
    private void RunIdct(int[] coef, int[] dst)
    {
        short[] scale = ScaleTable[0] != 0 ? ScaleTable : DefaultScaleTable;
        IdctPass(coef, _idctTmp, scale);
        IdctPass(_idctTmp, dst, scale);
    }

    private static void IdctPass(int[] src, int[] dst, short[] scale)
    {
        for (int x = 0; x < 8; x++)
        {
            for (int y = 0; y < 8; y++)
            {
                long sum = 0;
                for (int z = 0; z < 8; z++)
                {
                    int c = src[y + z * 8];
                    if (c != 0) sum += (long)c * (scale[x + z * 8] / 8);
                }
                dst[x + y * 8] = (int)((sum + 0x1000) >> 13);
            }
        }
    }

    private void EmitMono(int[] yBlk)
    {
        int xor = IsSignedOutput ? 0 : 0x80;
        if (Depth == 1)
        {
            for (int i = 0; i < 64; i += 4)
            {
                uint w = 0;
                for (int b = 0; b < 4; b++) w |= (uint)((Math.Clamp(yBlk[i + b], -128, 127) ^ xor) & 0xFF) << (b * 8);
                EnqueueOutWord(w);
            }
            return;
        }
        for (int i = 0; i < 64; i += 8)
        {
            uint w = 0;
            for (int b = 0; b < 8; b++) w |= (uint)(((Math.Clamp(yBlk[i + b], -128, 127) ^ xor) & 0xFF) >> 4) << (b * 4);
            EnqueueOutWord(w);
        }
    }

    private void EmitColorMacroblock()
    {
        int xor = IsSignedOutput ? 0 : 0x80;
        for (int q = 0; q < 4; q++)
        {
            int xx = (q & 1) * 8, yy = (q >> 1) * 8;
            int[] yBlk = _y[q];
            for (int y = 0; y < 8; y++)
            {
                for (int x = 0; x < 8; x++)
                {
                    int ci = ((x + xx) >> 1) + ((y + yy) >> 1) * 8;
                    int cr = _cr[ci], cb = _cb[ci];
                    int rC = (cr * 22970) >> 14;               // 1.402 * Cr
                    int gC = -((cb * 5631 + cr * 11703) >> 14); // -0.3437 * Cb - 0.7143 * Cr
                    int bC = (cb * 29032) >> 14;               // 1.772 * Cb
                    int yv = yBlk[x + y * 8];
                    int o = ((x + xx) + (y + yy) * 16) * 3;
                    _rgb[o] = (byte)(Math.Clamp(yv + rC, -128, 127) ^ xor);
                    _rgb[o + 1] = (byte)(Math.Clamp(yv + gC, -128, 127) ^ xor);
                    _rgb[o + 2] = (byte)(Math.Clamp(yv + bC, -128, 127) ^ xor);
                }
            }
        }

        if (Depth == 3)
        {
            uint bit15 = SetBit15 ? 0x8000U : 0U;
            for (int p = 0; p < 256; p += 2)
            {
                EnqueueOutWord(ToRgb15(p, bit15) | (ToRgb15(p + 1, bit15) << 16));
            }
            return;
        }
        for (int i = 0; i < _rgb.Length; i += 4)
        {
            EnqueueOutWord(_rgb[i] | ((uint)_rgb[i + 1] << 8) | ((uint)_rgb[i + 2] << 16) | ((uint)_rgb[i + 3] << 24));
        }
    }

    private uint ToRgb15(int pixel, uint bit15)
    {
        int o = pixel * 3;
        return bit15 | (uint)(_rgb[o] >> 3) | ((uint)(_rgb[o + 1] >> 3) << 5) | ((uint)(_rgb[o + 2] >> 3) << 10);
    }

    /// <summary>Standard PS1 IDCT scale table: (z == 0 ? 1/√2 : 1) * cos((2x + 1) z π / 16) in 1.15 fixed point.</summary>
    private static short[] BuildDefaultScaleTable()
    {
        short[] t = new short[64];
        for (int z = 0; z < 8; z++)
        {
            double s = z == 0 ? Math.Sqrt(0.5) : 1.0;
            for (int x = 0; x < 8; x++)
                t[x + z * 8] = (short)Math.Clamp(Math.Round(s * Math.Cos((2 * x + 1) * z * Math.PI / 16.0) * 32768.0), short.MinValue, short.MaxValue);
        }
        return t;
    }

    private static byte[] BuildDefaultQuantTable()
    {
        byte[] qt = new byte[64];
        Array.Fill(qt, (byte)1);
        return qt;
    }
}
