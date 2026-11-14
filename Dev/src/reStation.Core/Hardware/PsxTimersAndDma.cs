// @module: reStation.Core.Hardware.PsxTimersAndDma
// @purpose: PS1 interrupt controller (I_STAT/I_MASK), root counters T0-T2 (sysclk/dotclock/hblank/sysclk8), and DMA channels 0-6.
// @depends-on: reStation.Core.Cpu.PsxCpu (bus, GPU, MDEC, CD-ROM, SPU, BIOS events)
// @exports: PsxIrq, PsxTimersAndDma
// @owner: devv-cpu-cd (T2)

using reStation.Core.Cpu;

namespace reStation.Core.Hardware;

/// <summary>I_STAT / I_MASK bit numbers.</summary>
public static class PsxIrq
{
    public const int VBlank = 0, Gpu = 1, CdRom = 2, Dma = 3, Timer0 = 4, Timer1 = 5, Timer2 = 6, Controller = 7, Sio = 8, Spu = 9, Lightpen = 10;
}

public sealed class PsxTimersAndDma
{
    /// <summary>NTSC scanline length in CPU cycles (33,868,800 / 60 / 263).</summary>
    public const long CyclesPerScanline = 2146;
    private const uint SnapshotMarker = 0x55544D52U; // "RMTU"

    private readonly PsxCpu _cpu;
    private readonly uint[] _value = new uint[3];
    private readonly uint[] _mode = new uint[3];
    private readonly uint[] _target = new uint[3];
    private readonly long[] _fraction = new long[3];
    private readonly bool[] _oneShotFired = new bool[3];
    private readonly uint[] _madr = new uint[7];
    private readonly uint[] _bcr = new uint[7];
    private readonly uint[] _chcr = new uint[7];
    private long _lastCycle;

    public PsxTimersAndDma(PsxCpu cpu) => _cpu = cpu ?? throw new ArgumentNullException(nameof(cpu));

    public uint InterruptStatus { get; set; }
    public uint InterruptMask { get; set; }
    /// <summary>Bits acknowledged by guest writes to I_STAT since the BIOS HLE last cleared it.</summary>
    public uint AckedIrqBits { get; set; }
    /// <summary>Root counters whose IRQ fired since the last BIOS RCnt event delivery (bit i = timer i).</summary>
    public uint PendingTimerEvents { get; set; }
    public uint Dpcr { get; set; } = 0x07654321U;
    public uint Dicr { get; set; }
    public long DmaTransfers { get; private set; }

    public bool HasPendingInterrupt => (InterruptStatus & InterruptMask) != 0;

    public void RaiseIrq(int irqBit) => InterruptStatus |= 1U << irqBit;

    // ---- Root counters ------------------------------------------------------------------------------------------
    /// <summary>CPU cycles per counter tick for timer <paramref name="id"/> as a (numerator, denominator) ratio.</summary>
    private (long Num, long Den) CyclesPerTick(int id)
    {
        uint src = (_mode[id] >> 8) & 3U;
        if (id == 0 && (src & 1U) != 0)
        {
            int divider = _cpu.Gpu.DisplayWidth switch { 256 => 10, 320 => 8, 368 => 7, 512 => 5, _ => 4 };
            return (divider * 7L, 11L); // dotclock = 53.69 MHz / divider; sysclk = 53.69 MHz * 7/11
        }
        if (id == 1 && (src & 1U) != 0) return (CyclesPerScanline, 1);
        if (id == 2 && (src & 2U) != 0) return (8, 1);
        return (1, 1);
    }

    public void TickTo(long targetCycle)
    {
        long delta = targetCycle - _lastCycle;
        if (delta <= 0) return;
        _lastCycle = targetCycle;
        for (int i = 0; i < 3; i++)
        {
            if (i == 2 && ((_mode[2] & 1U) != 0) && ((_mode[2] >> 1) & 3U) is 0 or 3) continue; // T2 sync stop modes
            (long num, long den) = CyclesPerTick(i);
            long acc = delta * den + _fraction[i];
            long ticks = acc / num;
            _fraction[i] = acc % num;
            if (ticks > 0) AdvanceCounter(i, ticks);
        }
    }

    private void AdvanceCounter(int i, long ticks)
    {
        uint mode = _mode[i];
        long target = _target[i] == 0 ? 0x10000 : _target[i];
        bool resetOnTarget = (mode & 0x08U) != 0;
        long value = _value[i] + ticks;
        bool hitTarget = _value[i] < target && value >= target;
        bool hitMax = value > 0xFFFF;
        if (resetOnTarget && value >= target) value %= target;
        else value &= 0xFFFF;
        _value[i] = (uint)value;
        if (hitTarget) _mode[i] |= 1U << 11;
        if (hitMax) _mode[i] |= 1U << 12;
        bool fire = (hitTarget && (mode & 0x10U) != 0) || (hitMax && (mode & 0x20U) != 0);
        if (!fire) return;
        bool repeat = (mode & 0x40U) != 0;
        if (!repeat && _oneShotFired[i]) return;
        _oneShotFired[i] = true;
        _mode[i] &= ~(1U << 10);
        RaiseIrq(PsxIrq.Timer0 + i);
        PendingTimerEvents |= 1U << i;
        if ((mode & 0x80U) == 0) _mode[i] |= 1U << 10; // pulse mode: request bit returns high immediately
    }

