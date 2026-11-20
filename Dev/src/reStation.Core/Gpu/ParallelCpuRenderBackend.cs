// @module: reStation.Core.Gpu.ParallelCpuRenderBackend
// @purpose: Deterministic 16px horizontal scanline band parallel CPU backend ("parallelCpu"), 100% SHA-256 bit-exact with SoftwareCpuRenderBackend at 1x.
// @depends-on: System, System.Collections.Generic, System.Runtime.InteropServices, reStation.Contracts, reStation.Core.Enhancement, reStation.Core.Gte
// @exports: ParallelCpuRenderBackend
// @owner: devv-gpu-backends (T4)

using reStation.Contracts;
using reStation.Core.Enhancement;
using reStation.Core.Gte;

namespace reStation.Core.Gpu;

/// <summary>Deterministic tile/band multi-core CPU PS1 GPU backend bit-exact with <see cref="SoftwareCpuRenderBackend"/> at 1x.</summary>
public sealed class ParallelCpuRenderBackend : IPsxRenderBackend, IPsxGpuBackendHooks
{
    private readonly ushort[] _vram = new ushort[PsxGpu.VramWidth * PsxGpu.VramHeight];
    private uint[] _hiVram = Array.Empty<uint>();
    private byte[] _hiMask = Array.Empty<byte>();
    private uint[]? _canonical1x;
    private byte[]? _canonicalMask1x;
    private byte[] _frame = new byte[320 * 240 * 4];
    private int _frameW = 320, _frameH = 240;
    private int _scale = 1, _hiScale;
    private PgxpTracker? _pgxp;
    private HdTexturePackStore? _hdPack;
    private readonly HdTextureLookupCache _hdLookup = new();
    private readonly List<QueuedBandDrawOp> _pendingDraws = new(256);
    private readonly List<WideBandOp> _wideOps = new(256);
    private readonly WideSurfaceRenderer _wideRenderer = new();
    private int _texWinMaskX, _texWinMaskY, _texWinOffX, _texWinOffY;
    private int _frameStartWinMaskX, _frameStartWinMaskY, _frameStartWinOffX, _frameStartWinOffY;
    private bool _maskSetBit, _maskCheckBit;

    /// <summary>Initializes the backend using logical processor concurrency.</summary>
    public ParallelCpuRenderBackend() : this(Environment.ProcessorCount) { }

    /// <summary>Initializes the backend with a deterministic worker thread count.</summary>
    public ParallelCpuRenderBackend(int workerThreads) => WorkerThreads = Math.Clamp(workerThreads, 1, 64);

    /// <summary>Configured deterministic band worker concurrency.</summary>
    public int WorkerThreads { get; }
    /// <inheritdoc/>
    public string BackendId => PsxRenderBackendIds.ParallelCpu;
    /// <inheritdoc/>
    public int ScaleFactor
    {
        get => _scale;
        set
        {
            int norm = PsxVramSurfaceOps.NormalizeScale(value);
            if (_scale != norm) { FlushPendingDraws(); _scale = norm; }
            EnsureHiVram();
        }
    }
    public int SurfaceWidth { get; private set; } = 320;
    public int SurfaceHeight { get; private set; } = 240;
    public long PrimitivesSubmitted { get; private set; }
    internal int PendingDrawCount => _pendingDraws.Count;

    public void AttachPgxp(PgxpTracker? tracker) => _pgxp = tracker;

    public void SetTextureWindow(int maskX, int maskY, int offsetX, int offsetY)
    {
        _texWinMaskX = maskX & 0x1F; _texWinMaskY = maskY & 0x1F;
        _texWinOffX = offsetX & 0x1F; _texWinOffY = offsetY & 0x1F;
        uint packed = (uint)(_texWinMaskX | (_texWinMaskY << 5) | (_texWinOffX << 10) | (_texWinOffY << 15));
        _wideOps.Add(new WideBandOp { FrameOp = new WideFrameOp { Kind = WideFrameOpKind.SetTextureWindow, RawU32 = packed } });
    }

    public void SetMaskBits(bool setMaskBit, bool checkMaskBit) { _maskSetBit = setMaskBit; _maskCheckBit = checkMaskBit; }

