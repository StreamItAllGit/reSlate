// @module: reStation.Core.Gpu.OpenGl33RenderBackend.Queue
// @purpose: Thread-safe FIFO command queue marshaling off-thread GPU calls (SetTextureWindow, VRAM ops, primitives) onto the GL owner thread.
// @depends-on: reStation.Core.Gpu.OpenGl33RenderBackend
// @exports: OpenGl33RenderBackend (partial)
// @owner: devv-core

using System.Buffers;

namespace reStation.Core.Gpu;

public sealed unsafe partial class OpenGl33RenderBackend
{
    private enum QueuedOpKind { SetTextureWindow, UploadVramRect, CopyVramRect, FillDisplayRect, SubmitPolygon, SubmitLine, SubmitRect }

    private readonly struct QueuedGpuOp
    {
        public QueuedOpKind Kind { get; init; }
        public int A { get; init; }
        public int B { get; init; }
        public int C { get; init; }
        public int D { get; init; }
        public int E { get; init; }
        public int F { get; init; }
        public uint RawU32 { get; init; }
        public ushort[]? Pixels { get; init; }
        public PsxDrawState DrawState { get; init; }
        public PsxVertex V0 { get; init; }
        public PsxVertex V1 { get; init; }
        public PsxVertex V2 { get; init; }
        public PsxVertex V3 { get; init; }
        public PsxRect Rect { get; init; }
    }

    private readonly object _queueLock = new();
    private readonly List<QueuedGpuOp> _queuedOps = new();
    private int _ownerThreadId;

    /// <summary>True when called after GL initialization from a thread other than the GL context owner thread.</summary>
    private bool IsOffOwnerThread => _glReady && _ownerThreadId != 0 && Environment.CurrentManagedThreadId != _ownerThreadId;

    /// <summary>Number of GPU operations currently queued awaiting GL owner-thread flush (diagnostic/test inspection).</summary>
    public int QueuedOpCount
    {
        get { lock (_queueLock) return _queuedOps.Count; }
    }

    private void EnqueueSetTextureWindow(int maskX, int maskY, int offsetX, int offsetY)
    {
        lock (_queueLock)
        {
            _queuedOps.Add(new QueuedGpuOp { Kind = QueuedOpKind.SetTextureWindow, A = maskX, B = maskY, C = offsetX, D = offsetY });
        }
    }

    private void EnqueueUploadVramRect(int x, int y, int w, int h, ReadOnlySpan<ushort> pixels15)
    {
        if (pixels15.IsEmpty) return;
        ushort[] rented = ArrayPool<ushort>.Shared.Rent(pixels15.Length);
        pixels15.CopyTo(rented);
        int nx = x & 1023, ny = y & 511;
        bool fullVram = nx == 0 && ny == 0 && w >= PsxGpu.VramWidth && h >= PsxGpu.VramHeight;
        lock (_queueLock)
        {
            for (int i = _queuedOps.Count - 1; i >= 0; i--)
            {
                QueuedGpuOp prev = _queuedOps[i];
                if (prev.Kind is not (QueuedOpKind.UploadVramRect or QueuedOpKind.SetTextureWindow)) break;
                if (prev.Kind != QueuedOpKind.UploadVramRect) continue;
                bool sameRect = (prev.A & 1023) == nx && (prev.B & 511) == ny && prev.C == w && prev.D == h && prev.E <= pixels15.Length;
                if (!fullVram && !sameRect) continue;
                if (prev.Pixels is not null) ArrayPool<ushort>.Shared.Return(prev.Pixels);
                _queuedOps.RemoveAt(i);
                if (!fullVram) break;
            }
            _queuedOps.Add(new QueuedGpuOp { Kind = QueuedOpKind.UploadVramRect, A = x, B = y, C = w, D = h, E = pixels15.Length, Pixels = rented });
        }
    }

    private void EnqueueCopyVramRect(int srcX, int srcY, int dstX, int dstY, int w, int h)
    {
        lock (_queueLock)
        {
            _queuedOps.Add(new QueuedGpuOp { Kind = QueuedOpKind.CopyVramRect, A = srcX, B = srcY, C = dstX, D = dstY, E = w, F = h });
        }
    }

    private void EnqueueFillDisplayRect(int x, int y, int w, int h, uint bgr24)
    {
        lock (_queueLock)
        {
            _queuedOps.Add(new QueuedGpuOp { Kind = QueuedOpKind.FillDisplayRect, A = x, B = y, C = w, D = h, RawU32 = bgr24 });
        }
    }

