// @module: reStation.Core.Cpu.PsxCpu.Bus
// @purpose: IReStationCpu flattened bus/GTE/unaligned-load-store/multiply-divide surface delegating to PsxMemoryBus and PsxGte.
// @depends-on: reStation.Core.Cpu.PsxMemoryBus, reStation.Core.Gte.PsxGte
// @exports: PsxCpu (partial)
// @owner: devv-cpu-cd (T2)

namespace reStation.Core.Cpu;

public sealed partial class PsxCpu
{
    /// <summary>Main RAM (2 MB) — exposed for BIOS HLE EXE loading, fixtures, and tests.</summary>
    public Span<byte> Ram => Bus.Ram;

    public byte Read8(uint address) => Bus.Read8(address);
    public ushort Read16(uint address) => Bus.Read16(address);
    public uint Read32(uint address) => Bus.Read32(address);
    public void Write8(uint address, byte value) => Bus.Write8(address, value);
    public void Write16(uint address, ushort value) => Bus.Write16(address, value);
    public void Write32(uint address, uint value) => Bus.Write32(address, value);

    public uint GteReadData(int register) => Gte.ReadData(register);
    public void GteWriteData(int register, uint value) => Gte.WriteData(register, value);
    public uint GteReadControl(int register) => Gte.ReadControl(register);
    public void GteWriteControl(int register, uint value) => Gte.WriteControl(register, value);
    public void GteExecute(uint command) => Gte.Execute(command);

    public void Lwl(int rt, uint address)
    {
        if (rt == 0) return;
        uint aligned = Read32(address & ~3U);
        uint cur = R[rt];
        R[rt] = (address & 3U) switch
        {
            0 => (cur & 0x00FFFFFFU) | (aligned << 24),
            1 => (cur & 0x0000FFFFU) | (aligned << 16),
            2 => (cur & 0x000000FFU) | (aligned << 8),
            _ => aligned
        };
    }

    public void Lwr(int rt, uint address)
    {
        if (rt == 0) return;
        uint aligned = Read32(address & ~3U);
        uint cur = R[rt];
        R[rt] = (address & 3U) switch
        {
            0 => aligned,
            1 => (cur & 0xFF000000U) | (aligned >> 8),
            2 => (cur & 0xFFFF0000U) | (aligned >> 16),
            _ => (cur & 0xFFFFFF00U) | (aligned >> 24)
        };
    }

    public void Swl(uint address, uint value)
    {
        uint a = address & ~3U, cur = Read32(a);
        Write32(a, (address & 3U) switch
        {
            0 => (cur & 0xFFFFFF00U) | (value >> 24),
            1 => (cur & 0xFFFF0000U) | (value >> 16),
            2 => (cur & 0xFF000000U) | (value >> 8),
            _ => value
        });
    }

    public void Swr(uint address, uint value)
    {
        uint a = address & ~3U, cur = Read32(a);
        Write32(a, (address & 3U) switch
        {
            0 => value,
            1 => (cur & 0x000000FFU) | (value << 8),
            2 => (cur & 0x0000FFFFU) | (value << 16),
            _ => (cur & 0x00FFFFFFU) | (value << 24)
        });
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
}
