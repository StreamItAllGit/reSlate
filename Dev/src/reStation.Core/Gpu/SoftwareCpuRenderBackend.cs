// @module: reStation.Core.Gpu.SoftwareCpuRenderBackend
// @purpose: Deterministic 1x-8x PGXP software IPsxRenderBackend oracle ("software"): 15-bit VRAM mirror, mask bits, HD packs, scaled RGBA surface.
// @depends-on: reStation.Contracts, reStation.Core.Enhancement, reStation.Core.Gte
// @exports: SoftwareCpuRenderBackend, PsxVramSurfaceOps
// @owner: devv-gpu-backends (T3/T4)

using System.Runtime.InteropServices;
using reStation.Contracts;
using reStation.Core.Enhancement;
using reStation.Core.Gte;

namespace reStation.Core.Gpu;

public sealed partial class SoftwareCpuRenderBackend : IPsxRenderBackend, IPsxGpuBackendHooks
{
    internal static readonly uint[] Rgb15Lut = BuildRgb15Lut();

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
    private int _texWinMaskX, _texWinMaskY, _texWinOffX, _texWinOffY;
    private uint _texWindowPacked;
    private bool _maskSetBit, _maskCheckBit;

    public string BackendId => PsxRenderBackendIds.Software;
    public int ScaleFactor { get => _scale; set { _scale = PsxVramSurfaceOps.NormalizeScale(value); EnsureHiVram(); } }
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

    public void SetMaskBits(bool setMaskBit, bool checkMaskBit)
    {
        _maskSetBit = setMaskBit;
        _maskCheckBit = checkMaskBit;
    }

    public void AttachHdTexturePack(HdTexturePackStore? packStore)
    {
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
        _hdLookup.RecordUpload(x, y, w, h);
        PsxVramSurfaceOps.UploadVramRect(_vram, _hiVram, _hiMask, _hiScale, _canonical1x, _canonicalMask1x, x, y, w, h, pixels15);
    }

    public void CopyVramRect(int srcX, int srcY, int dstX, int dstY, int w, int h)
    {
        if (w <= 0 || h <= 0) return;
        EnsureHiVram();
        _hdLookup.Invalidate();
        PsxVramSurfaceOps.CopyVramRect(_vram, _hiVram, _hiMask, _hiScale, _canonical1x, _canonicalMask1x, srcX, srcY, dstX, dstY, w, h);
        _wideFrameOps.Add(new WideFrameOp
        {
            Kind = WideFrameOpKind.CopyVram, A = srcX & 1023, B = srcY & 511, C = dstX & 1023, D = dstY & 511, E = w, F = h
        });
    }

    public void FillDisplayRect(int x, int y, int w, int h, uint bgr24)
    {
        if (w <= 0 || h <= 0) return;
        EnsureHiVram();
        _hdLookup.Invalidate();
        PsxVramSurfaceOps.FillDisplayRect(_vram, _hiVram, _hiMask, _hiScale, _canonical1x, _canonicalMask1x, x, y, w, h, bgr24);
        _wideFrameOps.Add(new WideFrameOp { Kind = WideFrameOpKind.FillRect, A = x & 1023, B = y & 511, C = w, D = h, RawU32 = bgr24 });
    }

