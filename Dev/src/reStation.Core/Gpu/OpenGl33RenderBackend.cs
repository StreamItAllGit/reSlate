// @module: reStation.Core.Gpu.OpenGl33RenderBackend
// @purpose: Hardware OpenGL 3.3 Core IPsxRenderBackend ("opengl"): R16UI VRAM texture, scaled RGBA8 FBO, batched PGXP/CLUT/semi-transparent triangles, lazy glReadPixels.
// @depends-on: reStation.Contracts, reStation.Core.Gte, reStation.Core.Gpu.OpenGl33Bindings
// @exports: OpenGl33RenderBackend
// @owner: devv-gpu-gl (T4) — preserves all T1 locked members; pure net8.0, zero window-system dependencies.

using reStation.Contracts;
using reStation.Core.Gte;

namespace reStation.Core.Gpu;

public sealed unsafe partial class OpenGl33RenderBackend : IPsxRenderBackend, IPsxGpuBackendHooks
{
    private const int MaxBatchVertices = 12_288;

    private readonly ushort[] _vramMirror = new ushort[PsxGpu.VramWidth * PsxGpu.VramHeight];
    private readonly ushort[] _copyScratch = new ushort[PsxGpu.VramWidth * PsxGpu.VramHeight];
    private readonly GlVertex[] _batch = new GlVertex[MaxBatchVertices];
    private int _scale = 1, _outW = 320, _outH = 240, _vramFboScale, _dispFboW, _dispFboH, _scratchW, _scratchH;
    private int _batchCount, _clipL, _clipT, _clipR = PsxGpu.VramWidth - 1, _clipB = PsxGpu.VramHeight - 1;
    private int _presentW, _presentH, _overlayW, _overlayH, _wideW, _wideH, _wideFboScale;
    private long _primitivesSubmitted;
    private uint _batchBlendKey, _texWindowPacked;
    private uint _drawProg, _blitProg, _drawVao, _drawVbo, _blitVao, _blitVbo;
    private uint _vramTex, _vramFbo, _vramColorTex, _wideFbo, _wideColorTex, _displayFbo, _displayTex, _scratchFbo, _scratchTex, _presentTex, _overlayTex;
    private int _uDrawVramTex, _uDrawStpPass, _uDrawScale, _uBlitVramTex, _uBlitColorTex, _uBlitMode, _uBlitDstRect, _uBlitSrcRect, _uBlitDispInfo;
    private PgxpTracker? _pgxp;
    private bool _glReady, _hasPresented, _hasCachedWideFrame, _lastFullWindow, _lastWidescreen, _disposed;

    /// <summary>Requires bindings already initialized against a current GL 3.3 Core context.</summary>
    public OpenGl33RenderBackend(OpenGl33Bindings gl)
    {
        Gl = gl ?? throw new ArgumentNullException(nameof(gl));
        if (!gl.IsInitialized) throw new InvalidOperationException("OpenGl33Bindings must be initialized before creating the OpenGL backend.");
    }

    public OpenGl33Bindings Gl { get; }
    public string BackendId => PsxRenderBackendIds.OpenGl;
    public int ScaleFactor { get => _scale; set => _scale = value <= 1 ? 1 : value <= 2 ? 2 : value <= 4 ? 4 : 8; }
    public int SurfaceWidth { get; private set; } = 320;
    public int SurfaceHeight { get; private set; } = 240;
    public long PrimitivesSubmitted => Volatile.Read(ref _primitivesSubmitted);

    public void AttachPgxp(PgxpTracker? tracker) => _pgxp = tracker;

    public void SetTextureWindow(int maskX, int maskY, int offsetX, int offsetY)
    {
        if (IsOffOwnerThread) { EnqueueSetTextureWindow(maskX, maskY, offsetX, offsetY); return; }
        ApplyTextureWindow(maskX, maskY, offsetX, offsetY);
    }

