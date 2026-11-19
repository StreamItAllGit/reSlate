// @module: reStation.Core.Bios.PsxBiosHle.Events
// @purpose: BIOS IRQ trampolines, event delivery, TestEvent idle-spin fast-forward, and Span bulk RAM operations.
// @depends-on: reStation.Core.Bios.PsxBiosHle
// @exports: PsxBiosHle (partial)
// @owner: devv-cpu-cd (T2)

using System.Text;

namespace reStation.Core.Bios;

public sealed partial class PsxBiosHle
{
    private const uint KernelToT0Addr = 0x80000100U;
    private const uint IntRpTableAddr = 0x80000200U;
    private const int IntRpPriorityCount = 4;
    private const uint C0TableAddr = 0x80000500U;
    private const uint C0FastDsrTargetAddr = 0x80000540U;

    private uint _lastFailedEventHandle;
    private int _failedEventPolls;
    private long _lastVBlankEventSequence = -1;
    private long _lastVBlankIntRpSequence = -1;

    private void EnsureKernelTables()
    {
        if (_cpu.Bus.Read32(KernelToT0Addr) == 0U)
            _cpu.Bus.Write32(KernelToT0Addr, IntRpTableAddr);
        if (_cpu.Bus.Read32(C0TableAddr + 0x18U) == 0U)
            _cpu.Bus.Write32(C0TableAddr + 0x18U, C0FastDsrTargetAddr);
    }

    private void SysDeqIntRp(uint prio, uint node)
    {
        EnsureKernelTables();
        if (prio >= IntRpPriorityCount || node == 0U) return;
        uint headAddr = _cpu.Bus.Read32(KernelToT0Addr) + prio * 8U;
        uint prev = 0U;
        uint cur = _cpu.Bus.Read32(headAddr);
        for (int guard = 0; guard < 64 && cur != 0U; guard++)
        {
            uint next = _cpu.Bus.Read32(cur);
            if ((cur & 0x1FFFFFFFU) == (node & 0x1FFFFFFFU))
            {
                if (prev == 0U) _cpu.Bus.Write32(headAddr, next);
                else _cpu.Bus.Write32(prev, next);
                _cpu.Bus.Write32(cur, 0U);
                return;
            }
            prev = cur;
            cur = next;
        }
    }

    private void SysEnqIntRp(uint prio, uint node)
    {
        EnsureKernelTables();
        if (prio >= IntRpPriorityCount || node == 0U) return;
        SysDeqIntRp(prio, node);
        uint headAddr = _cpu.Bus.Read32(KernelToT0Addr) + prio * 8U;
        uint head = _cpu.Bus.Read32(headAddr);
        _cpu.Bus.Write32(node, head);
        _cpu.Bus.Write32(headAddr, node);
    }

    private void RunIntRpChains()
    {
        EnsureKernelTables();
        uint tableAddr = _cpu.Bus.Read32(KernelToT0Addr);
        if (tableAddr == 0U) return;
        for (uint prio = 0; prio < IntRpPriorityCount; prio++)
        {
            uint node = _cpu.Bus.Read32(tableAddr + prio * 8U);
            for (int guard = 0; guard < 64 && node != 0U; guard++)
            {
                uint next = _cpu.Bus.Read32(node);
                uint func2 = _cpu.Bus.Read32(node + 4U);
                uint func1 = _cpu.Bus.Read32(node + 8U);
                bool runFunc2 = true;
                if (func1 != 0U)
                {
                    _cpu.R[2] = 0U;
                    _cpu.R[31] = IrqReturnSentinel;
                    _cpu.RunSlice(func1, IrqReturnSentinel);
                    runFunc2 = _cpu.R[2] != 0U;
                }
                if (runFunc2 && func2 != 0U)
                {
                    _cpu.R[31] = IrqReturnSentinel;
                    _cpu.RunSlice(func2, IrqReturnSentinel);
                }
                node = next;
            }
        }
    }

