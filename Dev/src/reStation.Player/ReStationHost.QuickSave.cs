// @module: reStation.Player.ReStationHost.QuickSave
// @purpose: VBlank-edge quick-save/quick-load slot queuing, emulation resume loop, and live presentation TargetFps setter.
// @depends-on: Foundation.Core, reStation.Contracts, reStation.Core.Cpu, reStation.Core.Saves
// @exports: ReStationHost (partial)

using reStation.Contracts;
using reStation.Core;
using reStation.Core.Cpu;
using reStation.Core.Saves;

namespace reStation.Player;

internal sealed partial class ReStationHost
{
    private static readonly IReadOnlyDictionary<string, byte[]> EmptyRstxChunks = new Dictionary<string, byte[]>();
    private volatile int _pendingQuickSaveSlot = -1;

    /// <summary>Optional module provider consulted when saving/loading quick-save RSTX chunks.</summary>
    public IRecompQuickSaveProvider? QuickSaveProvider { get; set; }

    /// <summary>Queued quick-save slot executed on the emulation thread at the next VBlank edge (-1 = none).</summary>
    public int PendingQuickSaveSlot
    {
        get => _pendingQuickSaveSlot;
        set => _pendingQuickSaveSlot = value;
    }

    /// <summary>Queued quick-load slot triggering cooperative unwind and snapshot resume (-1 = none).</summary>
    public int PendingQuickLoadSlot
    {
        get => _cpu.PendingQuickLoadSlot;
        set => _cpu.PendingQuickLoadSlot = value;
    }

    /// <summary>Updates presentation target FPS live on both host options and <see cref="HighResFramePacer"/>.</summary>
    public void SetTargetFps(int targetFps, bool unlock = false)
    {
        bool unlocked = unlock || targetFps <= 0;
        double fps = unlocked ? 0.0 : Math.Clamp(targetFps, 30, 500);
        bool changed = Options.UnlockFps != unlocked || Math.Abs(Options.TargetFps - fps) > 0.01;
        Options = Options with { TargetFps = fps, UnlockFps = unlocked };
        _pacer.TargetFps = fps;
        if (changed) _logger.Info("HOST", $"Updated presentation targetFps={(unlocked ? 0 : (int)Math.Round(fps))} unlock={unlocked}.");
    }

    /// <summary>Records the module's presentation target (the VBlank loop itself stays hardware-paced at 60 Hz).</summary>
    public void SetTargetFps(double targetFps, bool unlock = false)
    {
        double fps = double.IsFinite(targetFps) && targetFps > 0 ? targetFps : 60.0;
        Options = Options with { TargetFps = fps, UnlockFps = unlock };
        _pacer.TargetFps = unlock ? 0.0 : fps;
        _logger.Info("HOST", $"Module requested presentation targetFps={fps:F0} unlock={unlock}.");
    }

    /// <summary>Queues a quick-save for the next VBlank edge (or saves immediately if emulation is not running).</summary>
    public bool RequestQuickSave(int slot)
    {
        if (_emuThread is not null && !_emuFinished && !ReferenceEquals(Thread.CurrentThread, _emuThread))
        {
            _pendingQuickSaveSlot = slot;
            return true;
        }
        SaveQuickSlot(slot);
        return true;
    }

    /// <summary>Saves a quick-save slot atomically using <see cref="QuickSaveManager.SaveToFileWithTrailer"/>.</summary>
    public void SaveQuickSlot(int slot)
    {
        var chunks = new Dictionary<string, byte[]>(QuickSaveTrailer.CaptureDefaultChunks(_cpu), StringComparer.Ordinal);
        QuickSaveProvider?.CaptureQuickSaveChunks(chunks);
        QuickSaveManager.SaveToFileWithTrailer(
            _cpu,
            QuickSaveManager.GetSlotPath(_dataDirectory, ModuleManifest.ModuleId, slot),
            ModuleManifest.ModuleId,
            chunks,
            _logger);
    }

    /// <summary>Requests a quick-load at the next frame boundary (unwinds lifted code when booted).</summary>
    public bool RequestQuickLoad(int slot)
    {
        if (_emuThread is not null && !_emuFinished)
        {
            _cpu.PendingQuickLoadSlot = slot;
            _cpu.ShouldUnwind = true;
            return true;
        }
        return LoadQuickSlot(slot);
    }

    private bool LoadQuickSlot(int slot)
    {
        try
        {
            QuickSaveLoadResult result = QuickSaveManager.LoadFromFile(_cpu, QuickSaveManager.GetSlotPath(_dataDirectory, ModuleManifest.ModuleId, slot), _logger);
            if (result.Success)
                QuickSaveProvider?.OnQuickSaveLoaded(result.RstxChunks ?? EmptyRstxChunks);
            return result.Success;
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            _logger.Warn("SAVE", $"Quick-load slot {slot} invalid ({ex.GetType().Name}: {ex.Message}).");
            return false;
        }
    }

    private void FlushPendingQuickActionsAtVBlank(PsxCpu cpu)
    {
        int saveSlot = Interlocked.Exchange(ref _pendingQuickSaveSlot, -1);
        if (saveSlot >= 0) SaveQuickSlot(saveSlot);
        if (cpu.PendingQuickLoadSlot >= 0) cpu.ShouldUnwind = true;
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
                if (!LoadQuickSlot(slot))
                {
                    _logger.Warn("SAVE", $"Quick-load slot {slot} unavailable.");
                    break;
                }
                _logger.Info("SAVE", $"Quick-loaded slot {slot}; resuming at pc=0x{_cpu.Pc:X8}.");
                _cpu.DispatchFunction(_cpu.Pc);
            }
            _logger.Info("EMU", $"Emulation thread finished frames={_cpu.FrameCount} exitRequested={_cpu.ExitRequested}.");
        }
        catch (Exception ex) { _emuFault = ex; }
        finally { _emuFinished = true; _hostActionDone.Set(); _frameReached.Set(); }
    }
}
