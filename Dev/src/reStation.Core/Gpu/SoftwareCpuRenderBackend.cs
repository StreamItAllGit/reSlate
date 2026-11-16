// @module: reStation.Core.Gpu.SoftwareCpuRenderBackend
// @purpose: Deterministic 1x-8x PGXP software IPsxRenderBackend oracle ("software"): 15-bit VRAM mirror, scaled RGBA surface, fill/upload/copy, 15/24-bit scanout.
// @depends-on: reStation.Contracts, reStation.Core.Gte
// @exports: SoftwareCpuRenderBackend
// @owner: devv-gpu-gl (T4) — preserves all T1 locked members and adds full 1x-8x PGXP primitive rasterization.

using System.Runtime.InteropServices;
using reStation.Contracts;
using reStation.Core.Gte;

namespace reStation.Core.Gpu;

public sealed partial class SoftwareCpuRenderBackend : IPsxRenderBackend, IPsxGpuBackendHooks
{
    internal static readonly uint[] Rgb15Lut = BuildRgb15Lut();

    private readonly ushort[] _vram = new ushort[PsxGpu.VramWidth * PsxGpu.VramHeight];
    private uint[] _hiVram = Array.Empty<uint>();
    private byte[] _frame = new byte[320 * 240 * 4];
    private int _frameW = 320, _frameH = 240;
    private int _scale = 1, _hiScale;
    private PgxpTracker? _pgxp;
    private int _texWinMaskX, _texWinMaskY, _texWinOffX, _texWinOffY;
    private uint _texWindowPacked;

    public string BackendId => PsxRenderBackendIds.Software;
    public int ScaleFactor { get => _scale; set { _scale = NormalizeScale(value); EnsureHiVram(); } }
    public int SurfaceWidth { get; private set; } = 320;
    public int SurfaceHeight { get; private set; } = 240;
    public long PrimitivesSubmitted { get; private set; }

    public void AttachPgxp(PgxpTracker? tracker) => _pgxp = tracker;

    public void SetTextureWindow(int maskX, int maskY, int offsetX, int offsetY)
    {
        _texWinMaskX = maskX & 0x1F; _texWinMaskY = maskY & 0x1F;
        _texWinOffX = offsetX & 0x1F; _texWinOffY = offsetY & 0x1F;
        _texWindowPacked = (uint)(_texWinMaskX | (_texWinMaskY << 5) | (_texWinOffX << 10) | (_texWinOffY << 15));
        _wideFrameOps.Add(new WideFrameOp { Kind = WideFrameOpKind.SetTextureWindow, RawU32 = _texWindowPacked });
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
        _wideFrameOps.Clear();
        _hasCachedWideFrame = false;
        _frameW = width;
        _frameH = height;
        _frame = rgba[..(width * height * 4)].ToArray();
    }

    public void UploadVramRect(int x, int y, int w, int h, ReadOnlySpan<ushort> pixels15)
    {
        if (w <= 0 || h <= 0) return;
        EnsureHiVram();
        int s = _hiScale, hiW = PsxGpu.VramWidth * s;
        for (int row = 0, i = 0; row < h; row++)
        {
            int vy = (y + row) & 511;
            int vramRow = vy * PsxGpu.VramWidth;
            for (int col = 0; col < w && i < pixels15.Length; col++, i++)
            {
                int vx = (x + col) & 1023;
                ushort p = pixels15[i];
                _vram[vramRow + vx] = p;
                WriteScaledPixel(vx, vy, Rgb15Lut[p], s, hiW);
            }
        }
    }