    public void SubmitPrimitive(uint gp0Opcode, ReadOnlySpan<uint> words, ushort texPage, int drawOffsetX, int drawOffsetY,
        int clipLeft, int clipTop, int clipRight, int clipBottom)
    {
        PrimitivesSubmitted++;
        EnsureHiVram();
        if (SoftwareCpuRasterizer.TrySubmitDirectPrimitive(gp0Opcode, words, texPage, drawOffsetX, drawOffsetY,
                clipLeft, clipTop, clipRight, clipBottom, _hiVram, _hiMask, _vram, _hiScale, _canonical1x, _canonicalMask1x,
                _texWinMaskX, _texWinMaskY, _texWinOffX, _texWinOffY, _maskSetBit, _maskCheckBit, _pgxp, _hdPack, _hdLookup,
                out WideBandOp wideOp))
        {
            _wideFrameOps.Add(wideOp.FrameOp);
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
        EnsureHiVram();
        return PsxVramSurfaceOps.TryReadVramRect(_vram, _hiVram, _hiMask, _hiScale, _canonical1x, _canonicalMask1x, x, y, w, h, destination);
    }

    public void RenderUiOverlay(ReadOnlySpan<byte> rgbaPixels, int width, int height, bool uploadPixels) { }
    public void RenderUiOverlay(ReadOnlySpan<byte> rgbaPixels, int width, int height, int dstX, int dstY, int canvasW, int canvasH, bool uploadPixels) { }
    public void Dispose() => PrimitivesSubmitted = 0;

    private void EnsureHiVram() => PsxVramSurfaceOps.EnsureHiVram(_vram, ref _hiVram, ref _hiMask, ref _hiScale, _scale);

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
}

internal static class PsxVramSurfaceOps
{
    public static int NormalizeScale(int s) => s <= 1 ? 1 : s <= 2 ? 2 : s <= 4 ? 4 : 8;
    public static ushort Rgb888To15(uint c) => (ushort)(((c >> 3) & 0x1F) | (((c >> 11) & 0x1F) << 5) | (((c >> 19) & 0x1F) << 10));
    public static ushort AbgrTo15(uint abgr) => (ushort)(((abgr >> 3) & 0x1F) | (((abgr >> 11) & 0x1F) << 5) | (((abgr >> 19) & 0x1F) << 10));

    public static void EnsureHiVram(ushort[] vram, ref uint[] hiVram, ref byte[] hiMask, ref int hiScale, int scale)
    {
        int expectedLen = PsxGpu.VramWidth * scale * PsxGpu.VramHeight * scale;
        if (hiScale == scale && hiVram.Length == expectedLen && hiMask.Length == expectedLen) return;
        hiScale = scale;
        int hiW = PsxGpu.VramWidth * hiScale;
        hiVram = new uint[expectedLen];
        hiMask = new byte[expectedLen];
        for (int vy = 0; vy < PsxGpu.VramHeight; vy++)
        {
            for (int vx = 0; vx < PsxGpu.VramWidth; vx++)
            {
                ushort p = vram[(vy * PsxGpu.VramWidth) + vx];
                WriteScaledPixel(hiVram, hiMask, vx, vy, SoftwareCpuRenderBackend.Rgb15Lut[p], (byte)((p >> 15) & 1), hiScale, hiW);
            }
        }
    }

    public static void EnsureCanonical1x(ushort[] vram, uint[] hiVram, byte[] hiMask, int hiScale, ref uint[]? canonical1x, ref byte[]? canonicalMask1x)
    {
        if (canonical1x is not null && canonicalMask1x is not null) return;
        int len = PsxGpu.VramWidth * PsxGpu.VramHeight;
        canonical1x = new uint[len];
        canonicalMask1x = new byte[len];
        int hiW = PsxGpu.VramWidth * Math.Max(1, hiScale);
        if (hiVram.Length == hiW * PsxGpu.VramHeight * Math.Max(1, hiScale))
        {
            for (int vy = 0; vy < PsxGpu.VramHeight; vy++)
            {
                int row1x = vy * PsxGpu.VramWidth, rowHi = (vy * hiScale) * hiW;
                for (int vx = 0; vx < PsxGpu.VramWidth; vx++)
                {
                    int idx = rowHi + (vx * hiScale);
                    canonical1x[row1x + vx] = hiVram[idx];
                    canonicalMask1x[row1x + vx] = hiMask[idx];
                }
            }
        }
        else
        {
            for (int i = 0; i < len; i++)
            {
                ushort p = vram[i];
                canonical1x[i] = SoftwareCpuRenderBackend.Rgb15Lut[p];
                canonicalMask1x[i] = (byte)((p >> 15) & 1);
            }
        }
    }

