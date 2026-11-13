// @module: reStation.Core.Gpu.SoftwareCpuRenderBackend
// @purpose: Deterministic headless-safe IPsxRenderBackend oracle ("software"): VRAM mirror, fill/upload/copy, 15/24-bit display resolve to RGBA.
// @depends-on: reStation.Contracts
// @exports: SoftwareCpuRenderBackend
// @owner: devv-gpu-gl (T4) — T1 skeleton; 1x-8x PGXP scanline rasterization (SubmitPrimitive) lands in T4.

using reStation.Contracts;

namespace reStation.Core.Gpu;

public sealed class SoftwareCpuRenderBackend : IPsxRenderBackend
{
    private readonly ushort[] _vram = new ushort[PsxGpu.VramWidth * PsxGpu.VramHeight];
    private byte[] _frame = new byte[320 * 240 * 4];
    private int _frameW = 320, _frameH = 240;
    private int _scale = 1;

    public string BackendId => PsxRenderBackendIds.Software;
    public int ScaleFactor { get => _scale; set => _scale = NormalizeScale(value); }
    public int SurfaceWidth { get; private set; } = 320;
    public int SurfaceHeight { get; private set; } = 240;
    public long PrimitivesSubmitted { get; private set; }

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
        _frameW = width;
        _frameH = height;
        _frame = rgba[..(width * height * 4)].ToArray();
    }

    public void UploadVramRect(int x, int y, int w, int h, ReadOnlySpan<ushort> pixels15)
    {
        for (int row = 0, i = 0; row < h; row++)
            for (int col = 0; col < w && i < pixels15.Length; col++, i++)
                _vram[(((y + row) & 511) * PsxGpu.VramWidth) + ((x + col) & 1023)] = pixels15[i];
    }

    public void CopyVramRect(int srcX, int srcY, int dstX, int dstY, int w, int h)
    {
        for (int row = 0; row < h; row++)
            for (int col = 0; col < w; col++)
                _vram[(((dstY + row) & 511) * PsxGpu.VramWidth) + ((dstX + col) & 1023)] =
                    _vram[(((srcY + row) & 511) * PsxGpu.VramWidth) + ((srcX + col) & 1023)];
    }

    public void FillDisplayRect(int x, int y, int w, int h, uint bgr24)
    {
        ushort c15 = (ushort)(((bgr24 >> 3) & 0x1F) | (((bgr24 >> 11) & 0x1F) << 5) | (((bgr24 >> 19) & 0x1F) << 10));
        for (int row = 0; row < h; row++)
            for (int col = 0; col < w; col++)
                _vram[(((y + row) & 511) * PsxGpu.VramWidth) + ((x + col) & 1023)] = c15;
    }

    public void SubmitPrimitive(uint gp0Opcode, ReadOnlySpan<uint> words, ushort texPage, int drawOffsetX, int drawOffsetY,
        int clipLeft, int clipTop, int clipRight, int clipBottom) => PrimitivesSubmitted++;

    public void EndFrameAndPresent(int dispX, int dispY, int dispW, int dispH, bool is24Bit, bool widescreen16By9)
    {
        int w = Math.Clamp(dispW, 1, PsxGpu.VramWidth), h = Math.Clamp(dispH, 1, PsxGpu.VramHeight);
        if (_frame.Length != w * h * 4) _frame = new byte[w * h * 4];
        _frameW = w;
        _frameH = h;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int o = ((y * w) + x) * 4;
                int vy = (dispY + y) & 511;
                if (is24Bit)
                {
                    int bytePos = (dispX * 2) + (x * 3);
                    _frame[o] = Vram24Byte(vy, bytePos); _frame[o + 1] = Vram24Byte(vy, bytePos + 1); _frame[o + 2] = Vram24Byte(vy, bytePos + 2);
                }
                else
                {
                    ushort p = _vram[(vy * PsxGpu.VramWidth) + ((dispX + x) & 1023)];
                    _frame[o] = (byte)((p & 0x1F) << 3); _frame[o + 1] = (byte)(((p >> 5) & 0x1F) << 3); _frame[o + 2] = (byte)(((p >> 10) & 0x1F) << 3);
                }
                _frame[o + 3] = 0xFF;
            }
        }
    }

    public byte[] CaptureRgba(out int width, out int height)
    {
        width = _frameW;
        height = _frameH;
        return (byte[])_frame.Clone();
    }

    public void Dispose() => PrimitivesSubmitted = 0;

    private byte Vram24Byte(int vy, int bytePos)
    {
        ushort p = _vram[(vy * PsxGpu.VramWidth) + ((bytePos >> 1) & 1023)];
        return (bytePos & 1) == 0 ? (byte)p : (byte)(p >> 8);
    }

    private static int NormalizeScale(int s) => s <= 1 ? 1 : s <= 2 ? 2 : s <= 4 ? 4 : 8;
}
