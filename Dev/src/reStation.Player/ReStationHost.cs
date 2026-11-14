// @module: reStation.Player.ReStationHost
// @purpose: IReStationHost implementation: module registration, PS-X EXE boot loading, cooperative per-VBlank RunFrame coroutine, SPU frame rendering, telemetry.
// @depends-on: System.Buffers.Binary, Foundation.Core, Foundation.Retro, reStation.Contracts, reStation.Core, reStation.Core.Cpu, reStation.Core.DiscIngestion, reStation.Core.Saves
// @exports: ReStationHost
// @owner: devv-player-ui (T6)

using System.Buffers.Binary;
using System.Diagnostics;
using Foundation.Core;
using Foundation.Retro;
using reStation.Contracts;
using reStation.Core;
using reStation.Core.Cpu;
using reStation.Core.DiscIngestion;
using reStation.Core.Saves;

namespace reStation.Player;

internal sealed record HostRuntimeContext(
    bool IsHeadless,
    Func<bool>? HasPendingCapture = null,
    Func<bool>? IsModernMouseLookActive = null,
    Func<float>? MouseSensitivity = null,
    Func<(float MouseDx, float MouseDy, float StickX, float StickY)>? PollSubTickLook = null,
    Action? PresentSubTickFrame = null,
    Func<bool>? IsEnhancedFrameRateEnabled = null);

/// <summary>
/// Runs lifted game code on a dedicated emulation thread that parks at every VBlank, so <see cref="RunFrame"/>
/// advances exactly one emulated frame and returns control to the SDL host thread.
/// </summary>
internal sealed partial class ReStationHost : IReStationHostEx, IDisposable
{
    public const int SamplesPerFrame = 735;
    private const int PsxExeHeaderSize = 0x800;

    private readonly PsxCpu _cpu;
    private readonly HighResFramePacer _pacer;
    private readonly ReStationLogger _logger;
    private readonly AutoResetEvent _resumeEmu = new(false);
    private readonly AutoResetEvent _frameReached = new(false);
    private readonly string _dataDirectory;
    private Thread? _emuThread;
    private uint _entryPc;
    private bool _bootPending;
    private volatile bool _emuFinished;
    private volatile bool _disposing;
    private Exception? _emuFault;
    private ISubTickFrameHook? _subTickHook;
    private HostRuntimeContext _ctx = new(false);
    private long _lastMdecBlocks;
    private long _lastMdecTicks = Stopwatch.GetTimestamp();
    private double _mdecPerSec, _cpuMs, _gpuMs, _waitMs;