    public void CopyVramRect(int srcX, int srcY, int dstX, int dstY, int w, int h)
    {
        if (w <= 0 || h <= 0) return;
        EnsureHiVram();
        var tmp15 = new ushort[w * h];
        for (int r = 0, i = 0; r < h; r++)
            for (int c = 0; c < w; c++, i++)
                tmp15[i] = _vram[(((srcY + r) & 511) * PsxGpu.VramWidth) + ((srcX + c) & 1023)];
        for (int r = 0, i = 0; r < h; r++)
            for (int c = 0; c < w; c++, i++)
                _vram[(((dstY + r) & 511) * PsxGpu.VramWidth) + ((dstX + c) & 1023)] = tmp15[i];

        int s = _hiScale, hiW = PsxGpu.VramWidth * s, hiH = PsxGpu.VramHeight * s;
        int sw = w * s, sh = h * s, sx0 = srcX * s, sy0 = srcY * s, dx0 = dstX * s, dy0 = dstY * s;
        var tmp32 = new uint[sw * sh];
        for (int r = 0, i = 0; r < sh; r++)
            for (int c = 0; c < sw; c++, i++)
                tmp32[i] = _hiVram[(((sy0 + r) % hiH) * hiW) + ((sx0 + c) % hiW)];
        for (int r = 0, i = 0; r < sh; r++)
            for (int c = 0; c < sw; c++, i++)
                _hiVram[(((dy0 + r) % hiH) * hiW) + ((dx0 + c) % hiW)] = tmp32[i];
        _wideFrameOps.Add(new WideFrameOp
        {
            Kind = WideFrameOpKind.CopyVram, A = srcX & 1023, B = srcY & 511, C = dstX & 1023, D = dstY & 511, E = w, F = h
        });
    }

    public void FillDisplayRect(int x, int y, int w, int h, uint bgr24)
    {
        if (w <= 0 || h <= 0) return;
        EnsureHiVram();
        ushort c15 = Rgb888To15(bgr24);
        uint abgr = 0xFF000000U | (bgr24 & 0xFFFFFFU);
        int s = _hiScale, hiW = PsxGpu.VramWidth * s;
        for (int row = 0; row < h; row++)
        {
            int vy = (y + row) & 511;
            int vramRow = vy * PsxGpu.VramWidth;
            for (int col = 0; col < w; col++)
            {
                int vx = (x + col) & 1023;
                _vram[vramRow + vx] = c15;
                WriteScaledPixel(vx, vy, abgr, s, hiW);
            }
        }
        _wideFrameOps.Add(new WideFrameOp
        {
            Kind = WideFrameOpKind.FillRect, A = x & 1023, B = y & 511, C = w, D = h, RawU32 = bgr24
        });
    }

