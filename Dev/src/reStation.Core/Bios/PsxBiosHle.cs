// @module: reStation.Core.Bios.PsxBiosHle
// @purpose: Generalized PS1 BIOS HLE kernel (A0/B0/C0 vectors, syscalls, PS-X EXE loader, dynamic overlay dispatch, PAD, Memory Card).
// @depends-on: System.Buffers.Binary, System.Text, reStation.Contracts, reStation.Core.Cpu.PsxCpu
// @exports: PsxBiosHle
// @owner: devv-cpu-cd (T2)

using System.Buffers.Binary;
using System.Text;
using reStation.Contracts;
using reStation.Core.Cpu;

namespace reStation.Core.Bios;

public sealed partial class PsxBiosHle
{
    private const uint IrqReturnSentinel = 0xFFFFFFF0U;
    private static readonly byte[] PsxExeMagic = Encoding.ASCII.GetBytes("PS-X EXE");

    private sealed class KernelEvent
    {
        public uint ClassId, Spec, Mode, Callback;
        public bool Enabled, Delivered, Closed;
    }

    private readonly PsxCpu _cpu;
    private readonly List<KernelEvent> _events = new();
    private uint _hookEntryIntBufAddr, _padBufAddr, _padType, _padLen, _padBuf2Addr, _padLen2, _randSeed = 1;
    private uint _heapPtr = 0x80180000U, _heapEnd = 0x801F0000U;
    private string _lastLoadedExeName = string.Empty;

    public PsxBiosHle(PsxCpu cpu, string dataDir, string? cardIdentity = null)
    {
        _cpu = cpu ?? throw new ArgumentNullException(nameof(cpu));
        MemCard = new MemCardMcrStore(dataDir, cardIdentity);
        EnsureKernelTables();
    }

    public MemCardMcrStore MemCard { get; }

    public void HandleSyscall(uint code)
    {
        uint op = code != 0 ? code : _cpu.R[4];
        if (op == 1)
        {
            bool prev = (_cpu.Cop0Sr & 0x0401U) == 0x0401U;
            _cpu.Cop0Sr &= ~0x0401U;
            _cpu.R[2] = prev ? 1U : 0U;
        }
        else if (op == 2)
        {
            _cpu.Cop0Sr |= 0x0401U;
        }
        else if (op == 3 && _cpu.R[5] != 0)
        {
            _hookEntryIntBufAddr = _cpu.R[5];
        }
    }

    public void HandleVectorCall(uint vector)
    {
        uint fn = _cpu.R[9] & 0xFFU;
        switch (vector & 0x1FFFFFFFU)
        {
            case 0xA0U: HandleA0(fn); break;
            case 0xB0U: HandleB0(fn); break;
            case 0xC0U: HandleC0(fn); break;
        }
    }

    public bool LoadExecutableIntoRam(string discName, out uint pc0, out uint gp0, out uint spBase, out uint spOffset)
    {
        pc0 = gp0 = spBase = spOffset = 0;
        byte[]? exe = ReadExecutableBytes(discName);
        if (exe is null || exe.Length < 0x800 || !exe.AsSpan(0, 8).SequenceEqual(PsxExeMagic)) return false;

        pc0 = BinaryPrimitives.ReadUInt32LittleEndian(exe.AsSpan(0x10, 4));
        gp0 = BinaryPrimitives.ReadUInt32LittleEndian(exe.AsSpan(0x14, 4));
        uint tAddr = BinaryPrimitives.ReadUInt32LittleEndian(exe.AsSpan(0x18, 4)) & 0x1FFFFFU;
        int tSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(exe.AsSpan(0x1C, 4));
        spBase = BinaryPrimitives.ReadUInt32LittleEndian(exe.AsSpan(0x30, 4));
        spOffset = BinaryPrimitives.ReadUInt32LittleEndian(exe.AsSpan(0x34, 4));

        int copyLen = Math.Min(tSize, Math.Min(exe.Length - 0x800, PsxMemoryBus.RamSize - (int)tAddr));
        if (copyLen <= 0) return false;
        Array.Copy(exe, 0x800, _cpu.Bus.Ram, (int)tAddr, copyLen);
        _cpu.Bus.NotifyRamWriteRange(tAddr, (uint)copyLen);
        _cpu.ClearBringupBlockCache();
        _lastLoadedExeName = NormalizeDiscPath(discName);
        return true;
    }