    /// <summary>Earliest cycle at which an IRQ-enabled counter reaches its target/overflow (long.MaxValue if none).</summary>
    public long GetNextWakeCycle(long totalCycles)
    {
        long best = long.MaxValue;
        for (int i = 0; i < 3; i++)
        {
            uint mode = _mode[i];
            if ((mode & 0x30U) == 0) continue;
            long limit = (mode & 0x10U) != 0 && _target[i] > _value[i] ? _target[i] : 0x10000;
            (long num, long den) = CyclesPerTick(i);
            long cycles = Math.Max(1, ((limit - _value[i]) * num + den - 1) / den);
            best = Math.Min(best, totalCycles + cycles);
        }
        return best;
    }

    public uint ReadTimerReg(uint p)
    {
        int id = (int)((p - 0x1F801100U) >> 4);
        if (id is < 0 or > 2) return 0;
        switch (p & 0xFU)
        {
            case 0x0: return _value[id] & 0xFFFFU;
            case 0x4:
                uint m = _mode[id];
                _mode[id] &= ~((1U << 11) | (1U << 12));
                return m;
            case 0x8: return _target[id] & 0xFFFFU;
            default: return 0;
        }
    }

    public void WriteTimerReg(uint p, uint val)
    {
        int id = (int)((p - 0x1F801100U) >> 4);
        if (id is < 0 or > 2) return;
        switch (p & 0xFU)
        {
            case 0x0: _value[id] = val & 0xFFFFU; break;
            case 0x4:
                _mode[id] = (val & 0x3FFU) | (1U << 10);
                _value[id] = 0;
                _fraction[id] = 0;
                _oneShotFired[id] = false;
                break;
            case 0x8: _target[id] = val & 0xFFFFU; break;
        }
    }

    // ---- Interrupt controller -----------------------------------------------------------------------------------
    public uint ReadIrqReg(uint p) => p == 0x1F801070U ? InterruptStatus : InterruptMask;

    public void WriteIrqReg(uint p, uint val)
    {
        if (p == 0x1F801070U) { AckedIrqBits |= ~val & 0x7FFU; InterruptStatus &= val; }
        else InterruptMask = val & 0x7FFU;
    }

    // ---- DMA ------------------------------------------------------------------------------------------------------
    public uint ReadDmaReg(uint p)
    {
        if (p == 0x1F8010F0U) return Dpcr;
        if (p == 0x1F8010F4U) return Dicr;
        int ch = (int)((p - 0x1F801080U) >> 4);
        if (ch is < 0 or > 6) return 0;
        return (p & 0xFU) switch { 0x0 => _madr[ch], 0x4 => _bcr[ch], 0x8 => _chcr[ch], _ => 0 };
    }

    public void WriteDmaReg(uint p, uint val)
    {
        if (p == 0x1F8010F0U) { Dpcr = val; return; }
        if (p == 0x1F8010F4U)
        {
            uint ack = val & 0x7F000000U;
            Dicr = ((Dicr & 0x7F000000U) & ~ack) | (val & 0x00FF803FU);
            UpdateDmaMasterFlag();
            return;
        }
        int ch = (int)((p - 0x1F801080U) >> 4);
        if (ch is < 0 or > 6) return;
        switch (p & 0xFU)
        {
            case 0x0: _madr[ch] = val & 0x00FFFFFFU; break;
            case 0x4: _bcr[ch] = val; break;
            case 0x8:
                _chcr[ch] = ch == 6 ? (val & 0x51000000U) | 0x00000002U : val;
                // Transfers complete instantly on the start/busy bit (HLE BIOS never gates DPCR enables).
                if ((val & 0x01000000U) != 0) ExecuteDma(ch);
                break;
        }
    }

