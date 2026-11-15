// @module: reStation.Core.Gpu.OpenGl33RenderBackend.Pipeline
// @purpose: VBO batch flush, PS1 semi-transparency state machine, FBO blits, wrapped VRAM uploads, and geometry builders for OpenGl33RenderBackend.
// @depends-on: reStation.Core.Gpu.OpenGl33Bindings
// @exports: OpenGl33RenderBackend (partial)
// @owner: devv-gpu-gl (T4)

namespace reStation.Core.Gpu;

public sealed unsafe partial class OpenGl33RenderBackend
{
    private void PrepareBatchState(in PsxDrawState st, int cl, int ct, int cr, int cb, int neededVerts)
    {
        uint key = (st.SemiTransparent ? 1U : 0U) | ((uint)(st.SemiMode & 3) << 1) | (st.Textured ? 8U : 0U);
        if (_batchCount > 0 && (_batchCount + neededVerts > MaxBatchVertices || _batchBlendKey != key ||
            _clipL != cl || _clipT != ct || _clipR != cr || _clipB != cb)) FlushBatch();
        _batchBlendKey = key; _clipL = cl; _clipT = ct; _clipR = cr; _clipB = cb;
    }

    private void FlushIfTwoPass(in PsxDrawState st)
    {
        if (st.Textured && st.SemiTransparent && st.SemiMode != 0) FlushBatch();
    }

    private void FlushBatch()
    {
        if (_batchCount == 0) return;
        int count = _batchCount;
        _batchCount = 0;
        int cl = Math.Max(0, _clipL), ct = Math.Max(0, _clipT), cr = Math.Min(PsxGpu.VramWidth - 1, _clipR), cb = Math.Min(PsxGpu.VramHeight - 1, _clipB);
        if (cl > cr || ct > cb) return;

        int s = _vramFboScale;
        Gl.BindFramebuffer(GlConst.Framebuffer, _vramFbo);
        Gl.Viewport(0, 0, PsxGpu.VramWidth * s, PsxGpu.VramHeight * s);
        Gl.Enable(GlConst.ScissorTest);
        Gl.Scissor(cl * s, ct * s, (cr - cl + 1) * s, (cb - ct + 1) * s);
        Gl.BindVertexArray(_drawVao);
        Gl.BindBuffer(GlConst.ArrayBuffer, _drawVbo);
        fixed (GlVertex* p = _batch) Gl.BufferSubData(GlConst.ArrayBuffer, 0, count * sizeof(GlVertex), p);
        Gl.UseProgram(_drawProg);
        Gl.ActiveTexture(GlConst.Texture0);
        Gl.BindTexture(GlConst.Texture2D, _vramTex);
        Gl.Uniform1i(_uDrawVramTex, 0);

        bool semi = (_batchBlendKey & 1U) != 0, textured = (_batchBlendKey & 8U) != 0;
        int mode = (int)((_batchBlendKey >> 1) & 3U);
        if (semi && textured && mode != 0)
        {
            Gl.Disable(GlConst.Blend);
            Gl.Uniform1i(_uDrawStpPass, 1);
            Gl.DrawArrays(GlConst.Triangles, 0, count);
            ApplyPs1Blend(mode);
            Gl.Uniform1i(_uDrawStpPass, 2);
            Gl.DrawArrays(GlConst.Triangles, 0, count);
        }
        else
        {
            if (semi) ApplyPs1Blend(mode); else Gl.Disable(GlConst.Blend);
            Gl.Uniform1i(_uDrawStpPass, 0);
            Gl.DrawArrays(GlConst.Triangles, 0, count);
        }
        Gl.Disable(GlConst.Blend);
        Gl.Disable(GlConst.ScissorTest);
        Gl.BindVertexArray(0);
        Gl.BindFramebuffer(GlConst.Framebuffer, 0);
    }

    private void ApplyPs1Blend(int mode)
    {
        Gl.Enable(GlConst.Blend);
        Gl.BlendEquation(mode == 2 ? GlConst.FuncReverseSubtract : GlConst.FuncAdd);
        if (mode == 0) Gl.BlendFunc(GlConst.SrcAlpha, GlConst.OneMinusSrcAlpha);
        else if (mode == 3) Gl.BlendFunc(GlConst.SrcAlpha, GlConst.One);
        else Gl.BlendFunc(GlConst.One, GlConst.One);
    }

    public void RenderUiOverlay(ReadOnlySpan<byte> rgbaPixels, int width, int height, bool uploadPixels)
        => RenderUiOverlay(rgbaPixels, width, height, 0, 0, Math.Max(1, width), Math.Max(1, height), uploadPixels);

