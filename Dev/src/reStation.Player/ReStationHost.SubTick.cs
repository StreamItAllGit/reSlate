// @module: reStation.Player.ReStationHost.SubTick
// @purpose: Host-thread (T:01) sub-tick marshaling, WaitHandle.WaitAny pump, and exception-safe wakeup guards for OpenGL/SDL3 thread affinity.
// @depends-on: System.Diagnostics, System.Runtime.ExceptionServices, reStation.Contracts
// @exports: ReStationHost (partial)

using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace reStation.Player;

internal sealed partial class ReStationHost
{
    private readonly AutoResetEvent _hostActionRequested = new(false);
    private readonly AutoResetEvent _hostActionDone = new(false);
    private readonly object _marshalLock = new();
    private int _hostThreadId;
    private Action? _pendingHostAction;
    private Exception? _pendingHostException;

    /// <summary>Number of sub-tick presents executed during the most recent <see cref="RunFrame"/> invocation.</summary>
    public int SubTickPresentsInStep { get; private set; }

    public (float MouseDx, float MouseDy, float StickX, float StickY) PollSubTickLook() =>
        MarshalToHostThread(() => _ctx.PollSubTickLook?.Invoke() ?? default);

    public void PresentSubTickFrame()
    {
        MarshalToHostThread(() =>
        {
            SubTickPresentsInStep++;
            _ctx.PresentSubTickFrame?.Invoke();
        });
    }

    private void BeginRunFrameHostPump()
    {
        if (_hostThreadId == 0) _hostThreadId = Environment.CurrentManagedThreadId;
        SubTickPresentsInStep = 0;
    }

    private bool WaitForFrameOrPumpHostActions()
    {
        WaitHandle[] waits = { _frameReached, _hostActionRequested };
        long deadlineTicks = Stopwatch.GetTimestamp() + (2L * Stopwatch.Frequency);
        while (!_disposing && !_emuFinished)
        {
            long remainingMs = Math.Max(0L, (deadlineTicks - Stopwatch.GetTimestamp()) * 1000L / Stopwatch.Frequency);
            int signaled = WaitHandle.WaitAny(waits, (int)Math.Clamp(remainingMs, 1L, int.MaxValue));
            if (signaled == 0) return true;
            if (signaled == WaitHandle.WaitTimeout) return false;
            ExecutePendingHostAction();
            deadlineTicks = Stopwatch.GetTimestamp() + (2L * Stopwatch.Frequency);
        }
        return true;
    }

    private void ExecutePendingHostAction()
    {
        Action? action = _pendingHostAction;
        _pendingHostAction = null;
        try
        {
            action?.Invoke();
        }
        catch (Exception ex)
        {
            _pendingHostException = ex;
        }
        finally
        {
            _hostActionDone.Set();
        }
    }

    private void MarshalToHostThread(Action action) =>
        MarshalToHostThread(() =>
        {
            action();
            return true;
        });

    private T MarshalToHostThread<T>(Func<T> func)
    {
        if (_disposing || _cpu.ExitRequested) return default!;
        int hostThreadId = Volatile.Read(ref _hostThreadId);
        if (hostThreadId == 0 || Environment.CurrentManagedThreadId == hostThreadId)
            return func();

        lock (_marshalLock)
        {
            if (_disposing || _emuFinished || _cpu.ExitRequested) return default!;
            _pendingHostException = null;
            T result = default!;
            _pendingHostAction = () => { result = func(); };
            _hostActionDone.Reset();
            _hostActionRequested.Set();

            if (!_hostActionDone.WaitOne(TimeSpan.FromSeconds(2)))
            {
                _pendingHostAction = null;
                _logger.Warn("HOST", "Timed out waiting 2s for T:01 sub-tick marshal.");
                return default!;
            }

            if (_pendingHostException is { } ex)
            {
                _pendingHostException = null;
                ExceptionDispatchInfo.Capture(ex).Throw();
            }
            return result;
        }
    }

    private void SignalHostWaitersOnShutdown()
    {
        _hostActionDone.Set();
        _resumeEmu.Set();
        _frameReached.Set();
    }

    private void DisposeSubTickHandles()
    {
        _hostActionRequested.Dispose();
        _hostActionDone.Dispose();
    }
}
