// @module: reStation.Player.PresentationFpsTracker
// @purpose: Rolling-window display swap FPS tracker decoupling host presentation telemetry (30 Hz decimation and 60-240+/Max swaps) from the 60 Hz VBlank loop.
// @depends-on: System.Diagnostics
// @exports: PresentationFpsTracker

using System.Diagnostics;

namespace reStation.Player;

/// <summary>Tracks actual display swap timestamps over a short rolling window for truthful HUD and automation telemetry.</summary>
internal sealed class PresentationFpsTracker
{
    private const int Capacity = 128;
    private readonly long[] _timestamps = new long[Capacity];
    private readonly double[] _deltasMs = new double[Capacity];
    private readonly object _sync = new();
    private int _head;
    private int _count;

    /// <summary>Records one actually presented display swap at the current monotonic timestamp.</summary>
    public void RecordPresentedFrame()
    {
        long now = Stopwatch.GetTimestamp();
        lock (_sync)
        {
            if (_count == Capacity)
            {
                _timestamps[_head] = now;
                _head = (_head + 1) % Capacity;
            }
            else
            {
                _timestamps[(_head + _count) % Capacity] = now;
                _count++;
            }
        }
    }

    /// <summary>Clears recorded swap samples (e.g. after switching presentation cadence modes).</summary>
    public void Reset()
    {
        lock (_sync)
        {
            _head = 0;
            _count = 0;
        }
    }

    /// <summary>Computes presented swap FPS and P99 frame interval across the recent ~1s rolling window.</summary>
    public (double CurrentFps, double P99FrameMs) Snapshot()
    {
        lock (_sync)
        {
            if (_count < 2) return (0.0, 0.0);

            long newest = _timestamps[(_head + _count - 1) % Capacity];
            long cutoff = newest - Stopwatch.Frequency;
            int startOffset = _count - 1;
            while (startOffset > 0)
            {
                int prevIdx = (_head + startOffset - 1) % Capacity;
                if (_timestamps[prevIdx] < cutoff && (_count - startOffset) >= 6)
                    break;
                startOffset--;
            }

            int used = _count - startOffset;
            if (used < 2) return (0.0, 0.0);

            long oldest = _timestamps[(_head + startOffset) % Capacity];
            double totalSec = (double)(newest - oldest) / Stopwatch.Frequency;
            if (totalSec <= 0.0) return (0.0, 0.0);

            int dCount = used - 1;
            double tickToMs = 1000.0 / Stopwatch.Frequency;
            for (int i = 0; i < dCount; i++)
            {
                long a = _timestamps[(_head + startOffset + i) % Capacity];
                long b = _timestamps[(_head + startOffset + i + 1) % Capacity];
                _deltasMs[i] = Math.Max(0.0, (b - a) * tickToMs);
            }
            Array.Sort(_deltasMs, 0, dCount);
            int p99Idx = Math.Clamp((int)Math.Ceiling(dCount * 0.99) - 1, 0, dCount - 1);
            double fps = dCount / totalSec;
            return (fps, _deltasMs[p99Idx]);
        }
    }
}
