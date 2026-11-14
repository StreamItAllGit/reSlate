// @module: reStation.Core.Gpu.PsxGpu
// @purpose: PS1 GPU unit: 1024x512 15-bit VRAM mirror (A0/80/C0 readback parity), GP0 command FIFO & packet decoder, GP1 control, VBlank timing, delegating rendering to IPsxRenderBackend.
// @depends-on: reStation.Contracts, reStation.Core, reStation.Core.Gte
// @exports: PsxGpu
// @owner: devv-gpu-gl (T4) — preserves all T1 locked members and snapshot binary format.

using reStation.Contracts;
using reStation.Core.Gte;

namespace reStation.Core.Gpu;

public sealed partial class PsxGpu
{
    public const int VramWidth = 1024;
    public const int VramHeight = 512;
    public const long CyclesPerVBlank = 564_480;
    private const uint SnapshotMarker = 0x55504752U; // "RGPU"

    private enum Gp0Mode { Idle, ImageLoad, ImageStore, Polyline }

    private readonly uint[] _cmdBuf = new uint[16];
    private ushort[] _uploadScratch = Array.Empty<ushort>();
    private ushort[] _copyScratch = Array.Empty<ushort>();
    private int _cmdLen, _cmdExpected;
    private Gp0Mode _gp0Mode = Gp0Mode.Idle;
    private int _vramX, _vramY, _vramW, _vramH, _vramCurX, _vramCurY, _vramWordsLeft, _uploadCount;
    private uint _polyOp, _polyPrevColor, _polyPrevXy, _polyPendingColor;
    private bool _polyHasFirst, _polyExpectingColor;
    private uint _gpuStat = 0x14802000U;
    private uint _gpuRead;
    private long _lastVBlankCycle;

    public PsxGpu(RecompSettings settings, PgxpTracker pgxp, IPsxRenderBackend backend)
    {
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Pgxp = pgxp ?? throw new ArgumentNullException(nameof(pgxp));
        Backend = backend ?? throw new ArgumentNullException(nameof(backend));
        (backend as IPsxGpuBackendHooks)?.AttachPgxp(pgxp);
    }

    public RecompSettings Settings { get; }
    public PgxpTracker Pgxp { get; }
    public IPsxRenderBackend Backend { get; }
    public ushort[] Vram { get; } = new ushort[VramWidth * VramHeight];

    public int DisplayX { get; private set; }
    public int DisplayY { get; private set; }
    public int DisplayWidth { get; private set; } = 320;
    public int DisplayHeight { get; private set; } = 240;
    public bool Is24BitDisplay { get; private set; }
    public bool DisplayDisabled { get; private set; } = true;
    public ushort TexPage { get; private set; }
    public int DrawAreaLeft { get; private set; }
    public int DrawAreaTop { get; private set; }
    public int DrawAreaRight { get; private set; } = VramWidth - 1;
    public int DrawAreaBottom { get; private set; } = VramHeight - 1;
    public int DrawOffsetX { get; private set; }
    public int DrawOffsetY { get; private set; }
    public int TextureWindowMaskX { get; private set; }
    public int TextureWindowMaskY { get; private set; }
    public int TextureWindowOffsetX { get; private set; }
    public int TextureWindowOffsetY { get; private set; }
    public bool MaskSetBit { get; private set; }
    public bool MaskCheckBit { get; private set; }
    public bool IsInterlaced { get; private set; }
    public bool IsPalMode { get; private set; }
    public bool IrqPending { get; private set; }
    public long PrimitiveCount { get; private set; }
    public long FramesPresented { get; private set; }
    public bool SkipPolygonRasterization { get; set; }
    public long NextVBlankCycle => _lastVBlankCycle + CyclesPerVBlank;
    public int OutputWidth => Is24BitDisplay ? Math.Max(1, DisplayWidth) : Math.Max(1, DisplayWidth * Backend.ScaleFactor);
    public int OutputHeight => Is24BitDisplay ? Math.Max(1, DisplayHeight) : Math.Max(1, DisplayHeight * Backend.ScaleFactor);

