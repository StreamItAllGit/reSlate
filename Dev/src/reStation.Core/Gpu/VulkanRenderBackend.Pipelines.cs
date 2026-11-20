// @module: reStation.Core.Gpu.VulkanRenderBackend.Pipelines
// @purpose: Hardware R16_UINT VkImage dirty-rect sync and PGXP/HD-pack primitive rasterization pipeline for VulkanRenderBackend.
// @depends-on: System, System.Collections.Generic
// @exports: VulkanRenderBackend (partial)
// @owner: devv-gpu-backends (T3)

namespace reStation.Core.Gpu;

public sealed partial class VulkanRenderBackend
{
    private readonly List<WideBandOp> _wideOps = new(256);
    private readonly WideSurfaceRenderer _wideRenderer = new();
    private int _texWinMaskX, _texWinMaskY, _texWinOffX, _texWinOffY;
    private int _frameStartWinMaskX, _frameStartWinMaskY, _frameStartWinOffX, _frameStartWinOffY;
    private bool _maskSetBit, _maskCheckBit;

    /// <inheritdoc/>
    public void SubmitPrimitive(uint gp0Opcode, ReadOnlySpan<uint> words, ushort texPage, int drawOffsetX, int drawOffsetY,
        int clipLeft, int clipTop, int clipRight, int clipBottom)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        PrimitivesSubmitted++;
        EnsureHiVram();
        if (SoftwareCpuRasterizer.TrySubmitDirectPrimitive(gp0Opcode, words, texPage, drawOffsetX, drawOffsetY,
                clipLeft, clipTop, clipRight, clipBottom, _hiVram, _hiMask, _vram, _hiScale, _canonical1x, _canonicalMask1x,
                _texWinMaskX, _texWinMaskY, _texWinOffX, _texWinOffY, _maskSetBit, _maskCheckBit, _pgxp, _hdPack, _hdLookup,
                out WideBandOp wideOp))
        {
            _wideOps.Add(wideOp);
        }
    }

    private void MarkVramDirty(int x, int y, int w, int h)
    {
        if (w <= 0 || h <= 0) return;
        int nx = x & 1023, ny = y & 511;
        if (w >= PsxGpu.VramWidth || h >= PsxGpu.VramHeight || nx + w > PsxGpu.VramWidth || ny + h > PsxGpu.VramHeight)
        {
            _vramDirty = true;
            _vramDirtyFull = true;
            return;
        }
        if (!_vramDirty)
        {
            _vramDirty = true;
            _vramDirtyFull = false;
            _dirtyX0 = nx; _dirtyY0 = ny;
            _dirtyX1 = nx + w; _dirtyY1 = ny + h;
            return;
        }
        if (_vramDirtyFull) return;
        _dirtyX0 = Math.Min(_dirtyX0, nx);
        _dirtyY0 = Math.Min(_dirtyY0, ny);
        _dirtyX1 = Math.Max(_dirtyX1, nx + w);
        _dirtyY1 = Math.Max(_dirtyY1, ny + h);
    }

    private void SyncDirtyVramToHardwareImage()
    {
        if (!_vramDirty || _deviceCtx is not { IsValid: true } ctx) return;
        if (_vramDirtyFull) _ = ctx.TryUploadVramRect(0, 0, PsxGpu.VramWidth, PsxGpu.VramHeight, _vram);
        else _ = ctx.TryUploadVramRect(_dirtyX0, _dirtyY0, _dirtyX1 - _dirtyX0, _dirtyY1 - _dirtyY0, _vram);
        _vramDirty = false;
        _vramDirtyFull = false;
    }

    private void ResetWideOps()
    {
        _wideOps.Clear();
        _frameStartWinMaskX = _texWinMaskX; _frameStartWinMaskY = _texWinMaskY;
        _frameStartWinOffX = _texWinOffX; _frameStartWinOffY = _texWinOffY;
    }
}

internal sealed class WideSurfaceRenderer
{
    private uint[] _wideHiBuf = Array.Empty<uint>();
    private byte[] _wideMaskBuf = Array.Empty<byte>();
    private int _wideBufW, _wideBufH, _wideBufScale;
    private bool _hasCachedWideFrame;

    public void ResetCache() => _hasCachedWideFrame = false;

