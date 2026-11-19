// @module: reStation.Core.Cpu.PsxCpu.Snapshot
// @purpose: Binary quick-save snapshot serialization/deserialization for PsxCpu and attached PS1 hardware units.
// @depends-on: System.IO, reStation.Core.Cpu.PsxCpu
// @exports: PsxCpu (Snapshot partial)

namespace reStation.Core.Cpu;

public sealed partial class PsxCpu
{
    private const uint SnapshotMarker = 0x55504352U; // "RCPU"

    public void WriteSnapshotState(BinaryWriter w)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.Write(SnapshotMarker);
        w.Write(ActiveExeId); w.Write(Pc); w.Write(Hi); w.Write(Lo); w.Write(Cop0Sr); w.Write(Cop0Cause); w.Write(Cop0Epc);
        w.Write(TotalCycles); w.Write(FrameCount);
        for (int i = 0; i < 32; i++) w.Write(R[i]);
        Bus.WriteSnapshotState(w);
        Gte.WriteSnapshotState(w);
        TimersAndDma.WriteSnapshotState(w);
        Joypad.WriteSnapshotState(w);
        CdRom.WriteSnapshotState(w);
        Gpu.WriteSnapshotState(w);
        Spu.WriteSnapshotState(w);
    }

    public void ReadSnapshotState(BinaryReader r)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (r.ReadUInt32() != SnapshotMarker) throw new InvalidDataException("Snapshot CPU marker mismatch.");
        ActiveExeId = r.ReadInt32(); Pc = r.ReadUInt32(); Hi = r.ReadUInt32(); Lo = r.ReadUInt32();
        Cop0Sr = r.ReadUInt32(); Cop0Cause = r.ReadUInt32(); Cop0Epc = r.ReadUInt32();
        TotalCycles = r.ReadInt64(); FrameCount = r.ReadInt64();
        VBlankSequence = FrameCount;
        for (int i = 0; i < 32; i++) R[i] = r.ReadUInt32();
        Bus.ReadSnapshotState(r);
        Gte.ReadSnapshotState(r);
        TimersAndDma.ReadSnapshotState(r);
        Joypad.ReadSnapshotState(r);
        CdRom.ReadSnapshotState(r);
        Gpu.ReadSnapshotState(r);
        Spu.ReadSnapshotState(r);
        NextEventCycle = Math.Clamp(ComputeNextWakeCycle(), TotalCycles + 256, TotalCycles + 4096);
        FramePatch?.Reset();
    }
}
