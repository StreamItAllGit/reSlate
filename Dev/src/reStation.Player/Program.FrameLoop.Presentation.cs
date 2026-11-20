// @module: reStation.Player.Program.FrameLoop.Presentation
// @purpose: Extracted presentation-rate helpers (Original 30/60 FPS resolution, presented swap tracking, and inter-VBlank >60/Max host swap pacing).
// @depends-on: System.Diagnostics, Foundation.Core, reStation.Contracts, reStation.Core, reStation.Core.Cpu
// @exports: Program (FrameLoop.Presentation partial)

using System.Diagnostics;
using Foundation.Core;
using reStation.Contracts;
using reStation.Core;
using reStation.Core.Cpu;

namespace reStation.Player;

internal static unsafe partial class Program
{
    /// <summary>Resolves host presentation target FPS (honoring Original + 30 FPS decimation while keeping 60 Hz PS1 VBlank lock).</summary>
    internal static int ResolveHostTargetFps(RecompSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.FrameRateMode == FrameRateMode.Original
            ? (settings.TargetFps == 30 ? 30 : 60)
            : settings.TargetFps;
    }

    private static void PresentAndRecordFrame(
        HostVideoSurface video,
        ReStationHost host,
        SlideOverSettingsDrawer drawer,
        UiOverlayPresenter overlay,
        IReStationModuleManifest? manifest,
        RecompSettings settings,
        bool effectiveWidescreen,
        string discInfo,
        bool loggingActive,
        bool isOffscreen,
        string? shotPath,
        HostContractBridge? bridge = null,
        bool captureCompositeForBridge = false,
        bool reblitGlDisplay = false)
    {
        PresentLoopFrame(video, host, drawer, overlay, manifest, settings, effectiveWidescreen, discInfo, loggingActive, isOffscreen, shotPath, bridge, captureCompositeForBridge, reblitGlDisplay);
        if (!isOffscreen)
            host.PresentationTracker?.RecordPresentedFrame();
    }

    private static void RunInterVBlankPresentationPacing(
        long loopStartTicks,
        HostVideoSurface video,
        ReStationHost host,
        PsxCpu cpu,
        SlideOverSettingsDrawer drawer,
        UiOverlayPresenter overlay,
        IReStationModuleManifest? manifest,
        RecompSettings settings,
        AppEnvironment env,
        HighResFramePacer pacer,
        ReStationLogger logger,
        LoopState st,
        HostContractBridge bridge)
    {
        bool wantOver60 = !st.IsOffscreen
            && !st.ActiveFastForwardThisFrame
            && settings.FrameRateMode == FrameRateMode.Enhanced
            && (settings.TargetFps > 60 || settings.TargetFps <= 0);

        if (!wantOver60 || !video.CanPresent)
        {
            st.InterVBlankSwapBudget = 0.0;
            pacer.TargetFps = ComputeTargetFps(settings, st.IsOffscreen);
            _ = pacer.WaitForNextFrame();
            return;
        }

        long vblankPeriodTicks = Math.Max(1L, Stopwatch.Frequency / 60L);
        long vblankGuardTicks = Math.Max(1L, Stopwatch.Frequency / 2000L); // reserve ~0.5 ms so VBlank wait never drifts
        long deadlineTicks = loopStartTicks + vblankPeriodTicks - vblankGuardTicks;

        bool uncapped = settings.TargetFps <= 0;
        int cappedTarget = Math.Clamp(settings.TargetFps, 61, 500);
        int plannedExtraSwaps = ComputePlannedExtraSwaps(uncapped, cappedTarget, ref st.InterVBlankSwapBudget);
        double intervalTicks = (double)vblankPeriodTicks / Math.Max(1, plannedExtraSwaps + 1);

        for (int s = 1; s <= plannedExtraSwaps && !st.QuitRequested && !host.HasFinished; s++)
        {
            if (Stopwatch.GetTimestamp() >= deadlineTicks) break;
            if (!uncapped)
            {
                long nextSwapTick = loopStartTicks + (long)(s * intervalTicks);
                if (nextSwapTick >= deadlineTicks) break;
                while (!st.QuitRequested && !host.HasFinished && Stopwatch.GetTimestamp() < nextSwapTick)
                {
                    double remMs = (nextSwapTick - Stopwatch.GetTimestamp()) * 1000.0 / Stopwatch.Frequency;
                    if (remMs > 1.8) Thread.Sleep(1);
                    else Thread.SpinWait(24);
                }
            }
            if (st.QuitRequested || host.HasFinished || Stopwatch.GetTimestamp() >= deadlineTicks) break;

            PumpEvents(video, host, cpu, drawer, overlay, settings, env, pacer, logger, st);
            bridge.DrainCommands(
                onPointerClick: (ox, oy, btn) => HandleOverlayPointerDown(ox, oy, btn, video, host, cpu, drawer, overlay, settings, env, pacer, logger, st),
                onSyntheticScancode: sc => HandleKeyDownScancode(sc, video, host, drawer, overlay, settings, env, st));
            if (st.QuitRequested) break;

            PresentAndRecordFrame(video, host, drawer, overlay, manifest, settings, cpu.Gpu.EffectiveWidescreen, st.DiscInfo, logger.IsEnabled, st.IsOffscreen, null, null, captureCompositeForBridge: false, reblitGlDisplay: true);
        }

        pacer.TargetFps = ComputeTargetFps(settings, st.IsOffscreen);
        _ = pacer.WaitForNextFrame();
    }

    internal static int ComputePlannedExtraSwaps(bool uncapped, int cappedTarget, ref double swapBudget)
    {
        if (uncapped)
        {
            swapBudget = 0.0;
            return 8;
        }
        swapBudget += (cappedTarget / 60.0) - 1.0;
        int extra = Math.Clamp((int)Math.Floor(swapBudget + 1e-6), 0, 8);
        swapBudget = Math.Max(0.0, swapBudget - extra);
        return extra;
    }
}
