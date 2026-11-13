// @module: reStation.Core.ReStationLogger
// @purpose: Thread-safe runtime.log writer with recent-line ring (CrashBundleService) and <= 15s PsxTelemetrySnapshot heartbeat gate.
// @depends-on: System.Diagnostics, System.Text, Foundation.Core, reStation.Contracts
// @exports: ReStationLogger
// @owner: devv-cpu-cd (T2) — public surface T1-locked; internals may adopt Foundation CappedSharedLogSink.

using System.Diagnostics;
using System.Text;
using Foundation.Core;
using reStation.Contracts;

namespace reStation.Core;

/// <summary>Structured runtime logger; file output only when enabled, recent ring always populated.</summary>
public sealed class ReStationLogger : IDisposable
{
    private readonly object _sync = new();
    private readonly Queue<string> _recent = new();
    private readonly int _recentCapacity;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly TimeSpan _heartbeatInterval;
    private StreamWriter? _writer;
    private long _lastHeartbeatMs = long.MinValue / 2;
    private bool _disposed;

    public ReStationLogger(string logDirectory, bool enabled = false, string fileName = "runtime.log", int recentCapacity = 200, TimeSpan? heartbeatInterval = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        LogDirectory = Path.GetFullPath(logDirectory);
        LogFilePath = Path.Combine(LogDirectory, fileName);
        _recentCapacity = Math.Max(16, recentCapacity);
        TimeSpan requested = heartbeatInterval ?? TimeSpan.FromSeconds(HeartbeatLogger.MaxHeartbeatIntervalSeconds);
        _heartbeatInterval = requested > TimeSpan.FromSeconds(HeartbeatLogger.MaxHeartbeatIntervalSeconds) || requested <= TimeSpan.Zero
            ? TimeSpan.FromSeconds(HeartbeatLogger.MaxHeartbeatIntervalSeconds)
            : requested;
        SetEnabled(enabled);
    }

    public string LogDirectory { get; }
    public string LogFilePath { get; }
    public bool IsEnabled { get; private set; }
    public int HeartbeatCount { get; private set; }

    public void SetEnabled(bool enabled)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            IsEnabled = enabled;
            if (enabled && _writer is null)
            {
                Directory.CreateDirectory(LogDirectory);
                _writer = new StreamWriter(new FileStream(LogFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
            }
        }
    }

    public void Info(string category, string message) => Write("INFO", category, message);
    public void Warn(string category, string message) => Write("WARN", category, message);
    public void Error(string category, string message) => Write("ERROR", category, message);

    /// <summary>Emits a heartbeat line when forced or when the <= 15s interval elapsed; returns true when emitted.</summary>
    public bool TryHeartbeat(PsxTelemetrySnapshot snapshot, bool force = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        long nowMs = _clock.ElapsedMilliseconds;
        lock (_sync)
        {
            if (!force && nowMs - _lastHeartbeatMs < _heartbeatInterval.TotalMilliseconds) return false;
            _lastHeartbeatMs = nowMs;
            HeartbeatCount++;
        }
        Write("INFO", "HEARTBEAT", snapshot.ToHeartbeatLine());
        return true;
    }

    /// <summary>Snapshot of the most recent log lines (for crash bundles and the HUD).</summary>
    public IReadOnlyList<string> GetRecentLines()
    {
        lock (_sync) return _recent.ToArray();
    }

    private void Write(string level, string category, string message)
    {
        string line = $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} [{level}] [{category}] {message}";
        lock (_sync)
        {
            if (_disposed) return;
            _recent.Enqueue(line);
            while (_recent.Count > _recentCapacity) _recent.Dequeue();
            if (IsEnabled) _writer?.WriteLine(line);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _writer?.Dispose();
            _writer = null;
        }
    }
}
