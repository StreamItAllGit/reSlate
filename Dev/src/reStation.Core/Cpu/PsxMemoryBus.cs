// @module: reStation.Core.Cpu.PsxMemoryBus
// @purpose: PS1 physical memory map: 2 MB RAM (+mirrors), 1 KB scratchpad, BIOS ROM window, and 0x1F801xxx MMIO routing.
// @depends-on: System.Runtime.CompilerServices, System.Runtime.InteropServices, reStation.Core.Cpu.PsxCpu
// @exports: PsxMemoryBus
// @owner: devv-cpu-cd (T2)

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace reStation.Core.Cpu;

/// <summary>
/// Routes CPU loads/stores to RAM, scratchpad, and devices (timers/DMA/IRQ, joypad SIO0, CD-ROM, GPU, MDEC, SPU).
/// Honours COP0 SR.IsC cache isolation (stores are swallowed while the BIOS flushes the I-cache).
/// </summary>
public sealed class PsxMemoryBus
{
    public const int RamSize = 2 * 1024 * 1024;
    public const int ScratchpadSize = 1024;
    public const int BiosRomSize = 512 * 1024;
    private const uint RamMask = RamSize - 1;

    private readonly PsxCpu _cpu;
    private readonly uint[] _memControl = new uint[9];

    public PsxMemoryBus(PsxCpu cpu) => _cpu = cpu ?? throw new ArgumentNullException(nameof(cpu));

    public byte[] Ram { get; } = new byte[RamSize];
    public byte[] Scratchpad { get; } = new byte[ScratchpadSize];
    /// <summary>Optional BIOS ROM image window (zero-filled under HLE; reads at 0xBFC00000).</summary>
    public byte[] BiosRom { get; } = new byte[BiosRomSize];
    public uint CacheControl { get; set; } = 0x00000800U;
    public uint RamSizeRegister { get; set; } = 0x00000B88U;
    public long UnmappedAccesses { get; private set; }

