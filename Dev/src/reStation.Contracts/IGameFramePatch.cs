// @module: reStation.Contracts.IGameFramePatch
// @purpose: Locked optional game-specific call-interception / sub-tick framerate interpolation hook.
// @depends-on: reStation.Contracts.IReStationCpu
// @exports: IGameFramePatch

namespace reStation.Contracts;

/// <summary>
/// Game-specific hook invoked by the CPU dispatcher before each compiled call (outside sub-tick renders), enabling
/// Enhanced-framerate interpolation, SDK busy-wait skipping, or title-specific fixes without patching lifted code.
/// </summary>
public interface IGameFramePatch
{
    /// <summary>
    /// Called with the canonical KSEG0 target PC before dispatch. Return true when the patch fully handled the call
    /// (the dispatcher then skips the compiled function); false to continue normal dispatch.
    /// </summary>
    bool TryInterceptCall(IReStationCpu cpu, uint canonPc);

    /// <summary>Clears all interpolation/world-snapshot state (called on boot, quick-load, and EXE switches).</summary>
    void Reset();
}
