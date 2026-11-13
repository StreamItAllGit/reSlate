// @module: reStation.Core.Cpu.PsxCpu.Bus
// @purpose: T1 skeleton flat PS1 memory map (2 MB RAM mirrors, scratchpad, GPU/MDEC/SPU MMIO), COP2 register file, and R3000A ALU helpers.
// @depends-on: reStation.Contracts
// @exports: PsxCpu (partial)
// @owner: devv-cpu-cd (T2) — replace internals with PsxMemoryBus/PsxGte/PsxTimersAndDma; IReStationCpu members stay.

namespace reStation.Core.Cpu;

public sealed partial class PsxCpu
{
    public const int RamSize = 2 * 1024 * 1024;
    public const int ScratchpadSize = 1024;

    private readonly byte[] _ram = new byte[RamSize];
    private readonly byte[] _scratch = new byte[ScratchpadSize];
    private readonly uint[] _gteData = new uint[32];
    private readonly uint[] _gteControl = new uint[32];

    /// <summary>Main RAM (2 MB) — exposed for BIOS HLE EXE loading, fixtures, and tests.</summary>
    public Span<byte> Ram => _ram;

    public byte Read8(uint address)
    {
        uint p = address & 0x1FFFFFFFU;
        if (p < 0x00800000U) return _ram[p & (RamSize - 1)];
        if (p is >= 0x1F800000U and < 0x1F800400U) return _scratch[p & (ScratchpadSize - 1)];
        return (byte)(ReadIo32(p & ~3U) >> (int)((p & 3U) * 8));
    }

    public ushort Read16(uint address)
    {
        uint p = address & 0x1FFFFFFFU;
        if (p < 0x00800000U) return BitConverter.ToUInt16(_ram, (int)(p & (RamSize - 2)));
        if (p is >= 0x1F800000U and < 0x1F800400U) return BitConverter.ToUInt16(_scratch, (int)(p & (ScratchpadSize - 2)));
        if (p is >= 0x1F801C00U and < 0x1F802000U) return Spu.Read16(p);
        return (ushort)(ReadIo32(p & ~3U) >> (int)((p & 2U) * 8));
    }

    public uint Read32(uint address)
    {
        uint p = address & 0x1FFFFFFFU;
        if (p < 0x00800000U) return BitConverter.ToUInt32(_ram, (int)(p & (RamSize - 4)));
        if (p is >= 0x1F800000U and < 0x1F800400U) return BitConverter.ToUInt32(_scratch, (int)(p & (ScratchpadSize - 4)));
        return ReadIo32(p);
    }

    public void Write8(uint address, byte value)
    {
        uint p = address & 0x1FFFFFFFU;
        if (p < 0x00800000U) { _ram[p & (RamSize - 1)] = value; return; }
        if (p is >= 0x1F800000U and < 0x1F800400U) _scratch[p & (ScratchpadSize - 1)] = value;
    }

    public void Write16(uint address, ushort value)
    {
        uint p = address & 0x1FFFFFFFU;
        if (p < 0x00800000U) { BitConverter.TryWriteBytes(_ram.AsSpan((int)(p & (RamSize - 2))), value); return; }
        if (p is >= 0x1F800000U and < 0x1F800400U) { BitConverter.TryWriteBytes(_scratch.AsSpan((int)(p & (ScratchpadSize - 2))), value); return; }
        if (p is >= 0x1F801C00U and < 0x1F802000U) Spu.Write16(p, value);
    }

    public void Write32(uint address, uint value)
    {
        uint p = address & 0x1FFFFFFFU;
        if (p < 0x00800000U) { BitConverter.TryWriteBytes(_ram.AsSpan((int)(p & (RamSize - 4))), value); return; }
        if (p is >= 0x1F800000U and < 0x1F800400U) { BitConverter.TryWriteBytes(_scratch.AsSpan((int)(p & (ScratchpadSize - 4))), value); return; }
        switch (p)
        {
            case 0x1F801810U: Gpu.WriteGp0(value); break;
            case 0x1F801814U: Gpu.WriteGp1(value); break;
            case 0x1F801820U: Mdec.DmaIn(value); break;
            case 0x1F801824U: Mdec.WriteControl(value); break;
        }
    }

