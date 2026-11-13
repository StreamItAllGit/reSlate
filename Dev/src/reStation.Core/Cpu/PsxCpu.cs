// @module: reStation.Core.Cpu.PsxCpu
// @purpose: PS1 R3000A machine composition root implementing IReStationCpu: register file, module dispatch, event polling, VBlank callbacks, snapshots.
// @depends-on: reStation.Contracts, reStation.Core.Audio, reStation.Core.Gpu, reStation.Core.Gte, reStation.Core.Mdec, reStation.Core.DiscIngestion
// @exports: PsxCpu
// @owner: devv-cpu-cd (T2) — T1 skeleton; public members below are cross-slice LOCKED (Saves, Input, Player, Gpu, Audio consume them).

using reStation.Contracts;
using reStation.Core.Audio;
using reStation.Core.DiscIngestion;
using reStation.Core.Gpu;
using reStation.Core.Gte;
using reStation.Core.Mdec;

namespace reStation.Core.Cpu;

public sealed partial class PsxCpu : IReStationCpu
{
    public const int CyclesPerFrame = 564_480; // 33,868,800 Hz / 60 Hz
    public const int ModuleSlotCount = 12;
    private const uint SnapshotMarker = 0x55504352U; // "RCPU"

    private readonly IRecompModule?[] _modules = new IRecompModule?[ModuleSlotCount];
    private readonly ushort[] _padButtons = new ushort[2];
    private readonly byte[] _padAxes = new byte[8];

    public PsxCpu(RecompSettings settings, string dataDirectory, IPsxRenderBackend renderBackend, ReStationLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(renderBackend);
        Settings = settings;
        Logger = logger;
        DataDirectory = Path.GetFullPath(dataDirectory);
        Pgxp = new PgxpTracker { IsEnabled = settings.IsPgxpEnabled };
        Gpu = new PsxGpu(settings, Pgxp, renderBackend);
        Spu = new PsxSpu(settings);
        Mdec = new PsxMdec();
        Array.Fill(_padAxes, (byte)0x80);
    }

    public uint[] R { get; } = new uint[32];
    public uint Hi { get; set; }
    public uint Lo { get; set; }
    public uint Pc { get; set; }
    public uint Cop0Sr { get; set; } = 0x00000401U;
    public long TotalCycles { get; set; }
    public long NextEventCycle { get; private set; } = 1024;
    public long FrameCount { get; private set; }
    public int ActiveExeId { get; set; }
    public bool ShouldUnwind { get; set; }
    public bool InSubTickRender { get; set; }
    public bool ExitRequested { get; private set; }

    public RecompSettings Settings { get; }
    public ReStationLogger? Logger { get; }
    public string DataDirectory { get; }
    public PgxpTracker Pgxp { get; }
    public PsxGpu Gpu { get; }
    public PsxSpu Spu { get; }
    public PsxMdec Mdec { get; }
    public DiscManifest? Disc { get; private set; }
    public IGameFramePatch? FramePatch { get; set; }
    /// <summary>Invoked on every emulated VBlank edge (host presents/paces/polls input here).</summary>
    public Action<PsxCpu>? OnVBlankFrame { get; set; }
    /// <summary>Quick-load slot requested by the host (-1 = none); lifted code unwinds when set.</summary>
    public int PendingQuickLoadSlot { get; set; } = -1;
    public long CompiledDispatchCount { get; private set; }
    public long InterpreterFallbackCount { get; private set; }

    /// <summary>Attaches a verified disc manifest (CD-ROM sector source + gamedata EXE lookup).</summary>
    public void AttachDisc(DiscManifest manifest) => Disc = manifest ?? throw new ArgumentNullException(nameof(manifest));

    public void RegisterModule(IRecompModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (module.ExeId is < 0 or >= ModuleSlotCount)
            throw new ArgumentOutOfRangeException(nameof(module), $"ExeId {module.ExeId} outside 0..{ModuleSlotCount - 1}.");
        _modules[module.ExeId] = module;
    }

    public void RequestExit()
    {
        ExitRequested = true;
        ShouldUnwind = true;
    }

    /// <summary>Sets controller state; <paramref name="pressedButtons"/> uses active-high PsxPadButton bits.</summary>
    public void SetControllerState(int port, ushort pressedButtons, byte leftX, byte leftY, byte rightX, byte rightY)
    {
        if (port is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(port));
        _padButtons[port] = pressedButtons;
        int o = port * 4;
        _padAxes[o] = leftX; _padAxes[o + 1] = leftY; _padAxes[o + 2] = rightX; _padAxes[o + 3] = rightY;
    }