    public void AttachHdTexturePack(HdTexturePackStore? packStore)
    {
        FlushPendingDraws();
        _hdPack = packStore;
        _hdLookup.Invalidate();
        if (packStore is { IsEnabled: true })
            PsxVramSurfaceOps.EnsureCanonical1x(_vram, _hiVram, _hiMask, _hiScale, ref _canonical1x, ref _canonicalMask1x);
    }

    public void Initialize(int initialWidth, int initialHeight, int scaleFactor) => Resize(initialWidth, initialHeight, scaleFactor);

    public void Resize(int width, int height, int scaleFactor)
    {
        SurfaceWidth = Math.Max(1, width);
        SurfaceHeight = Math.Max(1, height);
        ScaleFactor = scaleFactor;
    }

    public void BeginFrame(int width, int height) => Resize(width, height, _scale);

    public void PresentRgba(int width, int height, ReadOnlySpan<byte> rgba)
    {
        if (width <= 0 || height <= 0 || rgba.Length < width * height * 4) throw new ArgumentException("RGBA buffer too small.", nameof(rgba));
        FlushPendingDraws();
        ResetWideOps();
        _wideRenderer.ResetCache();
        _frameW = width;
        _frameH = height;
        _frame = rgba[..(width * height * 4)].ToArray();
    }

    public void UploadVramRect(int x, int y, int w, int h, ReadOnlySpan<ushort> pixels15)
    {
        if (w <= 0 || h <= 0) return;
        FlushPendingDraws();
        EnsureHiVram();
        _hdLookup.RecordUpload(x, y, w, h);
        PsxVramSurfaceOps.UploadVramRect(_vram, _hiVram, _hiMask, _hiScale, _canonical1x, _canonicalMask1x, x, y, w, h, pixels15);
    }

    public void CopyVramRect(int srcX, int srcY, int dstX, int dstY, int w, int h)
    {
        if (w <= 0 || h <= 0) return;
        FlushPendingDraws();
        EnsureHiVram();
        _hdLookup.Invalidate();
        PsxVramSurfaceOps.CopyVramRect(_vram, _hiVram, _hiMask, _hiScale, _canonical1x, _canonicalMask1x, srcX, srcY, dstX, dstY, w, h);
        _wideOps.Add(new WideBandOp
        {
            FrameOp = new WideFrameOp { Kind = WideFrameOpKind.CopyVram, A = srcX & 1023, B = srcY & 511, C = dstX & 1023, D = dstY & 511, E = w, F = h }
        });
    }

    public void FillDisplayRect(int x, int y, int w, int h, uint bgr24)
    {
        if (w <= 0 || h <= 0) return;
        FlushPendingDraws();
        EnsureHiVram();
        _hdLookup.Invalidate();
        PsxVramSurfaceOps.FillDisplayRect(_vram, _hiVram, _hiMask, _hiScale, _canonical1x, _canonicalMask1x, x, y, w, h, bgr24);
        _wideOps.Add(new WideBandOp { FrameOp = new WideFrameOp { Kind = WideFrameOpKind.FillRect, A = x & 1023, B = y & 511, C = w, D = h, RawU32 = bgr24 } });
    }