    public ReStationHost(PsxCpu cpu, IReStationModuleManifest manifest, string dataDirectory, RecompHostOptions options,
        HighResFramePacer pacer, ReStationLogger logger)
    {
        _cpu = cpu ?? throw new ArgumentNullException(nameof(cpu));
        ModuleManifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        _pacer = pacer ?? throw new ArgumentNullException(nameof(pacer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _dataDirectory = Path.GetFullPath(dataDirectory);
        Options = options ?? new RecompHostOptions();
        GameDataDirectory = DiscManifest.GetGameDataDir(_dataDirectory);
        SaveDirectory = Path.Combine(_dataDirectory, "saves");
        Directory.CreateDirectory(SaveDirectory);
        _cpu.OnVBlankFrame = OnVBlank;
    }

    public RecompHostOptions Options { get; private set; }
    public IRecompRenderBackend RenderBackend => _cpu.Gpu.Backend;
    public IPsxRenderBackend Backend => _cpu.Gpu.Backend;
    public IReStationCpu Cpu => _cpu;
    public PsxCpu Machine => _cpu;
    public IReStationModuleManifest ModuleManifest { get; }
    public string GameDataDirectory { get; }
    public string SaveDirectory { get; }
    public string DataDirectory => _dataDirectory;
    public long FramesRun { get; private set; }
    public bool IsBooted => _emuThread is not null;
    public bool HasFinished => _emuFinished || _cpu.ExitRequested;
    public bool IsHeadless => _ctx.IsHeadless;
    public bool HasPendingCapture => _ctx.HasPendingCapture?.Invoke() ?? false;
    public int TargetFps => Options.UnlockFps || Options.TargetFps <= 0 ? 0 : (int)Math.Round(Options.TargetFps);
    public bool IsEnhancedFrameRateEnabled => _ctx.IsEnhancedFrameRateEnabled?.Invoke() ?? (_cpu.Settings.FrameRateMode == FrameRateMode.Enhanced);
    public bool IsModernMouseLookActive => _ctx.IsModernMouseLookActive?.Invoke() ?? false;
    public float MouseSensitivity => _ctx.MouseSensitivity?.Invoke() ?? 1.0f;

    internal void AttachRuntimeContext(HostRuntimeContext context) => _ctx = context ?? throw new ArgumentNullException(nameof(context));
    public void WaitUntilTimestamp(long targetStopwatchTicks)
    {
        while (!_disposing && !_cpu.ExitRequested)
        {
            long remaining = targetStopwatchTicks - Stopwatch.GetTimestamp();
            if (remaining <= 0) break;
            if (remaining * 1000.0 / Stopwatch.Frequency > 2.0) Thread.Sleep(1);
            else Thread.SpinWait(32);
        }
    }

    public RecompFrameStats GetFrameStats() => GetTelemetry().ToFrameStats();
    /// <summary>Records the module's presentation target (the VBlank loop itself stays hardware-paced at 60 Hz).</summary>
    public void SetTargetFps(double targetFps, bool unlock = false)
    {
        double fps = double.IsFinite(targetFps) && targetFps > 0 ? targetFps : 60.0;
        Options = Options with { TargetFps = fps, UnlockFps = unlock };
        _logger.Info("HOST", $"Module requested presentation targetFps={fps:F0} unlock={unlock}.");
    }

    public void RegisterModule(IRecompModule module) => _cpu.RegisterModule(module);
    public void SetFramePatch(IGameFramePatch? patch) { _cpu.FramePatch = patch; _subTickHook = patch as ISubTickFrameHook; }

    /// <summary>Loads a PS-X EXE from gamedata into RAM, sets PC/GP/SP/RA, and arms the emulation thread for the next RunFrame.</summary>
    public bool LoadExecutable(string exeRelativePath, int exeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exeRelativePath);
        string? path = ResolveExecutablePath(exeRelativePath);
        if (path is null)
        {
            _logger.Error("BOOT", $"Executable '{exeRelativePath}' not found in gamedata.");
            return false;
        }

        byte[] exe = File.ReadAllBytes(path);
        if (exe.Length < PsxExeHeaderSize || !exe.AsSpan(0, 8).SequenceEqual("PS-X EXE"u8))
        {
            _logger.Error("BOOT", $"'{Path.GetFileName(path)}' is not a PS-X EXE.");
            return false;
        }

        uint pc0 = BinaryPrimitives.ReadUInt32LittleEndian(exe.AsSpan(0x10));
        uint gp0 = BinaryPrimitives.ReadUInt32LittleEndian(exe.AsSpan(0x14));
        uint tAddr = BinaryPrimitives.ReadUInt32LittleEndian(exe.AsSpan(0x18));
        uint tSize = BinaryPrimitives.ReadUInt32LittleEndian(exe.AsSpan(0x1C));
        uint sAddr = BinaryPrimitives.ReadUInt32LittleEndian(exe.AsSpan(0x30));
        uint sSize = BinaryPrimitives.ReadUInt32LittleEndian(exe.AsSpan(0x34));
        int ramOffset = (int)(tAddr & 0x1FFFFFU);
        int copyLen = (int)Math.Min(tSize, (uint)(exe.Length - PsxExeHeaderSize));
        if (ramOffset + copyLen > _cpu.Ram.Length)
        {
            _logger.Error("BOOT", $"'{Path.GetFileName(path)}' text segment 0x{tAddr:X8}+0x{tSize:X} exceeds 2 MB RAM.");
            return false;
        }

        exe.AsSpan(PsxExeHeaderSize, copyLen).CopyTo(_cpu.Ram.Slice(ramOffset, copyLen));
        _cpu.Bus.NotifyRamWriteRange(tAddr, (uint)copyLen);
        _cpu.Pc = pc0;
        _cpu.R[28] = gp0;
        if (sAddr != 0) _cpu.R[29] = _cpu.R[30] = sAddr + sSize;
        _cpu.R[31] = IReStationCpu.ReturnSentinelPc;
        _cpu.ActiveExeId = exeId;
        _entryPc = pc0;
        _bootPending = _emuThread is null;
        _logger.Info("BOOT", $"Loaded '{Path.GetFileName(path)}' exeId={exeId} pc=0x{pc0:X8} text=0x{tAddr:X8}+0x{copyLen:X}.");
        return true;
    }

    private string? ResolveExecutablePath(string exeRelativePath)
    {
        string rel = _cpu.Disc?.FindByName(exeRelativePath)?.RelativeFile ?? exeRelativePath;
        foreach (string candidate in new[] { rel, exeRelativePath, Path.GetFileName(exeRelativePath) })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            string full = SandboxEnvironment.AssertPathContained(
                Path.GetFullPath(Path.Combine(GameDataDirectory, candidate.Replace('/', Path.DirectorySeparatorChar))), GameDataDirectory);
            if (File.Exists(full)) return full;
        }
        return null;
    }

