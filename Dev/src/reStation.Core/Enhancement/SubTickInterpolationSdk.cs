// @module: reStation.Core.Enhancement.SubTickInterpolationSdk
// @purpose: Shared Enhancement SDK helpers driving ISubTickFrameHook >60 Hz / Max interpolation while preserving 60 Hz VBlank/SPU lock and cycle watchdogs.
// @depends-on: reStation.Contracts
// @exports: SubTickInterpolationSdk
// @owner: devv-sdk-qol (T5)

using reStation.Contracts;

namespace reStation.Core.Enhancement;

/// <summary>
/// Coordinates <see cref="ISubTickFrameHook"/> intermediate interpolated render passes between 30/60 Hz game ticks
/// while preserving <see cref="IReStationCpuEx.SubTickDeadlineCycle"/> watchdogs and 60 Hz SPU/VBlank cadence.
/// </summary>
public static class SubTickInterpolationSdk
{
    /// <summary>Returns <see langword="true"/> when host cadence supports extra sub-tick interpolated frames beyond 60 Hz.</summary>
    public static bool ShouldRunSubTick(bool isEnhancedFrameRate, int targetFps, bool isFastForward)
        => isEnhancedFrameRate && !isFastForward && (targetFps == 0 || targetFps > 60);

    /// <summary>
    /// Executes one cycle-budgeted sub-tick interpolation pass via <paramref name="hook"/>, restoring
    /// <see cref="IReStationCpuEx.SubTickDeadlineCycle"/> cleanly even when the slice unwinds.
    /// </summary>
    public static bool TryExecuteSubTickPass(
        ISubTickFrameHook? hook,
        IReStationCpuEx cpu,
        uint subFrameIndex,
        uint totalSubFrames)
    {
        ArgumentNullException.ThrowIfNull(cpu);
        if (hook is null || totalSubFrames == 0U || !hook.ShouldRunSubTick(subFrameIndex, totalSubFrames))
        {
            return false;
        }

        long prevDeadline = cpu.SubTickDeadlineCycle;
        bool completedCleanly = false;
        try
        {
            cpu.BeginSubTickFrame();
            cpu.SubTickDeadlineCycle = hook.MaxSubTickCycles > 0U
                ? cpu.TotalCycles + hook.MaxSubTickCycles
                : 0L;
            hook.OnSubTickBegin(cpu, subFrameIndex, totalSubFrames);
            cpu.RunSlice(hook.SubTickEntryPc, IReStationCpu.ReturnSentinelPc, inSubTickRender: true);
            completedCleanly = cpu.SubTickDeadlineCycle == 0L || cpu.TotalCycles <= cpu.SubTickDeadlineCycle;
            return completedCleanly;
        }
        finally
        {
            cpu.SubTickDeadlineCycle = prevDeadline;
            hook.OnSubTickEnd(cpu, subFrameIndex, completedCleanly);
        }
    }
}