    public void SubmitPrimitive(uint gp0Opcode, ReadOnlySpan<uint> words, ushort texPage, int drawOffsetX, int drawOffsetY,
        int clipLeft, int clipTop, int clipRight, int clipBottom)
    {
        PrimitivesSubmitted++;
        EnsureHiVram();
        int s = _hiScale, hiH = PsxGpu.VramHeight * s;
        uint op = gp0Opcode & 0xFFU;
        if (op is >= 0x20 and <= 0x3F)
        {
            Span<PsxVertex> verts = stackalloc PsxVertex[4];
            int count = PsxPrimitiveDecoder.DecodePolygon(op, words, texPage, drawOffsetX, drawOffsetY, _pgxp, verts, out PsxDrawState state);
            if (count < 3) return;
            HdTexOverride hd = _hdLookup.ResolveForPolygon(_vram, _hdPack, in state, in verts[0], in verts[1], in verts[2], in verts[3], count);
            EnqueueTriangle(in verts[0], in verts[1], in verts[2], in state, clipLeft, clipTop, clipRight, clipBottom, s, hiH, in hd);
            if (count == 4)
                EnqueueTriangle(in verts[1], in verts[2], in verts[3], in state, clipLeft, clipTop, clipRight, clipBottom, s, hiH, in hd);
            _wideOps.Add(new WideBandOp
            {
                FrameOp = new WideFrameOp
                {
                    Kind = WideFrameOpKind.Polygon, DrawState = state, PolyCount = count,
                    V0 = verts[0], V1 = verts[1], V2 = verts[2], V3 = verts[3],
                    ClipL = clipLeft, ClipT = clipTop, ClipR = clipRight, ClipB = clipBottom
                },
                MaskSetBit = _maskSetBit, MaskCheckBit = _maskCheckBit, HdTex = hd
            });
        }
        else if (op is >= 0x40 and <= 0x5F && PsxPrimitiveDecoder.DecodeLine(op, words, texPage, drawOffsetX, drawOffsetY, out PsxVertex a, out PsxVertex b, out PsxDrawState ls))
        {
            int half = s >> 1;
            int minY = Math.Max(Math.Max(0, clipTop * s), (int)MathF.Floor(MathF.Min(a.Y * s, b.Y * s)) - half - 1);
            int maxY = Math.Min(Math.Min(hiH - 1, ((clipBottom + 1) * s) - 1), (int)MathF.Ceiling(MathF.Max(a.Y * s, b.Y * s)) + (s - half) + 1);
            if (minY <= maxY)
            {
                _pendingDraws.Add(new QueuedBandDrawOp
                {
                    Kind = CpuDrawKind.Line, State = ls, V0 = a, V1 = b,
                    ClipL = clipLeft, ClipT = clipTop, ClipR = clipRight, ClipB = clipBottom,
                    MaskSetBit = _maskSetBit, MaskCheckBit = _maskCheckBit, MinY = minY, MaxY = maxY
                });
            }
            _wideOps.Add(new WideBandOp
            {
                FrameOp = new WideFrameOp { Kind = WideFrameOpKind.Line, DrawState = ls, V0 = a, V1 = b, ClipL = clipLeft, ClipT = clipTop, ClipR = clipRight, ClipB = clipBottom },
                MaskSetBit = _maskSetBit, MaskCheckBit = _maskCheckBit
            });
        }
        else if (op is >= 0x60 and <= 0x7F && PsxPrimitiveDecoder.DecodeRect(op, words, texPage, drawOffsetX, drawOffsetY, out PsxRect rect, out PsxDrawState rs))
        {
            HdTexOverride hd = _hdLookup.ResolveForRect(_vram, _hdPack, in rs, in rect);
            int minY = Math.Max(Math.Max(0, clipTop * s), rect.Y * s);
            int maxY = Math.Min(Math.Min(hiH - 1, ((clipBottom + 1) * s) - 1), ((rect.Y + rect.Height) * s) - 1);
            if (minY <= maxY)
            {
                _pendingDraws.Add(new QueuedBandDrawOp
                {
                    Kind = CpuDrawKind.Rect, State = rs, Rect = rect,
                    ClipL = clipLeft, ClipT = clipTop, ClipR = clipRight, ClipB = clipBottom,
                    TexWinMaskX = _texWinMaskX, TexWinMaskY = _texWinMaskY, TexWinOffX = _texWinOffX, TexWinOffY = _texWinOffY,
                    MaskSetBit = _maskSetBit, MaskCheckBit = _maskCheckBit, MinY = minY, MaxY = maxY, HdTex = hd
                });
            }
            _wideOps.Add(new WideBandOp
            {
                FrameOp = new WideFrameOp { Kind = WideFrameOpKind.Rect, DrawState = rs, Rect = rect, ClipL = clipLeft, ClipT = clipTop, ClipR = clipRight, ClipB = clipBottom },
                MaskSetBit = _maskSetBit, MaskCheckBit = _maskCheckBit, HdTex = hd
            });
        }
    }

