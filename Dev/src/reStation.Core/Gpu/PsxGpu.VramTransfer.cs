// @module: reStation.Core.Gpu.PsxGpu.VramTransfer
// @purpose: PS1 GP0 VRAM transfer (A0h/C0h), FillRect (02h), and CopyVram (80h) state machine extracted from PsxGpu.
// @depends-on: reStation.Core.Gpu.PsxGpu
// @exports: PsxGpu (VramTransfer partial)

namespace reStation.Core.Gpu;

public sealed partial class PsxGpu
{
    private void ExecuteFillRect()
    {
        int x = (int)(_cmdBuf[1] & 0x3F0U), y = (int)((_cmdBuf[1] >> 16) & 0x1FFU);
        int rawW = (int)(_cmdBuf[2] & 0x7FFU), rawH = (int)((_cmdBuf[2] >> 16) & 0x3FFU);
        int w = rawW == 0 ? 0 : Math.Min(VramWidth, (rawW + 0x0F) & ~0x0F);
        int h = rawH == 0 ? 0 : Math.Min(VramHeight, rawH);
        if (w <= 0 || h <= 0) return;
        ushort c15 = Rgb888To15(_cmdBuf[0]);
        for (int row = 0; row < h; row++)
        {
            int vy = ((y + row) & 511) * VramWidth;
            for (int col = 0; col < w; col++) Vram[vy + ((x + col) & 1023)] = c15;
        }
        Backend.FillDisplayRect(x, y, w, h, _cmdBuf[0] & 0xFFFFFFU);
        RecordFillBounds(x, y, w, h);
    }

    private void ExecuteCopyVram()
    {
        int sx = (int)(_cmdBuf[1] & 0x3FFU), sy = (int)((_cmdBuf[1] >> 16) & 0x1FFU);
        int dx = (int)(_cmdBuf[2] & 0x3FFU), dy = (int)((_cmdBuf[2] >> 16) & 0x1FFU);
        int w = WrapDim(_cmdBuf[3] & 0xFFFFU, VramWidth), h = WrapDim(_cmdBuf[3] >> 16, VramHeight);
        if (w <= 0 || h <= 0) return;
        int total = w * h;
        if (_copyScratch.Length < total) _copyScratch = new ushort[total];
        for (int row = 0, i = 0; row < h; row++)
        {
            int svy = ((sy + row) & 511) * VramWidth;
            for (int col = 0; col < w; col++, i++) _copyScratch[i] = Vram[svy + ((sx + col) & 1023)];
        }
        for (int row = 0, i = 0; row < h; row++)
        {
            int dvy = ((dy + row) & 511) * VramWidth;
            for (int col = 0; col < w; col++, i++) Vram[dvy + ((dx + col) & 1023)] = _copyScratch[i];
        }
        Backend.CopyVramRect(sx, sy, dx, dy, w, h);
        FrameDrawOps++;
    }

    private void BeginVramTransfer(Gp0Mode mode, uint xy, uint wh)
    {
        _vramX = (int)(xy & 0x3FFU); _vramY = (int)((xy >> 16) & 0x1FFU);
        _vramW = WrapDim(wh & 0xFFFFU, VramWidth); _vramH = WrapDim(wh >> 16, VramHeight);
        _vramCurX = _vramCurY = _uploadCount = 0;
        int totalPixels = _vramW * _vramH;
        _vramWordsLeft = (totalPixels + 1) / 2;
        if (_vramWordsLeft <= 0) return;
        if (mode == Gp0Mode.ImageLoad && _uploadScratch.Length < totalPixels) _uploadScratch = new ushort[totalPixels];
        if (mode == Gp0Mode.ImageStore && Backend is IPsxGpuBackendHooks hooks)
        {
            if (_copyScratch.Length < totalPixels) _copyScratch = new ushort[totalPixels];
            if (hooks.TryReadVramRect(_vramX, _vramY, _vramW, _vramH, _copyScratch.AsSpan(0, totalPixels)))
                for (int row = 0, i = 0; row < _vramH; row++)
                    for (int col = 0; col < _vramW; col++, i++)
                        Vram[(((_vramY + row) & 511) * VramWidth) + ((_vramX + col) & 1023)] = _copyScratch[i];
        }
        _gp0Mode = mode;
    }

    private void ConsumeImageLoadWord(uint word)
    {
        WriteSinglePixel((ushort)(word & 0xFFFFU));
        WriteSinglePixel((ushort)(word >> 16));
        if (--_vramWordsLeft <= 0)
        {
            _gp0Mode = Gp0Mode.Idle;
            Backend.UploadVramRect(_vramX, _vramY, _vramW, _vramH, _uploadScratch.AsSpan(0, _uploadCount));
            FrameDrawOps++;
        }
    }

    private void WriteSinglePixel(ushort c)
    {
        if (_vramCurY >= _vramH) return;
        Vram[(((_vramY + _vramCurY) & 511) * VramWidth) + ((_vramX + _vramCurX) & 1023)] = c;
        _uploadScratch[_uploadCount++] = c;
        if (++_vramCurX >= _vramW) { _vramCurX = 0; _vramCurY++; }
    }

    private ushort ReadSinglePixel()
    {
        if (_vramCurY >= _vramH) return 0;
        ushort c = Vram[(((_vramY + _vramCurY) & 511) * VramWidth) + ((_vramX + _vramCurX) & 1023)];
        if (++_vramCurX >= _vramW) { _vramCurX = 0; _vramCurY++; }
        return c;
    }

    private static int WrapDim(uint raw, int max) => raw == 0 ? 0 : (int)(((raw - 1U) & (uint)(max - 1)) + 1U);
    private static ushort Rgb888To15(uint c) => (ushort)(((c >> 3) & 0x1F) | (((c >> 11) & 0x1F) << 5) | (((c >> 19) & 0x1F) << 10));
}
