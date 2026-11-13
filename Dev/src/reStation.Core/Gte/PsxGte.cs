// @module: reStation.Core.Gte.PsxGte
// @purpose: PS1 COP2 Geometry Transformation Engine register file (data/control), FLAG saturation, UNR division, and snapshots.
// @depends-on: System.Numerics, reStation.Core, reStation.Core.Gte.PgxpTracker
// @exports: PsxGte
// @owner: devv-cpu-cd (T2)

using System.Numerics;

namespace reStation.Core.Gte;

/// <summary>
/// Hardware-faithful GTE (psx-spx register layout). Commands live in <c>PsxGte.Commands.cs</c>; this file owns the
/// register file, MAC/IR/colour saturation with FLAG reporting, and the Newton-Raphson UNR perspective divider.
/// </summary>
public sealed partial class PsxGte
{
    private const uint SnapshotMarker = 0x55544752U; // "RGTU"
    private static readonly byte[] UnrTable = BuildUnrTable();

    private readonly uint[] _d = new uint[32];
    private readonly uint[] _c = new uint[32];
    private uint _flag;

    public PsxGte(RecompSettings settings, PgxpTracker pgxp)
    {
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Pgxp = pgxp ?? throw new ArgumentNullException(nameof(pgxp));
    }

    public RecompSettings Settings { get; }
    public PgxpTracker Pgxp { get; }
    /// <summary>Total GTE commands executed (telemetry / tests).</summary>
    public long CommandsExecuted { get; private set; }
    /// <summary>FLAG register (control 31) after the last command.</summary>
    public uint Flag => _flag;

    /// <summary>MFC2 / SWC2 source.</summary>
    public uint ReadData(int register)
    {
        int r = register & 31;
        return r switch
        {
            1 or 3 or 5 or 8 or 9 or 10 or 11 => (uint)(int)(short)_d[r],
            7 or 16 or 17 or 18 or 19 => _d[r] & 0xFFFFU,
            15 => _d[14],
            28 or 29 => PackIrToRgb15(),
            31 => (uint)LeadingCount(_d[30]),
            _ => _d[r]
        };
    }

    /// <summary>MTC2 / LWC2 target.</summary>
    public void WriteData(int register, uint value)
    {
        int r = register & 31;
        switch (r)
        {
            case 15: PushSxy(value); return;
            case 28:
                _d[28] = value & 0x7FFFU;
                _d[9] = (uint)((value & 0x1FU) << 7);
                _d[10] = (uint)(((value >> 5) & 0x1FU) << 7);
                _d[11] = (uint)(((value >> 10) & 0x1FU) << 7);
                return;
            case 29: case 31: return; // ORGB / LZCR are read-only
            default: _d[r] = value; return;
        }
    }

    /// <summary>CFC2 source (H reads back sign-extended, a documented hardware quirk).</summary>
    public uint ReadControl(int register)
    {
        int r = register & 31;
        return r switch
        {
            4 or 12 or 20 or 26 or 27 or 29 or 30 => (uint)(int)(short)_c[r],
            31 => _flag,
            _ => _c[r]
        };
    }

    /// <summary>CTC2 target.</summary>
    public void WriteControl(int register, uint value)
    {
        int r = register & 31;
        if (r == 31) { _flag = value & 0x7FFFF000U; if ((_flag & 0x7F87E000U) != 0) _flag |= 0x80000000U; return; }
        _c[r] = value;
    }

    /// <summary>Zeroes every register (power-on / BIOS reset).</summary>
    public void Reset()
    {
        Array.Clear(_d);
        Array.Clear(_c);
        _flag = 0;
    }