    public void EndFrameAndPresent(int dispX, int dispY, int dispW, int dispH, bool is24Bit, bool widescreen16By9)
    {
        FlushPendingDraws();
        EnsureHiVram();
        int dw = Math.Clamp(dispW, 1, PsxGpu.VramWidth), dh = Math.Clamp(dispH, 1, PsxGpu.VramHeight);
        int ox = dispX & 1023, oy = dispY & 511;
        int s = is24Bit ? 1 : _hiScale;
        if (widescreen16By9 && !is24Bit)
        {
            PsxWidescreenGeometry.ComputeDimensions(dw, dh, out int wideW, out int leftPad);
            if (wideW > dw)
            {
                _wideRenderer.Render(_wideOps, _hiVram, _hiMask, _vram, s, ox, oy, dw, dh, wideW, leftPad,
                    WorkerThreads, _frameStartWinMaskX, _frameStartWinMaskY, _frameStartWinOffX, _frameStartWinOffY,
                    ref _frame, out _frameW, out _frameH);
                ResetWideOps();
                return;
            }
        }
        ResetWideOps();
        _wideRenderer.ResetCache();
        PsxVramSurfaceOps.PresentStandardFrame(_vram, _hiVram, _hiScale, dispX, dispY, dw, dh, is24Bit, ref _frame, out _frameW, out _frameH);
    }

    public byte[] CaptureRgba(out int width, out int height)
    {
        width = _frameW;
        height = _frameH;
        return (byte[])_frame.Clone();
    }

    public bool TryReadVramRect(int x, int y, int w, int h, Span<ushort> destination)
    {
        FlushPendingDraws();
        EnsureHiVram();
        return PsxVramSurfaceOps.TryReadVramRect(_vram, _hiVram, _hiMask, _hiScale, _canonical1x, _canonicalMask1x, x, y, w, h, destination);
    }

    public void RenderUiOverlay(ReadOnlySpan<byte> rgbaPixels, int width, int height, bool uploadPixels) { }
    public void RenderUiOverlay(ReadOnlySpan<byte> rgbaPixels, int width, int height, int dstX, int dstY, int canvasW, int canvasH, bool uploadPixels) { }

    public void Dispose()
    {
        _pendingDraws.Clear();
        _wideOps.Clear();
        PrimitivesSubmitted = 0;
    }

    private void FlushPendingDraws()
    {
        if (_pendingDraws.Count == 0) return;
        EnsureHiVram();
        ParallelBandRasterizer.ExecuteBands(_hiVram, _hiMask, _vram, _hiScale, PsxGpu.VramWidth, PsxGpu.VramHeight, _pendingDraws, WorkerThreads, _canonical1x, _canonicalMask1x);
        _pendingDraws.Clear();
    }

    private void EnsureHiVram() => PsxVramSurfaceOps.EnsureHiVram(_vram, ref _hiVram, ref _hiMask, ref _hiScale, _scale);

    private void ResetWideOps()
    {
        _wideOps.Clear();
        _frameStartWinMaskX = _texWinMaskX; _frameStartWinMaskY = _texWinMaskY;
        _frameStartWinOffX = _texWinOffX; _frameStartWinOffY = _texWinOffY;
    }

    private void EnqueueTriangle(in PsxVertex a, in PsxVertex b, in PsxVertex c, in PsxDrawState state,
        int clipL, int clipT, int clipR, int clipB, int s, int hiH, in HdTexOverride hd)
    {
        if (!PsxPrimitiveDecoder.IsDrawable(a, b, c)) return;
        int minY = Math.Max(Math.Max(0, clipT * s), (int)MathF.Floor(MathF.Min(a.Y * s, MathF.Min(b.Y * s, c.Y * s))));
        int maxY = Math.Min(Math.Min(hiH - 1, ((clipB + 1) * s) - 1), (int)MathF.Ceiling(MathF.Max(a.Y * s, MathF.Max(b.Y * s, c.Y * s))));
        if (minY > maxY) return;
        _pendingDraws.Add(new QueuedBandDrawOp
        {
            Kind = CpuDrawKind.Polygon, State = state, V0 = a, V1 = b, V2 = c,
            ClipL = clipL, ClipT = clipT, ClipR = clipR, ClipB = clipB,
            TexWinMaskX = _texWinMaskX, TexWinMaskY = _texWinMaskY, TexWinOffX = _texWinOffX, TexWinOffY = _texWinOffY,
            MaskSetBit = _maskSetBit, MaskCheckBit = _maskCheckBit, MinY = minY, MaxY = maxY, HdTex = hd
        });
    }
}