    private uint OpenKernelEvent(uint classId, uint spec, uint mode, uint callback)
    {
        if (mode == 0x1000U && callback != 0U)
        {
            for (int i = 0; i < _events.Count; i++)
            {
                KernelEvent existing = _events[i];
                if (!existing.Closed && existing.ClassId == classId && existing.Spec == spec && existing.Mode == mode && existing.Callback == callback)
                {
                    existing.Enabled = false;
                    existing.Delivered = false;
                    return (uint)(i | 0xF1000000U);
                }
            }
        }
        for (int i = 0; i < _events.Count; i++)
        {
            if (!_events[i].Closed) continue;
            _events[i] = new KernelEvent { ClassId = classId, Spec = spec, Mode = mode, Callback = callback };
            return (uint)(i | 0xF1000000U);
        }
        _events.Add(new KernelEvent { ClassId = classId, Spec = spec, Mode = mode, Callback = callback });
        return (uint)((_events.Count - 1) | 0xF1000000U);
    }

    private bool CloseKernelEvent(uint h)
    {
        uint hi = h & 0xFFFF0000U;
        if (hi != 0xF1000000U && hi != 0U) return false;
        int i = (int)(h & 0xFFFFU);
        if (i < 0 || i >= _events.Count || _events[i].Closed) return false;
        KernelEvent ev = _events[i];
        ev.Closed = true;
        ev.Enabled = false;
        ev.Delivered = false;
        if (_lastFailedEventHandle == h) { _lastFailedEventHandle = 0; _failedEventPolls = 0; }
        return true;
    }

    private void SaveAndClearIntRpHeads(Span<uint> saved)
    {
        EnsureKernelTables();
        uint tableAddr = _cpu.Bus.Read32(KernelToT0Addr);
        if (tableAddr == 0U) { saved.Clear(); return; }
        for (int p = 0; p < saved.Length; p++)
        {
            uint headAddr = tableAddr + (uint)(p * 8);
            saved[p] = _cpu.Bus.Read32(headAddr);
            _cpu.Bus.Write32(headAddr, 0U);
        }
    }

    private void RestoreIntRpHeads(ReadOnlySpan<uint> saved)
    {
        EnsureKernelTables();
        uint tableAddr = _cpu.Bus.Read32(KernelToT0Addr);
        if (tableAddr == 0U) return;
        for (int p = 0; p < saved.Length; p++)
            _cpu.Bus.Write32(tableAddr + (uint)(p * 8), saved[p]);
    }

    public void OnVBlank()
    {
        _failedEventPolls = 0;
        WritePadState();
        long seq = _cpu.VBlankSequence;
        if (_lastVBlankEventSequence == seq) return;
        _lastVBlankEventSequence = seq;
        MemCard.FlushIfDirty(seq);
        foreach (KernelEvent ev in _events)
            if (!ev.Closed && ev.Enabled && (ev.ClassId is 0xF0000001U or 0xF2000003U) && ev.Spec == 0x0002U)
                TriggerEvent(ev);
    }

    public void DeliverClassEvent(uint classId, uint spec)
    {
        _failedEventPolls = 0;
        foreach (KernelEvent ev in _events)
            if (!ev.Closed && ev.Enabled && ev.ClassId == classId && (ev.Spec == spec || ev.Spec == 0xFFFFU))
                TriggerEvent(ev);
    }

    private void DeliverCardIoEnd() => DeliverCardEvent(0x0004U);

    private void DeliverCardEvent(uint spec)
    {
        DeliverClassEvent(0xF0000011U, spec);
        DeliverClassEvent(0xF4000001U, spec);
    }

    private bool HasEnabledEvent(uint classId, uint spec) =>
        _events.Any(ev => !ev.Closed && ev.Enabled && ev.ClassId == classId && (ev.Spec == spec || ev.Spec == 0xFFFFU));

    private void TriggerEvent(KernelEvent ev)
    {
        if (ev.Mode != 0x1000U || ev.Callback == 0) { ev.Delivered = true; return; }
        bool prevIrq = _cpu.InInterrupt;
        _cpu.InInterrupt = true;
        Span<uint> savedR = stackalloc uint[32];
        _cpu.R.AsSpan().CopyTo(savedR);
        uint savedHi = _cpu.Hi, savedLo = _cpu.Lo, savedPc = _cpu.Pc;
        try
        {
            _cpu.R[31] = IrqReturnSentinel;
            _cpu.RunSlice(ev.Callback, IrqReturnSentinel);
        }
        finally
        {
            savedR.CopyTo(_cpu.R);
            _cpu.Hi = savedHi; _cpu.Lo = savedLo; _cpu.Pc = savedPc;
            _cpu.InInterrupt = prevIrq;
        }
    }