    /// <summary>Advances exactly one VBlank: resumes the parked emulation thread (or ticks idle hardware) and renders 735 SPU frames.</summary>
    public void RunFrame()
    {
        if (_disposing) return;
        BeginRunFrameHostPump();
        if (_bootPending && _emuThread is null)
        {
            _bootPending = false;
            _emuThread = new Thread(EmulationThreadMain) { IsBackground = true, Name = "reStation.Emu" };
            _emuThread.Start();
        }

        bool parked = true;
        if (_emuThread is not null && !_emuFinished)
        {
            _resumeEmu.Set();
            parked = WaitForFrameOrPumpHostActions();
            if (!parked) _logger.Warn("EMU", $"No VBlank within 2s (pc=0x{_cpu.Pc:X8} cycles={_cpu.TotalCycles}); keeping host responsive.");
        }
        else
        {
            _cpu.TotalCycles += PsxCpu.CyclesPerFrame;
            _cpu.PollEvents();
        }

        if (_emuFault is { } fault)
        {
            _emuFault = null;
            _logger.Error("EMU", $"Emulation thread faulted: {fault.GetType().Name}: {fault.Message}");
            CrashBundleService.TryWriteCrashBundle(_logger.LogDirectory, ReStationEngineInfo.AppId, ReStationEngineInfo.Version, fault, _logger.GetRecentLines());
            _cpu.RequestExit();
        }

        long frameStartTicks = Stopwatch.GetTimestamp();
        if (parked) _cpu.Spu.RenderFrames(SamplesPerFrame);
        FramesRun++;
        if (parked && _subTickHook is not null && !_ctx.IsHeadless)
            ForwardSubTickHook(frameStartTicks);
    }

    private void ForwardSubTickHook(long frameStartTicks)
    {
        if (_subTickHook is not { } hook || _disposing || _cpu.ExitRequested) return;
        int fps = TargetFps;
        uint total = fps <= 0 ? 2U : (uint)Math.Clamp((fps + 59) / 60, 1, 8);
        long subStepTicks = Math.Max(1L, (Stopwatch.Frequency / 60L) / total);
        for (uint sub = 0; sub < total && !_disposing && !_cpu.ExitRequested; sub++)
        {
            if (!hook.ShouldRunSubTick(sub, total)) continue;
            if (fps > 0 && sub > 0) WaitUntilTimestamp(frameStartTicks + sub * subStepTicks);
            _cpu.BeginSubTickFrame();
            hook.OnSubTickBegin(_cpu, sub, total);
            long prevDeadline = _cpu.SubTickDeadlineCycle;
            bool completed = true;
            try
            {
                if (hook.SubTickEntryPc != 0U && _cpu.HasCompiledEntry(hook.SubTickEntryPc))
                {
                    _cpu.SubTickDeadlineCycle = hook.MaxSubTickCycles > 0U ? _cpu.TotalCycles + hook.MaxSubTickCycles : 0L;
                    _cpu.RunSlice(hook.SubTickEntryPc, IReStationCpu.ReturnSentinelPc, inSubTickRender: true);
                    completed = _cpu.SubTickDeadlineCycle == 0L || _cpu.TotalCycles < _cpu.SubTickDeadlineCycle;
                }
            }
            finally { _cpu.SubTickDeadlineCycle = prevDeadline; hook.OnSubTickEnd(_cpu, sub, completed); }
            if (sub > 0) PresentSubTickFrame();
        }
    }

    /// <summary>Steps the module one frame; if the module did not advance the machine itself, the host does.</summary>
    public async Task StepModuleFrameAsync(IRecompGameModule module, CancellationToken ct)
    {
        long before = FramesRun;
        await module.StepFrameAsync(ct).ConfigureAwait(false);
        if (FramesRun == before) RunFrame();
    }

    /// <summary>Saves a quick-save slot atomically using <see cref="QuickSaveManager.SaveToFileWithTrailer"/>.</summary>
    public void SaveQuickSlot(int slot) =>
        QuickSaveManager.SaveToFileWithTrailer(_cpu, QuickSaveManager.GetSlotPath(_dataDirectory, ModuleManifest.ModuleId, slot), ModuleManifest.ModuleId, QuickSaveTrailer.CaptureDefaultChunks(_cpu), _logger);