    /// <summary>Active-high pressed-button mask last set for <paramref name="port"/>.</summary>
    public ushort GetControllerButtons(int port) => _padButtons[Math.Clamp(port, 0, 1)];

    public void DispatchFunction(uint pc)
    {
        R[31] = IReStationCpu.ReturnSentinelPc;
        ShouldUnwind = false;
        Call(pc);
        if (!ExitRequested && PendingQuickLoadSlot < 0) ShouldUnwind = false;
    }

    public void Call(uint targetPc)
    {
        if (ExitRequested || PendingQuickLoadSlot >= 0) { ShouldUnwind = true; return; }
        if (targetPc == IReStationCpu.ReturnSentinelPc) { Pc = targetPc; ShouldUnwind = true; return; }
        uint canonPc = 0x80000000U | (targetPc & 0x1FFFFFFFU);
        if (!InSubTickRender && FramePatch is not null && FramePatch.TryInterceptCall(this, canonPc)) return;
        IRecompModule? module = ResolveModule(canonPc);
        if (module is not null && module.TryExecuteFunction(this, canonPc))
        {
            CompiledDispatchCount++;
            return;
        }
        InterpreterFallbackCount++;
        if (InterpreterFallbackCount <= 5) Logger?.Warn("CPU", $"No compiled entry for pc=0x{canonPc:X8} exe={ActiveExeId}; unwinding.");
        Pc = canonPc;
        ShouldUnwind = true;
    }

    public void JumpIndirect(uint targetPc)
    {
        if (targetPc == IReStationCpu.ReturnSentinelPc) { Pc = targetPc; ShouldUnwind = true; return; }
        Call(targetPc);
    }

    public void Syscall(uint code) => Logger?.Info("CPU", $"syscall 0x{code:X5} a0=0x{R[4]:X8}");

    public void Break(uint code)
    {
        uint c = code & 0xFFFFFU;
        if (c is 0x1C00 or 0x1800) return; // compiler divide-by-zero / overflow traps
        Logger?.Warn("CPU", $"break 0x{c:X5} at pc=0x{Pc:X8}");
    }

    public void PollEvents()
    {
        if (InSubTickRender) return;
        Gpu.TickTo(TotalCycles, out bool vblankEdge);
        if (vblankEdge)
        {
            FrameCount++;
            Pgxp.ClearFrame();
            OnVBlankFrame?.Invoke(this);
            if (ExitRequested || PendingQuickLoadSlot >= 0) ShouldUnwind = true;
        }
        NextEventCycle = Math.Clamp(Gpu.NextVBlankCycle, TotalCycles + 256, TotalCycles + 4096);
    }

    private IRecompModule? ResolveModule(uint canonPc)
    {
        IRecompModule? active = _modules[Math.Clamp(ActiveExeId, 0, ModuleSlotCount - 1)];
        if (active is not null && canonPc >= active.TextStart && canonPc < active.TextEnd) return active;
        IRecompModule? resident = _modules[0];
        return resident is not null && canonPc >= resident.TextStart && canonPc < resident.TextEnd ? resident : active;
    }

    /// <summary>Serializes CPU, RAM, GPU, and SPU state (QuickSaveManager wraps this in the "RST1" container).</summary>
    public void WriteSnapshotState(BinaryWriter w)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.Write(SnapshotMarker);
        w.Write(ActiveExeId); w.Write(Pc); w.Write(Hi); w.Write(Lo); w.Write(Cop0Sr);
        w.Write(TotalCycles); w.Write(FrameCount);
        for (int i = 0; i < 32; i++) w.Write(R[i]);
        WriteMemorySnapshot(w);
        Gpu.WriteSnapshotState(w);
        Spu.WriteSnapshotState(w);
    }

    public void ReadSnapshotState(BinaryReader r)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (r.ReadUInt32() != SnapshotMarker) throw new InvalidDataException("Snapshot CPU marker mismatch.");
        ActiveExeId = r.ReadInt32(); Pc = r.ReadUInt32(); Hi = r.ReadUInt32(); Lo = r.ReadUInt32(); Cop0Sr = r.ReadUInt32();
        TotalCycles = r.ReadInt64(); FrameCount = r.ReadInt64();
        for (int i = 0; i < 32; i++) R[i] = r.ReadUInt32();
        ReadMemorySnapshot(r);
        Gpu.ReadSnapshotState(r);
        Spu.ReadSnapshotState(r);
        NextEventCycle = TotalCycles + 256;
        FramePatch?.Reset();
    }
}
