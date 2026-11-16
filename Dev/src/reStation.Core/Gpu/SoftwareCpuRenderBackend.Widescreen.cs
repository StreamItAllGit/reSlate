// @module: reStation.Core.Gpu.SoftwareCpuRenderBackend.Widescreen
// @purpose: Unstretched Hor+ 16:9 side-wing software framebuffer rendering for SoftwareCpuRenderBackend.
// @depends-on: System, System.Collections.Generic, System.Runtime.InteropServices
// @exports: SoftwareCpuRenderBackend (partial)
// @owner: devv-gpu-gl (T4)

using System.Runtime.InteropServices;

namespace reStation.Core.Gpu;

public sealed partial class SoftwareCpuRenderBackend
{
    private readonly List<WideFrameOp> _wideFrameOps = new();
    private uint[] _wideHiBuf = Array.Empty<uint>();
    private int _wideBufW, _wideBufH, _wideBufScale;
    private bool _hasCachedWideFrame;

    private void RenderWideSoftwareFrame(int dispX, int dispY, int dispW, int dispH, int wideW, int leftPad, int s)
    {
        int outW = wideW * s, outH = dispH * s;
        bool sameTarget = _hasCachedWideFrame && _wideBufW == wideW && _wideBufH == dispH && _wideBufScale == s;
        if (_wideHiBuf.Length != outW * outH)
        {
            _wideHiBuf = new uint[outW * outH];
            sameTarget = false;
        }
        _wideBufW = wideW; _wideBufH = dispH; _wideBufScale = s;

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

        if (!hasDrawsOrFills && sameTarget)
        {
            CopyWideHiBufToFrame(outW, outH);
            return;
        }

        if (!sameTarget || hasFullClear || !hasMatchingOp)
        {
            _wideHiBuf.AsSpan().Fill(0xFF000000U);
            if (!hasFullClear)
                BlitVramCenterToWide(dispX, dispY, dispW, dispH, leftPad, s, outW);
        }

        int wxMask = _texWinMaskX, wyMask = _texWinMaskY, wxOff = _texWinOffX, wyOff = _texWinOffY;
        foreach (WideFrameOp op in _wideFrameOps)
        {
            switch (op.Kind)
            {
                case WideFrameOpKind.SetTextureWindow:
                    wxMask = (int)(op.RawU32 & 31U);
                    wyMask = (int)((op.RawU32 >> 5) & 31U);
                    wxOff = (int)((op.RawU32 >> 10) & 31U);
                    wyOff = (int)((op.RawU32 >> 15) & 31U);
                    break;
                case WideFrameOpKind.FillRect:
                    if (PsxWidescreenGeometry.TryComputeWideFillRect(op, dispX, dispY, dispW, dispH, wideW, leftPad, out int fx, out int fy, out int fw, out int fh))
                    {
                        uint abgr = 0xFF000000U | (op.RawU32 & 0xFFFFFFU);
                        int ys = fy * s, ye = (fy + fh) * s, xs = fx * s, cw = fw * s;
                        for (int py = ys; py < ye; py++) _wideHiBuf.AsSpan((py * outW) + xs, cw).Fill(abgr);
                    }
                    break;
                case WideFrameOpKind.CopyVram:
                    if (PsxWidescreenGeometry.OverlapsDisplay(op.C, op.D, op.C + op.E - 1, op.D + op.F - 1, dispX, dispY, dispW, dispH))
                    {
                        int dstWx = Math.Clamp(op.C - dispX + leftPad, 0, wideW);
                        int dstWy = Math.Clamp(op.D - dispY, 0, dispH);
                        int cw = Math.Min(op.E, wideW - dstWx) * s;
                        int ch = Math.Min(op.F, dispH - dstWy) * s;
                        if (cw > 0 && ch > 0)
                        {
                            int hiW = PsxGpu.VramWidth * s, hiH = PsxGpu.VramHeight * s;
                            int srcXs = op.A * s, srcYs = op.B * s, dstXs = dstWx * s, dstYs = dstWy * s;
                            for (int r = 0; r < ch; r++)
                            {
                                int srcRow = ((srcYs + r) % hiH) * hiW;
                                int dstRow = ((dstYs + r) * outW) + dstXs;
                                int take = Math.Min(cw, hiW - srcXs);
                                _hiVram.AsSpan(srcRow + srcXs, take).CopyTo(_wideHiBuf.AsSpan(dstRow, take));
                                if (take < cw) _hiVram.AsSpan(srcRow, cw - take).CopyTo(_wideHiBuf.AsSpan(dstRow + take, cw - take));
                            }
                        }
                    }
                    break;
                case WideFrameOpKind.Polygon:
                    if (PsxWidescreenGeometry.TryMapClipToWide(op.ClipL, op.ClipT, op.ClipR, op.ClipB, dispX, dispY, dispW, dispH, wideW, leftPad, out int pcl, out int pct, out int pcr, out int pcb))
                    {
                        PsxDrawState pst = op.DrawState;
                        PsxVertex v0 = PsxWidescreenGeometry.ShiftVertex(op.V0, dispX, dispY, leftPad);
                        PsxVertex v1 = PsxWidescreenGeometry.ShiftVertex(op.V1, dispX, dispY, leftPad);
                        PsxVertex v2 = PsxWidescreenGeometry.ShiftVertex(op.V2, dispX, dispY, leftPad);
                        PsxVertex v3 = PsxWidescreenGeometry.ShiftVertex(op.V3, dispX, dispY, leftPad);
                        PsxWidescreenGeometry.ExpandIfFullScreenFadeQuad(in pst, op.PolyCount, dispW, dispH, wideW, leftPad, ref v0, ref v1, ref v2, ref v3);
                        SoftwareCpuRasterizer.RasterizeTriangle(_wideHiBuf, _vram, s, in v0, in v1, in v2, in pst, pcl, pct, pcr, pcb, wxMask, wyMask, wxOff, wyOff, wideW, dispH);
                        if (op.PolyCount == 4)
                            SoftwareCpuRasterizer.RasterizeTriangle(_wideHiBuf, _vram, s, in v1, in v2, in v3, in pst, pcl, pct, pcr, pcb, wxMask, wyMask, wxOff, wyOff, wideW, dispH);
                    }
                    break;
                case WideFrameOpKind.Line:
                    if (PsxWidescreenGeometry.TryMapClipToWide(op.ClipL, op.ClipT, op.ClipR, op.ClipB, dispX, dispY, dispW, dispH, wideW, leftPad, out int lcl, out int lct, out int lcr, out int lcb))
                    {
                        PsxDrawState lst = op.DrawState;
                        PsxVertex a = PsxWidescreenGeometry.ShiftVertex(op.V0, dispX, dispY, leftPad);
                        PsxVertex b = PsxWidescreenGeometry.ShiftVertex(op.V1, dispX, dispY, leftPad);
                        SoftwareCpuRasterizer.RasterizeLine(_wideHiBuf, s, in a, in b, in lst, lcl, lct, lcr, lcb, wideW, dispH);
                    }
                    break;
                case WideFrameOpKind.Rect:
                    if (PsxWidescreenGeometry.TryMapClipToWide(op.ClipL, op.ClipT, op.ClipR, op.ClipB, dispX, dispY, dispW, dispH, wideW, leftPad, out int rcl, out int rct, out int rcr, out int rcb))
                    {
                        PsxDrawState rst = op.DrawState;
                        PsxRect wr = PsxWidescreenGeometry.MapRectToWide(op.Rect, in rst, dispX, dispY, dispW, dispH, wideW, leftPad);
                        SoftwareCpuRasterizer.RasterizeRect(_wideHiBuf, _vram, s, in wr, in rst, rcl, rct, rcr, rcb, wxMask, wyMask, wxOff, wyOff, wideW, dispH);
                    }
                    break;
            }
        }
        _hasCachedWideFrame = true;
        CopyWideHiBufToFrame(outW, outH);
    }

    private void BlitVramCenterToWide(int dispX, int dispY, int dispW, int dispH, int leftPad, int s, int outW)
    {
        int hiW = PsxGpu.VramWidth * s, hiH = PsxGpu.VramHeight * s;
        int sx = (dispX & 1023) * s, sy = (dispY & 511) * s;
        int cw = dispW * s, ch = dispH * s, lx = leftPad * s;
        for (int y = 0; y < ch; y++)
        {
            int srcRow = ((sy + y) % hiH) * hiW;
            int dstRow = (y * outW) + lx;
            int take = Math.Min(cw, hiW - sx);
            _hiVram.AsSpan(srcRow + sx, take).CopyTo(_wideHiBuf.AsSpan(dstRow, take));
            if (take < cw) _hiVram.AsSpan(srcRow, cw - take).CopyTo(_wideHiBuf.AsSpan(dstRow + take, cw - take));
        }
    }

    private void CopyWideHiBufToFrame(int outW, int outH)
    {
        if (_frame.Length != outW * outH * 4) _frame = new byte[outW * outH * 4];
        _frameW = outW;
        _frameH = outH;
        MemoryMarshal.Cast<uint, byte>(_wideHiBuf.AsSpan(0, outW * outH)).CopyTo(_frame);
    }
}
