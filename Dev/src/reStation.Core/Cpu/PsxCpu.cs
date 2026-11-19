// @module: reStation.Core.Cpu.PsxCpu
// @purpose: PS1 R3000A machine composition root implementing IReStationCpu: Bus, GTE, GPU, SPU, MDEC, CD-ROM, Timers/DMA, Joypad, BIOS HLE.
// @depends-on: reStation.Contracts, reStation.Core.Audio, reStation.Core.Bios, reStation.Core.CdRom, reStation.Core.DiscIngestion, reStation.Core.Gpu, reStation.Core.Gte, reStation.Core.Hardware, reStation.Core.Mdec
// @exports: PsxCpu
// @owner: devv-cpu-cd (T2)

using System.Security.Cryptography;
using reStation.Contracts;
using reStation.Core.Audio;
using reStation.Core.Bios;
using reStation.Core.Bringup;
using reStation.Core.CdRom;
using reStation.Core.DiscIngestion;
using reStation.Core.Gpu;
using reStation.Core.Gte;
using reStation.Core.Hardware;
using reStation.Core.Mdec;

namespace reStation.Core.Cpu;

public sealed partial class PsxCpu : IReStationCpuEx, IDisposable
{
    public const int CyclesPerFrame = 564_480; // 33,868,800 Hz / 60 Hz
    public const int ModuleSlotCount = 12;

    private readonly List<IRecompModule> _modules = new();
    private OverlayEntry[] _overlays = [];
    private bool _ramWriteHooked;
    private readonly ushort[] _padButtons = new ushort[2];
    private readonly byte[] _padAxes = new byte[8];
    private readonly Stack<(bool ShouldUnwind, uint UnwindTarget, bool InSubTickRender, bool InCompiledTrampoline, bool HasPendingTailJump, uint PendingTailJumpPc)> _unwindStack = new();
    private bool _inCompiledTrampoline;
    private bool _hasPendingTailJump;
    private uint _pendingTailJumpPc;
    private long _lastBringupSpinCycle;
    private readonly Dictionary<(uint Pc, int ExeId), (string Sha, uint Size)> _bringupBlockCache = new();

    public JitBringupRecorder? BringupRecorder { get; set; }

    public PsxCpu(RecompSettings settings, string dataDirectory, IPsxRenderBackend renderBackend, ReStationLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(renderBackend);
        Settings = settings;
        Logger = logger;
        DataDirectory = Path.GetFullPath(dataDirectory);
        GameDataDir = DiscManifest.GetGameDataDir(DataDirectory);
        Pgxp = new PgxpTracker { IsEnabled = settings.IsPgxpEnabled };
        Bus = new PsxMemoryBus(this) { IsSubTickRenderGuard = () => InSubTickRender };
        Gte = new PsxGte(settings, Pgxp);
        Gpu = new PsxGpu(settings, Pgxp, renderBackend)
        {
            WidescreenSupportChecker = () =>
                (WidescreenProvider ?? (FramePatch as IRecompWidescreenProvider))?.IsActiveTrueWidescreenSupported(this) ?? true
        };
        Spu = new PsxSpu(settings);
        Mdec = new PsxMdec();
        CdRom = new PsxCdRom(this);
        TimersAndDma = new PsxTimersAndDma(this);
        Joypad = new PsxJoypadPort(this);
        Bios = new PsxBiosHle(this, DataDirectory);
        Array.Fill(_padAxes, (byte)0x80);
    }