    public void TryRunInterruptHook()
    {
        EnsureKernelTables();
        uint tableAddr = _cpu.Bus.Read32(KernelToT0Addr);
        bool hasIntRp = false;
        if (tableAddr != 0U)
        {
            for (uint p = 0; p < IntRpPriorityCount; p++)
            {
                if (_cpu.Bus.Read32(tableAddr + p * 8U) != 0U) { hasIntRp = true; break; }
            }
        }
        uint beforeStat = _cpu.TimersAndDma.InterruptStatus;
        const uint vblankBit = 1U << 0;
        if ((beforeStat & vblankBit) != 0U)
        {
            long seq = _cpu.VBlankSequence;
            if (_lastVBlankIntRpSequence == seq)
            {
                _cpu.TimersAndDma.InterruptStatus &= ~vblankBit;
                beforeStat &= ~vblankBit;
                if (beforeStat == 0U) return;
            }
            else
            {
                _lastVBlankIntRpSequence = seq;
            }
        }
        if (!hasIntRp && _hookEntryIntBufAddr == 0)
        {
            _cpu.TimersAndDma.InterruptStatus = 0;
            return;
        }
        _cpu.InInterrupt = true;
        _cpu.TimersAndDma.AckedIrqBits = 0;
        Span<uint> savedR = stackalloc uint[32];
        _cpu.R.AsSpan().CopyTo(savedR);
        uint savedHi = _cpu.Hi, savedLo = _cpu.Lo, savedPc = _cpu.Pc;
        try
        {
            if (hasIntRp) RunIntRpChains();
            if (_hookEntryIntBufAddr != 0)
            {
                uint targetPc = _cpu.Bus.Read32(_hookEntryIntBufAddr);
                _cpu.R[29] = _cpu.Bus.Read32(_hookEntryIntBufAddr + 4);
                _cpu.R[30] = _cpu.Bus.Read32(_hookEntryIntBufAddr + 8);
                for (int i = 0; i < 8; i++) _cpu.R[16 + i] = _cpu.Bus.Read32(_hookEntryIntBufAddr + 12U + (uint)i * 4U);
                _cpu.R[28] = _cpu.Bus.Read32(_hookEntryIntBufAddr + 44U);
                _cpu.R[31] = IrqReturnSentinel;
                _cpu.R[2] = 1U;
                _cpu.RunSlice(targetPc, IrqReturnSentinel);
            }
        }
        finally
        {
            savedR.CopyTo(_cpu.R);
            _cpu.Hi = savedHi; _cpu.Lo = savedLo; _cpu.Pc = savedPc;
            _cpu.InInterrupt = false;
            uint unacked = beforeStat & ~_cpu.TimersAndDma.AckedIrqBits;
            if (unacked != 0) _cpu.TimersAndDma.InterruptStatus &= ~unacked;
        }
    }

    private bool TestEvent(uint h)
    {
        int i = (int)(h & 0xFFFFU);
        if (i < 0 || i >= _events.Count || _events[i].Closed) return false;
        bool d = _events[i].Delivered;
        _events[i].Delivered = false;
        if (d) { _failedEventPolls = 0; return true; }
        if (h == _lastFailedEventHandle) _failedEventPolls++;
        else { _lastFailedEventHandle = h; _failedEventPolls = 1; }
        if (!_cpu.InInterrupt && _failedEventPolls >= 2) _cpu.FastForwardIdleSpin();
        return false;
    }

    private void SetEventEnabled(uint h, bool en) { int i = (int)(h & 0xFFFFU); if (i >= 0 && i < _events.Count && !_events[i].Closed) _events[i].Enabled = en; }
    private void ClearEvent(uint h) { int i = (int)(h & 0xFFFFU); if (i >= 0 && i < _events.Count && !_events[i].Closed) _events[i].Delivered = false; }

    private void ZeroRam(uint d, uint n)
    {
        if (n == 0) return;
        if (_cpu.Bus.TryGetRamSpan(d, n, out var span)) { span.Clear(); _cpu.Bus.NotifyRamWriteRange(d, n); }
        else for (uint i = 0; i < n; i++) _cpu.Bus.Write8(d + i, 0);
    }

