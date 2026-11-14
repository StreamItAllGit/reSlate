// @module: reStation.Core.Gpu.PsxGpu.Scanout
// @purpose: Backbuffer scanout origin tracking (LastDrawnX/Y), PGXP frame epoch reset, and sub-tick FillRect/pass bounds tracking for PsxGpu.
// @depends-on: reStation.Contracts, reStation.Core.Gte
// @exports: PsxGpu (partial)

namespace reStation.Core.Gpu;

public sealed partial class PsxGpu
{
    private bool _hasFrameBounds;
    private bool _setFromFillThisFrame;
    private int _frameMinX, _frameMinY, _frameMaxX, _frameMaxY;

    /// <summary>Total rasterized GP0/VRAM draw operations recorded since the previous <see cref="PresentFrame(bool)"/>.</summary>
    public int FrameDrawOps { get; private set; }
    /// <summary>VRAM X coordinate of the most recent full-screen draw/clear area (e.g. active backbuffer origin).</summary>
    public int LastDrawnX { get; private set; }
    /// <summary>VRAM Y coordinate of the most recent full-screen draw/clear area (e.g. 0 or 240).</summary>
    public int LastDrawnY { get; private set; }
    /// <summary>True once a screen-sized FillRect or draw pass has established <see cref="LastDrawnX"/>/<see cref="LastDrawnY"/>.</summary>
    public bool HasLastDrawnOrigin { get; private set; }

    /// <summary>Resets transient GPU draw command state at the start of an interpolated sub-tick pass.</summary>
    public void BeginSubTickFrame()
    {
        ResetCommandFifo();
        _vramWordsLeft = _uploadCount = 0;
        _polyHasFirst = _polyExpectingColor = false;
        _hasFrameBounds = _setFromFillThisFrame = false;
    }

    /// <summary>Resolves the current display area through the backend using GP1 DisplayX/Y.</summary>
    public void PresentFrame() => PresentFrame(preferLastDrawnOrigin: false);

    /// <summary>
    /// Resolves the current display area through the backend and advances the PGXP frame epoch.
    /// When <paramref name="preferLastDrawnOrigin"/> is true, prefers the most recently drawn backbuffer page over GP1 DisplayX/Y.
    /// </summary>
    public void PresentFrame(bool preferLastDrawnOrigin)
    {
        FinalizeFrameOriginBounds();
        int ox = preferLastDrawnOrigin && HasLastDrawnOrigin ? LastDrawnX : DisplayX;
        int oy = preferLastDrawnOrigin && HasLastDrawnOrigin ? LastDrawnY : DisplayY;
        Backend.EndFrameAndPresent(ox, oy, DisplayWidth, DisplayHeight, Is24BitDisplay, Settings.IsWidescreen16By9);
        Pgxp.ClearFrame();
        FrameDrawOps = 0;
        _hasFrameBounds = _setFromFillThisFrame = false;
        FramesPresented++;
    }

    private void ResetScanoutTracking()
    {
        FrameDrawOps = 0;
        LastDrawnX = LastDrawnY = 0;
        HasLastDrawnOrigin = _hasFrameBounds = _setFromFillThisFrame = false;
        Pgxp.ClearFrame();
    }

    private void RecordFillBounds(int x, int y, int w, int h)
    {
        FrameDrawOps++;
        ExtendFrameBounds(x, y, x + w, y + h);
        if (w is >= 128 and <= 640 && h is >= 120 and <= 480)
        {
            LastDrawnX = Math.Clamp(x & ~0xF, 0, VramWidth - 1);
            LastDrawnY = Math.Clamp(y & ~0xF, 0, VramHeight - 1);
            HasLastDrawnOrigin = true;
            _setFromFillThisFrame = true;
        }
    }

    private void RecordPrimitiveBounds(uint op, ReadOnlySpan<uint> words)
    {
        if (op is >= 0x20 and <= 0x3F)
        {
            bool shaded = (op & 0x10U) != 0, quad = (op & 0x08U) != 0, textured = (op & 0x04U) != 0;
            int n = quad ? 4 : 3;
            int idx = 1;
            if (words.Length < PsxPrimitiveDecoder.ExpectedWords(op)) return;
            for (int i = 0; i < n; i++)
            {
                if (shaded && i > 0) idx++;
                uint xy = words[idx++];
                int vx = PsxPrimitiveDecoder.SignExtend11(xy) + DrawOffsetX;
                int vy = PsxPrimitiveDecoder.SignExtend11(xy >> 16) + DrawOffsetY;
                ExtendFrameBounds(vx, vy, vx + 1, vy + 1);
                if (textured) idx++;
            }
        }
        else if (op is >= 0x60 and <= 0x7F &&
                 PsxPrimitiveDecoder.DecodeRect(op, words, TexPage, DrawOffsetX, DrawOffsetY, out PsxRect rect, out _))
        {
            ExtendFrameBounds(rect.X, rect.Y, rect.X + rect.Width, rect.Y + rect.Height);
        }
    }

    private void ExtendFrameBounds(int minX, int minY, int maxX, int maxY)
    {
        if (!_hasFrameBounds)
        {
            _hasFrameBounds = true;
            _frameMinX = minX;
            _frameMinY = minY;
            _frameMaxX = maxX;
            _frameMaxY = maxY;
            return;
        }
        if (minX < _frameMinX) _frameMinX = minX;
        if (minY < _frameMinY) _frameMinY = minY;
        if (maxX > _frameMaxX) _frameMaxX = maxX;
        if (maxY > _frameMaxY) _frameMaxY = maxY;
    }

    private void FinalizeFrameOriginBounds()
    {
        if (_setFromFillThisFrame || !_hasFrameBounds) return;
        int spanW = _frameMaxX - _frameMinX, spanH = _frameMaxY - _frameMinY;
        if (spanW < 128 || spanH < 120) return;

        int drawW = DrawAreaRight - DrawAreaLeft + 1;
        int drawH = DrawAreaBottom - DrawAreaTop + 1;
        bool validDrawArea = drawW is >= 128 and <= 640 && drawH is >= 120 and <= 480;
        int rx = validDrawArea ? DrawAreaLeft : _frameMinX;
        int ry = validDrawArea ? DrawAreaTop : _frameMinY;
        LastDrawnX = Math.Clamp(rx & ~0xF, 0, VramWidth - 1);
        LastDrawnY = Math.Clamp(ry & ~0xF, 0, VramHeight - 1);
        HasLastDrawnOrigin = true;
    }
}