    /// <summary>MMIO 0x1F801814 read (GPUSTAT).</summary>
    public uint ReadGpuStat() => _gpuStat | 0x1C000000U | (IrqPending ? (1U << 24) : 0U);

    /// <summary>MMIO 0x1F801810 read (GPUREAD / VRAM->CPU transfers).</summary>
    public uint ReadGpuRead()
    {
        if (_gp0Mode == Gp0Mode.ImageStore)
        {
            ushort p0 = ReadSinglePixel();
            ushort p1 = ReadSinglePixel();
            _gpuRead = (uint)(p0 | (p1 << 16));
            if (--_vramWordsLeft <= 0) _gp0Mode = Gp0Mode.Idle;
        }
        return _gpuRead;
    }

    /// <summary>MMIO 0x1F801810 write / DMA2: GP0 rendering + VRAM transfer command stream.</summary>
    public void WriteGp0(uint word)
    {
        if (_gp0Mode == Gp0Mode.ImageLoad) { ConsumeImageLoadWord(word); return; }
        if (_gp0Mode == Gp0Mode.Polyline) { ConsumePolylineWord(word); return; }
        if (_cmdExpected == 0)
        {
            uint cmd = word >> 24;
            if (cmd is (>= 0x48 and <= 0x4F) or (>= 0x58 and <= 0x5F)) { BeginPolyline(cmd, word); return; }
            _cmdExpected = PsxPrimitiveDecoder.ExpectedWords(cmd);
            _cmdLen = 0;
            if (_cmdExpected == 0) return;
        }
        _cmdBuf[_cmdLen++] = word;
        if (_cmdLen >= _cmdExpected)
        {
            ExecuteGp0Command();
            _cmdLen = _cmdExpected = 0;
        }
    }

    /// <summary>MMIO 0x1F801814 write: GP1 display control.</summary>
    public void WriteGp1(uint value)
    {
        uint op = (value >> 24) & 0x3FU;
        switch (op)
        {
            case 0x00: Reset(); break;
            case 0x01: ResetCommandFifo(); break;
            case 0x02: IrqPending = false; break;
            case 0x03: DisplayDisabled = (value & 1U) != 0; RefreshGpuStatBits(); break;
            case 0x04: _gpuStat = (_gpuStat & ~0x60000000U) | ((value & 3U) << 29); break;
            case 0x05: DisplayX = (int)(value & 0x3FFU); DisplayY = (int)((value >> 10) & 0x1FFU); break;
            case 0x08:
                DisplayWidth = ((value >> 6) & 1U) != 0 ? 368 : (value & 3U) switch { 0 => 256, 1 => 320, 2 => 512, _ => 640 };
                DisplayHeight = (value & 0x04U) != 0 ? 480 : 240;
                IsPalMode = (value & 0x08U) != 0;
                Is24BitDisplay = (value & 0x10U) != 0;
                IsInterlaced = (value & 0x20U) != 0;
                _gpuStat = (_gpuStat & ~0x007F0000U) | ((value & 0x3FU) << 17) | (((value >> 6) & 1U) << 16);
                RefreshGpuStatBits();
                break;
            case 0x10:
                _gpuRead = (value & 0x7U) switch
                {
                    2 => (uint)(TextureWindowMaskX | (TextureWindowMaskY << 5) | (TextureWindowOffsetX << 10) | (TextureWindowOffsetY << 15)),
                    3 => (uint)(DrawAreaLeft | (DrawAreaTop << 10)),
                    4 => (uint)(DrawAreaRight | (DrawAreaBottom << 10)),
                    5 => (uint)((DrawOffsetX & 0x7FF) | ((DrawOffsetY & 0x7FF) << 11)),
                    7 => 2U,
                    _ => _gpuRead
                };
                break;
        }
    }

    /// <summary>Advances VBlank timing; <paramref name="vblankEdge"/> is true once per 564,480 cycles.</summary>
    public void TickTo(long totalCycles, out bool vblankEdge)
    {
        vblankEdge = totalCycles - _lastVBlankCycle >= CyclesPerVBlank;
        if (!vblankEdge) return;
        _lastVBlankCycle = totalCycles;
        _gpuStat ^= 0x80000000U;
    }

