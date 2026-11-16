// @module: reStation.Core.Gpu.OpenGl33RenderBackend.Widescreen
// @purpose: Unstretched Hor+ 16:9 side-wing FBO rendering and frame-op replay for OpenGl33RenderBackend.
// @depends-on: System.Collections.Generic, reStation.Core.Gpu.OpenGl33Bindings
// @exports: OpenGl33RenderBackend (partial)
// @owner: devv-gpu-gl (T4)

namespace reStation.Core.Gpu;

public sealed unsafe partial class OpenGl33RenderBackend
{
    private readonly List<WideFrameOp> _wideFrameOps = new();

    private void ResetWideFrameTracking()
    {
        _wideFrameOps.Clear();
        _hasCachedWideFrame = false;
    }

    private void EnsureWideFboSize(int w, int h, int s)
    {
        if (_wideFbo != 0 && _wideW == w && _wideH == h && _wideFboScale == s) return;
        _wideW = w;
        _wideH = h;
        _wideFboScale = s;
        _hasCachedWideFrame = false;
        RecreateFbo(ref _wideFbo, ref _wideColorTex, w * s, h * s, GlConst.ClampToEdge);
    }

    private void RenderWideFrameToFbo(int dispX, int dispY, int dispW, int dispH, int wideW, int leftPad, int s)
    {
        bool sameTarget = _wideFbo != 0 && _wideW == wideW && _wideH == dispH && _wideFboScale == s;
        EnsureWideFboSize(wideW, dispH, s);

        bool hasDrawsOrFills = false;
        bool hasMatchingOp = false;
        bool hasFullClear = false;
        foreach (WideFrameOp op in _wideFrameOps)
        {
            if (op.Kind == WideFrameOpKind.SetTextureWindow) continue;
            hasDrawsOrFills = true;
            if (op.Kind == WideFrameOpKind.FillRect)
            {
                if (PsxWidescreenGeometry.TryComputeWideFillRect(op, dispX, dispY, dispW, dispH, wideW, leftPad, out _, out _, out _, out _))
                {
                    hasMatchingOp = true;
                    if (PsxWidescreenGeometry.IsFullWidthFillForDisplay(op, dispX, dispY, dispW, dispH)) hasFullClear = true;
                }
            }
            else if (op.Kind == WideFrameOpKind.CopyVram)
            {
                if (PsxWidescreenGeometry.OverlapsDisplay(op.C, op.D, op.C + op.E - 1, op.D + op.F - 1, dispX, dispY, dispW, dispH))
                    hasMatchingOp = true;
            }
            else if (PsxWidescreenGeometry.TryMapClipToWide(op.ClipL, op.ClipT, op.ClipR, op.ClipB, dispX, dispY, dispW, dispH, wideW, leftPad, out _, out _, out _, out _))
            {
                hasMatchingOp = true;
                if (op.Kind == WideFrameOpKind.Rect && !op.DrawState.Textured
                    && op.Rect.X <= dispX + 2 && op.Rect.X + op.Rect.Width >= dispX + dispW - 2
                    && op.Rect.Y <= dispY + 2 && op.Rect.Y + op.Rect.Height >= dispY + dispH - 2)
                {
                    hasFullClear = true;
                }
            }
        }

        if (!hasDrawsOrFills && sameTarget && _hasCachedWideFrame)
            return; // 30 Hz idle VBlank: keep previous complete wide frame untouched.

        if (!sameTarget || !_hasCachedWideFrame || hasFullClear || !hasMatchingOp)
        {
            Gl.Disable(GlConst.ScissorTest);
            Gl.BindFramebuffer(GlConst.Framebuffer, _wideFbo);
            Gl.ClearColor(0f, 0f, 0f, 1f);
            Gl.Clear(GlConst.ColorBufferBit);
            Gl.BindFramebuffer(GlConst.Framebuffer, 0);

            if (!hasFullClear)
            {
                float sx = dispX / (float)PsxGpu.VramWidth, sy = dispY / (float)PsxGpu.VramHeight;
                float sw = dispW / (float)PsxGpu.VramWidth, sh = dispH / (float)PsxGpu.VramHeight;
                float dx = -1f + (2f * leftPad / (float)wideW), dw = 2f * dispW / (float)wideW;
                DrawBlitQuad(_wideFbo, wideW * s, dispH * s, 1, _vramColorTex, dx, -1f, dw, 2f, sx, sy, sw, sh, dispX, dispY, dispW, dispH);
            }
        }

        uint savedTexWin = _texWindowPacked;
        int savedCl = _clipL, savedCt = _clipT, savedCr = _clipR, savedCb = _clipB;
        uint savedKey = _batchBlendKey;
        try
        {
            foreach (WideFrameOp op in _wideFrameOps)
            {
                switch (op.Kind)
                {
                    case WideFrameOpKind.SetTextureWindow:
                        FlushWideBatch(wideW, dispH);
                        _texWindowPacked = op.RawU32;
                        break;
                    case WideFrameOpKind.FillRect:
                        if (PsxWidescreenGeometry.TryComputeWideFillRect(op, dispX, dispY, dispW, dispH, wideW, leftPad, out int wx, out int wy, out int ww, out int wh))
                        {
                            FlushWideBatch(wideW, dispH);
                            Gl.BindFramebuffer(GlConst.Framebuffer, _wideFbo);
                            Gl.Enable(GlConst.ScissorTest);
                            Gl.ClearColor((op.RawU32 & 0xFFU) / 255f, ((op.RawU32 >> 8) & 0xFFU) / 255f, ((op.RawU32 >> 16) & 0xFFU) / 255f, 1f);
                            Gl.Scissor(wx * s, wy * s, ww * s, wh * s);
                            Gl.Clear(GlConst.ColorBufferBit);
                            Gl.Disable(GlConst.ScissorTest);
                            Gl.BindFramebuffer(GlConst.Framebuffer, 0);
                        }
                        break;
                    case WideFrameOpKind.CopyVram:
                        if (PsxWidescreenGeometry.OverlapsDisplay(op.C, op.D, op.C + op.E - 1, op.D + op.F - 1, dispX, dispY, dispW, dispH))
                        {
                            FlushWideBatch(wideW, dispH);
                            int dstWx = Math.Clamp(op.C - dispX + leftPad, 0, wideW);
                            int dstWy = Math.Clamp(op.D - dispY, 0, dispH);
                            int cw = Math.Min(op.E, wideW - dstWx), ch = Math.Min(op.F, dispH - dstWy);
                            if (cw > 0 && ch > 0)
                            {
                                DrawBlitQuad(_wideFbo, wideW * s, dispH * s, 1, _vramColorTex,
                                    -1f + (2f * dstWx / wideW), -1f + (2f * dstWy / dispH), 2f * cw / wideW, 2f * ch / dispH,
                                    op.A / (float)PsxGpu.VramWidth, op.B / (float)PsxGpu.VramHeight,
                                    cw / (float)PsxGpu.VramWidth, ch / (float)PsxGpu.VramHeight, dispX, dispY, dispW, dispH);
                            }
                        }
                        break;
                    case WideFrameOpKind.Polygon:
                        if (PsxWidescreenGeometry.TryMapClipToWide(op.ClipL, op.ClipT, op.ClipR, op.ClipB, dispX, dispY, dispW, dispH, wideW, leftPad, out int pcl, out int pct, out int pcr, out int pcb))
                        {
                            PsxVertex v0 = PsxWidescreenGeometry.ShiftVertex(op.V0, dispX, dispY, leftPad);
                            PsxVertex v1 = PsxWidescreenGeometry.ShiftVertex(op.V1, dispX, dispY, leftPad);
                            PsxVertex v2 = PsxWidescreenGeometry.ShiftVertex(op.V2, dispX, dispY, leftPad);
                            PsxVertex v3 = PsxWidescreenGeometry.ShiftVertex(op.V3, dispX, dispY, leftPad);
                            PsxWidescreenGeometry.ExpandIfFullScreenFadeQuad(op.DrawState, op.PolyCount, dispW, dispH, wideW, leftPad, ref v0, ref v1, ref v2, ref v3);
                            EmitWidePolygon(op.DrawState, v0, v1, v2, v3, op.PolyCount, wideW, dispH, pcl, pct, pcr, pcb);
                        }
                        break;
                    case WideFrameOpKind.Line:
                        if (PsxWidescreenGeometry.TryMapClipToWide(op.ClipL, op.ClipT, op.ClipR, op.ClipB, dispX, dispY, dispW, dispH, wideW, leftPad, out int lcl, out int lct, out int lcr, out int lcb))
                        {
                            PrepareWideBatchState(op.DrawState, wideW, dispH, lcl, lct, lcr, lcb, 6);
                            AppendLineQuad(
                                PsxWidescreenGeometry.ShiftVertex(op.V0, dispX, dispY, leftPad),
                                PsxWidescreenGeometry.ShiftVertex(op.V1, dispX, dispY, leftPad),
                                PackTexInfo(op.DrawState));
                        }
                        break;
                    case WideFrameOpKind.Rect:
                        if (PsxWidescreenGeometry.TryMapClipToWide(op.ClipL, op.ClipT, op.ClipR, op.ClipB, dispX, dispY, dispW, dispH, wideW, leftPad, out int rcl, out int rct, out int rcr, out int rcb))
                        {
                            PsxRect wr = PsxWidescreenGeometry.MapRectToWide(op.Rect, op.DrawState, dispX, dispY, dispW, dispH, wideW, leftPad);
                            PrepareWideBatchState(op.DrawState, wideW, dispH, rcl, rct, rcr, rcb, 6);
                            AppendRectQuad(wr, PackTexInfo(op.DrawState));
                            if (op.DrawState.Textured && op.DrawState.SemiTransparent && op.DrawState.SemiMode != 0)
                                FlushWideBatch(wideW, dispH);
                        }
                        break;
                }
            }
            FlushWideBatch(wideW, dispH);
            _hasCachedWideFrame = true;
        }
        finally
        {
            _texWindowPacked = savedTexWin;
            _clipL = savedCl; _clipT = savedCt; _clipR = savedCr; _clipB = savedCb;
            _batchBlendKey = savedKey;
        }
    }