    public void WriteSnapshotState(BinaryWriter w)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.Write(SnapshotMarker);
        foreach (uint v in _d) w.Write(v);
        foreach (uint v in _c) w.Write(v);
        w.Write(_flag);
    }

    public void ReadSnapshotState(BinaryReader r)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (r.ReadUInt32() != SnapshotMarker) throw new InvalidDataException("Snapshot GTE marker mismatch.");
        for (int i = 0; i < 32; i++) _d[i] = r.ReadUInt32();
        for (int i = 0; i < 32; i++) _c[i] = r.ReadUInt32();
        _flag = r.ReadUInt32();
    }

    // ---- Register accessors -------------------------------------------------------------------------------------
    private short Ir(int i) => (short)_d[8 + i];
    private int Mac(int i) => (int)_d[24 + i];
    private static short Lo16(uint v) => (short)(v & 0xFFFFU);
    private static short Hi16(uint v) => (short)(v >> 16);
    private ushort Sz(int i) => (ushort)_d[16 + i];
    private short Sx(int i) => Lo16(_d[12 + i]);
    private short Sy(int i) => Hi16(_d[12 + i]);

    /// <summary>Reads matrix <paramref name="baseCtrl"/> (0 = RT, 8 = LLM, 16 = LCM) element (row, col).</summary>
    private short MatrixElement(int baseCtrl, int row, int col)
    {
        int index = row * 3 + col; // 0..8, packed two per word
        uint word = _c[baseCtrl + (index >> 1)];
        return (index & 1) == 0 ? Lo16(word) : Hi16(word);
    }

    private (short X, short Y, short Z) Vector(int n) => n switch
    {
        0 => (Lo16(_d[0]), Hi16(_d[0]), Lo16(_d[1])),
        1 => (Lo16(_d[2]), Hi16(_d[2]), Lo16(_d[3])),
        2 => (Lo16(_d[4]), Hi16(_d[4]), Lo16(_d[5])),
        _ => (Ir(1), Ir(2), Ir(3))
    };

    // ---- Saturation & FLAG ----------------------------------------------------------------------------------------
    /// <summary>Checks the 44-bit MAC1..3 overflow window, then stores MAC (after the sf shift) and returns it.</summary>
    private long SetMac(int i, long value, int shift)
    {
        if (value > 0x7FFFFFFFFFFL) _flag |= 1U << (31 - i);        // bits 30..28
        else if (value < -0x80000000000L) _flag |= 1U << (28 - i);  // bits 27..25
        long shifted = value >> shift;
        _d[24 + i] = (uint)(int)shifted;
        return shifted;
    }

    private long SetMac0(long value)
    {
        if (value > int.MaxValue) _flag |= 1U << 16;
        else if (value < int.MinValue) _flag |= 1U << 15;
        _d[24] = (uint)(int)value;
        return value;
    }

    private void SetIr(int i, long value, bool lm)
    {
        long min = lm ? 0 : -0x8000;
        if (value < min) { value = min; _flag |= 1U << (25 - i); }
        else if (value > 0x7FFF) { value = 0x7FFF; _flag |= 1U << (25 - i); }
        _d[8 + i] = (uint)(int)(short)value;
    }

    private void SetIr0(long value)
    {
        if (value < 0) { value = 0; _flag |= 1U << 12; }
        else if (value > 0x1000) { value = 0x1000; _flag |= 1U << 12; }
        _d[8] = (uint)value;
    }

    private void SetMacIr(int i, long value, int shift, bool lm) => SetIr(i, SetMac(i, value, shift), lm);

    private byte SaturateColor(int i, int value)
    {
        if (value < 0) { _flag |= 1U << (22 - i); return 0; }
        if (value > 0xFF) { _flag |= 1U << (22 - i); return 0xFF; }
        return (byte)value;
    }

    /// <summary>Pushes MAC1..3 / 16 into the RGB colour FIFO with the current CODE byte.</summary>
    private void PushColorFromMac()
    {
        uint r = SaturateColor(1, Mac(1) >> 4), g = SaturateColor(2, Mac(2) >> 4), b = SaturateColor(3, Mac(3) >> 4);
        _d[20] = _d[21];
        _d[21] = _d[22];
        _d[22] = r | (g << 8) | (b << 16) | (_d[6] & 0xFF000000U);
    }

    private void PushSz(long value)
    {
        if (value < 0) { value = 0; _flag |= 1U << 18; }
        else if (value > 0xFFFF) { value = 0xFFFF; _flag |= 1U << 18; }
        _d[16] = _d[17]; _d[17] = _d[18]; _d[18] = _d[19];
        _d[19] = (uint)value;
    }

    private void PushSxy(uint packed)
    {
        _d[12] = _d[13];
        _d[13] = _d[14];
        _d[14] = packed;
    }

    private static short SaturateScreen(long value, uint flagBit, ref uint flag)
    {
        if (value < -0x400) { flag |= flagBit; return -0x400; }
        if (value > 0x3FF) { flag |= flagBit; return 0x3FF; }
        return (short)value;
    }

    private void SetOtz(long mac0)
    {
        long z = mac0 >> 12;
        if (z < 0) { z = 0; _flag |= 1U << 18; }
        else if (z > 0xFFFF) { z = 0xFFFF; _flag |= 1U << 18; }
        _d[7] = (uint)z;
    }

    /// <summary>Hardware Newton-Raphson reciprocal division used by RTPS/RTPT (returns 0..0x1FFFF, FLAG bit 17 on overflow).</summary>
    private uint Divide(ushort h, ushort sz3)
    {
        if (sz3 == 0 || h >= sz3 * 2)
        {
            _flag |= 1U << 17;
            return 0x1FFFF;
        }
        int z = BitOperations.LeadingZeroCount((uint)sz3) - 16;
        ulong n = (ulong)h << z;
        uint d = (uint)sz3 << z;
        uint u = UnrTable[(d - 0x7FC0) >> 7] + 0x101U;
        d = (uint)((0x2000080L - (long)d * u) >> 8);
        d = (uint)((0x0000080L + (long)d * u) >> 8);
        ulong result = ((n * d) + 0x8000UL) >> 16;
        return (uint)Math.Min(0x1FFFFUL, result);
    }

    private static byte[] BuildUnrTable()
    {
        var table = new byte[0x101];
        for (int i = 0; i < table.Length; i++)
            table[i] = (byte)Math.Max(0, (0x40000 / (i + 0x100) + 1) / 2 - 0x101);
        return table;
    }

    private uint PackIrToRgb15()
    {
        static uint C(short ir) => (uint)Math.Clamp(ir >> 7, 0, 0x1F);
        return C(Ir(1)) | (C(Ir(2)) << 5) | (C(Ir(3)) << 10);
    }

    private static int LeadingCount(uint value)
    {
        uint v = (value & 0x80000000U) != 0 ? ~value : value;
        return v == 0 ? 32 : BitOperations.LeadingZeroCount(v);
    }
}