    private bool CacheIsolated => (_cpu.Cop0Sr & 0x00010000U) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref byte At(byte[] buffer, uint offset) => ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(buffer), (nint)offset);

    public byte Read8(uint address)
    {
        uint p = address & 0x1FFFFFFFU;
        if (p < 0x00800000U) return Ram[p & RamMask];
        if (p is >= 0x1F800000U and < 0x1F800400U) return Scratchpad[p & 0x3FFU];
        if (p is >= 0x1F801000U and < 0x1F802000U) return ReadIo8(p);
        return (byte)ReadOther(p, 1);
    }

    public ushort Read16(uint address)
    {
        uint p = address & 0x1FFFFFFFU;
        if (p < 0x00800000U) return Unsafe.ReadUnaligned<ushort>(ref At(Ram, p & (RamMask & ~1U)));
        if (p is >= 0x1F800000U and < 0x1F800400U) return Unsafe.ReadUnaligned<ushort>(ref At(Scratchpad, p & 0x3FEU));
        if (p is >= 0x1F801000U and < 0x1F802000U)
        {
            if (p >= 0x1F801C00U) return _cpu.Spu.Read16(p);
            if (p is >= 0x1F801040U and < 0x1F801050U) return _cpu.Joypad.Read16(p);
            if (p is >= 0x1F801800U and < 0x1F801804U) return (ushort)(ReadIo8(p) | (ReadIo8(p + 1) << 8));
            return (ushort)(ReadIo32(p & ~3U) >> (int)((p & 2U) * 8));
        }
        return (ushort)ReadOther(p, 2);
    }

    public uint Read32(uint address)
    {
        uint p = address & 0x1FFFFFFFU;
        if (p < 0x00800000U) return Unsafe.ReadUnaligned<uint>(ref At(Ram, p & (RamMask & ~3U)));
        if (p is >= 0x1F800000U and < 0x1F800400U) return Unsafe.ReadUnaligned<uint>(ref At(Scratchpad, p & 0x3FCU));
        if (p is >= 0x1F801000U and < 0x1F802000U) return ReadIo32(p & ~3U);
        return ReadOther(p, 4);
    }

    public void Write8(uint address, byte value)
    {
        if (CacheIsolated) return;
        uint p = address & 0x1FFFFFFFU;
        if (p < 0x00800000U) { Ram[p & RamMask] = value; return; }
        if (p is >= 0x1F800000U and < 0x1F800400U) { Scratchpad[p & 0x3FFU] = value; return; }
        if (p is >= 0x1F801800U and < 0x1F801804U) { _cpu.CdRom.WriteRegister((int)(p & 3U), value); return; }
        if (p is >= 0x1F801040U and < 0x1F801050U)
        {
            if ((p & 0xFU) == 0) _cpu.Joypad.Write16(p, value); // JOY_TX_DATA byte transfer
            else _cpu.Joypad.Write16(p & ~1U, (ushort)((p & 1U) != 0
                ? (_cpu.Joypad.Read16(p & ~1U) & 0x00FF) | (value << 8)
                : (_cpu.Joypad.Read16(p) & 0xFF00) | value));
            return;
        }
        if (p is >= 0x1F801000U and < 0x1F802000U)
        {
            uint aligned = p & ~3U;
            int shift = (int)((p & 3U) * 8);
            WriteIo32(aligned, (ReadIo32Raw(aligned) & ~(0xFFU << shift)) | ((uint)value << shift));
            return;
        }
        UnmappedAccesses++;
    }

    public void Write16(uint address, ushort value)
    {
        if (CacheIsolated) return;
        uint p = address & 0x1FFFFFFFU;
        if (p < 0x00800000U) { Unsafe.WriteUnaligned(ref At(Ram, p & (RamMask & ~1U)), value); return; }
        if (p is >= 0x1F800000U and < 0x1F800400U) { Unsafe.WriteUnaligned(ref At(Scratchpad, p & 0x3FEU), value); return; }
        if (p is >= 0x1F801C00U and < 0x1F802000U) { _cpu.Spu.Write16(p, value); return; }
        if (p is >= 0x1F801040U and < 0x1F801050U) { _cpu.Joypad.Write16(p, value); return; }
        if (p is >= 0x1F801000U and < 0x1F802000U)
        {
            uint aligned = p & ~3U;
            int shift = (int)((p & 2U) * 8);
            if (aligned is >= 0x1F801100U and < 0x1F801130U) { _cpu.TimersAndDma.WriteTimerReg(p & ~1U, value); return; }
            WriteIo32(aligned, (ReadIo32Raw(aligned) & ~(0xFFFFU << shift)) | ((uint)value << shift));
            return;
        }
        UnmappedAccesses++;
    }

    public void Write32(uint address, uint value)
    {
        if (CacheIsolated) return;
        uint p = address & 0x1FFFFFFFU;
        if (p < 0x00800000U) { Unsafe.WriteUnaligned(ref At(Ram, p & (RamMask & ~3U)), value); return; }
        if (p is >= 0x1F800000U and < 0x1F800400U) { Unsafe.WriteUnaligned(ref At(Scratchpad, p & 0x3FCU), value); return; }
        if (p is >= 0x1F801000U and < 0x1F802000U) { WriteIo32(p & ~3U, value); return; }
        if (p == 0x1FFE0130U) { CacheControl = value; return; }
        UnmappedAccesses++;
    }

    /// <summary>Returns a RAM span for bulk BIOS operations when [address, address+length) maps linearly into RAM.</summary>
    public bool TryGetRamSpan(uint address, uint length, out Span<byte> span)
    {
        uint phys = address & 0x1FFFFFFFU, offset = phys & RamMask;
        if (phys < 0x00800000U && length <= RamSize && offset <= RamSize - length)
        {
            span = Ram.AsSpan((int)offset, (int)length);
            return true;
        }
        span = default;
        return false;
    }

    private byte ReadIo8(uint p)
    {
        if (p is >= 0x1F801800U and < 0x1F801804U) return _cpu.CdRom.ReadRegister((int)(p & 3U));
        if (p is >= 0x1F801040U and < 0x1F801050U) return _cpu.Joypad.Read8(p);
        if (p >= 0x1F801C00U) return (byte)(_cpu.Spu.Read16(p & ~1U) >> (int)((p & 1U) * 8));
        return (byte)(ReadIo32(p & ~3U) >> (int)((p & 3U) * 8));
    }

    private uint ReadIo32(uint p)
    {
        if (p is >= 0x1F801800U and < 0x1F801804U) return (uint)(ReadIo8(p) | (ReadIo8(p + 1) << 8) | (ReadIo8(p + 2) << 16) | (ReadIo8(p + 3) << 24));
        if (p is >= 0x1F801040U and < 0x1F801050U) return _cpu.Joypad.Read16(p) | ((uint)_cpu.Joypad.Read16(p + 2) << 16);
        return ReadIo32Raw(p);
    }

    /// <summary>Side-effect-tolerant 32-bit device read used by both loads and sub-word read-modify-write stores.</summary>
    private uint ReadIo32Raw(uint p)
    {
        if (p < 0x1F801024U) return _memControl[(p - 0x1F801000U) >> 2];
        if (p == 0x1F801060U) return RamSizeRegister;
        if (p is >= 0x1F801070U and <= 0x1F801074U) return _cpu.TimersAndDma.ReadIrqReg(p);
        if (p is >= 0x1F801080U and < 0x1F801100U) return _cpu.TimersAndDma.ReadDmaReg(p);
        if (p is >= 0x1F801100U and < 0x1F801130U) return _cpu.TimersAndDma.ReadTimerReg(p);
        if (p == 0x1F801810U) return _cpu.Gpu.ReadGpuRead();
        if (p == 0x1F801814U) return _cpu.Gpu.ReadGpuStat();
        if (p == 0x1F801820U) return _cpu.Mdec.DmaOut();
        if (p == 0x1F801824U) return _cpu.Mdec.ReadStatus();
        if (p >= 0x1F801C00U) return _cpu.Spu.Read16(p) | ((uint)_cpu.Spu.Read16(p + 2) << 16);
        return 0;
    }

    private void WriteIo32(uint p, uint value)
    {
        if (p < 0x1F801024U) { _memControl[(p - 0x1F801000U) >> 2] = value; return; }
        if (p == 0x1F801060U) { RamSizeRegister = value; return; }
        if (p is >= 0x1F801040U and < 0x1F801050U) { _cpu.Joypad.Write16(p, (ushort)value); return; }
        if (p is >= 0x1F801070U and <= 0x1F801074U) { _cpu.TimersAndDma.WriteIrqReg(p, value); return; }
        if (p is >= 0x1F801080U and < 0x1F801100U) { _cpu.TimersAndDma.WriteDmaReg(p, value); return; }
        if (p is >= 0x1F801100U and < 0x1F801130U) { _cpu.TimersAndDma.WriteTimerReg(p, value); return; }
        if (p is >= 0x1F801800U and < 0x1F801804U) { for (int i = 0; i < 4; i++) _cpu.CdRom.WriteRegister(i, (byte)(value >> (i * 8))); return; }
        if (p == 0x1F801810U) { _cpu.Gpu.WriteGp0(value); return; }
        if (p == 0x1F801814U) { _cpu.Gpu.WriteGp1(value); return; }
        if (p == 0x1F801820U) { _cpu.Mdec.DmaIn(value); return; }
        if (p == 0x1F801824U) { _cpu.Mdec.WriteControl(value); return; }
        if (p >= 0x1F801C00U) { _cpu.Spu.Write16(p, (ushort)value); _cpu.Spu.Write16(p + 2, (ushort)(value >> 16)); }
    }

    private uint ReadOther(uint p, int width)
    {
        if (p is >= 0x1FC00000U and < 0x1FC80000U)
        {
            uint off = p & (BiosRomSize - 1) & ~(uint)(width - 1);
            return width switch
            {
                1 => BiosRom[off],
                2 => Unsafe.ReadUnaligned<ushort>(ref At(BiosRom, off)),
                _ => Unsafe.ReadUnaligned<uint>(ref At(BiosRom, off))
            };
        }
        if (p == 0x1FFE0130U) return CacheControl;
        if (p is >= 0x1F000000U and < 0x1F800000U) return width == 1 ? 0xFFU : width == 2 ? 0xFFFFU : 0xFFFFFFFFU; // expansion 1 (no cart)
        UnmappedAccesses++;
        return 0;
    }

    public void WriteSnapshotState(BinaryWriter w)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.Write(Ram);
        w.Write(Scratchpad);
        foreach (uint v in _memControl) w.Write(v);
        w.Write(CacheControl);
        w.Write(RamSizeRegister);
    }

    public void ReadSnapshotState(BinaryReader r)
    {
        ArgumentNullException.ThrowIfNull(r);
        r.BaseStream.ReadExactly(Ram);
        r.BaseStream.ReadExactly(Scratchpad);
        for (int i = 0; i < _memControl.Length; i++) _memControl[i] = r.ReadUInt32();
        CacheControl = r.ReadUInt32();
        RamSizeRegister = r.ReadUInt32();
    }
}