    private void FillRam(uint d, byte v, uint n)
    {
        if (n == 0) return;
        if (_cpu.Bus.TryGetRamSpan(d, n, out var span)) { span.Fill(v); _cpu.Bus.NotifyRamWriteRange(d, n); }
        else for (uint i = 0; i < n; i++) _cpu.Bus.Write8(d + i, v);
    }

    private void CopyRam(uint d, uint s, uint n, bool swapOrder)
    {
        if (n == 0) return;
        uint dst = swapOrder ? s : d, src = swapOrder ? d : s;
        if (_cpu.Bus.TryGetRamSpan(src, n, out var srcSpan) && _cpu.Bus.TryGetRamSpan(dst, n, out var dstSpan))
        {
            srcSpan.CopyTo(dstSpan);
            _cpu.Bus.NotifyRamWriteRange(dst, n);
            return;
        }
        byte[] t = new byte[n];
        for (uint i = 0; i < n; i++) t[i] = _cpu.Bus.Read8(src + i);
        for (uint i = 0; i < n; i++) _cpu.Bus.Write8(dst + i, t[i]);
    }

    /// <summary>Serializes BIOS HLE kernel events, heap cursors, RNG seed, and hook registers for the "BIOS" RSTX chunk.</summary>
    public byte[] CaptureSnapshot()
    {
        using var ms = new MemoryStream(128);
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write(0x534F4942U); // "BIOS"
        w.Write((ushort)1);
        w.Write(_hookEntryIntBufAddr); w.Write(_padBufAddr); w.Write(_padType); w.Write(_padLen);
        w.Write(_randSeed); w.Write(_heapPtr); w.Write(_heapEnd);
        w.Write(_lastFailedEventHandle); w.Write(_failedEventPolls);
        w.Write(_lastLoadedExeName ?? string.Empty);
        w.Write(_events.Count);
        foreach (KernelEvent ev in _events)
        {
            w.Write(ev.ClassId); w.Write(ev.Spec); w.Write(ev.Mode); w.Write(ev.Callback);
            w.Write(ev.Enabled); w.Write(ev.Delivered);
        }
        w.Write(_padBuf2Addr); w.Write(_padLen2);
        w.Write(0x434C5344U);
        foreach (KernelEvent ev in _events) w.Write(ev.Closed);
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>Restores BIOS HLE kernel state from an "RSTX" "BIOS" chunk.</summary>
    public void RestoreSnapshot(ReadOnlySpan<byte> payload)
    {
        using var ms = new MemoryStream(payload.ToArray(), writable: false);
        using var r = new BinaryReader(ms, Encoding.UTF8);
        if (r.ReadUInt32() != 0x534F4942U || r.ReadUInt16() != 1)
            throw new InvalidDataException("Invalid BIOS RSTX chunk header.");
        _hookEntryIntBufAddr = r.ReadUInt32(); _padBufAddr = r.ReadUInt32();
        _padType = r.ReadUInt32(); _padLen = r.ReadUInt32();
        _randSeed = r.ReadUInt32(); _heapPtr = r.ReadUInt32(); _heapEnd = r.ReadUInt32();
        _lastFailedEventHandle = r.ReadUInt32(); _failedEventPolls = r.ReadInt32();
        _lastLoadedExeName = r.ReadString();
        int count = r.ReadInt32();
        if (count is < 0 or > 4096) throw new InvalidDataException($"Invalid BIOS event count: {count}.");
        _events.Clear();
        for (int i = 0; i < count; i++)
            _events.Add(new KernelEvent
            {
                ClassId = r.ReadUInt32(), Spec = r.ReadUInt32(), Mode = r.ReadUInt32(), Callback = r.ReadUInt32(),
                Enabled = r.ReadBoolean(), Delivered = r.ReadBoolean()
            });
        if (ms.Position + 8 <= ms.Length) { _padBuf2Addr = r.ReadUInt32(); _padLen2 = r.ReadUInt32(); }
        else { _padBuf2Addr = 0; _padLen2 = 0; }
        if (ms.Position + 4 + count <= ms.Length && r.ReadUInt32() == 0x434C5344U)
        {
            for (int i = 0; i < count; i++) _events[i].Closed = r.ReadBoolean();
        }
        _lastVBlankEventSequence = -1;
        _lastVBlankIntRpSequence = -1;
    }
}
