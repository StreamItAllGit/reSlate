// @module: reStation.Core.Cpu.PsxCpu.Scheduling
// @purpose: Cycle-accurate NextEventCycle wake computation, nested interrupt/overlay RunSlice, module lookup, and idle-spin fast-forward.
// @depends-on: reStation.Contracts, reStation.Core.Cpu.PsxCpu
// @exports: PsxCpu (partial)
// @owner: devv-cpu-cd (T2)

using reStation.Contracts;

namespace reStation.Core.Cpu;

public sealed partial class PsxCpu
{
    public long SubTickDeadlineCycle { get; set; }

    public void RunSlice(uint entryPc, uint unwindTarget)
    {
        _unwindStack.Push((ShouldUnwind, UnwindTargetPc));
        ShouldUnwind = false;
        UnwindTargetPc = unwindTarget;
        try
        {
            Call(entryPc);
        }
        finally
        {
            bool unwindingOut = ExitRequested || PendingQuickLoadSlot >= 0;
            var prev = _unwindStack.Pop();
            UnwindTargetPc = prev.UnwindTarget;
            if (!unwindingOut) ShouldUnwind = prev.ShouldUnwind;
        }
    }

    internal void PollSubTickWatchdog()
    {
        if (SubTickDeadlineCycle > 0 && TotalCycles >= SubTickDeadlineCycle) ShouldUnwind = true;
        else NextEventCycle = TotalCycles + 4096;
    }

    public long ComputeNextWakeCycle() =>
        Math.Min(Gpu.NextVBlankCycle,
            Math.Min(CdRom.GetNextWakeCycle(),
                Math.Min(Joypad.NextWakeCycle, TimersAndDma.GetNextWakeCycle(TotalCycles))));

    public void FastForwardIdleSpin()
    {
        if (InInterrupt || ShouldUnwind || ExitRequested) return;
        long minWake = TotalCycles + 64;
        long maxWake = Math.Max(minWake, Gpu.NextVBlankCycle);
        long wake = Math.Clamp(ComputeNextWakeCycle(), minWake, maxWake);
        TotalCycles = wake;
        NextEventCycle = wake;
    }

    public bool HasCompiledEntry(uint pc)
    {
        uint canonPc = 0x80000000U | (pc & 0x1FFFFFFFU);
        IRecompModule? mod = ResolveModule(canonPc);
        return mod is not null && canonPc >= mod.TextStart && canonPc < mod.TextEnd;
    }

    internal IRecompModule? ResolveModule(uint canonPc)
    {
        IRecompModule? active = _modules[Math.Clamp(ActiveExeId, 0, ModuleSlotCount - 1)];
        if (active is not null && canonPc >= active.TextStart && canonPc < active.TextEnd) return active;
        IRecompModule? resident = _modules[0];
        if (resident is not null && canonPc >= resident.TextStart && canonPc < resident.TextEnd) return resident;
        foreach (IRecompModule? mod in _modules)
            if (mod is not null && canonPc >= mod.TextStart && canonPc < mod.TextEnd) return mod;
        return active;
    }

    internal int ResolveOverlayExeId(string exeName, uint pc0)
    {
        string wanted = Path.GetFileName(exeName);
        foreach (IRecompModule? mod in _modules)
            if (mod is not null && !string.IsNullOrEmpty(wanted) && string.Equals(Path.GetFileName(mod.ExeName), wanted, StringComparison.OrdinalIgnoreCase))
                return mod.ExeId;
        foreach (IRecompModule? mod in _modules)
            if (mod is not null && pc0 >= mod.TextStart && pc0 < mod.TextEnd)
                return mod.ExeId;
        return ActiveExeId;
    }

    internal (byte LeftX, byte LeftY, byte RightX, byte RightY) GetControllerAxes(int port)
    {
        int o = Math.Clamp(port, 0, 1) * 4;
        return (_padAxes[o], _padAxes[o + 1], _padAxes[o + 2], _padAxes[o + 3]);
    }

    public void SetCardIdentity(string? cardIdentity) => Bios.MemCard.SetCardIdentity(cardIdentity);
}
