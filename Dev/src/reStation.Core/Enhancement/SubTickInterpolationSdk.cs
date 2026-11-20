// @module: reStation.Core.Enhancement.SubTickInterpolationSdk
// @purpose: Shared Enhancement SDK helpers driving ISubTickFrameHook >60 Hz / Max interpolation while preserving 60 Hz VBlank/SPU lock and cycle watchdogs.
// @depends-on: reStation.Contracts
// @exports: SubTickInterpolationSdk
// @owner: devv-sdk-qol (T5)

using reStation.Contracts;

namespace reStation.Core.Enhancement;

/// <summary>
/// Coordinates <see cref="ISubTickFrameHook"/> intermediate interpolated render passes between 30/60 Hz game ticks
/// while preserving <see cref="IReStationCpuEx.InSubTickRender"/>, <see cref="IReStationCpuEx.SubTickDeadlineCycle"/>
/// watchdogs, and the canonical 60 Hz SPU/VBlank cadence.
/// </summary>
public static class SubTickInterpolationSdk
{
    public const int BaseVBlankHz = 60;
    public const uint MaxSubFramesPerVBlank = 8U;

    /// <summary>Returns <see langword="true"/> when host cadence supports extra sub-tick interpolated frames beyond 60 Hz.</summary>
    public static bool ShouldRunSubTick(bool isEnhancedFrameRate, int targetFps, bool isFastForward)
        => isEnhancedFrameRate && !isFastForward && (targetFps <= 0 || targetFps > BaseVBlankHz);

    /// <summary>
    /// Resolves total sub-frames presented per 60 Hz VBlank interval (<c>1</c> when sub-tick interpolation is inactive,
    /// <c>2</c> for unlocked/120 Hz, up to <see cref="MaxSubFramesPerVBlank"/>).
    /// </summary>
    public static uint ResolveSubFrameCount(int targetFps, bool isEnhancedFrameRate, bool isFastForward)
    {
        if (!ShouldRunSubTick(isEnhancedFrameRate, targetFps, isFastForward))
        {
            return 1U;
        }
        return targetFps <= 0
            ? 2U
            : (uint)Math.Clamp((targetFps + (BaseVBlankHz - 1)) / BaseVBlankHz, 1, (int)MaxSubFramesPerVBlank);
    }

    /// <summary>
    /// Computes normalized sub-tick interpolation alpha in <c>[0.0, 1.0]</c> for <paramref name="subFrameIndex"/>
    /// within <paramref name="totalSubFrames"/>.
    /// </summary>
    public static double ComputeSubTickAlpha(uint subFrameIndex, uint totalSubFrames)
    {
        if (totalSubFrames <= 1U)
        {
            return 0.0;
        }
        return Math.Clamp((double)subFrameIndex / totalSubFrames, 0.0, 1.0);
    }

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
        if (hook is null || totalSubFrames == 0U || cpu.InSubTickRender || cpu.ExitRequested)
        {
            return false;
        }
        if (!hook.ShouldRunSubTick(subFrameIndex, totalSubFrames))
        {
            return false;
        }
        if (hook.SubTickEntryPc != 0U && !cpu.HasCompiledEntry(hook.SubTickEntryPc))
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
            if (hook.SubTickEntryPc != 0U)
            {
                cpu.RunSlice(hook.SubTickEntryPc, IReStationCpu.ReturnSentinelPc, inSubTickRender: true);
            }
            completedCleanly = cpu.SubTickDeadlineCycle == 0L || cpu.TotalCycles < cpu.SubTickDeadlineCycle;
            return completedCleanly;
        }
        finally
        {
            cpu.SubTickDeadlineCycle = prevDeadline;
            hook.OnSubTickEnd(cpu, subFrameIndex, completedCleanly);
        }
    }
}