    private void FlushWideBatch(int wideW, int wideH) => FlushBatchTo(_wideFbo, wideW, wideH);

    private void PrepareWideBatchState(in PsxDrawState st, int wideW, int wideH, int cl, int ct, int cr, int cb, int neededVerts)
    {
        uint key = (st.SemiTransparent ? 1U : 0U) | ((uint)(st.SemiMode & 3) << 1) | (st.Textured ? 8U : 0U);
        if (_batchCount > 0 && (_batchCount + neededVerts > MaxBatchVertices || _batchBlendKey != key ||
            _clipL != cl || _clipT != ct || _clipR != cr || _clipB != cb))
            FlushWideBatch(wideW, wideH);
        _batchBlendKey = key; _clipL = cl; _clipT = ct; _clipR = cr; _clipB = cb;
    }

    private void EmitWidePolygon(in PsxDrawState st, in PsxVertex v0, in PsxVertex v1, in PsxVertex v2, in PsxVertex v3,
        int vertCount, int wideW, int wideH, int cl, int ct, int cr, int cb)
    {
        uint texInfo = PackTexInfo(st);
        PrepareWideBatchState(st, wideW, wideH, cl, ct, cr, cb, vertCount == 4 ? 6 : 3);
        AppendTri(v0, v1, v2, texInfo);
        if (vertCount == 4 && PsxPrimitiveDecoder.IsDrawable(v1, v2, v3)) AppendTri(v1, v2, v3, texInfo);
        if (st.Textured && st.SemiTransparent && st.SemiMode != 0) FlushWideBatch(wideW, wideH);
    }
}