    public void SubmitPrimitive(uint gp0Opcode, ReadOnlySpan<uint> words, ushort texPage, int drawOffsetX, int drawOffsetY,
        int clipLeft, int clipTop, int clipRight, int clipBottom)
    {
        PrimitivesSubmitted++;
        EnsureHiVram();
        uint op = gp0Opcode & 0xFFU;
        if (op is >= 0x20 and <= 0x3F)
        {
            Span<PsxVertex> verts = stackalloc PsxVertex[4];
            int count = PsxPrimitiveDecoder.DecodePolygon(op, words, texPage, drawOffsetX, drawOffsetY, _pgxp, verts, out PsxDrawState state);
            if (count < 3) return;
            SoftwareCpuRasterizer.RasterizeTriangle(_hiVram, _vram, _hiScale, in verts[0], in verts[1], in verts[2],
                in state, clipLeft, clipTop, clipRight, clipBottom, _texWinMaskX, _texWinMaskY, _texWinOffX, _texWinOffY);
            if (count == 4)
                SoftwareCpuRasterizer.RasterizeTriangle(_hiVram, _vram, _hiScale, in verts[1], in verts[2], in verts[3],
                    in state, clipLeft, clipTop, clipRight, clipBottom, _texWinMaskX, _texWinMaskY, _texWinOffX, _texWinOffY);
            _wideFrameOps.Add(new WideFrameOp
            {
                Kind = WideFrameOpKind.Polygon, DrawState = state, PolyCount = count,
                V0 = verts[0], V1 = verts[1], V2 = verts[2], V3 = verts[3],
                ClipL = clipLeft, ClipT = clipTop, ClipR = clipRight, ClipB = clipBottom
            });
        }
        else if (op is >= 0x40 and <= 0x5F && PsxPrimitiveDecoder.DecodeLine(op, words, texPage, drawOffsetX, drawOffsetY, out PsxVertex a, out PsxVertex b, out PsxDrawState ls))
        {
            SoftwareCpuRasterizer.RasterizeLine(_hiVram, _hiScale, in a, in b, in ls, clipLeft, clipTop, clipRight, clipBottom);
            _wideFrameOps.Add(new WideFrameOp
            {
                Kind = WideFrameOpKind.Line, DrawState = ls, V0 = a, V1 = b,
                ClipL = clipLeft, ClipT = clipTop, ClipR = clipRight, ClipB = clipBottom
            });
        }
        else if (op is >= 0x60 and <= 0x7F && PsxPrimitiveDecoder.DecodeRect(op, words, texPage, drawOffsetX, drawOffsetY, out PsxRect rect, out PsxDrawState rs))
        {
            SoftwareCpuRasterizer.RasterizeRect(_hiVram, _vram, _hiScale, in rect, in rs,
                clipLeft, clipTop, clipRight, clipBottom, _texWinMaskX, _texWinMaskY, _texWinOffX, _texWinOffY);
            _wideFrameOps.Add(new WideFrameOp
            {
                Kind = WideFrameOpKind.Rect, DrawState = rs, Rect = rect,
                ClipL = clipLeft, ClipT = clipTop, ClipR = clipRight, ClipB = clipBottom
            });
        }
    }

    public void EndFrameAndPresent(int dispX, int dispY, int dispW, int dispH, bool is24Bit, bool widescreen16By9)
    {
        EnsureHiVram();
        int dw = Math.Clamp(dispW, 1, PsxGpu.VramWidth), dh = Math.Clamp(dispH, 1, PsxGpu.VramHeight);
        int ox = dispX & 1023, oy = dispY & 511;
        int s = is24Bit ? 1 : _hiScale;
        if (widescreen16By9 && !is24Bit)
        {
            PsxWidescreenGeometry.ComputeDimensions(dw, dh, out int wideW, out int leftPad);
            if (wideW > dw)
            {
                RenderWideSoftwareFrame(ox, oy, dw, dh, wideW, leftPad, s);
                _wideFrameOps.Clear();
                return;
            }
        }
        _wideFrameOps.Clear();
        _hasCachedWideFrame = false;
        int outW = dw * s, outH = dh * s;
        if (_frame.Length != outW * outH * 4) _frame = new byte[outW * outH * 4];
        _frameW = outW; _frameH = outH;

        if (is24Bit)
        {
            for (int y = 0; y < dh; y++)
            {
                int vy = (dispY + y) & 511;
                for (int x = 0; x < dw; x++)
                {
                    int o = ((y * dw) + x) * 4, bp = (dispX * 2) + (x * 3);
                    _frame[o] = Vram24Byte(vy, bp);
                    _frame[o + 1] = Vram24Byte(vy, bp + 1);
                    _frame[o + 2] = Vram24Byte(vy, bp + 2);
                    _frame[o + 3] = 0xFF;
                }
            }
            return;
        }

        int hiW = PsxGpu.VramWidth * s, hiH = PsxGpu.VramHeight * s;
        int sx = (dispX & 1023) * s, sy = (dispY & 511) * s;
        Span<uint> dstU32 = MemoryMarshal.Cast<byte, uint>(_frame.AsSpan());
        for (int y = 0; y < outH; y++)
        {
            int srcRow = ((sy + y) % hiH) * hiW, dstRow = y * outW;
            int firstTake = Math.Min(outW, hiW - sx);
            _hiVram.AsSpan(srcRow + sx, firstTake).CopyTo(dstU32.Slice(dstRow, firstTake));
            if (firstTake < outW) _hiVram.AsSpan(srcRow, outW - firstTake).CopyTo(dstU32.Slice(dstRow + firstTake, outW - firstTake));
        }
    }