    public void Reset()
    {
        ResetCommandFifo();
        ResetScanoutTracking();
        DrawAreaLeft = 0; DrawAreaTop = 0; DrawAreaRight = VramWidth - 1; DrawAreaBottom = VramHeight - 1;
        DrawOffsetX = 0; DrawOffsetY = 0; TexPage = 0;
        TextureWindowMaskX = TextureWindowMaskY = TextureWindowOffsetX = TextureWindowOffsetY = 0;
        (Backend as IPsxGpuBackendHooks)?.SetTextureWindow(0, 0, 0, 0);
        MaskSetBit = MaskCheckBit = false;
        DisplayX = DisplayY = 0; DisplayWidth = 320; DisplayHeight = 240;
        Is24BitDisplay = IsInterlaced = IsPalMode = IrqPending = false;
        DisplayDisabled = true;
        _gpuRead = 0;
        _gpuStat = 0x14802000U;
    }

    public void WriteSnapshotState(BinaryWriter w)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.Write(SnapshotMarker);
        w.Write(DisplayX); w.Write(DisplayY); w.Write(DisplayWidth); w.Write(DisplayHeight);
        w.Write(Is24BitDisplay); w.Write(DisplayDisabled); w.Write(TexPage);
        w.Write(DrawAreaLeft); w.Write(DrawAreaTop); w.Write(DrawAreaRight); w.Write(DrawAreaBottom);
        w.Write(DrawOffsetX); w.Write(DrawOffsetY); w.Write(_gpuStat); w.Write(_lastVBlankCycle);
        foreach (ushort px in Vram) w.Write(px);
    }

    public void ReadSnapshotState(BinaryReader r)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (r.ReadUInt32() != SnapshotMarker) throw new InvalidDataException("Snapshot GPU marker mismatch.");
        DisplayX = r.ReadInt32(); DisplayY = r.ReadInt32(); DisplayWidth = r.ReadInt32(); DisplayHeight = r.ReadInt32();
        Is24BitDisplay = r.ReadBoolean(); DisplayDisabled = r.ReadBoolean(); TexPage = r.ReadUInt16();
        DrawAreaLeft = r.ReadInt32(); DrawAreaTop = r.ReadInt32(); DrawAreaRight = r.ReadInt32(); DrawAreaBottom = r.ReadInt32();
        DrawOffsetX = r.ReadInt32(); DrawOffsetY = r.ReadInt32(); _gpuStat = r.ReadUInt32(); _lastVBlankCycle = r.ReadInt64();
        for (int i = 0; i < Vram.Length; i++) Vram[i] = r.ReadUInt16();
        ResetCommandFifo();
        Backend.UploadVramRect(0, 0, VramWidth, VramHeight, Vram);
    }

    private void ExecuteGp0Command()
    {
        uint op = _cmdBuf[0] >> 24;
        if (op == 0x02) { if (!SkipPolygonRasterization) ExecuteFillRect(); return; }
        if (op == 0x1F) { IrqPending = true; return; }
        if (op is >= 0x20 and <= 0x7F)
        {
            if (!SkipPolygonRasterization)
            {
                PrimitiveCount++;
                FrameDrawOps++;
                RecordPrimitiveBounds(op, _cmdBuf.AsSpan(0, _cmdLen));
                Backend.SubmitPrimitive(op, _cmdBuf.AsSpan(0, _cmdLen), TexPage, DrawOffsetX, DrawOffsetY, DrawAreaLeft, DrawAreaTop, DrawAreaRight, DrawAreaBottom);
            }
            return;
        }
        if (op is >= 0x80 and <= 0x9F) { ExecuteCopyVram(); return; }
        if (op is >= 0xA0 and <= 0xBF) { BeginVramTransfer(Gp0Mode.ImageLoad, _cmdBuf[1], _cmdBuf[2]); return; }
        if (op is >= 0xC0 and <= 0xDF) { BeginVramTransfer(Gp0Mode.ImageStore, _cmdBuf[1], _cmdBuf[2]); return; }
        switch (op)
        {
            case 0xE1: TexPage = (ushort)(_cmdBuf[0] & 0x7FFU); RefreshGpuStatBits(); break;
            case 0xE2:
                TextureWindowMaskX = (int)(_cmdBuf[0] & 0x1FU); TextureWindowMaskY = (int)((_cmdBuf[0] >> 5) & 0x1FU);
                TextureWindowOffsetX = (int)((_cmdBuf[0] >> 10) & 0x1FU); TextureWindowOffsetY = (int)((_cmdBuf[0] >> 15) & 0x1FU);
                (Backend as IPsxGpuBackendHooks)?.SetTextureWindow(TextureWindowMaskX, TextureWindowMaskY, TextureWindowOffsetX, TextureWindowOffsetY);
                break;
            case 0xE3: DrawAreaLeft = (int)(_cmdBuf[0] & 0x3FFU); DrawAreaTop = (int)((_cmdBuf[0] >> 10) & 0x3FFU); break;
            case 0xE4: DrawAreaRight = (int)(_cmdBuf[0] & 0x3FFU); DrawAreaBottom = (int)((_cmdBuf[0] >> 10) & 0x3FFU); break;
            case 0xE5: DrawOffsetX = PsxPrimitiveDecoder.SignExtend11(_cmdBuf[0]); DrawOffsetY = PsxPrimitiveDecoder.SignExtend11(_cmdBuf[0] >> 11); break;
            case 0xE6: MaskSetBit = (_cmdBuf[0] & 1U) != 0; MaskCheckBit = (_cmdBuf[0] & 2U) != 0; RefreshGpuStatBits(); break;
        }
    }

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
                        Vram[((( _vramY + row) & 511) * VramWidth) + ((_vramX + col) & 1023)] = _copyScratch[i];
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

    private void BeginPolyline(uint op, uint firstColor)
    {
        _gp0Mode = Gp0Mode.Polyline;
        _polyOp = op & ~0x08U;
        _polyPrevColor = firstColor;
        _polyHasFirst = false;
        _polyExpectingColor = false;
    }

    private void ConsumePolylineWord(uint word)
    {
        if (!_polyHasFirst) { _polyPrevXy = word; _polyHasFirst = true; _polyExpectingColor = (_polyOp & 0x10U) != 0; return; }
        if ((word & 0xF000F000U) == 0x50005000U) { _gp0Mode = Gp0Mode.Idle; return; }
        if (_polyExpectingColor) { _polyPendingColor = word; _polyExpectingColor = false; return; }
        bool shaded = (_polyOp & 0x10U) != 0;
        Span<uint> seg = stackalloc uint[4];
        seg[0] = _polyPrevColor; seg[1] = _polyPrevXy;
        if (shaded) { seg[2] = _polyPendingColor; seg[3] = word; _polyPrevColor = _polyPendingColor; _polyExpectingColor = true; }
        else seg[2] = word;
        _polyPrevXy = word;
        if (!SkipPolygonRasterization)
        {
            PrimitiveCount++;
            FrameDrawOps++;
            Backend.SubmitPrimitive(_polyOp, seg[..(shaded ? 4 : 3)], TexPage, DrawOffsetX, DrawOffsetY, DrawAreaLeft, DrawAreaTop, DrawAreaRight, DrawAreaBottom);
        }
    }

    private void ResetCommandFifo() { _cmdLen = _cmdExpected = 0; _gp0Mode = Gp0Mode.Idle; }

    private void RefreshGpuStatBits() =>
        _gpuStat = (_gpuStat & ~0x00801FFFU) | (TexPage & 0x7FFU) | (MaskSetBit ? 1U << 11 : 0U) |
                   (MaskCheckBit ? 1U << 12 : 0U) | (DisplayDisabled ? 1U << 23 : 0U);

    private static int WrapDim(uint raw, int max) => raw == 0 ? 0 : (int)(((raw - 1U) & (uint)(max - 1)) + 1U);
    private static ushort Rgb888To15(uint c) => (ushort)(((c >> 3) & 0x1F) | (((c >> 11) & 0x1F) << 5) | (((c >> 19) & 0x1F) << 10));
}
