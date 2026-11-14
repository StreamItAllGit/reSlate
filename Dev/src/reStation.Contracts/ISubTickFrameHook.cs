// @module: reStation.Contracts.ISubTickFrameHook
// @purpose: Additive frame-patch hook for high-framerate sub-tick interpolated rendering passes.
// @depends-on: reStation.Contracts.IGameFramePatch, reStation.Contracts.IReStationCpuEx
// @exports: ISubTickFrameHook

namespace reStation.Contracts;

/// <summary>Frame patch extension driving intermediate sub-tick render slices between 30 Hz game ticks.</summary>
public interface ISubTickFrameHook : IGameFramePatch
{
    /// <summary>Canonical PC entry point invoked for sub-tick render passes.</summary>
    uint SubTickEntryPc { get; }
    /// <summary>Maximum R3000A cycles budgeted per sub-tick render pass.</summary>
    uint MaxSubTickCycles { get; }
    /// <summary>Returns true when sub-tick pass <paramref name="subFrameIndex"/> of <paramref name="totalSubFrames"/> should execute.</summary>
    bool ShouldRunSubTick(uint subFrameIndex, uint totalSubFrames);
    /// <summary>Applies pre-slice interpolation state before executing the sub-tick render pass.</summary>
    void OnSubTickBegin(IReStationCpuEx cpu, uint subFrameIndex, uint totalSubFrames);
    /// <summary>Restores state after the sub-tick render pass finishes or unwinds.</summary>
    void OnSubTickEnd(IReStationCpuEx cpu, uint subFrameIndex, bool completedCleanly);
    /// <summary>Optional hook to intercept or skip a call during an active sub-tick render pass.</summary>
    bool TryInterceptSubTickCall(IReStationCpuEx cpu, uint canonPc) => false;
    /// <summary>Invoked after a non-intercepted main-tick call returns or unwinds (never during sub-tick passes).</summary>
    void OnAfterCall(IReStationCpuEx cpu, uint canonPc) { }
}