    public void RenderUiOverlay(ReadOnlySpan<byte> rgbaPixels, int width, int height, int dstX, int dstY, int canvasW, int canvasH, bool uploadPixels)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (width <= 0 || height <= 0 || canvasW <= 0 || canvasH <= 0 || rgbaPixels.Length < width * height * 4) return;
        InitGlIfNeeded();
        if (_overlayTex == 0 || _overlayW != width || _overlayH != height)
        {
            DeleteTex(ref _overlayTex);
            _overlayTex = CreateTexture2D(GlConst.Rgba8, width, height, GlConst.Rgba, GlConst.UnsignedByte, GlConst.Linear, GlConst.ClampToEdge);
            _overlayW = width;
            _overlayH = height;
            uploadPixels = true;
        }
        if (uploadPixels)
        {
            Gl.BindTexture(GlConst.Texture2D, _overlayTex);
            Gl.PixelStorei(GlConst.UnpackAlignment, 1);
            Gl.PixelStorei(GlConst.UnpackRowLength, 0);
            fixed (byte* p = rgbaPixels)
                Gl.TexSubImage2D(GlConst.Texture2D, 0, 0, 0, width, height, GlConst.Rgba, GlConst.UnsignedByte, p);
        }
        float ndcX = -1f + (2f * (dstX / (float)canvasW));
        float ndcW = 2f * (width / (float)canvasW);
        float ndcY = 1f - (2f * ((dstY + height) / (float)canvasH));
        float ndcH = 2f * (height / (float)canvasH);
        DrawBlitQuad(0, SurfaceWidth, SurfaceHeight, 3, _overlayTex, ndcX, ndcY, ndcW, ndcH, 0f, 1f, 1f, -1f, 0, 0, width, height, alphaBlend: true);
    }

    private void BlitDisplayToScreen(bool fullWindow, bool widescreen16By9)
    {
        Gl.Disable(GlConst.ScissorTest);
        Gl.Disable(GlConst.Blend);
        int winW = Math.Max(1, SurfaceWidth), winH = Math.Max(1, SurfaceHeight);
        int dstX = 0, dstY = 0, dstW = winW, dstH = winH;
        if (!fullWindow)
        {
            float targetAspect = widescreen16By9 ? (16f / 9f) : (4f / 3f);
            float winAspect = winW / (float)winH;
            if (winAspect > targetAspect + 0.001f)
            {
                dstW = Math.Clamp((int)MathF.Round(winH * targetAspect), 1, winW);
                dstX = (winW - dstW) / 2;
            }
            else if (winAspect < targetAspect - 0.001f)
            {
                dstH = Math.Clamp((int)MathF.Round(winW / targetAspect), 1, winH);
                dstY = (winH - dstH) / 2;
            }
            Gl.BindFramebuffer(GlConst.Framebuffer, 0);
            Gl.Viewport(0, 0, winW, winH);
            Gl.ClearColor(0f, 0f, 0f, 1f);
            Gl.Clear(GlConst.ColorBufferBit);
        }

        Gl.BindFramebuffer(GlConst.ReadFramebuffer, _displayFbo);
        Gl.BindFramebuffer(GlConst.DrawFramebuffer, 0);
        Gl.BlitFramebuffer(0, 0, _outW, _outH, dstX, winH - dstY, dstX + dstW, winH - dstY - dstH, GlConst.ColorBufferBit,
            (uint)(_outW == dstW && _outH == dstH ? GlConst.Nearest : GlConst.Linear));
        Gl.BindFramebuffer(GlConst.Framebuffer, 0);
    }

    private void UploadWrappedVramSubRects(int x, int y, int w, int h)
    {
        Gl.BindTexture(GlConst.Texture2D, _vramTex);
        Gl.PixelStorei(GlConst.UnpackAlignment, 2);
        Gl.PixelStorei(GlConst.UnpackRowLength, PsxGpu.VramWidth);
        UploadSubRectsToVramTex(x & 1023, y & 511, w, h);
        Gl.PixelStorei(GlConst.UnpackRowLength, 0);
        BlitVramRegionToFbo(x, y, w, h);
    }

    private void UploadSubRectsToVramTex(int x, int y, int w, int h) => ForEachWrappedRect(x, y, w, h, (rx, ry, rw, rh) =>
    {
        fixed (ushort* p = _vramMirror)
            Gl.TexSubImage2D(GlConst.Texture2D, 0, rx, ry, rw, rh, GlConst.RedInteger, GlConst.UnsignedShort, p + (ry * PsxGpu.VramWidth) + rx);
    });

    private void BlitVramRegionToFbo(int x, int y, int w, int h)
    {
        int s = _vramFboScale, fw = PsxGpu.VramWidth * s, fh = PsxGpu.VramHeight * s;
        ForEachWrappedRect(x & 1023, y & 511, w, h, (rx, ry, rw, rh) =>
            DrawBlitQuad(_vramFbo, fw, fh, 0, 0, (rx / 512f) - 1f, (ry / 256f) - 1f, rw / 512f, rh / 256f, rx, ry, rw, rh, 0, 0, 0, 0));
    }

    private void DrawBlitQuad(uint targetFbo, int fboW, int fboH, int mode, uint colorTex, float dx, float dy, float dw, float dh,
        float sx, float sy, float sw, float sh, int dispX, int dispY, int dispW, int dispH, bool alphaBlend = false)
    {
        Gl.Disable(GlConst.ScissorTest);
        if (alphaBlend)
        {
            Gl.Enable(GlConst.Blend);
            Gl.BlendEquation(GlConst.FuncAdd);
            Gl.BlendFunc(GlConst.SrcAlpha, GlConst.OneMinusSrcAlpha);
        }
        else
        {
            Gl.Disable(GlConst.Blend);
        }
        Gl.BindFramebuffer(GlConst.Framebuffer, targetFbo);
        Gl.Viewport(0, 0, fboW, fboH);
        Gl.UseProgram(_blitProg);
        Gl.ActiveTexture(GlConst.Texture0); Gl.BindTexture(GlConst.Texture2D, _vramTex); Gl.Uniform1i(_uBlitVramTex, 0);
        Gl.ActiveTexture(GlConst.Texture1); Gl.BindTexture(GlConst.Texture2D, colorTex); Gl.Uniform1i(_uBlitColorTex, 1);
        Gl.Uniform1i(_uBlitMode, mode);
        Gl.Uniform4f(_uBlitDstRect, dx, dy, dw, dh);
        Gl.Uniform4f(_uBlitSrcRect, sx, sy, sw, sh);
        Gl.Uniform4f(_uBlitDispInfo, dispX, dispY, dispW, dispH);
        Gl.BindVertexArray(_blitVao);
        Gl.DrawArrays(GlConst.Triangles, 0, 6);
        Gl.BindVertexArray(0);
        if (alphaBlend) Gl.Disable(GlConst.Blend);
        Gl.BindFramebuffer(GlConst.Framebuffer, 0);
    }

    private static void ForEachWrappedRect(int x, int y, int w, int h, Action<int, int, int, int> action)
    {
        int w1 = Math.Min(w, PsxGpu.VramWidth - x), w2 = w - w1;
        int h1 = Math.Min(h, PsxGpu.VramHeight - y), h2 = h - h1;
        action(x, y, w1, h1);
        if (w2 > 0) action(0, y, w2, h1);
        if (h2 > 0) action(x, 0, w1, h2);
        if (w2 > 0 && h2 > 0) action(0, 0, w2, h2);
    }

    private void EmitDecodedPolygon(in PsxDrawState st, in PsxVertex v0, in PsxVertex v1, in PsxVertex v2, in PsxVertex v3,
        int vertCount, int cl, int ct, int cr, int cb)
    {
        uint texInfo = PackTexInfo(st);
        PrepareBatchState(st, cl, ct, cr, cb, vertCount == 4 ? 6 : 3);
        AppendTri(v0, v1, v2, texInfo);
        if (vertCount == 4 && PsxPrimitiveDecoder.IsDrawable(v1, v2, v3)) AppendTri(v1, v2, v3, texInfo);
        FlushIfTwoPass(st);
    }

    private void EmitDecodedLine(in PsxDrawState ls, in PsxVertex a, in PsxVertex b, int cl, int ct, int cr, int cb)
    {
        PrepareBatchState(ls, cl, ct, cr, cb, 6);
        AppendLineQuad(a, b, PackTexInfo(ls));
    }

    private void EmitDecodedRect(in PsxDrawState rs, in PsxRect rc, int cl, int ct, int cr, int cb)
    {
        PrepareBatchState(rs, cl, ct, cr, cb, 6);
        AppendRectQuad(rc, PackTexInfo(rs));
        FlushIfTwoPass(rs);
    }

    private void AppendTri(in PsxVertex a, in PsxVertex b, in PsxVertex c, uint texInfo)
    {
        _batch[_batchCount++] = ToGl(a, texInfo); _batch[_batchCount++] = ToGl(b, texInfo); _batch[_batchCount++] = ToGl(c, texInfo);
    }

    private void AppendRectQuad(in PsxRect r, uint texInfo)
    {
        uint col = 0xFF000000U | r.Color;
        var v0 = new GlVertex { X = r.X, Y = r.Y, W = 1f, U = r.U0, V = r.V0, ColorRgba = col, TexInfo = texInfo, TexWindow = _texWindowPacked };
        var v1 = new GlVertex { X = r.X + r.Width, Y = r.Y, W = 1f, U = r.U0 + r.Width, V = r.V0, ColorRgba = col, TexInfo = texInfo, TexWindow = _texWindowPacked };
        var v2 = new GlVertex { X = r.X + r.Width, Y = r.Y + r.Height, W = 1f, U = r.U0 + r.Width, V = r.V0 + r.Height, ColorRgba = col, TexInfo = texInfo, TexWindow = _texWindowPacked };
        var v3 = new GlVertex { X = r.X, Y = r.Y + r.Height, W = 1f, U = r.U0, V = r.V0 + r.Height, ColorRgba = col, TexInfo = texInfo, TexWindow = _texWindowPacked };
        _batch[_batchCount++] = v0; _batch[_batchCount++] = v1; _batch[_batchCount++] = v2;
        _batch[_batchCount++] = v0; _batch[_batchCount++] = v2; _batch[_batchCount++] = v3;
    }

    private void AppendLineQuad(in PsxVertex a, in PsxVertex b, uint texInfo)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y, len = MathF.Sqrt((dx * dx) + (dy * dy));
        float ux = len < 0.001f ? 0.5f : (dx / len) * 0.5f, uy = len < 0.001f ? 0f : (dy / len) * 0.5f;
        float nx = len < 0.001f ? 0f : -uy, ny = len < 0.001f ? 0.5f : ux;
        float ax = a.X + 0.5f - ux, ay = a.Y + 0.5f - uy, bx = b.X + 0.5f + ux, by = b.Y + 0.5f + uy;
        uint ca = 0xFF000000U | a.R | ((uint)a.G << 8) | ((uint)a.B << 16);
        uint cb = 0xFF000000U | b.R | ((uint)b.G << 8) | ((uint)b.B << 16);
        var v0 = new GlVertex { X = ax - nx, Y = ay - ny, W = 1f, ColorRgba = ca, TexInfo = texInfo };
        var v1 = new GlVertex { X = ax + nx, Y = ay + ny, W = 1f, ColorRgba = ca, TexInfo = texInfo };
        var v2 = new GlVertex { X = bx + nx, Y = by + ny, W = 1f, ColorRgba = cb, TexInfo = texInfo };
        var v3 = new GlVertex { X = bx - nx, Y = by - ny, W = 1f, ColorRgba = cb, TexInfo = texInfo };
        _batch[_batchCount++] = v0; _batch[_batchCount++] = v1; _batch[_batchCount++] = v2;
        _batch[_batchCount++] = v0; _batch[_batchCount++] = v2; _batch[_batchCount++] = v3;
    }

    private GlVertex ToGl(in PsxVertex v, uint texInfo) => new()
    {
        X = v.X, Y = v.Y, W = v.W, U = v.U, V = v.V,
        ColorRgba = 0xFF000000U | v.R | ((uint)v.G << 8) | ((uint)v.B << 16),
        TexInfo = texInfo, TexWindow = _texWindowPacked
    };

    private static uint PackTexInfo(in PsxDrawState s) =>
        (uint)((s.TexPage & 0x1F) | ((s.ColorDepth & 3) << 5) | ((s.Clut & 0x7FFF) << 7) |
               (s.Textured ? 1 << 22 : 0) | (s.RawTexture ? 1 << 23 : 0) | (s.SemiTransparent ? 1 << 24 : 0) | ((s.SemiMode & 3) << 25));

    private void DeleteProg(ref uint h) { if (h != 0) { Gl.DeleteProgram(h); h = 0; } }
    private void DeleteVao(ref uint h) { if (h != 0) { uint t = h; Gl.DeleteVertexArrays(1, &t); h = 0; } }
    private void DeleteBuf(ref uint h) { if (h != 0) { uint t = h; Gl.DeleteBuffers(1, &t); h = 0; } }
    private void DeleteTex(ref uint h) { if (h != 0) { uint t = h; Gl.DeleteTextures(1, &t); h = 0; } }
    private void DeleteFbo(ref uint h) { if (h != 0) { uint t = h; Gl.DeleteFramebuffers(1, &t); h = 0; } }
}
