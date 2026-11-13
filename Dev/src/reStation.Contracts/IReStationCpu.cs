// @module: reStation.Contracts.IReStationCpu
// @purpose: Locked hardware-facing MIPS R3000A surface consumed by lifted .recomp code, IGameFramePatch hooks, and engine hosts.
// @depends-on: reStation.Contracts.IRecompModule
// @exports: IReStationCpu

namespace reStation.Contracts;

/// <summary>
/// CPU, bus, and COP2 (GTE) surface emitted against by <c>reStation.Compiler</c>. Lifted code calls these members
/// directly (bus access is flattened: <c>Read32</c> instead of <c>Bus.Read32</c>; GTE via <c>Gte*</c> methods).
/// </summary>
public interface IReStationCpu
{
    /// <summary>PC value used as the return sentinel for top-level dispatch (<c>$ra</c> at boot).</summary>
    const uint ReturnSentinelPc = 0xFFFFFFFCU;

    /// <summary>General purpose register file (32 entries; <c>R[0]</c> must be kept at zero by emitters).</summary>
    uint[] R { get; }
    /// <summary>Multiply/divide HI register.</summary>
    uint Hi { get; set; }
    /// <summary>Multiply/divide LO register.</summary>
    uint Lo { get; set; }
    /// <summary>Current program counter.</summary>
    uint Pc { get; set; }
    /// <summary>COP0 status register (SR).</summary>
    uint Cop0Sr { get; set; }
    /// <summary>Total emulated CPU cycles (33.8688 MHz clock).</summary>
    long TotalCycles { get; set; }
    /// <summary>Cycle at which lifted code must call <see cref="PollEvents"/>.</summary>
    long NextEventCycle { get; }
    /// <summary>Number of emulated VBlank frames elapsed.</summary>
    long FrameCount { get; }
    /// <summary>Active per-EXE module slot (see <see cref="IRecompModule.ExeId"/>).</summary>
    int ActiveExeId { get; set; }
    /// <summary>Set when lifted code must unwind to the current dispatch boundary.</summary>
    bool ShouldUnwind { get; set; }
    /// <summary>True while an <see cref="IGameFramePatch"/> renders an interpolated sub-tick frame.</summary>
    bool InSubTickRender { get; }
    /// <summary>True once the host requested shutdown; lifted code must unwind.</summary>
    bool ExitRequested { get; }

    /// <summary>Reads an unsigned byte from the PS1 address space.</summary>
    byte Read8(uint address);
    /// <summary>Reads an unsigned halfword from the PS1 address space.</summary>
    ushort Read16(uint address);
    /// <summary>Reads a word from the PS1 address space.</summary>
    uint Read32(uint address);
    /// <summary>Writes a byte to the PS1 address space.</summary>
    void Write8(uint address, byte value);
    /// <summary>Writes a halfword to the PS1 address space.</summary>
    void Write16(uint address, ushort value);
    /// <summary>Writes a word to the PS1 address space.</summary>
    void Write32(uint address, uint value);

    /// <summary>MFC2/LWC2 source: reads GTE data register 0-31.</summary>
    uint GteReadData(int register);
    /// <summary>MTC2/SWC2 target: writes GTE data register 0-31.</summary>
    void GteWriteData(int register, uint value);
    /// <summary>CFC2: reads GTE control register 0-31.</summary>
    uint GteReadControl(int register);
    /// <summary>CTC2: writes GTE control register 0-31.</summary>
    void GteWriteControl(int register, uint value);
    /// <summary>COP2 imm25: executes a GTE command (RTPS, NCLIP, MVMVA, ...).</summary>
    void GteExecute(uint command);

    /// <summary>Lifted <c>jal</c>/<c>jalr</c>: dispatches to a compiled function, BIOS vector, or interpreter fallback.</summary>
    void Call(uint targetPc);
    /// <summary>Lifted <c>jr</c> to a non-<c>$ra</c> register: sentinel-aware indirect jump.</summary>
    void JumpIndirect(uint targetPc);
    /// <summary>Top-level entry: runs <paramref name="pc"/> until it returns to <see cref="ReturnSentinelPc"/> or unwinds.</summary>
    void DispatchFunction(uint pc);
    /// <summary>Lifted <c>syscall</c>.</summary>
    void Syscall(uint code);
    /// <summary>Lifted <c>break</c>.</summary>
    void Break(uint code);
    /// <summary>Advances timers/CD-ROM/GPU/SPU to <see cref="TotalCycles"/>, raises IRQs, and fires VBlank callbacks.</summary>
    void PollEvents();
    /// <summary>Registers a per-EXE lifted module into its <see cref="IRecompModule.ExeId"/> slot.</summary>
    void RegisterModule(IRecompModule module);
    /// <summary>Requests cooperative shutdown (sets <see cref="ExitRequested"/>).</summary>
    void RequestExit();

    /// <summary>LWL merge into <c>R[rt]</c>.</summary>
    void Lwl(int rt, uint address);
    /// <summary>LWR merge into <c>R[rt]</c>.</summary>
    void Lwr(int rt, uint address);
    /// <summary>SWL partial store.</summary>
    void Swl(uint address, uint value);
    /// <summary>SWR partial store.</summary>
    void Swr(uint address, uint value);
    /// <summary>Signed multiply into HI/LO.</summary>
    void Mult(uint a, uint b);
    /// <summary>Unsigned multiply into HI/LO.</summary>
    void Multu(uint a, uint b);
    /// <summary>Signed divide into HI/LO with R3000A divide-by-zero semantics.</summary>
    void Div(uint a, uint b);
    /// <summary>Unsigned divide into HI/LO with R3000A divide-by-zero semantics.</summary>
    void Divu(uint a, uint b);
}
