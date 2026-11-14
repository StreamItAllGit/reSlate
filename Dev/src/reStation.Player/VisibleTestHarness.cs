// @module: reStation.Player.VisibleTestHarness
// @purpose: IVisibleTestHarness implementation for --visible-test: real #0E0E10 1280x720 foreground window, 3s PS1 showcase, PNG capture, auto-exit 0.
// @depends-on: System.Diagnostics, Foundation.Core, Foundation.Retro, reStation.Contracts, reStation.Core
// @exports: VisibleTestHarness
// @owner: devv-player-ui (T6)

using System.Diagnostics;
using Foundation.Core;
using Foundation.Retro;
using reStation.Contracts;
using reStation.Core;

namespace reStation.Player;

/// <summary>Implements <see cref="IVisibleTestHarness"/> for <c>--visible-test</c>.</summary>
internal sealed unsafe class VisibleTestHarness : IVisibleTestHarness
{
    private readonly RecompSettings _settings;
    private readonly ReStationLogger _logger;

    public VisibleTestHarness(RecompSettings settings, ReStationLogger logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<bool> RunVisibleTestAsync(VisibleTestShowcaseOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        int durationMs = Math.Max(100, options.DurationMs);
        using HostVideoSurface video = HostVideoSurface.Create(
            $"reStation v{ReStationEngineInfo.Version} \u2014 Visible Test", _settings, headless: false, foreground: true, _logger, alwaysOnTop: true);
        using var pacer = new HighResFramePacer(60.0);
        var canvas = new PixelOverlayCanvas(UiOverlayPresenter.OverlayWidth, UiOverlayPresenter.OverlayHeight);
        var sw = Stopwatch.StartNew();
        long estimatedTotal = Math.Max(1L, (long)Math.Ceiling(durationMs * 60.0 / 1000.0));
        double ticksToMs = 1000.0 / Stopwatch.Frequency;
        byte* ev = stackalloc byte[128];
        long frame = 0;
        bool quit = false;

        while (!quit && !ct.IsCancellationRequested && sw.ElapsedMilliseconds < durationMs)
        {
            while (video.SdlAvailable && Sdl3Native.SDL_PollEvent(ev) != 0)
            {
                if (Sdl3Native.EventType(ev) == Sdl3Native.EventQuit) { quit = true; break; }
            }
            if (quit) break;

            frame++;
            long tStart = Stopwatch.GetTimestamp();
            UiOverlayPresenter.RenderShowcaseScene(canvas, frame, _settings, video.Backend.BackendId, pacer.CurrentFps, sw.ElapsedMilliseconds, durationMs);
            long tAfterCpu = Stopwatch.GetTimestamp();
            if (video.CanPresent) video.PresentRgba(canvas.Width, canvas.Height, canvas.Pixels);
            long tAfterGpu = Stopwatch.GetTimestamp();
            _ = pacer.WaitForNextFrame();
            long tAfterWait = Stopwatch.GetTimestamp();

            PsxTelemetrySnapshot snapshot = CreateTelemetrySnapshot(
                frame,
                (tAfterCpu - tStart) * ticksToMs,
                (tAfterGpu - tAfterCpu) * ticksToMs,
                (tAfterWait - tAfterGpu) * ticksToMs,
                pacer.CurrentFps,
                pacer.P99FrameMs,
                _settings.ResolutionScale,
                video.Backend.BackendId);
            _ = _logger.TryHeartbeat(frame, Math.Max(frame, estimatedTotal), snapshot.ToHeartbeatMetrics(), force: frame == 1);
            if (!options.AutoExit && sw.ElapsedMilliseconds >= durationMs) break;
        }

        if (!string.IsNullOrWhiteSpace(options.CaptureScreenshotPath))
            RgbaPngWriter.WritePng(options.CaptureScreenshotPath, canvas.Width, canvas.Height, canvas.Pixels);

        _logger.Info("VISIBLE_TEST", $"Completed frames={frame} elapsedMs={sw.ElapsedMilliseconds} backend={video.Backend.BackendId} presented={video.CanPresent} screenshot={Path.GetFileName(options.CaptureScreenshotPath ?? "none")}");
        return Task.FromResult(video.HasWindow && video.CanPresent);
    }

    internal static PsxTelemetrySnapshot CreateTelemetrySnapshot(
        long frame,
        double cpuMs,
        double gpuMs,
        double waitMs,
        double currentFps,
        double p99FrameMs,
        int resolutionScale,
        string backendId)
    {
        double safeCpu = Math.Max(0.0, cpuMs);
        double safeGpu = Math.Max(0.0, gpuMs);
        double safeWait = Math.Max(0.0, waitMs);
        double effectiveP99 = p99FrameMs > 0.0 ? p99FrameMs : safeCpu + safeGpu + safeWait;
        return PsxTelemetrySnapshot.Empty with
        {
            FrameNumber = frame,
            CpuMs = safeCpu,
            GpuMs = safeGpu,
            WaitMs = safeWait,
            CurrentFps = currentFps,
            P99FrameMs = effectiveP99,
            ResolutionScale = resolutionScale,
            BackendId = string.IsNullOrWhiteSpace(backendId) ? PsxRenderBackendIds.Software : backendId
        };
    }
}
