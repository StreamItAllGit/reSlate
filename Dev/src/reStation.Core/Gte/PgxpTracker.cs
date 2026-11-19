// @module: reStation.Core.Gte.PgxpTracker
// @purpose: Sub-pixel PGXP vertex cache mapping GTE-projected integer screen XY to precise float XY + depth for perspective-correct rendering.
// @depends-on: System
// @exports: PgxpVertex, PgxpTracker
// @owner: devv-cpu-cd (T2) — public surface T1-locked (consumed by devv-gpu-gl PsxGpu/backends).

namespace reStation.Core.Gte;

/// <summary>Precise projected vertex (screen-space X/Y plus view depth Z).</summary>
public readonly record struct PgxpVertex(float X, float Y, float DepthZ, bool Valid);

/// <summary>Per-frame bounded cache keyed by packed GPU vertex word (<c>(sy &lt;&lt; 16) | (ushort)sx</c>).</summary>
public sealed class PgxpTracker
{
    public const int MaxEntriesPerFrame = 8192;
    private readonly Dictionary<int, PgxpVertex> _entries = new(MaxEntriesPerFrame);

    public bool IsEnabled { get; set; } = true;
    public int Count => _entries.Count;

    /// <summary>Records a GTE-projected vertex (RTPS/RTPT SXY FIFO push).</summary>
    public void Record(short sx, short sy, float fx, float fy, float depthZ)
    {
        if (!IsEnabled || _entries.Count >= MaxEntriesPerFrame) return;
        _entries[Pack(sx, sy)] = new PgxpVertex(fx, fy, depthZ, true);
    }

    /// <summary>Resolves a GP0 vertex word to its precise vertex if one was recorded this frame.</summary>
    public bool TryResolve(int xyPacked, out PgxpVertex vertex)
    {
        if (IsEnabled && _entries.TryGetValue(xyPacked, out vertex)) return true;
        vertex = default;
        return false;
    }

    /// <summary>Clears all cached vertices (call once per rendered frame).</summary>
    public void ClearFrame() => _entries.Clear();

    /// <summary>Packs signed 16-bit screen coordinates exactly like a GP0 vertex word.</summary>
    public static int Pack(short sx, short sy) => (sy << 16) | (ushort)sx;
}