    public static void UploadVramRect(ushort[] vram, uint[] hiVram, byte[] hiMask, int hiScale, uint[]? canonical1x, byte[]? canonicalMask1x, int x, int y, int w, int h, ReadOnlySpan<ushort> pixels15)
    {
        int hiW = PsxGpu.VramWidth * hiScale;
        for (int row = 0, i = 0; row < h; row++)
        {
            int vy = (y + row) & 511, vramRow = vy * PsxGpu.VramWidth;
            for (int col = 0; col < w && i < pixels15.Length; col++, i++)
            {
                int vx = (x + col) & 1023;
                ushort p = pixels15[i];
                uint abgr = SoftwareCpuRenderBackend.Rgb15Lut[p];
                byte m = (byte)((p >> 15) & 1);
                vram[vramRow + vx] = p;
                WriteScaledPixel(hiVram, hiMask, vx, vy, abgr, m, hiScale, hiW);
                if (canonical1x is not null && canonicalMask1x is not null) { canonical1x[vramRow + vx] = abgr; canonicalMask1x[vramRow + vx] = m; }
            }
        }
    }

    public static void CopyVramRect(ushort[] vram, uint[] hiVram, byte[] hiMask, int hiScale, uint[]? canonical1x, byte[]? canonicalMask1x, int srcX, int srcY, int dstX, int dstY, int w, int h)
    {
        int sx = srcX & 1023, sy = srcY & 511, dx = dstX & 1023, dy = dstY & 511;
        CopyWrappedRect(vram, PsxGpu.VramWidth, PsxGpu.VramHeight, sx, sy, dx, dy, w, h);
        int hiW = PsxGpu.VramWidth * hiScale, hiH = PsxGpu.VramHeight * hiScale;
        CopyWrappedRect(hiVram, hiW, hiH, sx * hiScale, sy * hiScale, dx * hiScale, dy * hiScale, w * hiScale, h * hiScale);
        CopyWrappedRect(hiMask, hiW, hiH, sx * hiScale, sy * hiScale, dx * hiScale, dy * hiScale, w * hiScale, h * hiScale);
        if (canonical1x is not null && canonicalMask1x is not null)
        {
            CopyWrappedRect(canonical1x, PsxGpu.VramWidth, PsxGpu.VramHeight, sx, sy, dx, dy, w, h);
            CopyWrappedRect(canonicalMask1x, PsxGpu.VramWidth, PsxGpu.VramHeight, sx, sy, dx, dy, w, h);
        }
    }

    public static void FillDisplayRect(ushort[] vram, uint[] hiVram, byte[] hiMask, int hiScale, uint[]? canonical1x, byte[]? canonicalMask1x, int x, int y, int w, int h, uint bgr24)
    {
        ushort c15 = Rgb888To15(bgr24);
        uint abgr = 0xFF000000U | (bgr24 & 0xFFFFFFU);
        int hiW = PsxGpu.VramWidth * hiScale;
        for (int row = 0; row < h; row++)
        {
            int vy = (y + row) & 511, vramRow = vy * PsxGpu.VramWidth;
            for (int col = 0; col < w; col++)
            {
                int vx = (x + col) & 1023;
                vram[vramRow + vx] = c15;
                WriteScaledPixel(hiVram, hiMask, vx, vy, abgr, 0, hiScale, hiW);
                if (canonical1x is not null && canonicalMask1x is not null) { canonical1x[vramRow + vx] = abgr; canonicalMask1x[vramRow + vx] = 0; }
            }
        }
    }