    public uint[] R { get; } = new uint[32];
    public uint Hi { get; set; }
    public uint Lo { get; set; }
    public uint Pc { get; set; }
    public uint Cop0Sr { get; set; } = 0x00000401U;
    public uint Cop0Cause { get; set; }
    public uint Cop0Epc { get; set; }
    public uint Cop0BadVAddr { get; set; }
    public long TotalCycles { get; set; }
    public long NextEventCycle { get; set; } = 1024;
    public long FrameCount { get; private set; }
    /// <summary>Monotonic hardware VBlank edge sequence counter incremented once per 564,480-cycle VBlank edge.</summary>
    public long VBlankSequence { get; private set; }
    public int ActiveExeId { get; set; }
    public bool ShouldUnwind { get; set; }
    public bool InInterrupt { get; set; }
    public bool InSubTickRender { get; set; }
    public bool ExitRequested { get; private set; }
    public bool FrameReadyFlag { get; set; }
    public uint UnwindTargetPc { get; set; } = IReStationCpu.ReturnSentinelPc;
    public bool SkipPolygonRasterization { get => Gpu.SkipPolygonRasterization; set => Gpu.SkipPolygonRasterization = value; }

    public void BeginSubTickFrame() => Gpu.BeginSubTickFrame();

    public RecompSettings Settings { get; }
    public ReStationLogger? Logger { get; }
    public string DataDirectory { get; }
    public string GameDataDir { get; }
    public PgxpTracker Pgxp { get; }
    public PsxMemoryBus Bus { get; }
    public PsxGte Gte { get; }
    public PsxGpu Gpu { get; }
    public PsxSpu Spu { get; }
    public PsxMdec Mdec { get; }
    public PsxCdRom CdRom { get; }
    public PsxTimersAndDma TimersAndDma { get; }
    public PsxJoypadPort Joypad { get; }
    public PsxBiosHle Bios { get; }
    public DiscManifest? Disc { get; private set; }
    public IGameFramePatch? FramePatch { get; set; }
    public IRecompWidescreenProvider? WidescreenProvider { get; set; }
    /// <summary>Invoked on every emulated VBlank edge (host presents/paces/polls input here).</summary>
    public Action<PsxCpu>? OnVBlankFrame { get; set; }
    /// <summary>Quick-load slot requested by the host (-1 = none); lifted code unwinds when set.</summary>
    public int PendingQuickLoadSlot { get; set; } = -1;
    public long CompiledDispatchCount { get; private set; }
    public long InterpreterFallbackCount { get; private set; }
    public long OverlayHashComputations { get; private set; }
    public double CdSectorsPerSec => CdRom.SectorsPerSec;

    /// <summary>Attaches a verified disc manifest (CD-ROM sector source + memory card scope + gamedata lookup).</summary>
    public void AttachDisc(DiscManifest manifest)
    {
        Disc = manifest ?? throw new ArgumentNullException(nameof(manifest));
        string cardId = !string.IsNullOrWhiteSpace(manifest.SerialNumber) ? manifest.SerialNumber : manifest.ModuleId;
        if (!string.IsNullOrWhiteSpace(cardId)) Bios.MemCard.SetCardIdentity(cardId);
        CdRom.SetManifest(manifest, GameDataDir);
    }

    public void RegisterModule(IRecompModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (module.ExeId < 0)
            throw new ArgumentOutOfRangeException(nameof(module), $"ExeId {module.ExeId} must be non-negative.");
        if (module is IOverlayRecompModule overlay)
        {
            RegisterOverlayModule(overlay);
            return;
        }
        int existingIdx = _modules.FindIndex(m => m is not IOverlayRecompModule && m.ExeId == module.ExeId);
        if (existingIdx >= 0) _modules[existingIdx] = module;
        else _modules.Add(module);
    }

    /// <summary>Loads a PS-X EXE into RAM, sets PC/GP/SP/RA, and switches <see cref="ActiveExeId"/>.</summary>
    public bool LoadExecutable(string exeRelativePath, int exeId = 0)
    {
        if (!Bios.LoadExecutableIntoRam(exeRelativePath, out uint pc0, out uint gp0, out uint spBase, out _)) return false;
        ActiveExeId = Math.Max(0, exeId);
        Pc = pc0;
        R[28] = gp0;
        R[29] = spBase != 0 ? spBase : 0x801FFF00U;
        R[30] = R[29];
        R[31] = IReStationCpu.ReturnSentinelPc;
        return true;
    }

