// @module: reStation.Core.Gpu.PsxGpu
// @purpose: PS1 GPU unit: 1024x512 15-bit VRAM mirror, GP0/GP1 state registers, VBlank timing, delegating rendering to IPsxRenderBackend.
// @depends-on: reStation.Contracts, reStation.Core, reStation.Core.Gte
// @exports: PsxGpu
// @owner: devv-gpu-gl (T4) — T1 skeleton; constructor + public members are cross-slice LOCKED (PsxCpu bus/scheduler + Player consume them).

using reStation.Contracts;
using reStation.Core.Gte;

namespace reStation.Core.Gpu;

public sealed class PsxGpu
{
    public const int VramWidth = 1024;
    public const int VramHeight = 512;
    public const long CyclesPerVBlank = 564_480;
    private const uint SnapshotMarker = 0x55504752U; // "RGPU"

    private uint _gpuStat = 0x14802000U;
    private uint _gpuRead;
    private long _lastVBlankCycle;

    public PsxGpu(RecompSettings settings, PgxpTracker pgxp, IPsxRenderBackend backend)
    {
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Pgxp = pgxp ?? throw new ArgumentNullException(nameof(pgxp));
        Backend = backend ?? throw new ArgumentNullException(nameof(backend));
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
    public long PrimitiveCount { get; private set; }
    public long FramesPresented { get; private set; }
    public long NextVBlankCycle => _lastVBlankCycle + CyclesPerVBlank;

    /// <summary>MMIO 0x1F801814 read (GPUSTAT).</summary>
    public uint ReadGpuStat() => _gpuStat | 0x1C000000U;

    /// <summary>MMIO 0x1F801810 read (GPUREAD / VRAM->CPU transfers).</summary>
    public uint ReadGpuRead() => _gpuRead;

    /// <summary>MMIO 0x1F801810 write / DMA2: GP0 rendering + VRAM transfer command stream.</summary>
    public void WriteGp0(uint word)
    {
        switch (word >> 24)
        {
            case 0xE1: TexPage = (ushort)(word & 0x7FFU); break;
            case 0xE3: DrawAreaLeft = (int)(word & 0x3FFU); DrawAreaTop = (int)((word >> 10) & 0x3FFU); break;
            case 0xE4: DrawAreaRight = (int)(word & 0x3FFU); DrawAreaBottom = (int)((word >> 10) & 0x3FFU); break;
            case 0xE5:
                DrawOffsetX = SignExtend11((int)(word & 0x7FFU));
                DrawOffsetY = SignExtend11((int)((word >> 11) & 0x7FFU));
                break;
        }
    }

    /// <summary>MMIO 0x1F801814 write: GP1 display control.</summary>
    public void WriteGp1(uint value)
    {
        switch ((value >> 24) & 0x3FU)
        {
            case 0x00: Reset(); break;
            case 0x03: DisplayDisabled = (value & 1U) != 0; break;
            case 0x05: DisplayX = (int)(value & 0x3FFU); DisplayY = (int)((value >> 10) & 0x1FFU); break;
            case 0x08:
                DisplayWidth = ((value >> 6) & 1U) != 0 ? 368 : (value & 3U) switch { 0 => 256, 1 => 320, 2 => 512, _ => 640 };
                DisplayHeight = (value & 0x04U) != 0 ? 480 : 240;
                Is24BitDisplay = (value & 0x10U) != 0;
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

    /// <summary>Resolves the current display area through the backend (called by the host at VBlank).</summary>
    public void PresentFrame()
    {
        Backend.EndFrameAndPresent(DisplayX, DisplayY, DisplayWidth, DisplayHeight, Is24BitDisplay, Settings.IsWidescreen16By9);
        FramesPresented++;
    }

    public void Reset()
    {
        DrawAreaLeft = 0; DrawAreaTop = 0; DrawAreaRight = VramWidth - 1; DrawAreaBottom = VramHeight - 1;
        DrawOffsetX = 0; DrawOffsetY = 0; TexPage = 0;
        DisplayDisabled = true;
        _gpuRead = 0;
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
        Backend.UploadVramRect(0, 0, VramWidth, VramHeight, Vram);
    }

    private static int SignExtend11(int v) => (v & 0x400) != 0 ? v | ~0x7FF : v;
}