    private void EnqueueSubmitPrimitive(uint gp0Opcode, ReadOnlySpan<uint> words, ushort texPage, int drawOffsetX, int drawOffsetY,
        int clipLeft, int clipTop, int clipRight, int clipBottom)
    {
        uint op = gp0Opcode & 0xFFU;
        if (op is >= 0x20 and <= 0x3F)
        {
            Span<PsxVertex> v = stackalloc PsxVertex[4];
            int n = PsxPrimitiveDecoder.DecodePolygon(op, words, texPage, drawOffsetX, drawOffsetY, _pgxp, v, out PsxDrawState st);
            if (n < 3 || !PsxPrimitiveDecoder.IsDrawable(v[0], v[1], v[2])) return;
            lock (_queueLock)
            {
                _queuedOps.Add(new QueuedGpuOp
                {
                    Kind = QueuedOpKind.SubmitPolygon, DrawState = st, V0 = v[0], V1 = v[1], V2 = v[2], V3 = v[3],
                    A = n, B = clipLeft, C = clipTop, D = clipRight, E = clipBottom
                });
            }
        }
        else if (op is >= 0x40 and <= 0x5F && PsxPrimitiveDecoder.DecodeLine(op, words, texPage, drawOffsetX, drawOffsetY, out PsxVertex a, out PsxVertex b, out PsxDrawState ls))
        {
            lock (_queueLock)
            {
                _queuedOps.Add(new QueuedGpuOp
                {
                    Kind = QueuedOpKind.SubmitLine, DrawState = ls, V0 = a, V1 = b,
                    B = clipLeft, C = clipTop, D = clipRight, E = clipBottom
                });
            }
        }
        else if (op is >= 0x60 and <= 0x7F && PsxPrimitiveDecoder.DecodeRect(op, words, texPage, drawOffsetX, drawOffsetY, out PsxRect rc, out PsxDrawState rs))
        {
            lock (_queueLock)
            {
                _queuedOps.Add(new QueuedGpuOp
                {
                    Kind = QueuedOpKind.SubmitRect, DrawState = rs, Rect = rc,
                    B = clipLeft, C = clipTop, D = clipRight, E = clipBottom
                });
            }
        }
    }

    private void FlushQueuedOpsOnOwnerThread()
    {
        if (!_glReady || (_ownerThreadId != 0 && Environment.CurrentManagedThreadId != _ownerThreadId)) return;
        QueuedGpuOp[] batch;
        lock (_queueLock)
        {
            if (_queuedOps.Count == 0) return;
            batch = _queuedOps.ToArray();
            _queuedOps.Clear();
        }
        try
        {
            foreach (QueuedGpuOp op in batch)
            {
                switch (op.Kind)
                {
                    case QueuedOpKind.SetTextureWindow:
                        ApplyTextureWindow(op.A, op.B, op.C, op.D);
                        break;
                    case QueuedOpKind.UploadVramRect:
                        ExecuteUploadVramRectCore(op.A, op.B, op.C, op.D, op.Pixels is null ? ReadOnlySpan<ushort>.Empty : op.Pixels.AsSpan(0, op.E));
                        break;
                    case QueuedOpKind.CopyVramRect:
                        ExecuteCopyVramRectCore(op.A, op.B, op.C, op.D, op.E, op.F);
                        break;
                    case QueuedOpKind.FillDisplayRect:
                        ExecuteFillDisplayRectCore(op.A, op.B, op.C, op.D, op.RawU32);
                        break;
                    case QueuedOpKind.SubmitPolygon:
                        EnsureVramFboSize();
                        EmitDecodedPolygon(op.DrawState, op.V0, op.V1, op.V2, op.V3, op.A, op.B, op.C, op.D, op.E);
                        break;
                    case QueuedOpKind.SubmitLine:
                        EnsureVramFboSize();
                        EmitDecodedLine(op.DrawState, op.V0, op.V1, op.B, op.C, op.D, op.E);
                        break;
                    case QueuedOpKind.SubmitRect:
                        EnsureVramFboSize();
                        EmitDecodedRect(op.DrawState, op.Rect, op.B, op.C, op.D, op.E);
                        break;
                }
            }
        }
        finally
        {
            foreach (QueuedGpuOp op in batch)
                if (op.Pixels is not null)
                    ArrayPool<ushort>.Shared.Return(op.Pixels);
        }
    }

    private void ClearQueuedOpsAndReturnBuffersLocked()
    {
        foreach (QueuedGpuOp op in _queuedOps)
            if (op.Pixels is not null)
                ArrayPool<ushort>.Shared.Return(op.Pixels);
        _queuedOps.Clear();
    }
}