    public void RequestExit()
    {
        ExitRequested = true;
        ShouldUnwind = true;
    }

    public void SetControllerState(int port, ushort pressedButtons, byte leftX, byte leftY, byte rightX, byte rightY)
    {
        if (port is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(port));
        _padButtons[port] = pressedButtons;
        int o = port * 4;
        _padAxes[o] = leftX; _padAxes[o + 1] = leftY; _padAxes[o + 2] = rightX; _padAxes[o + 3] = rightY;
    }

    public ushort GetControllerButtons(int port) => _padButtons[Math.Clamp(port, 0, 1)];

    public void DispatchFunction(uint pc)
    {
        if (ExitRequested || PendingQuickLoadSlot >= 0) { ShouldUnwind = true; return; }
        if (R[31] == 0) R[31] = IReStationCpu.ReturnSentinelPc;
        ShouldUnwind = false;
        Pc = pc;
        if (R[31] != IReStationCpu.ReturnSentinelPc)
            PsxInterpreter.ResumeFromSnapshot(this);
        else
            Call(pc);
        if (!ExitRequested && PendingQuickLoadSlot < 0) ShouldUnwind = false;
    }

    public void Call(uint targetPc)
    {
        uint entryCanonPc = 0U;
        uint lastBodyCanonPc = 0U;
        bool ranAnyBody = false;
        try
        {
            while (true)
            {
                if (ExitRequested || PendingQuickLoadSlot >= 0) { ShouldUnwind = true; return; }
                uint masked = targetPc & 0x1FFFFFFFU;
                if (masked is 0xA0U or 0xB0U or 0xC0U) { Bios.HandleVectorCall(masked); return; }
                if (targetPc == UnwindTargetPc || targetPc == IReStationCpu.ReturnSentinelPc)
                {
                    Pc = targetPc;
                    ShouldUnwind = true;
                    return;
                }
                uint canonPc = 0x80000000U | masked;
                if (InSubTickRender)
                {
                    if (FramePatch is ISubTickFrameHook subHook && subHook.TryInterceptSubTickCall(this, canonPc)) return;
                }
                else if (FramePatch is not null && FramePatch.TryInterceptCall(this, canonPc))
                {
                    return;
                }
                if (!ranAnyBody)
                {
                    entryCanonPc = canonPc;
                    ranAnyBody = true;
                }
                lastBodyCanonPc = canonPc;
                RecordBringupCall(canonPc);
                IRecompModule? module = ResolveModule(canonPc);
                if (module is not null && TryExecuteCompiledModule(module, canonPc, out bool hasTailJump, out uint tailTargetPc))
                {
                    if (hasTailJump && !ShouldUnwind && !ExitRequested && PendingQuickLoadSlot < 0)
                    {
                        targetPc = tailTargetPc;
                        continue;
                    }
                    return;
                }
                if (InterpreterFallbackCount < 5) Logger?.Warn("CPU", $"Interpreter fallback #{InterpreterFallbackCount + 1} pc=0x{canonPc:X8} exe={ActiveExeId}");
                InterpreterFallbackCount++;
                if (PsxInterpreter.ExecuteUntilReturnOrCompiled(this, canonPc, module, out uint interpTailPc))
                {
                    targetPc = interpTailPc;
                    continue;
                }
                return;
            }
        }
        finally
        {
            if (ranAnyBody && !InSubTickRender && FramePatch is ISubTickFrameHook afterHook)
            {
                if (lastBodyCanonPc != entryCanonPc)
                    afterHook.OnAfterCall(this, lastBodyCanonPc);
                afterHook.OnAfterCall(this, entryCanonPc);
            }
        }
    }

