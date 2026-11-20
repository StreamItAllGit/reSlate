// @module: reStation.Contracts.IReStationCpuEx
// @purpose: Additive v0.1.1 CPU contract exposing direct RAM access, sub-tick unwind/watchdog, and GPU polygon-skip flags.
// @depends-on: reStation.Contracts.IReStationCpu
// @exports: IReStationCpuEx

namespace reStation.Contracts;

/// <summary>Additive CPU surface for sub-tick interpolated rendering, direct RAM access, and cycle-bounded slice execution.</summary>
public interface IReStationCpuEx : IReStationCpu
{
    /// <summary>Direct 2 MB PS1 main RAM span.</summary>
    Span<byte> Ram { get; }
    /// <summary>Writable cycle deadline at which lifted code polls events or sub-tick watchdogs.</summary>
    new long NextEventCycle { get; set; }
    /// <summary>PC sentinel that causes <see cref="IReStationCpu.Call"/> / <see cref="IReStationCpu.JumpIndirect"/> to unwind.</summary>
    uint UnwindTargetPc { get; set; }
    /// <summary>When true, GPU skips polygon rasterization while preserving GP0 state and VRAM transfers.</summary>
    bool SkipPolygonRasterization { get; set; }
    /// <summary>Cycle budget deadline for sub-tick slices (0 = disabled).</summary>
    long SubTickDeadlineCycle { get; set; }
    /// <summary>Prepares transient GPU/CPU state before executing a sub-tick render slice.</summary>
    void BeginSubTickFrame();
    /// <summary>Returns true when a registered module contains a compiled entry for <paramref name="canonPc"/>.</summary>
    bool HasCompiledEntry(uint canonPc);
    /// <summary>Executes a compiled slice at <paramref name="entryPc"/> until <paramref name="returnSentinelPc"/> or unwind.</summary>
    void RunSlice(uint entryPc, uint returnSentinelPc = IReStationCpu.ReturnSentinelPc, bool inSubTickRender = false);
    /// <summary>Advances cycles to the next VBlank when unmasked and outside sub-tick/unwind states.</summary>
    bool TryAdvanceToNextVBlank();
    /// <summary>
    /// Bounded guest VBlank-counter wait helper preventing tight-loop PsyQ <c>VSync(n)</c> spin timeouts.
    /// Returns <see langword="true"/> once <paramref name="counterAddr"/> reaches <paramref name="targetValue"/> within <paramref name="maxVBlanks"/> VBlanks.
    /// </summary>
    bool TryWaitForVBlankCounter(uint counterAddr, int targetValue, uint waitEntryPc = 0U, int maxVBlanks = 16) => false;
}
