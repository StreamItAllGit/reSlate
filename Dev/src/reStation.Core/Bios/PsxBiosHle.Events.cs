// @module: reStation.Core.Bios.PsxBiosHle.Events
// @purpose: BIOS IRQ trampolines, event delivery, TestEvent idle-spin fast-forward, and Span bulk RAM operations.
// @depends-on: reStation.Core.Bios.PsxBiosHle
// @exports: PsxBiosHle (partial)
// @owner: devv-cpu-cd (T2)

using System.Text;

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
    }
}
