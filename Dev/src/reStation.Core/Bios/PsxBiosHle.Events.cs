// @module: reStation.Core.Bios.PsxBiosHle.Events
// @purpose: BIOS IRQ trampolines, event delivery, TestEvent idle-spin fast-forward, and Span bulk RAM operations.
// @depends-on: reStation.Core.Bios.PsxBiosHle
// @exports: PsxBiosHle (partial)
// @owner: devv-cpu-cd (T2)

namespace reStation.Core.Bios;

public sealed partial class PsxBiosHle
{
    private uint _lastFailedEventHandle;
    private int _failedEventPolls;

    public void OnVBlank()
    {
        _failedEventPolls = 0;
        WritePadState();
        foreach (KernelEvent ev in _events)
            if (ev.Enabled && (ev.ClassId is 0xF0000001U or 0xF2000003U) && ev.Spec == 0x0002U)
                TriggerEvent(ev);
    }

    public void DeliverClassEvent(uint classId, uint spec)
    {
        _failedEventPolls = 0;
        foreach (KernelEvent ev in _events)
            if (ev.Enabled && ev.ClassId == classId && (ev.Spec == spec || ev.Spec == 0xFFFFU))
                TriggerEvent(ev);
    }

    private void DeliverCardIoEnd()
    {
        DeliverClassEvent(0xF0000011U, 0x0004U);
        DeliverClassEvent(0xF4000001U, 0x0004U);
    }

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
        if (_hookEntryIntBufAddr == 0) { _cpu.TimersAndDma.InterruptStatus = 0; return; }
        _cpu.InInterrupt = true;
        _cpu.TimersAndDma.AckedIrqBits = 0;
        uint beforeStat = _cpu.TimersAndDma.InterruptStatus;
        Span<uint> savedR = stackalloc uint[32];
        _cpu.R.AsSpan().CopyTo(savedR);
        uint savedHi = _cpu.Hi, savedLo = _cpu.Lo, savedPc = _cpu.Pc;
        try
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
        if (i < 0 || i >= _events.Count) return false;
        bool d = _events[i].Delivered;
        _events[i].Delivered = false;
        if (d) { _failedEventPolls = 0; return true; }
        if (h == _lastFailedEventHandle) _failedEventPolls++;
        else { _lastFailedEventHandle = h; _failedEventPolls = 1; }
        if (!_cpu.InInterrupt && _failedEventPolls >= 2) _cpu.FastForwardIdleSpin();
        return false;
    }

    private void SetEventEnabled(uint h, bool en) { int i = (int)(h & 0xFFFFU); if (i >= 0 && i < _events.Count) _events[i].Enabled = en; }
    private void ClearEvent(uint h) { int i = (int)(h & 0xFFFFU); if (i >= 0 && i < _events.Count) _events[i].Delivered = false; }

    private void ZeroRam(uint d, uint n)
    {
        if (n == 0) return;
        if (_cpu.Bus.TryGetRamSpan(d, n, out var span)) span.Clear();
        else for (uint i = 0; i < n; i++) _cpu.Bus.Write8(d + i, 0);
    }

    private void FillRam(uint d, byte v, uint n)
    {
        if (n == 0) return;
        if (_cpu.Bus.TryGetRamSpan(d, n, out var span)) span.Fill(v);
        else for (uint i = 0; i < n; i++) _cpu.Bus.Write8(d + i, v);
    }

    private void CopyRam(uint d, uint s, uint n, bool swapOrder)
    {
        if (n == 0) return;
        uint dst = swapOrder ? s : d, src = swapOrder ? d : s;
        if (_cpu.Bus.TryGetRamSpan(src, n, out var srcSpan) && _cpu.Bus.TryGetRamSpan(dst, n, out var dstSpan))
        {
            srcSpan.CopyTo(dstSpan);
            return;
        }
        byte[] t = new byte[n];
        for (uint i = 0; i < n; i++) t[i] = _cpu.Bus.Read8(src + i);
        for (uint i = 0; i < n; i++) _cpu.Bus.Write8(dst + i, t[i]);
    }
}
