// @module: reStation.Core.Gpu.OpenGl33RenderBackend.Readback
// @purpose: On-demand owner-thread 15-bit VRAM readback from OpenGL _vramFbo for GP0(C0h), QuickSave snapshots, and live backend switches.
// @depends-on: System.Buffers, reStation.Core.Gpu.OpenGl33Bindings
// @exports: OpenGl33RenderBackend (partial)

using System.Buffers;

namespace reStation.Core.Gpu;

public sealed unsafe partial class OpenGl33RenderBackend
{
    /// <summary>
    /// Reads a wrapped 15-bit VRAM rectangle back from <c>_vramFbo</c> on the OpenGL owner thread.
    /// Returns <see langword="false"/> cleanly off-thread or before GL initialization.
    /// </summary>
    public bool TryReadVramRect(int x, int y, int w, int h, Span<ushort> destination)
    {
        if (_disposed || !_glReady || _vramFbo == 0 || _vramFboScale <= 0 || IsOffOwnerThread)
            return false;
        if (w <= 0 || h <= 0 || w > PsxGpu.VramWidth || h > PsxGpu.VramHeight || destination.Length < w * h)
            return false;

        FlushQueuedOpsOnOwnerThread();
        FlushBatch();

        int nx = x & (PsxGpu.VramWidth - 1);
        int ny = y & (PsxGpu.VramHeight - 1);
        int w1 = Math.Min(w, PsxGpu.VramWidth - nx);
        int w2 = w - w1;
        int h1 = Math.Min(h, PsxGpu.VramHeight - ny);
        int h2 = h - h1;

        byte[] rgbaBuf = ArrayPool<byte>.Shared.Rent(w * h * 4);
        try
        {
            ReadVramSubRect(nx, ny, w1, h1, 0, 0, w, destination, rgbaBuf);
            if (w2 > 0) ReadVramSubRect(0, ny, w2, h1, w1, 0, w, destination, rgbaBuf);
            if (h2 > 0) ReadVramSubRect(nx, 0, w1, h2, 0, h1, w, destination, rgbaBuf);
            if (w2 > 0 && h2 > 0) ReadVramSubRect(0, 0, w2, h2, w1, h1, w, destination, rgbaBuf);
            return true;
        }
        finally
        {
            Gl.BindFramebuffer(GlConst.Framebuffer, 0);
            ArrayPool<byte>.Shared.Return(rgbaBuf);
        }
    }

    private void ReadVramSubRect(
        int rx, int ry, int rw, int rh,
        int dstCol0, int dstRow0, int dstStride,
        Span<ushort> destination, byte[] rgbaBuf)
    {
        if (rw <= 0 || rh <= 0) return;

        int s = Math.Max(1, _vramFboScale);
        if (s > 1)
        {
            EnsureScratchFboSize(rw, rh);
            Gl.Disable(GlConst.ScissorTest);
            Gl.BindFramebuffer(GlConst.ReadFramebuffer, _vramFbo);
            Gl.BindFramebuffer(GlConst.DrawFramebuffer, _scratchFbo);
            Gl.BlitFramebuffer(
                rx * s, ry * s, (rx + rw) * s, (ry + rh) * s,
                0, 0, rw, rh, GlConst.ColorBufferBit, (uint)GlConst.Nearest);
            Gl.BindFramebuffer(GlConst.ReadFramebuffer, _scratchFbo);
            Gl.PixelStorei(GlConst.PackAlignment, 1);
            fixed (byte* p = rgbaBuf)
                Gl.ReadPixels(0, 0, rw, rh, GlConst.Rgba, GlConst.UnsignedByte, p);
        }
        else
        {
            Gl.BindFramebuffer(GlConst.ReadFramebuffer, _vramFbo);
            Gl.PixelStorei(GlConst.PackAlignment, 1);
            fixed (byte* p = rgbaBuf)
                Gl.ReadPixels(rx, ry, rw, rh, GlConst.Rgba, GlConst.UnsignedByte, p);
        }

        for (int row = 0; row < rh; row++)
        {
            int vy = (ry + row) & (PsxGpu.VramHeight - 1);
            int mirrorRowBase = vy * PsxGpu.VramWidth;
            int dstRowBase = (dstRow0 + row) * dstStride + dstCol0;
            int srcRowBase = row * rw * 4;
            for (int col = 0; col < rw; col++)
            {
                int vx = (rx + col) & (PsxGpu.VramWidth - 1);
                int pxOff = srcRowBase + (col * 4);
                byte r8 = rgbaBuf[pxOff];
                byte g8 = rgbaBuf[pxOff + 1];
                byte b8 = rgbaBuf[pxOff + 2];

                ushort raw15 = _vramMirror[mirrorRowBase + vx];
                int er = ((raw15 & 31) * 255 + 15) / 31;
                int eg = (((raw15 >> 5) & 31) * 255 + 15) / 31;
                int eb = (((raw15 >> 10) & 31) * 255 + 15) / 31;

                ushort c15;
                if (Math.Abs(r8 - er) <= 1 && Math.Abs(g8 - eg) <= 1 && Math.Abs(b8 - eb) <= 1)
                {
                    c15 = raw15;
                }
                else
                {
                    int r5 = (r8 * 31 + 127) / 255;
                    int g5 = (g8 * 31 + 127) / 255;
                    int b5 = (b8 * 31 + 127) / 255;
                    c15 = (ushort)(r5 | (g5 << 5) | (b5 << 10));
                    _vramMirror[mirrorRowBase + vx] = c15;
                }
                destination[dstRowBase + col] = c15;
            }
        }
    }
}
