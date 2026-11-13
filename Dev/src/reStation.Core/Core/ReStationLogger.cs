// @module: reStation.Core.ReStationLogger
// @purpose: Thread-safe runtime.log sink (Foundation CappedSharedLogSink + HeartbeatLogger <= 15s) and recent-line ring for crash bundles.
// @depends-on: Foundation.Core, reStation.Contracts
// @exports: ReStationLogger
// @owner: devv-cpu-cd (T2)

using Foundation.Core;
using reStation.Contracts;

namespace reStation.Core;

/// <summary>Structured runtime logger backed by Foundation CappedSharedLogSink and HeartbeatLogger.</summary>
public sealed class ReStationLogger : IDisposable
{
    private readonly object _sync = new();
    private readonly Queue<string> _recent = new();
    private readonly int _recentCapacity;
    private readonly CappedSharedLogSink _sink;
    private readonly HeartbeatLogger _heartbeat;
    private bool _disposed;

    public ReStationLogger(
        string logDirectory,
        bool enabled = false,
        string fileName = "runtime.log",
        int recentCapacity = 200,
        TimeSpan? heartbeatInterval = null,
        long maxLogBytes = 4L * 1024 * 1024)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        LogDirectory = Path.GetFullPath(logDirectory);
        LogFilePath = Path.Combine(LogDirectory, fileName);
        _recentCapacity = Math.Max(16, recentCapacity);
        _sink = new CappedSharedLogSink(LogFilePath, maxLogBytes);
        _heartbeat = new HeartbeatLogger(
            ReStationEngineInfo.EngineId,
            logSink: line => Write("INFO", "HEARTBEAT", line),
            terminalWriter: null,
            enableInPlaceAnsi: false,
            heartbeatInterval: heartbeatInterval);
        SetEnabled(enabled);
    }

    public string LogDirectory { get; }
    public string LogFilePath { get; }
    public bool IsEnabled { get; private set; }
    public int HeartbeatCount => _heartbeat.HeartbeatEmitCount;

    public void SetEnabled(bool enabled)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            IsEnabled = enabled;
            if (enabled) Directory.CreateDirectory(LogDirectory);
        }
    }

    public void Info(string category, string message) => Write("INFO", category, message);
    public void Warn(string category, string message) => Write("WARN", category, message);
    public void Error(string category, string message) => Write("ERROR", category, message);

    /// <summary>Emits a structured heartbeat line when forced or when the <= 15s interval elapsed; returns true when emitted.</summary>
    public bool TryHeartbeat(PsxTelemetrySnapshot snapshot, bool force = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_sync)
        {
            if (_disposed) return false;
            int before = _heartbeat.HeartbeatEmitCount;
            _heartbeat.ReportProgress(snapshot.FrameNumber, 0, snapshot.ToHeartbeatLine(), forceEmit: force);
            return _heartbeat.HeartbeatEmitCount > before;
        }
    }

    /// <summary>Snapshot of the most recent log lines (for crash bundles and the HUD).</summary>
    public IReadOnlyList<string> GetRecentLines()
    {
        lock (_sync) return _recent.ToArray();
    }

    private void Write(string level, string category, string message)
    {
        string line = $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} [{level}] [{category}] {message}";
        bool writeFile;
        lock (_sync)
        {
            if (_disposed) return;
            _recent.Enqueue(line);
            while (_recent.Count > _recentCapacity) _recent.Dequeue();
            writeFile = IsEnabled;
        }
        if (writeFile) _sink.AppendLine(line);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
        }
    }
}