    internal bool TryExecuteCompiledModule(IRecompModule module, uint canonPc, out bool hasTailJump, out uint tailTargetPc)
    {
        bool prevInTrampoline = _inCompiledTrampoline;
        bool prevHasTail = _hasPendingTailJump;
        uint prevTailPc = _pendingTailJumpPc;
        _inCompiledTrampoline = true;
        _hasPendingTailJump = false;
        _pendingTailJumpPc = 0U;
        try
        {
            bool executed = module.TryExecuteFunction(this, canonPc);
            if (executed) CompiledDispatchCount++;
            hasTailJump = executed && _hasPendingTailJump;
            tailTargetPc = hasTailJump ? _pendingTailJumpPc : 0U;
            return executed;
        }
        finally
        {
            _inCompiledTrampoline = prevInTrampoline;
            _hasPendingTailJump = prevHasTail;
            _pendingTailJumpPc = prevTailPc;
        }
    }

    public void JumpIndirect(uint targetPc)
    {
        if (targetPc == UnwindTargetPc || targetPc == IReStationCpu.ReturnSentinelPc)
        {
            Pc = targetPc;
            ShouldUnwind = true;
            return;
        }
        BringupRecorder?.RecordIndirectJumpTarget(Pc, targetPc);
        if (_inCompiledTrampoline)
        {
            _hasPendingTailJump = true;
            _pendingTailJumpPc = targetPc;
            return;
        }
        Call(targetPc);
    }

    public void Syscall(uint code) => Bios.HandleSyscall(code);

    public void Break(uint code)
    {
        uint c = code & 0xFFFFFU;
        if (c is 0x1C00 or 0x1800) return;
        Logger?.Warn("CPU", $"break 0x{c:X5} at pc=0x{Pc:X8} exe={ActiveExeId} ra=0x{R[31]:X8}");
    }

    public void PollEvents()
    {
        if (InSubTickRender) { PollSubTickWatchdog(); return; }
        TimersAndDma.TickTo(TotalCycles);
        Joypad.TickTo(TotalCycles);
        CdRom.TickTo(TotalCycles);
        Gpu.TickTo(TotalCycles, out bool vblankEdge);
        if (vblankEdge)
        {
            FrameCount++;
            VBlankSequence++;
            FrameReadyFlag = true;
            Pgxp.ClearFrame();
            TimersAndDma.RaiseIrq(PsxIrq.VBlank);
            Bios.OnVBlank();
            BringupRecorder?.ResetVBlankSpinCounter();
            _lastBringupSpinCycle = TotalCycles;
            OnVBlankFrame?.Invoke(this);
            if (ExitRequested || PendingQuickLoadSlot >= 0) { ShouldUnwind = true; return; }
        }
        if (BringupRecorder is { } bringupRec)
        {
            long spinDelta = TotalCycles - _lastBringupSpinCycle;
            if (spinDelta > 0L)
            {
                _lastBringupSpinCycle = TotalCycles;
                if (bringupRec.AdvanceCyclesSinceVBlank(spinDelta))
                {
                    ExitRequested = true;
                    ShouldUnwind = true;
                    return;
                }
            }
        }
        NextEventCycle = Math.Clamp(ComputeNextWakeCycle(), TotalCycles + 256, TotalCycles + 4096);
        if (ShouldUnwind || InInterrupt || ExitRequested) return;
        if ((Cop0Sr & 0x0401U) == 0x0401U)
        {
            if (TimersAndDma.PendingTimerEvents != 0)
            {
                uint tev = TimersAndDma.PendingTimerEvents;
                TimersAndDma.PendingTimerEvents = 0;
                for (int i = 0; i < 3; i++)
                    if (((tev >> i) & 1U) != 0) Bios.DeliverClassEvent(0xF2000000U + (uint)i, 0x0002U);
            }
            if (TimersAndDma.HasPendingInterrupt) Bios.TryRunInterruptHook();
        }
    }

    public void Dispose() => CdRom.Dispose();
}