    private void ExecuteDma(int ch)
    {
        uint madr = _madr[ch] & 0x1FFFFCU, bcr = _bcr[ch], chcr = _chcr[ch];
        uint sync = (chcr >> 9) & 3U;
        bool toDevice = (chcr & 1U) != 0;
        int step = (chcr & 2U) != 0 ? -4 : 4;
        uint words = sync == 0 ? (bcr & 0xFFFFU) : (bcr & 0xFFFFU) * Math.Max(1U, bcr >> 16);
        if (sync == 0 && words == 0) words = 0x10000U;
        DmaTransfers++;

        switch (ch)
        {
            case 6:
                uint addr = madr;
                for (uint i = 1; i < words; i++) { _cpu.Bus.Write32(addr, (addr - 4) & 0x1FFFFCU); addr = (addr - 4) & 0x1FFFFCU; }
                _cpu.Bus.Write32(addr, 0x00FFFFFFU);
                NotifyDmaRamWrite(madr, -4, words);
                break;
            case 2 when sync == 2:
                uint node = madr;
                for (int guard = 0; guard < 0x40000; guard++)
                {
                    uint header = _cpu.Bus.Read32(node);
                    for (uint i = 1; i <= header >> 24; i++) _cpu.Gpu.WriteGp0(_cpu.Bus.Read32((node + i * 4) & 0x1FFFFCU));
                    if ((header & 0x00800000U) != 0) break;
                    node = header & 0x1FFFFCU;
                }
                break;
            default:
                uint a = madr;
                for (uint i = 0; i < words; i++, a = (uint)(a + step) & 0x1FFFFCU)
                {
                    if (toDevice) WriteDevice(ch, _cpu.Bus.Read32(a));
                    else _cpu.Bus.Write32(a, ReadDevice(ch));
                }
                if (!toDevice) NotifyDmaRamWrite(madr, step, words);
                if (sync == 1) _madr[ch] = a;
                break;
        }
        if (ch == 4) _cpu.Bios.DeliverClassEvent(0xF0000009U, 0x0020U); // HwSPU / EvSpCOMP
        _chcr[ch] &= ~0x11000000U;
        if (((Dicr >> (16 + ch)) & 1U) != 0)
        {
            Dicr |= 1U << (24 + ch);
            UpdateDmaMasterFlag();
        }
    }

    private void NotifyDmaRamWrite(uint madr, int step, uint words)
    {
        if (words == 0) return;
        long first = madr, last = madr + ((long)words - 1L) * step;
        long low = Math.Min(first, last), high = Math.Max(first, last);
        if (low >= 0 && high < Cpu.PsxMemoryBus.RamSize) _cpu.Bus.NotifyRamWriteRange((uint)low, (uint)(high - low + 4L));
        else _cpu.Bus.NotifyRamWriteRange(0, Cpu.PsxMemoryBus.RamSize);
    }

    private void WriteDevice(int ch, uint word)
    {
        switch (ch)
        {
            case 0: _cpu.Mdec.DmaIn(word); break;
            case 2: _cpu.Gpu.WriteGp0(word); break;
            case 4: _cpu.Spu.WriteDmaWord(word); break;
        }
    }

    private uint ReadDevice(int ch) => ch switch
    {
        1 => _cpu.Mdec.DmaOut(),
        2 => _cpu.Gpu.ReadGpuRead(),
        3 => _cpu.CdRom.ReadDataWord(),
        4 => _cpu.Spu.ReadDmaWord(),
        _ => 0xFFFFFFFFU
    };

    private void UpdateDmaMasterFlag()
    {
        bool master = (Dicr & 0x00008000U) != 0 || ((Dicr & 0x00800000U) != 0 && ((Dicr >> 24) & (Dicr >> 16) & 0x7FU) != 0);
        bool was = (Dicr & 0x80000000U) != 0;
        Dicr = master ? Dicr | 0x80000000U : Dicr & ~0x80000000U;
        if (master && !was) RaiseIrq(PsxIrq.Dma);
    }

    public void WriteSnapshotState(BinaryWriter w)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.Write(SnapshotMarker);
        w.Write(InterruptStatus); w.Write(InterruptMask); w.Write(PendingTimerEvents); w.Write(_lastCycle);
        for (int i = 0; i < 3; i++) { w.Write(_value[i]); w.Write(_mode[i]); w.Write(_target[i]); w.Write(_fraction[i]); w.Write(_oneShotFired[i]); }
        for (int i = 0; i < 7; i++) { w.Write(_madr[i]); w.Write(_bcr[i]); w.Write(_chcr[i]); }
        w.Write(Dpcr); w.Write(Dicr);
    }

    public void ReadSnapshotState(BinaryReader r)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (r.ReadUInt32() != SnapshotMarker) throw new InvalidDataException("Snapshot timers/DMA marker mismatch.");
        InterruptStatus = r.ReadUInt32(); InterruptMask = r.ReadUInt32(); PendingTimerEvents = r.ReadUInt32(); _lastCycle = r.ReadInt64();
        for (int i = 0; i < 3; i++) { _value[i] = r.ReadUInt32(); _mode[i] = r.ReadUInt32(); _target[i] = r.ReadUInt32(); _fraction[i] = r.ReadInt64(); _oneShotFired[i] = r.ReadBoolean(); }
        for (int i = 0; i < 7; i++) { _madr[i] = r.ReadUInt32(); _bcr[i] = r.ReadUInt32(); _chcr[i] = r.ReadUInt32(); }
        Dpcr = r.ReadUInt32(); Dicr = r.ReadUInt32();
        AckedIrqBits = 0;
    }
}