    /// <summary>Requests a quick-load at the next frame boundary (unwinds lifted code when booted).</summary>
    public bool RequestQuickLoad(int slot)
    {
        if (_emuThread is not null && !_emuFinished) { _cpu.PendingQuickLoadSlot = slot; _cpu.ShouldUnwind = true; return true; }
        return LoadQuickSlot(slot);
    }

    private bool LoadQuickSlot(int slot)
    {
        try { return QuickSaveManager.LoadFromFile(_cpu, QuickSaveManager.GetSlotPath(_dataDirectory, ModuleManifest.ModuleId, slot), _logger).Success; }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            _logger.Warn("SAVE", $"Quick-load slot {slot} invalid ({ex.GetType().Name}: {ex.Message}).");
            return false;
        }
    }

    private void EmulationThreadMain()
    {
        try
        {
            _resumeEmu.WaitOne();
            if (_disposing) return;
            _cpu.DispatchFunction(_entryPc);
            while (!_disposing && !_cpu.ExitRequested && _cpu.PendingQuickLoadSlot >= 0)
            {
                int slot = _cpu.PendingQuickLoadSlot;
                _cpu.PendingQuickLoadSlot = -1;
                _cpu.ShouldUnwind = false;
                if (!LoadQuickSlot(slot)) { _logger.Warn("SAVE", $"Quick-load slot {slot} unavailable."); break; }
                _logger.Info("SAVE", $"Quick-loaded slot {slot}; resuming at pc=0x{_cpu.Pc:X8}.");
                _cpu.DispatchFunction(_cpu.Pc);
            }
            _logger.Info("EMU", $"Emulation thread finished frames={_cpu.FrameCount} exitRequested={_cpu.ExitRequested}.");
        }
        catch (Exception ex) { _emuFault = ex; }
        finally { _emuFinished = true; _hostActionDone.Set(); _frameReached.Set(); }
    }

    private void OnVBlank(PsxCpu cpu)
    {
        if (!ReferenceEquals(Thread.CurrentThread, _emuThread)) return; // idle hardware tick on the host thread
        _frameReached.Set();
        _resumeEmu.WaitOne();
        if (_disposing) cpu.RequestExit();
    }

    /// <summary>Stores per-frame host timings consumed by telemetry, HUD, and heartbeats.</summary>
    public void RecordTimings(double cpuMs, double gpuMs, double waitMs)
    {
        _cpuMs = cpuMs;
        _gpuMs = gpuMs;
        _waitMs = waitMs;
        long now = Stopwatch.GetTimestamp();
        double elapsed = (now - _lastMdecTicks) / (double)Stopwatch.Frequency;
        if (elapsed >= 1.0)
        {
            long blocks = _cpu.Mdec.MacroblocksDecoded;
            _mdecPerSec = (blocks - _lastMdecBlocks) / elapsed;
            _lastMdecBlocks = blocks;
            _lastMdecTicks = now;
        }
    }

    public PsxTelemetrySnapshot GetTelemetry() => new(
        FramesRun, _cpuMs, _gpuMs, _waitMs, _pacer.CurrentFps, _pacer.P99FrameMs, Backend.ScaleFactor, Backend.BackendId,
        CdSectorsPerSecProvider?.Invoke() ?? 0.0, _mdecPerSec, _cpu.Spu.ActiveVoiceCount, _cpu.Spu.XaDrops,
        _cpu.Spu.AudioFillRatio, _cpu.Spu.AudioUnderruns);

    /// <summary>Optional CD-ROM throughput source (wired by the host once PsxCpu exposes the rolling counter).</summary>
    public Func<double>? CdSectorsPerSecProvider { get; set; }

    public void Dispose()
    {
        if (_disposing) return;
        _disposing = true;
        _cpu.RequestExit();
        _cpu.OnVBlankFrame = null;
        SignalHostWaitersOnShutdown();
        if (_emuThread is { IsAlive: true } thread)
        {
            if (!thread.Join(TimeSpan.FromSeconds(2)))
            {
                _logger.Warn("EMU", "Emulation thread did not exit within 2s; abandoning background thread (semaphores left alive).");
                return;
            }
        }
        _resumeEmu.Dispose();
        _frameReached.Dispose();
        DisposeSubTickHandles();
    }
}