    private byte[]? ReadExecutableBytes(string discName)
    {
        string clean = NormalizeDiscPath(discName);
        var entry = _cpu.Disc?.FindByName(clean);
        if (entry is not null)
        {
            string entryPath = Path.Combine(_cpu.GameDataDir, entry.RelativeFile.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(entryPath)) return File.ReadAllBytes(entryPath);
        }
        string direct = Path.Combine(_cpu.GameDataDir, clean.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(direct)) return File.ReadAllBytes(direct);
        string underDisc = Path.Combine(_cpu.GameDataDir, "disc", clean.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(underDisc) ? File.ReadAllBytes(underDisc) : null;
    }

    private void HandleA0(uint fn)
    {
        switch (fn)
        {
            case 0x00: _cpu.R[2] = OpenCardFile(ReadCString(_cpu.R[4]), _cpu.R[5]); break;
            case 0x01: _cpu.R[2] = SeekCardFile((int)_cpu.R[4], (int)_cpu.R[5], (int)_cpu.R[6]); break;
            case 0x02: _cpu.R[2] = ReadCardFile((int)_cpu.R[4], _cpu.R[5], (int)_cpu.R[6]); break;
            case 0x03: _cpu.R[2] = WriteCardFile((int)_cpu.R[4], _cpu.R[5], (int)_cpu.R[6]); break;
            case 0x04: _cpu.R[2] = CloseCardFile((int)_cpu.R[4]); break;
            case 0x06 or 0x3A: _cpu.ShouldUnwind = true; break;
            case 0x10: _cpu.R[2] = ToDigit((byte)_cpu.R[4]); break;
            case 0x13: SaveJmpBuf(_cpu.R[4], _cpu.R[31]); _cpu.R[2] = 0; break;
            case 0x14: LoadJmpBuf(_cpu.R[4]); _cpu.R[2] = _cpu.R[5] == 0 ? 1U : _cpu.R[5]; break;
            case 0x17: _cpu.R[2] = (uint)string.CompareOrdinal(ReadCString(_cpu.R[4]), ReadCString(_cpu.R[5])); break;
            case 0x18: _cpu.R[2] = (uint)CompareN(ReadCString(_cpu.R[4]), ReadCString(_cpu.R[5]), (int)_cpu.R[6]); break;
            case 0x19: WriteCString(_cpu.R[4], ReadCString(_cpu.R[5])); _cpu.R[2] = _cpu.R[4]; break;
            case 0x1B: _cpu.R[2] = (uint)ReadCString(_cpu.R[4]).Length; break;
            case 0x15 or 0x1E: WriteCString(_cpu.R[4], ReadCString(_cpu.R[4]) + ReadCString(_cpu.R[5])); _cpu.R[2] = _cpu.R[4]; break;
            case 0x28: ZeroRam(_cpu.R[4], _cpu.R[5]); break;
            case 0x27 or 0x29 or 0x2A: CopyRam(_cpu.R[4], _cpu.R[5], _cpu.R[6], fn != 0x2A); break;
            case 0x2B: FillRam(_cpu.R[4], (byte)_cpu.R[5], _cpu.R[6]); _cpu.R[2] = _cpu.R[4]; break;
            case 0x2F: _randSeed = _randSeed * 1103515245U + 12345U; _cpu.R[2] = (_randSeed >> 16) & 0x7FFFU; break;
            case 0x30: _randSeed = _cpu.R[4]; break;
            case 0x33: _cpu.R[2] = AllocHeap(_cpu.R[4]); break;
            case 0x39: _heapPtr = _cpu.R[4]; _heapEnd = _cpu.R[4] + _cpu.R[5]; break;
            case 0x42: _cpu.R[2] = LoadExecHeader(ReadCString(_cpu.R[4]), _cpu.R[5]) ? 1U : 0U; break;
            case 0x43: ExecOverlay(_cpu.R[4], _cpu.R[5], _cpu.R[6]); break;
            case 0x49: _cpu.Gpu.WriteGp0(_cpu.R[4]); break;
            case 0x44 or 0x54 or 0x71 or 0x72 or 0x9F: _cpu.R[2] = 1; break;
            case 0x70: DeliverCardIoEnd(); _cpu.R[2] = 1; break;
            case 0xAB: _cpu.R[2] = CardInfo(_cpu.R[4]); break;
            case 0xAC: _cpu.R[2] = CardLoadDirectory(_cpu.R[4]); break;
            default: _cpu.R[2] = 0; break;
        }
    }

    private void HandleB0(uint fn)
    {
        switch (fn)
        {
            case 0x00: _cpu.R[2] = AllocHeap(_cpu.R[4]); break;
            case 0x07: DeliverClassEvent(_cpu.R[4], _cpu.R[5]); break;
            case 0x08: _cpu.R[2] = OpenKernelEvent(_cpu.R[4], _cpu.R[5], _cpu.R[6], _cpu.R[7]); break;
            case 0x09: _cpu.R[2] = CloseKernelEvent(_cpu.R[4]) ? 1U : 0U; break;
            case 0x0B: _cpu.R[2] = TestEvent(_cpu.R[4]) ? 1U : 0U; break;
            case 0x0C: SetEventEnabled(_cpu.R[4], true); _cpu.R[2] = 1; break;
            case 0x0D: SetEventEnabled(_cpu.R[4], false); _cpu.R[2] = 1; break;
            case 0x12:
                if (_cpu.R[4] is 0x20000000U or 0x20000001U)
                {
                    _padType = _cpu.R[4]; _padBufAddr = _cpu.R[5]; _padLen = _cpu.R[6]; _padBuf2Addr = 0; _padLen2 = 0;
                }
                else
                {
                    _padType = 0; _padBufAddr = _cpu.R[4]; _padLen = _cpu.R[5]; _padBuf2Addr = _cpu.R[6]; _padLen2 = _cpu.R[7];
                }
                WritePadState(); _cpu.R[2] = 1U; break;
            case 0x15: _padType = _cpu.R[4]; _padBufAddr = _cpu.R[5]; _padLen = _cpu.R[6]; _padBuf2Addr = 0; _padLen2 = 0; WritePadState(); _cpu.R[2] = 2U; break;
            case 0x13 or 0x16:
                uint drWord = BuildPadDrWord();
                WritePadState(drWord);
                _cpu.R[2] = fn == 0x16 ? drWord : 1U;
                break;
            case 0x14: _padBufAddr = 0; _padLen = 0; _padBuf2Addr = 0; _padLen2 = 0; _padType = 0; _cpu.R[2] = 1; break;
            case 0x17 or 0x38: _cpu.ShouldUnwind = true; break;
            case 0x18: _hookEntryIntBufAddr = 0; break;
            case 0x19: _hookEntryIntBufAddr = _cpu.R[4]; break;
            case 0x20: ClearEvent(_cpu.R[4]); break;
            case 0x32: _cpu.R[2] = OpenCardFile(ReadCString(_cpu.R[4]), _cpu.R[5]); break;
            case 0x33: _cpu.R[2] = SeekCardFile((int)_cpu.R[4], (int)_cpu.R[5], (int)_cpu.R[6]); break;
            case 0x34: _cpu.R[2] = ReadCardFile((int)_cpu.R[4], _cpu.R[5], (int)_cpu.R[6]); break;
            case 0x35: _cpu.R[2] = WriteCardFile((int)_cpu.R[4], _cpu.R[5], (int)_cpu.R[6]); break;
            case 0x36: _cpu.R[2] = CloseCardFile((int)_cpu.R[4]); break;
            case 0x3B or 0x42: _cpu.R[2] = FirstFile(ReadCString(_cpu.R[4]), _cpu.R[5]); break;
            case 0x3C or 0x43: _cpu.R[2] = NextFile(_cpu.R[4]); break;
            case 0x40 or 0x41: _cpu.R[2] = FormatCard(ReadCString(_cpu.R[4])); break;
            case 0x45: _cpu.R[2] = MemCard.Erase(ReadCString(_cpu.R[4])) ? 1U : 0U; break;
            case 0x4A or 0x4B or 0x55 or 0x5B: _cpu.R[2] = 1; break;
            case 0x4C: DeliverCardIoEnd(); _cpu.R[2] = 1; break;
            case 0x4E: _cpu.R[2] = CardWriteSector(_cpu.R[4], _cpu.R[5], _cpu.R[6]); break;
            case 0x4F: _cpu.R[2] = CardReadSector(_cpu.R[4], _cpu.R[5], _cpu.R[6]); break;
            case 0x50: _cpu.R[2] = NewCard(); break;
            case 0x56: EnsureKernelTables(); _cpu.R[2] = C0TableAddr; break;
            default: _cpu.R[2] = 0; break;
        }
    }

    private void HandleC0(uint fn)
    {
        switch (fn)
        {
            case 0x02U: SysEnqIntRp(_cpu.R[4], _cpu.R[5]); _cpu.R[2] = 1U; break;
            case 0x03U: SysDeqIntRp(_cpu.R[4], _cpu.R[5]); _cpu.R[2] = 1U; break;
            case 0x0AU: _cpu.R[2] = 1U; break;
        }
    }

    private static ushort SwapPadBytes(ushort raw) => (ushort)((raw << 8) | (raw >> 8));

    private uint BuildPadDrWord()
    {
        uint low = (ushort)~SwapPadBytes(_cpu.GetControllerButtons(0));
        uint high = (ushort)~SwapPadBytes(_cpu.GetControllerButtons(1));
        return (high << 16) | low;
    }

    private void WritePadState() => WritePadState(BuildPadDrWord());

    private void WritePadState(uint packedDrWord)
    {
        if (_padBuf2Addr != 0)
        {
            if (_padLen2 >= 2) _cpu.Bus.Write16(_padBuf2Addr, 0xFFFF);
            else if (_padLen2 == 1) _cpu.Bus.Write8(_padBuf2Addr, 0xFF);
            if (_padLen2 >= 4) _cpu.Bus.Write16(_padBuf2Addr + 2, 0xFFFF);
        }
        if (_padBufAddr == 0) return;
        if (_padType == 0x20000001U) { _cpu.Bus.Write32(_padBufAddr, packedDrWord); return; }
        ushort rawActiveLow = (ushort)~_cpu.GetControllerButtons(0);
        if (_padLen >= 2) _cpu.Bus.Write16(_padBufAddr, 0x4100);
        else if (_padLen == 1) _cpu.Bus.Write8(_padBufAddr, 0x00);
        if (_padLen >= 4) _cpu.Bus.Write16(_padBufAddr + 2, rawActiveLow);
        if (_padLen >= 8)
        {
            (byte lx, byte ly, byte rx, byte ry) = _cpu.GetControllerAxes(0);
            _cpu.Bus.Write8(_padBufAddr + 4, rx); _cpu.Bus.Write8(_padBufAddr + 5, ry);
            _cpu.Bus.Write8(_padBufAddr + 6, lx); _cpu.Bus.Write8(_padBufAddr + 7, ly);
        }
    }

    private bool LoadExecHeader(string cdPath, uint headerPtr)
    {
        string clean = NormalizeDiscPath(cdPath);
        if (!LoadExecutableIntoRam(clean, out uint pc0, out uint gp0, out uint spBase, out uint spOff)) return false;
        _cpu.Bus.Write32(headerPtr + 0x00, pc0);
        _cpu.Bus.Write32(headerPtr + 0x04, gp0);
        _cpu.Bus.Write32(headerPtr + 0x1C, spBase);
        _cpu.Bus.Write32(headerPtr + 0x20, spOff);
        _cpu.Logger?.Info("BIOS", $"Loaded PS-X EXE '{clean}' pc0=0x{pc0:x8}");
        return true;
    }

    private void ExecOverlay(uint headerPtr, uint argc, uint argv)
    {
        uint pc0 = _cpu.Bus.Read32(headerPtr + 0x00);
        uint gp0 = _cpu.Bus.Read32(headerPtr + 0x04);
        uint spBase = _cpu.Bus.Read32(headerPtr + 0x1C);
        int prevExe = _cpu.ActiveExeId;
        uint prevSr = _cpu.Cop0Sr, prevHook = _hookEntryIntBufAddr, prevPad = _padBufAddr, prevType = _padType, prevLen = _padLen, prevPad2 = _padBuf2Addr, prevLen2 = _padLen2;
        var prevEvents = new List<KernelEvent>(_events.Count);
        foreach (KernelEvent ev in _events)
        {
            prevEvents.Add(new KernelEvent
            {
                ClassId = ev.ClassId, Spec = ev.Spec, Mode = ev.Mode, Callback = ev.Callback,
                Enabled = ev.Enabled, Delivered = ev.Delivered, Closed = ev.Closed
            });
        }
        Span<uint> savedIntRpHeads = stackalloc uint[IntRpPriorityCount];
        SaveAndClearIntRpHeads(savedIntRpHeads);
        Span<uint> savedR = stackalloc uint[32];
        _cpu.R.AsSpan().CopyTo(savedR);

        _hookEntryIntBufAddr = 0; _padBufAddr = 0; _padType = 0; _padLen = 0; _padBuf2Addr = 0; _padLen2 = 0; _events.Clear();
        _failedEventPolls = 0; _lastFailedEventHandle = 0;
        _cpu.Spu.Output.Clear();
        _cpu.Cop0Sr = 0x00000401U;
        _cpu.TimersAndDma.InterruptMask = 0; _cpu.TimersAndDma.InterruptStatus = 0; _cpu.TimersAndDma.PendingTimerEvents = 0;

        int nextExe = _cpu.ResolveOverlayExeId(_lastLoadedExeName, pc0);
        if (nextExe != prevExe) _cpu.FramePatch?.Reset();
        _cpu.ActiveExeId = nextExe;
        _cpu.R[4] = argc; _cpu.R[5] = argv; _cpu.R[28] = gp0;
        _cpu.R[29] = spBase != 0 ? spBase : 0x801F8000U;
        _cpu.R[30] = _cpu.R[29];
        _cpu.R[31] = IReStationCpu.ReturnSentinelPc;
        _cpu.RunSlice(pc0, IReStationCpu.ReturnSentinelPc);

        if (!_cpu.ExitRequested && _cpu.PendingQuickLoadSlot < 0)
        {
            bool switchingBack = _cpu.ActiveExeId != prevExe;
            _cpu.ActiveExeId = prevExe; _cpu.Cop0Sr = prevSr;
            _hookEntryIntBufAddr = prevHook; _padBufAddr = prevPad; _padType = prevType; _padLen = prevLen; _padBuf2Addr = prevPad2; _padLen2 = prevLen2;
            _events.Clear(); _events.AddRange(prevEvents);
            RestoreIntRpHeads(savedIntRpHeads);
            _cpu.Spu.Output.Clear();
            _cpu.TimersAndDma.InterruptMask = 0; _cpu.TimersAndDma.InterruptStatus = 0;
            savedR.CopyTo(_cpu.R);
            if (switchingBack) _cpu.FramePatch?.Reset();
        }
    }

    private static string NormalizeDiscPath(string path)
    {
        string clean = path.Replace("cdrom:", "", StringComparison.OrdinalIgnoreCase).Replace('\\', '/').TrimStart('/');
        int semi = clean.IndexOf(';');
        return semi >= 0 ? clean[..semi] : clean;
    }

    private void SaveJmpBuf(uint addr, uint pc) { _cpu.Bus.Write32(addr, pc); _cpu.Bus.Write32(addr + 4, _cpu.R[29]); _cpu.Bus.Write32(addr + 8, _cpu.R[30]); for (int i = 0; i < 8; i++) _cpu.Bus.Write32(addr + 12U + (uint)i * 4U, _cpu.R[16 + i]); _cpu.Bus.Write32(addr + 44, _cpu.R[28]); }
    private void LoadJmpBuf(uint addr) { _cpu.Pc = _cpu.Bus.Read32(addr); _cpu.R[29] = _cpu.Bus.Read32(addr + 4); _cpu.R[30] = _cpu.Bus.Read32(addr + 8); for (int i = 0; i < 8; i++) _cpu.R[16 + i] = _cpu.Bus.Read32(addr + 12U + (uint)i * 4U); _cpu.R[28] = _cpu.Bus.Read32(addr + 44); }
    private uint AllocHeap(uint sz) { uint p = _heapPtr; _heapPtr += (sz + 3U) & ~3U; return _heapPtr <= _heapEnd ? p : 0; }
    private string ReadCString(uint addr) { var b = new List<byte>(); for (uint i = 0; i < 512; i++) { byte c = _cpu.Bus.Read8(addr + i); if (c == 0) break; b.Add(c); } return Encoding.ASCII.GetString(b.ToArray()); }
    private void WriteCString(uint addr, string s, int max = 256) { byte[] b = Encoding.ASCII.GetBytes(s); int n = Math.Min(b.Length, max - 1); for (int i = 0; i < n; i++) _cpu.Bus.Write8(addr + (uint)i, b[i]); _cpu.Bus.Write8(addr + (uint)n, 0); }
    private static uint ToDigit(byte c) => c is >= (byte)'0' and <= (byte)'9' ? (uint)(c - '0') : c is >= (byte)'A' and <= (byte)'Z' ? (uint)(c - 'A' + 10) : c is >= (byte)'a' and <= (byte)'z' ? (uint)(c - 'a' + 10) : 0x009FFFFFU;
    private static int CompareN(string a, string b, int n) => string.CompareOrdinal(a.Length > n ? a[..n] : a, b.Length > n ? b[..n] : b);
}
