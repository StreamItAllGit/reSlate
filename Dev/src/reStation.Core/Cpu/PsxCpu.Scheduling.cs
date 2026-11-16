// @module: reStation.Core.Cpu.PsxCpu.Scheduling
// @purpose: Cycle-accurate NextEventCycle wake computation, nested interrupt/overlay RunSlice, module lookup, and idle-spin fast-forward.
// @depends-on: reStation.Contracts, reStation.Core.Cpu.PsxCpu
// @exports: PsxCpu (partial)
// @owner: devv-cpu-cd (T2)

using System.Buffers.Binary;
using System.Security.Cryptography;
using reStation.Contracts;

namespace reStation.Core.Cpu;

public sealed partial class PsxCpu
{
    public long SubTickDeadlineCycle { get; set; }

    public void RunSlice(uint entryPc, uint returnSentinelPc = IReStationCpu.ReturnSentinelPc, bool inSubTickRender = false)
    {
        if (inSubTickRender && InSubTickRender)
        {
            Logger?.Warn("CPU", $"Rejected nested RunSlice(inSubTickRender=true) at pc=0x{entryPc:X8}.");
            return;
        }
        _unwindStack.Push((ShouldUnwind, UnwindTargetPc, InSubTickRender, _inCompiledTrampoline, _hasPendingTailJump, _pendingTailJumpPc));
        ShouldUnwind = false;
        UnwindTargetPc = returnSentinelPc;
        _inCompiledTrampoline = false;
        _hasPendingTailJump = false;
        _pendingTailJumpPc = 0U;
        if (inSubTickRender) InSubTickRender = true;
        try
        {
            Call(entryPc);
        }
        finally
        {
            bool unwindingOut = ExitRequested || PendingQuickLoadSlot >= 0;
            var prev = _unwindStack.Pop();
            UnwindTargetPc = prev.UnwindTarget;
            InSubTickRender = prev.InSubTickRender;
            _inCompiledTrampoline = prev.InCompiledTrampoline;
            _hasPendingTailJump = prev.HasPendingTailJump;
            _pendingTailJumpPc = prev.PendingTailJumpPc;
            if (inSubTickRender) SubTickDeadlineCycle = 0;
            if (!unwindingOut) ShouldUnwind = prev.ShouldUnwind;
        }
    }