    private uint ReadIo32(uint p) => p switch
    {
        0x1F801810U => Gpu.ReadGpuRead(),
        0x1F801814U => Gpu.ReadGpuStat(),
        0x1F801820U => Mdec.DmaOut(),
        0x1F801824U => Mdec.ReadStatus(),
        _ => 0U
    };

    public uint GteReadData(int register) => _gteData[register & 31];
    public void GteWriteData(int register, uint value) => _gteData[register & 31] = value;
    public uint GteReadControl(int register) => _gteControl[register & 31];
    public void GteWriteControl(int register, uint value) => _gteControl[register & 31] = value;
    public void GteExecute(uint command) => _gteControl[31] = 0; // T2 devv-cpu-cd: route to PsxGte.ExecuteCommand

    public void Lwl(int rt, uint address)
    {
        if (rt == 0) return;
        uint aligned = Read32(address & ~3U);
        uint cur = R[rt];
        R[rt] = (address & 3U) switch { 0 => (cur & 0x00FFFFFFU) | (aligned << 24), 1 => (cur & 0x0000FFFFU) | (aligned << 16), 2 => (cur & 0x000000FFU) | (aligned << 8), _ => aligned };
    }

    public void Lwr(int rt, uint address)
    {
        if (rt == 0) return;
        uint aligned = Read32(address & ~3U);
        uint cur = R[rt];
        R[rt] = (address & 3U) switch { 0 => aligned, 1 => (cur & 0xFF000000U) | (aligned >> 8), 2 => (cur & 0xFFFF0000U) | (aligned >> 16), _ => (cur & 0xFFFFFF00U) | (aligned >> 24) };
    }

    public void Swl(uint address, uint value)
    {
        uint a = address & ~3U, cur = Read32(a);
        Write32(a, (address & 3U) switch { 0 => (cur & 0xFFFFFF00U) | (value >> 24), 1 => (cur & 0xFFFF0000U) | (value >> 16), 2 => (cur & 0xFF000000U) | (value >> 8), _ => value });
    }

    public void Swr(uint address, uint value)
    {
        uint a = address & ~3U, cur = Read32(a);
        Write32(a, (address & 3U) switch { 0 => value, 1 => (cur & 0x000000FFU) | (value << 8), 2 => (cur & 0x0000FFFFU) | (value << 16), _ => (cur & 0x00FFFFFFU) | (value << 24) });
    }

    public void Mult(uint a, uint b) { long p = (long)(int)a * (int)b; Lo = (uint)p; Hi = (uint)(p >> 32); }
    public void Multu(uint a, uint b) { ulong p = (ulong)a * b; Lo = (uint)p; Hi = (uint)(p >> 32); }

    public void Div(uint a, uint b)
    {
        int sa = (int)a, sb = (int)b;
        if (sb == 0) { Lo = sa >= 0 ? 0xFFFFFFFFU : 1U; Hi = (uint)sa; return; }
        if (sa == int.MinValue && sb == -1) { Lo = 0x80000000U; Hi = 0; return; }
        Lo = (uint)(sa / sb); Hi = (uint)(sa % sb);
    }

    public void Divu(uint a, uint b)
    {
        if (b == 0) { Lo = 0xFFFFFFFFU; Hi = a; return; }
        Lo = a / b; Hi = a % b;
    }

    private void WriteMemorySnapshot(BinaryWriter w)
    {
        w.Write(_ram); w.Write(_scratch);
        foreach (uint v in _gteData) w.Write(v);
        foreach (uint v in _gteControl) w.Write(v);
    }

    private void ReadMemorySnapshot(BinaryReader r)
    {
        r.BaseStream.ReadExactly(_ram);
        r.BaseStream.ReadExactly(_scratch);
        for (int i = 0; i < 32; i++) _gteData[i] = r.ReadUInt32();
        for (int i = 0; i < 32; i++) _gteControl[i] = r.ReadUInt32();
    }
}