    public byte[] CaptureRgba(out int width, out int height)
    {
        width = _frameW;
        height = _frameH;
        return (byte[])_frame.Clone();
    }

    public bool TryReadVramRect(int x, int y, int w, int h, Span<ushort> destination)
    {
        if (w <= 0 || h <= 0 || destination.Length < w * h) return false;
        EnsureHiVram();
        int s = _hiScale, hiW = PsxGpu.VramWidth * s;
        for (int r = 0, i = 0; r < h; r++)
        {
            int vy = (y + r) & 511;
            for (int c = 0; c < w; c++, i++)
            {
                int vx = (x + c) & 1023;
                ushort raw15 = _vram[(vy * PsxGpu.VramWidth) + vx];
                uint hi = _hiVram[((vy * s) * hiW) + (vx * s)];
                destination[i] = hi == Rgb15Lut[raw15] ? raw15 : AbgrTo15(hi);
            }
        }
        return true;
    }

    public void RenderUiOverlay(ReadOnlySpan<byte> rgbaPixels, int width, int height, bool uploadPixels) { }

    public void RenderUiOverlay(ReadOnlySpan<byte> rgbaPixels, int width, int height, int dstX, int dstY, int canvasW, int canvasH, bool uploadPixels) { }

    public void Dispose() => PrimitivesSubmitted = 0;

    private void EnsureHiVram()
    {
        if (_hiScale == _scale && _hiVram.Length == PsxGpu.VramWidth * _scale * PsxGpu.VramHeight * _scale) return;
        _hiScale = _scale;
        int hiW = PsxGpu.VramWidth * _hiScale;
        _hiVram = new uint[hiW * PsxGpu.VramHeight * _hiScale];
        for (int vy = 0; vy < PsxGpu.VramHeight; vy++)
            for (int vx = 0; vx < PsxGpu.VramWidth; vx++)
                WriteScaledPixel(vx, vy, Rgb15Lut[_vram[(vy * PsxGpu.VramWidth) + vx]], _hiScale, hiW);
    }

    private void WriteScaledPixel(int vx, int vy, uint abgr, int s, int hiW)
    {
        int x0 = vx * s, y0 = vy * s;
        for (int dy = 0; dy < s; dy++) _hiVram.AsSpan(((y0 + dy) * hiW) + x0, s).Fill(abgr);
    }

    private byte Vram24Byte(int vy, int bytePos)
    {
        ushort p = _vram[(vy * PsxGpu.VramWidth) + ((bytePos >> 1) & 1023)];
        return (bytePos & 1) == 0 ? (byte)p : (byte)(p >> 8);
    }

    private static uint[] BuildRgb15Lut()
    {
        var lut = new uint[65536];
        for (int p = 0; p < 65536; p++)
        {
            uint r = (uint)(((p & 0x1F) * 255 + 15) / 31);
            uint g = (uint)((((p >> 5) & 0x1F) * 255 + 15) / 31);
            uint b = (uint)((((p >> 10) & 0x1F) * 255 + 15) / 31);
            lut[p] = 0xFF000000U | r | (g << 8) | (b << 16);
        }
        return lut;
    }

    private static ushort Rgb888To15(uint c) => (ushort)(((c >> 3) & 0x1F) | (((c >> 11) & 0x1F) << 5) | (((c >> 19) & 0x1F) << 10));
    private static ushort AbgrTo15(uint abgr) => (ushort)(((abgr >> 3) & 0x1F) | (((abgr >> 11) & 0x1F) << 5) | (((abgr >> 19) & 0x1F) << 10));
    private static int NormalizeScale(int s) => s <= 1 ? 1 : s <= 2 ? 2 : s <= 4 ? 4 : 8;
}
