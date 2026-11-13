// @module: reStation.Contracts.PsxTelemetrySnapshot
// @purpose: Locked immutable engine telemetry record with Standard 18 heartbeat (<= 15s) and F3 HUD formatting helpers.
// @depends-on: System.Globalization, Foundation.Retro
// @exports: PsxTelemetrySnapshot

using System.Globalization;
using Foundation.Retro;

namespace reStation.Contracts;

/// <summary>Per-frame engine telemetry captured by the host and emitted through heartbeats, HUD, and crash bundles.</summary>
/// <param name="FrameNumber">Emulated VBlank frame counter.</param>
/// <param name="CpuMs">Host milliseconds spent executing lifted/interpreted CPU work this frame.</param>
/// <param name="GpuMs">Host milliseconds spent in the render backend this frame.</param>
/// <param name="WaitMs">Host milliseconds spent pacing/waiting this frame.</param>
/// <param name="CurrentFps">Rolling presented frames per second.</param>
/// <param name="P99FrameMs">Rolling 99th percentile frame time.</param>
/// <param name="ResolutionScale">Internal resolution multiplier (1, 2, 4, 8).</param>
/// <param name="BackendId">Active render backend id.</param>
/// <param name="CdSectorsPerSec">Rolling CD-ROM sectors delivered per emulated second.</param>
/// <param name="MdecMacroblocksPerSec">Rolling MDEC macroblocks decoded per second.</param>
/// <param name="ActiveSpuVoices">Number of SPU voices with non-zero envelope.</param>
/// <param name="XaDrops">XA samples dropped due to a full audio ring (0 in healthy playback).</param>
/// <param name="AudioFillRatio">Audio ring fill ratio 0..1.</param>
/// <param name="AudioUnderruns">Audio ring underrun count.</param>
public sealed record PsxTelemetrySnapshot(
    long FrameNumber,
    double CpuMs,
    double GpuMs,
    double WaitMs,
    double CurrentFps,
    double P99FrameMs,
    int ResolutionScale,
    string BackendId,
    double CdSectorsPerSec,
    double MdecMacroblocksPerSec,
    int ActiveSpuVoices,
    long XaDrops,
    double AudioFillRatio,
    long AudioUnderruns)
{
    /// <summary>Zeroed snapshot used before the first frame.</summary>
    public static PsxTelemetrySnapshot Empty { get; } = new(0, 0, 0, 0, 0, 0, 1, PsxRenderBackendIds.Software, 0, 0, 0, 0, 0, 0);

    /// <summary>Structured key=value heartbeat line (invariant culture) for runtime.log.</summary>
    public string ToHeartbeatLine() => string.Create(CultureInfo.InvariantCulture,
        $"[HEARTBEAT] frame={FrameNumber} fps={CurrentFps:F1} p99Ms={P99FrameMs:F2} cpuMs={CpuMs:F2} gpuMs={GpuMs:F2} waitMs={WaitMs:F2} " +
        $"resolutionScale={ResolutionScale} backend={BackendId} sectorsPerSec={CdSectorsPerSec:F1} mdecMbPerSec={MdecMacroblocksPerSec:F0} " +
        $"activeSpuVoices={ActiveSpuVoices} xaDrops={XaDrops} audioFillPct={AudioFillRatio * 100.0:F1} audioUnderruns={AudioUnderruns}");

    /// <summary>Compact single-line F3 HUD text.</summary>
    public string ToHudLine() => string.Create(CultureInfo.InvariantCulture,
        $"FPS {CurrentFps:F0} | {BackendId} {ResolutionScale}x | CPU {CpuMs:F1}ms GPU {GpuMs:F1}ms | CD {CdSectorsPerSec:F0}/s | XA drops {XaDrops} | Audio {AudioFillRatio * 100.0:F0}%");

    /// <summary>Projects onto the Foundation.Retro host frame statistics record.</summary>
    public RecompFrameStats ToFrameStats() =>
        new(FrameNumber, CpuMs + GpuMs + WaitMs, CurrentFps, P99FrameMs, AudioFillRatio, AudioUnderruns);
}