    public void Render(
        List<WideBandOp> wideOps, uint[] hiVram, byte[] hiMask, ushort[] vram, int s,
        int dispX, int dispY, int dispW, int dispH, int wideW, int leftPad, int workerThreads,
        int initMaskX, int initMaskY, int initOffX, int initOffY,
        ref byte[] frame, out int frameW, out int frameH)
    {
        int outW = wideW * s, outH = dispH * s;
        bool sameTarget = _hasCachedWideFrame && _wideBufW == wideW && _wideBufH == dispH && _wideBufScale == s;
        if (_wideHiBuf.Length != outW * outH) { _wideHiBuf = new uint[outW * outH]; _wideMaskBuf = new byte[outW * outH]; sameTarget = false; }
        _wideBufW = wideW; _wideBufH = dispH; _wideBufScale = s;

        bool hasDrawsOrFills = false, hasMatchingOp = false, hasFullClear = false;
        foreach (WideBandOp entry in wideOps)
        {
            WideFrameOp op = entry.FrameOp;
            if (op.Kind == WideFrameOpKind.SetTextureWindow) continue;
            hasDrawsOrFills = true;
            if (op.Kind == WideFrameOpKind.FillRect && PsxWidescreenGeometry.TryComputeWideFillRect(op, dispX, dispY, dispW, dispH, wideW, leftPad, out _, out _, out _, out _))
            {
                hasMatchingOp = true;
                if (PsxWidescreenGeometry.IsFullWidthFillForDisplay(op, dispX, dispY, dispW, dispH)) hasFullClear = true;
            }
            else if (op.Kind == WideFrameOpKind.CopyVram && PsxWidescreenGeometry.OverlapsDisplay(op.C, op.D, op.C + op.E - 1, op.D + op.F - 1, dispX, dispY, dispW, dispH))
                hasMatchingOp = true;
            else if (op.Kind is WideFrameOpKind.Polygon or WideFrameOpKind.Line or WideFrameOpKind.Rect &&
                     PsxWidescreenGeometry.TryMapClipToWide(op.ClipL, op.ClipT, op.ClipR, op.ClipB, dispX, dispY, dispW, dispH, wideW, leftPad, out _, out _, out _, out _))
            {
                hasMatchingOp = true;
                if (op.Kind == WideFrameOpKind.Rect && !op.DrawState.Textured && op.Rect.X <= dispX + 2 && op.Rect.X + op.Rect.Width >= dispX + dispW - 2 && op.Rect.Y <= dispY + 2 && op.Rect.Y + op.Rect.Height >= dispY + dispH - 2)
                    hasFullClear = true;
            }
        }

        if (!hasDrawsOrFills && sameTarget) { CopyWideToFrame(outW, outH, ref frame, out frameW, out frameH); return; }
        if (!sameTarget || hasFullClear || !hasMatchingOp)
        {
            _wideHiBuf.AsSpan().Fill(0xFF000000U);
            _wideMaskBuf.AsSpan().Clear();
            if (!hasFullClear) BlitCenterToWide(hiVram, hiMask, dispX, dispY, dispW, dispH, leftPad, s, outW);
        }

        ParallelBandRasterizer.ExecuteWideBands(_wideHiBuf, _wideMaskBuf, hiVram, hiMask, vram, s, dispX, dispY, dispW, dispH, wideW, leftPad,
            wideOps.ToArray(), workerThreads, initMaskX, initMaskY, initOffX, initOffY);
        _hasCachedWideFrame = true;
        CopyWideToFrame(outW, outH, ref frame, out frameW, out frameH);
    }

    private void BlitCenterToWide(uint[] hiVram, byte[] hiMask, int dispX, int dispY, int dispW, int dispH, int leftPad, int s, int outW)
    {
        int hiW = PsxGpu.VramWidth * s, hiH = PsxGpu.VramHeight * s, sx = (dispX & 1023) * s, sy = (dispY & 511) * s;
        int cw = dispW * s, ch = dispH * s, lx = leftPad * s;
        for (int y = 0; y < ch; y++)
        {
            int srcRow = ((sy + y) % hiH) * hiW, dstRow = (y * outW) + lx, take = Math.Min(cw, hiW - sx);
            hiVram.AsSpan(srcRow + sx, take).CopyTo(_wideHiBuf.AsSpan(dstRow, take));
            hiMask.AsSpan(srcRow + sx, take).CopyTo(_wideMaskBuf.AsSpan(dstRow, take));
            if (take < cw)
            {
                hiVram.AsSpan(srcRow, cw - take).CopyTo(_wideHiBuf.AsSpan(dstRow + take, cw - take));
                hiMask.AsSpan(srcRow, cw - take).CopyTo(_wideMaskBuf.AsSpan(dstRow + take, cw - take));
            }
        }
    }

    private void CopyWideToFrame(int outW, int outH, ref byte[] frame, out int frameW, out int frameH)
    {
        if (frame.Length != outW * outH * 4) frame = new byte[outW * outH * 4];
        frameW = outW; frameH = outH;
        System.Runtime.InteropServices.MemoryMarshal.Cast<uint, byte>(_wideHiBuf.AsSpan(0, outW * outH)).CopyTo(frame);
    }
}