    internal void PollSubTickWatchdog()
    {
        if (SubTickDeadlineCycle > 0 && TotalCycles >= SubTickDeadlineCycle) ShouldUnwind = true;
        else NextEventCycle = SubTickDeadlineCycle > TotalCycles
            ? Math.Min(TotalCycles + 4096, SubTickDeadlineCycle)
            : TotalCycles + 4096;
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

    public bool TryAdvanceToNextVBlank()
    {
        if (ShouldUnwind || ExitRequested || InSubTickRender || (TimersAndDma.InterruptMask & 1) == 0)
            return false;
        long targetVBlank = Math.Max(TotalCycles + 64, Gpu.NextVBlankCycle);
        int guard = 0;
        while (TotalCycles < targetVBlank && !ShouldUnwind && !ExitRequested && guard++ < 1024)
        {
            long nextWake = Math.Clamp(ComputeNextWakeCycle(), TotalCycles + 64, targetVBlank);
            TotalCycles = nextWake;
            PollEvents();
        }
        if (TotalCycles < targetVBlank && !ShouldUnwind && !ExitRequested)
        {
            TotalCycles = targetVBlank;
            PollEvents();
        }
        return true;
    }

    public bool HasCompiledEntry(uint pc)
    {
        uint canonPc = 0x80000000U | (pc & 0x1FFFFFFFU);
        IRecompModule? mod = ResolveModule(canonPc);
        if (mod is null) return false;
        if (mod is reStation.Core.Aot.IRecompCompiledEntryLookup lookup) return lookup.ContainsCompiledEntry(canonPc);
        return canonPc >= mod.TextStart && canonPc < mod.TextEnd;
    }

    internal IRecompModule? ResolveModule(uint canonPc)
    {
        OverlayEntry[] overlays = Volatile.Read(ref _overlays);
        foreach (OverlayEntry entry in overlays)
            if (canonPc >= entry.Mod.TextStart && canonPc < entry.Mod.TextEnd && IsOverlayRamMatch(entry))
                return entry.Mod;

        IRecompModule? activeNonOverlay = null;
        IRecompModule? resident = null;
        IRecompModule? anyMatch = null;
        foreach (IRecompModule mod in _modules)
        {
            if (mod is IOverlayRecompModule) continue;
            if (mod.ExeId == ActiveExeId) activeNonOverlay ??= mod;
            if (mod.ExeId == 0) resident ??= mod;
            if (canonPc >= mod.TextStart && canonPc < mod.TextEnd)
            {
                if (mod.ExeId == ActiveExeId) return mod;
                anyMatch ??= mod;
            }
        }
        if (resident is not null && canonPc >= resident.TextStart && canonPc < resident.TextEnd) return resident;
        return anyMatch ?? activeNonOverlay;
    }

    internal int ResolveOverlayExeId(string exeName, uint pc0)
    {
        string wanted = Path.GetFileName(exeName);
        if (!string.IsNullOrEmpty(wanted))
        {
            foreach (IRecompModule mod in _modules)
                if (string.Equals(Path.GetFileName(mod.ExeName), wanted, StringComparison.OrdinalIgnoreCase))
                    return mod.ExeId;
        }
        OverlayEntry[] overlays = Volatile.Read(ref _overlays);
        foreach (OverlayEntry entry in overlays)
            if (pc0 >= entry.Mod.TextStart && pc0 < entry.Mod.TextEnd && IsOverlayRamMatch(entry))
                return entry.Mod.ExeId;
        foreach (IRecompModule mod in _modules)
            if (mod is not IOverlayRecompModule && pc0 >= mod.TextStart && pc0 < mod.TextEnd)
                return mod.ExeId;
        return ActiveExeId;
    }

    private void RegisterOverlayModule(IOverlayRecompModule overlay)
    {
        if (!_ramWriteHooked) { Bus.OnRamWriteRange += OnBusRamWriteRange; _ramWriteHooked = true; }
        string normHex = (overlay.ContentSha256Hex ?? string.Empty).Trim().ToLowerInvariant();
        byte[]? expectedBytes = normHex.Length == 64 && TryParseHex32(normHex, out byte[] decoded) ? decoded : null;
        uint physStart = overlay.LoadRamAddress & 0x1FFFFFFFU & (PsxMemoryBus.RamSize - 1U);
        ulong physEnd = (ulong)physStart + (uint)Math.Max(0, overlay.ContentSizeBytes);
        OverlayEntry[] existing = Volatile.Read(ref _overlays);
        int dupOverlayIdx = Array.FindIndex(existing, e =>
            e.Mod.ExeId == overlay.ExeId && e.PhysStart == physStart && string.Equals(e.NormHex, normHex, StringComparison.Ordinal));
        var entry = new OverlayEntry(overlay, physStart, physEnd, normHex, expectedBytes);
        if (dupOverlayIdx >= 0)
        {
            var next = (OverlayEntry[])existing.Clone();
            next[dupOverlayIdx] = entry;
            Volatile.Write(ref _overlays, next);
            int modIdx = _modules.FindIndex(m => ReferenceEquals(m, existing[dupOverlayIdx].Mod));
            if (modIdx >= 0) _modules[modIdx] = overlay; else _modules.Add(overlay);
        }
        else
        {
            var next = new OverlayEntry[existing.Length + 1];
            Array.Copy(existing, next, existing.Length);
            next[^1] = entry;
            Volatile.Write(ref _overlays, next);
            _modules.Add(overlay);
        }
    }

    private void OnBusRamWriteRange(uint writeStart, uint writeLen)
    {
        ulong writeEnd = (ulong)writeStart + writeLen;
        foreach (OverlayEntry entry in Volatile.Read(ref _overlays))
            if (entry.PhysEnd > writeStart && writeEnd > entry.PhysStart)
                Interlocked.Increment(ref entry.CurrentGeneration);
    }

    private bool IsOverlayRamMatch(OverlayEntry entry)
    {
        int size = entry.Mod.ContentSizeBytes;
        if (size <= 0 || !Bus.TryGetRamSpan(entry.Mod.LoadRamAddress, (uint)size, out Span<byte> ramSpan)) return false;
        long gen = Interlocked.Read(ref entry.CurrentGeneration);
        if (entry.CachedGeneration == gen) return entry.CachedMatched;
        Span<byte> digest = stackalloc byte[32];
        System.Security.Cryptography.SHA256.HashData(ramSpan, digest);
        OverlayHashComputations++;
        bool matched = entry.ExpectedHashBytes is not null
            ? digest.SequenceEqual(entry.ExpectedHashBytes)
            : string.Equals(Convert.ToHexString(digest), entry.NormHex, StringComparison.OrdinalIgnoreCase);
        entry.CachedGeneration = gen;
        entry.CachedMatched = matched;
        return matched;
    }

    private static int HexVal(char c) =>
        c is >= '0' and <= '9' ? c - '0' : c is >= 'a' and <= 'f' ? c - 'a' + 10 : c is >= 'A' and <= 'F' ? c - 'A' + 10 : -1;

    private static bool TryParseHex32(string hex, out byte[] bytes)
    {
        bytes = [];
        if (hex.Length != 64) return false;
        var buf = new byte[32];
        for (int i = 0; i < 32; i++)
        {
            int hi = HexVal(hex[i * 2]), lo = HexVal(hex[i * 2 + 1]);
            if (hi < 0 || lo < 0) return false;
            buf[i] = (byte)((hi << 4) | lo);
        }
        bytes = buf;
        return true;
    }

    private sealed class OverlayEntry(IOverlayRecompModule mod, uint physStart, ulong physEnd, string normHex, byte[]? expectedHashBytes)
    {
        public IOverlayRecompModule Mod { get; } = mod;
        public uint PhysStart { get; } = physStart;
        public ulong PhysEnd { get; } = physEnd;
        public string NormHex { get; } = normHex;
        public byte[]? ExpectedHashBytes { get; } = expectedHashBytes;
        public long CurrentGeneration = 1;
        public long CachedGeneration;
        public bool CachedMatched;
    }

    internal (byte LeftX, byte LeftY, byte RightX, byte RightY) GetControllerAxes(int port)
    {
        int o = Math.Clamp(port, 0, 1) * 4;
        return (_padAxes[o], _padAxes[o + 1], _padAxes[o + 2], _padAxes[o + 3]);
    }

    public void SetCardIdentity(string? cardIdentity) => Bios.MemCard.SetCardIdentity(cardIdentity);

    internal void ClearBringupBlockCache() => _bringupBlockCache.Clear();

    private void RecordBringupCall(uint canonPc)
    {
        if (BringupRecorder is not { } rec) return;
        var key = (canonPc, ActiveExeId);
        if (!_bringupBlockCache.TryGetValue(key, out (string Sha, uint Size) info))
        {
            if (!Bus.TryGetRamSpan(canonPc, 64U, out Span<byte> codeSpan)) return;
            int offset = 0;
            while (offset + 8 <= codeSpan.Length)
            {
                uint word = BinaryPrimitives.ReadUInt32LittleEndian(codeSpan.Slice(offset, 4));
                offset += 4;
                if (word == 0x03E00008U) { offset += 4; break; }
            }
            uint byteSize = (uint)Math.Max(4, Math.Min(offset, codeSpan.Length));
            Span<byte> digest = stackalloc byte[32];
            SHA256.HashData(codeSpan[..(int)byteSize], digest);
            info = (Convert.ToHexString(digest).ToLowerInvariant(), byteSize);
            _bringupBlockCache[key] = info;
        }
        rec.RecordDiscoveredBlock(info.Sha, canonPc, info.Size, ActiveExeId);
    }
}