    private void ApplyTextureWindow(int maskX, int maskY, int offsetX, int offsetY)
    {
        _texWindowPacked = (uint)((maskX & 31) | ((maskY & 31) << 5) | ((offsetX & 31) << 10) | ((offsetY & 31) << 15));
        _wideFrameOps.Add(new WideFrameOp { Kind = WideFrameOpKind.SetTextureWindow, RawU32 = _texWindowPacked });
    }

    private void InitGlIfNeeded()
    {
        if (_ownerThreadId == 0) _ownerThreadId = Environment.CurrentManagedThreadId;
        EnsureGlResources();
    }

    public void Initialize(int initialWidth, int initialHeight, int scaleFactor)
    {
        _ownerThreadId = Environment.CurrentManagedThreadId;
        Resize(initialWidth, initialHeight, scaleFactor);
        EnsureGlResources();
        FlushQueuedOpsOnOwnerThread();
    }

    public void Resize(int width, int height, int scaleFactor)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SurfaceWidth = Math.Max(1, width);
        SurfaceHeight = Math.Max(1, height);
        ScaleFactor = scaleFactor;
    }

    public void BeginFrame(int width, int height) => Resize(width, height, _scale);

    public void PresentRgba(int width, int height, ReadOnlySpan<byte> rgba)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (width <= 0 || height <= 0 || rgba.Length < width * height * 4) throw new ArgumentException("RGBA buffer too small.", nameof(rgba));
        InitGlIfNeeded();
        FlushQueuedOpsOnOwnerThread();
        FlushBatch();
        ResetWideFrameTracking();
        if (_presentTex == 0 || _presentW != width || _presentH != height)
        {
            if (_presentTex != 0) { uint t = _presentTex; Gl.DeleteTextures(1, &t); }
            _presentTex = CreateTexture2D(GlConst.Rgba8, width, height, GlConst.Rgba, GlConst.UnsignedByte, GlConst.Linear, GlConst.ClampToEdge);
            _presentW = width; _presentH = height;
        }
        Gl.BindTexture(GlConst.Texture2D, _presentTex);
        Gl.PixelStorei(GlConst.UnpackAlignment, 1);
        Gl.PixelStorei(GlConst.UnpackRowLength, 0);
        fixed (byte* p = rgba) Gl.TexSubImage2D(GlConst.Texture2D, 0, 0, 0, width, height, GlConst.Rgba, GlConst.UnsignedByte, p);
        EnsureDisplayFboSize(width, height);
        _outW = width; _outH = height;
        DrawBlitQuad(_displayFbo, width, height, 3, _presentTex, -1f, -1f, 2f, 2f, 0f, 0f, 1f, 1f, 0, 0, width, height);
        BlitDisplayToScreen(fullWindow: true, widescreen16By9: true);
        _hasPresented = true;
    }

    public void UploadVramRect(int x, int y, int w, int h, ReadOnlySpan<ushort> pixels15)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (w <= 0 || h <= 0) return;
        if (IsOffOwnerThread) { EnqueueUploadVramRect(x, y, w, h, pixels15); return; }
        ExecuteUploadVramRectCore(x, y, w, h, pixels15);
    }

    private void ExecuteUploadVramRectCore(int x, int y, int w, int h, ReadOnlySpan<ushort> pixels15)
    {
        InitGlIfNeeded();
        EnsureVramFboSize();
        FlushBatch();
        for (int row = 0, i = 0; row < h; row++)
        {
            int vy = (y + row) & 511;
            for (int col = 0; col < w && i < pixels15.Length; col++, i++)
                _vramMirror[(vy * PsxGpu.VramWidth) + ((x + col) & 1023)] = pixels15[i];
        }
        UploadWrappedVramSubRects(x, y, w, h);
    }

    public void CopyVramRect(int srcX, int srcY, int dstX, int dstY, int w, int h)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (w <= 0 || h <= 0) return;
        if (IsOffOwnerThread) { EnqueueCopyVramRect(srcX, srcY, dstX, dstY, w, h); return; }
        ExecuteCopyVramRectCore(srcX, srcY, dstX, dstY, w, h);
    }

    private void ExecuteCopyVramRectCore(int srcX, int srcY, int dstX, int dstY, int w, int h)
    {
        InitGlIfNeeded();
        EnsureVramFboSize();
        FlushBatch();
        for (int r = 0, i = 0; r < h; r++)
            for (int c = 0; c < w; c++, i++)
                _copyScratch[i] = _vramMirror[(((srcY + r) & 511) * PsxGpu.VramWidth) + ((srcX + c) & 1023)];
        for (int r = 0, i = 0; r < h; r++)
            for (int c = 0; c < w; c++, i++)
                _vramMirror[(((dstY + r) & 511) * PsxGpu.VramWidth) + ((dstX + c) & 1023)] = _copyScratch[i];

        Gl.BindTexture(GlConst.Texture2D, _vramTex);
        Gl.PixelStorei(GlConst.UnpackAlignment, 2);
        Gl.PixelStorei(GlConst.UnpackRowLength, PsxGpu.VramWidth);
        UploadSubRectsToVramTex(dstX & 1023, dstY & 511, w, h);
        Gl.PixelStorei(GlConst.UnpackRowLength, 0);

        int s = _vramFboScale;
        if ((srcX & 1023) + w <= PsxGpu.VramWidth && (srcY & 511) + h <= PsxGpu.VramHeight &&
            (dstX & 1023) + w <= PsxGpu.VramWidth && (dstY & 511) + h <= PsxGpu.VramHeight)
        {
            EnsureScratchFboSize(w * s, h * s);
            Gl.Disable(GlConst.ScissorTest);
            Gl.BindFramebuffer(GlConst.ReadFramebuffer, _vramFbo);
            Gl.BindFramebuffer(GlConst.DrawFramebuffer, _scratchFbo);
            Gl.BlitFramebuffer((srcX & 1023) * s, (srcY & 511) * s, ((srcX & 1023) + w) * s, ((srcY & 511) + h) * s,
                0, 0, w * s, h * s, GlConst.ColorBufferBit, (uint)GlConst.Nearest);
            Gl.BindFramebuffer(GlConst.ReadFramebuffer, _scratchFbo);
            Gl.BindFramebuffer(GlConst.DrawFramebuffer, _vramFbo);
            Gl.BlitFramebuffer(0, 0, w * s, h * s, (dstX & 1023) * s, (dstY & 511) * s,
                ((dstX & 1023) + w) * s, ((dstY & 511) + h) * s, GlConst.ColorBufferBit, (uint)GlConst.Nearest);
            Gl.BindFramebuffer(GlConst.Framebuffer, 0);
        }
        else
        {
            BlitVramRegionToFbo(dstX, dstY, w, h);
        }
        _wideFrameOps.Add(new WideFrameOp
        {
            Kind = WideFrameOpKind.CopyVram, A = srcX & 1023, B = srcY & 511, C = dstX & 1023, D = dstY & 511, E = w, F = h
        });
    }

    public void FillDisplayRect(int x, int y, int w, int h, uint bgr24)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (w <= 0 || h <= 0) return;
        if (IsOffOwnerThread) { EnqueueFillDisplayRect(x, y, w, h, bgr24); return; }
        ExecuteFillDisplayRectCore(x, y, w, h, bgr24);
    }

    private void ExecuteFillDisplayRectCore(int x, int y, int w, int h, uint bgr24)
    {
        InitGlIfNeeded();
        EnsureVramFboSize();
        FlushBatch();
        ushort c15 = (ushort)(((bgr24 >> 3) & 0x1F) | (((bgr24 >> 11) & 0x1F) << 5) | (((bgr24 >> 19) & 0x1F) << 10));
        for (int row = 0; row < h; row++)
        {
            int vy = ((y + row) & 511) * PsxGpu.VramWidth;
            for (int col = 0; col < w; col++) _vramMirror[vy + ((x + col) & 1023)] = c15;
        }
        Gl.BindTexture(GlConst.Texture2D, _vramTex);
        Gl.PixelStorei(GlConst.UnpackAlignment, 2);
        Gl.PixelStorei(GlConst.UnpackRowLength, PsxGpu.VramWidth);
        UploadSubRectsToVramTex(x & 1023, y & 511, w, h);
        Gl.PixelStorei(GlConst.UnpackRowLength, 0);

        int s = _vramFboScale;
        Gl.BindFramebuffer(GlConst.Framebuffer, _vramFbo);
        Gl.Enable(GlConst.ScissorTest);
        Gl.ClearColor((bgr24 & 0xFFU) / 255f, ((bgr24 >> 8) & 0xFFU) / 255f, ((bgr24 >> 16) & 0xFFU) / 255f, 1f);
        ForEachWrappedRect(x & 1023, y & 511, w, h, (rx, ry, rw, rh) =>
        {
            Gl.Scissor(rx * s, ry * s, rw * s, rh * s);
            Gl.Clear(GlConst.ColorBufferBit);
        });
        Gl.Disable(GlConst.ScissorTest);
        Gl.BindFramebuffer(GlConst.Framebuffer, 0);
        _wideFrameOps.Add(new WideFrameOp
        {
            Kind = WideFrameOpKind.FillRect, A = x & 1023, B = y & 511, C = w, D = h, RawU32 = bgr24
        });
    }

    public void SubmitPrimitive(uint gp0Opcode, ReadOnlySpan<uint> words, ushort texPage, int drawOffsetX, int drawOffsetY,
        int clipLeft, int clipTop, int clipRight, int clipBottom)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Interlocked.Increment(ref _primitivesSubmitted);
        if (IsOffOwnerThread)
        {
            EnqueueSubmitPrimitive(gp0Opcode, words, texPage, drawOffsetX, drawOffsetY, clipLeft, clipTop, clipRight, clipBottom);
            return;
        }
        ExecuteSubmitPrimitiveCore(gp0Opcode, words, texPage, drawOffsetX, drawOffsetY, clipLeft, clipTop, clipRight, clipBottom);
    }

    private void ExecuteSubmitPrimitiveCore(uint gp0Opcode, ReadOnlySpan<uint> words, ushort texPage, int drawOffsetX, int drawOffsetY,
        int clipLeft, int clipTop, int clipRight, int clipBottom)
    {
        InitGlIfNeeded();
        EnsureVramFboSize();
        uint op = gp0Opcode & 0xFFU;
        if (op is >= 0x20 and <= 0x3F)
        {
            Span<PsxVertex> v = stackalloc PsxVertex[4];
            int n = PsxPrimitiveDecoder.DecodePolygon(op, words, texPage, drawOffsetX, drawOffsetY, _pgxp, v, out PsxDrawState st);
            if (n < 3 || !PsxPrimitiveDecoder.IsDrawable(v[0], v[1], v[2])) return;
            EmitDecodedPolygon(st, v[0], v[1], v[2], v[3], n, clipLeft, clipTop, clipRight, clipBottom);
            _wideFrameOps.Add(new WideFrameOp
            {
                Kind = WideFrameOpKind.Polygon, DrawState = st, PolyCount = n,
                V0 = v[0], V1 = v[1], V2 = v[2], V3 = v[3],
                ClipL = clipLeft, ClipT = clipTop, ClipR = clipRight, ClipB = clipBottom
            });
        }
        else if (op is >= 0x40 and <= 0x5F && PsxPrimitiveDecoder.DecodeLine(op, words, texPage, drawOffsetX, drawOffsetY, out PsxVertex a, out PsxVertex b, out PsxDrawState ls))
        {
            EmitDecodedLine(ls, a, b, clipLeft, clipTop, clipRight, clipBottom);
            _wideFrameOps.Add(new WideFrameOp
            {
                Kind = WideFrameOpKind.Line, DrawState = ls, V0 = a, V1 = b,
                ClipL = clipLeft, ClipT = clipTop, ClipR = clipRight, ClipB = clipBottom
            });
        }
        else if (op is >= 0x60 and <= 0x7F && PsxPrimitiveDecoder.DecodeRect(op, words, texPage, drawOffsetX, drawOffsetY, out PsxRect rc, out PsxDrawState rs))
        {
            EmitDecodedRect(rs, rc, clipLeft, clipTop, clipRight, clipBottom);
            _wideFrameOps.Add(new WideFrameOp
            {
                Kind = WideFrameOpKind.Rect, DrawState = rs, Rect = rc,
                ClipL = clipLeft, ClipT = clipTop, ClipR = clipRight, ClipB = clipBottom
            });
        }
    }

    public void EndFrameAndPresent(int dispX, int dispY, int dispW, int dispH, bool is24Bit, bool widescreen16By9)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        InitGlIfNeeded();
        EnsureVramFboSize();
        FlushQueuedOpsOnOwnerThread();
        FlushBatch();

        int dw = Math.Clamp(dispW, 1, PsxGpu.VramWidth), dh = Math.Clamp(dispH, 1, PsxGpu.VramHeight);
        int ox = dispX & 1023, oy = dispY & 511;
        int s = is24Bit ? 1 : _vramFboScale;
        if (widescreen16By9 && !is24Bit)
        {
            PsxWidescreenGeometry.ComputeDimensions(dw, dh, out int wideW, out int leftPad);
            if (wideW > dw)
            {
                RenderWideFrameToFbo(ox, oy, dw, dh, wideW, leftPad, s);
                _outW = wideW * s;
                _outH = dh * s;
                EnsureDisplayFboSize(_outW, _outH);
                DrawBlitQuad(_displayFbo, _outW, _outH, 1, _wideColorTex, -1f, -1f, 2f, 2f, 0f, 0f, 1f, 1f, ox, oy, wideW, dh);
                _wideFrameOps.Clear();
                BlitDisplayToScreen(fullWindow: false, widescreen16By9: true);
                _hasPresented = true;
                return;
            }
        }
        _wideFrameOps.Clear();
        _hasCachedWideFrame = false;
        _outW = dw * s; _outH = dh * s;
        EnsureDisplayFboSize(_outW, _outH);
        int mode = is24Bit ? 2 : 1;
        DrawBlitQuad(_displayFbo, _outW, _outH, mode, _vramColorTex, -1f, -1f, 2f, 2f,
            ox / (float)PsxGpu.VramWidth, oy / (float)PsxGpu.VramHeight,
            dw / (float)PsxGpu.VramWidth, dh / (float)PsxGpu.VramHeight, ox, oy, dw, dh);
        BlitDisplayToScreen(fullWindow: false, widescreen16By9: widescreen16By9);
        _hasPresented = true;
    }

    public byte[] CaptureRgba(out int width, out int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        FlushQueuedOpsOnOwnerThread();
        width = _outW; height = _outH;
        byte[] rgba = new byte[width * height * 4];
        if (!_glReady || !_hasPresented || _displayFbo == 0) return rgba;
        FlushBatch();
        Gl.BindFramebuffer(GlConst.ReadFramebuffer, _displayFbo);
        Gl.PixelStorei(GlConst.PackAlignment, 1);
        fixed (byte* p = rgba) Gl.ReadPixels(0, 0, width, height, GlConst.Rgba, GlConst.UnsignedByte, p);
        Gl.BindFramebuffer(GlConst.ReadFramebuffer, 0);
        return rgba;
    }

    public void Dispose()
    {
        if (_disposed) return;
        FlushQueuedOpsOnOwnerThread();
        lock (_queueLock) ClearQueuedOpsAndReturnBuffersLocked();
        _disposed = true;
        if (!_glReady || !Gl.IsInitialized) return;
        DeleteProg(ref _drawProg); DeleteProg(ref _blitProg);
        DeleteVao(ref _drawVao); DeleteVao(ref _blitVao);
        DeleteBuf(ref _drawVbo); DeleteBuf(ref _blitVbo);
        DeleteTex(ref _vramTex); DeleteTex(ref _vramColorTex); DeleteTex(ref _wideColorTex); DeleteTex(ref _displayTex); DeleteTex(ref _scratchTex); DeleteTex(ref _presentTex); DeleteTex(ref _overlayTex);
        DeleteFbo(ref _vramFbo); DeleteFbo(ref _wideFbo); DeleteFbo(ref _displayFbo); DeleteFbo(ref _scratchFbo);
        _glReady = false;
    }
}