    public static bool TryReadVramRect(ushort[] vram, uint[] hiVram, byte[] hiMask, int hiScale, uint[]? canonical1x, byte[]? canonicalMask1x, int x, int y, int w, int h, Span<ushort> destination)
    {
        if (w <= 0 || h <= 0 || destination.Length < w * h) return false;
        int hiW = PsxGpu.VramWidth * hiScale;
        bool useCanon = canonical1x is not null && canonicalMask1x is not null;
        for (int r = 0, i = 0; r < h; r++)
        {
            int vy = (y + r) & 511, row1x = vy * PsxGpu.VramWidth;
            for (int c = 0; c < w; c++, i++)
            {
                int vx = (x + c) & 1023;
                ushort raw15 = vram[row1x + vx];
                uint col = useCanon ? canonical1x![row1x + vx] : hiVram[((vy * hiScale) * hiW) + (vx * hiScale)];
                byte mask = useCanon ? canonicalMask1x![row1x + vx] : hiMask[((vy * hiScale) * hiW) + (vx * hiScale)];
                ushort rgb15 = col == SoftwareCpuRenderBackend.Rgb15Lut[raw15] ? (ushort)(raw15 & 0x7FFF) : AbgrTo15(col);
                destination[i] = (ushort)(rgb15 | (mask != 0 ? 0x8000 : 0));
            }
        }
        return true;
    }

    public static void PresentStandardFrame(ushort[] vram, uint[] hiVram, int hiScale, int dispX, int dispY, int dispW, int dispH, bool is24Bit, ref byte[] frame, out int frameW, out int frameH)
    {
        int s = is24Bit ? 1 : hiScale, outW = dispW * s, outH = dispH * s;
        if (frame.Length != outW * outH * 4) frame = new byte[outW * outH * 4];
        frameW = outW; frameH = outH;
        if (is24Bit)
        {
            for (int y = 0; y < dispH; y++)
            {
                int vy = (dispY + y) & 511;
                for (int x = 0; x < dispW; x++)
                {
                    int o = ((y * dispW) + x) * 4, bp = (dispX * 2) + (x * 3);
                    frame[o] = Vram24Byte(vram, vy, bp); frame[o + 1] = Vram24Byte(vram, vy, bp + 1); frame[o + 2] = Vram24Byte(vram, vy, bp + 2); frame[o + 3] = 0xFF;
                }
            }
            return;
        }
        int hiW = PsxGpu.VramWidth * s, hiH = PsxGpu.VramHeight * s;
        int sx = (dispX & 1023) * s, sy = (dispY & 511) * s;
        Span<uint> dstU32 = MemoryMarshal.Cast<byte, uint>(frame.AsSpan());
        for (int y = 0; y < outH; y++)
        {
            int srcRow = ((sy + y) % hiH) * hiW, dstRow = y * outW, firstTake = Math.Min(outW, hiW - sx);
            hiVram.AsSpan(srcRow + sx, firstTake).CopyTo(dstU32.Slice(dstRow, firstTake));
            if (firstTake < outW) hiVram.AsSpan(srcRow, outW - firstTake).CopyTo(dstU32.Slice(dstRow + firstTake, outW - firstTake));
        }
    }

    private static void CopyWrappedRect<T>(T[] buf, int stride, int height, int sx0, int sy0, int dx0, int dy0, int w, int h)
    {
        var tmp = new T[w * h];
        for (int r = 0, i = 0; r < h; r++)
            for (int c = 0; c < w; c++, i++)
                tmp[i] = buf[(((sy0 + r) % height) * stride) + ((sx0 + c) % stride)];
        for (int r = 0, i = 0; r < h; r++)
            for (int c = 0; c < w; c++, i++)
                buf[(((dy0 + r) % height) * stride) + ((dx0 + c) % stride)] = tmp[i];
    }

    private static void WriteScaledPixel(uint[] hiVram, byte[] hiMask, int vx, int vy, uint abgr, byte mask, int s, int hiW)
    {
        int x0 = vx * s, y0 = vy * s;
        for (int dy = 0; dy < s; dy++) { int st = ((y0 + dy) * hiW) + x0; hiVram.AsSpan(st, s).Fill(abgr); hiMask.AsSpan(st, s).Fill(mask); }
    }

    private static byte Vram24Byte(ushort[] vram, int vy, int bytePos)
    {
        ushort p = vram[(vy * PsxGpu.VramWidth) + ((bytePos >> 1) & 1023)];
        return (bytePos & 1) == 0 ? (byte)p : (byte)(p >> 8);
    }
}
