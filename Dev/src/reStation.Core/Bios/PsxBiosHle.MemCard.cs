// @module: reStation.Core.Bios.PsxBiosHle.MemCard
// @purpose: BIOS Memory Card file descriptor table, Slot 1+2 identity/flush coordination, and B0/A0 card helpers extracted from PsxBiosHle.
// @depends-on: System.IO, reStation.Core.Bios.MemCardMcrStore
// @exports: PsxBiosHle (MemCard partial)

namespace reStation.Core.Bios;

public sealed partial class PsxBiosHle
{
    private sealed class OpenMemCardFile : IDisposable
    {
        public string Name = string.Empty;
        public bool WriteMode;
        public bool AsyncMode;
        public int Cursor;
        public MemoryStream Buffer = new();
        public void Dispose() => Buffer.Dispose();
    }

    private readonly Dictionary<int, OpenMemCardFile> _openFiles = new();
    private List<(string Name, int SizeBytes, int FirstBlock)> _dirSearch = new();
    private int _dirCursor, _nextFd = 2;

    public void SetCardIdentity(string? moduleId, string? serialFallback = null) =>
        MemCard.SetCardIdentity(moduleId, serialFallback);

    internal void FlushMemCardsIfDirty() => MemCard.FlushIfDirty();

    private static bool TryResolveCardChannel(uint chan, out int slot)
    {
        if (chan == 0x00U) { slot = 0; return true; }
        if (chan == 0x10U) { slot = 1; return true; }
        slot = -1;
        return false;
    }

    private uint CardWriteSector(uint chan, uint sector, uint srcAddr)
    {
        if (!TryResolveCardChannel(chan, out int slot)) { DeliverCardEvent(0x0100U); return 0U; }
        if (sector >= MemCardMcrStore.SectorCount) { DeliverCardEvent(0x8000U); return 0U; }
        Span<byte> buf = stackalloc byte[MemCardMcrStore.SectorSize];
        if (_cpu.Bus.TryGetRamSpan(srcAddr, MemCardMcrStore.SectorSize, out Span<byte> ram)) ram.CopyTo(buf);
        else for (int i = 0; i < MemCardMcrStore.SectorSize; i++) buf[i] = _cpu.Bus.Read8(srcAddr + (uint)i);
        MemCard.WriteSector(slot, (int)sector, buf);
        DeliverCardIoEnd();
        return 1U;
    }

    private uint CardReadSector(uint chan, uint sector, uint dstAddr)
    {
        if (!TryResolveCardChannel(chan, out int slot)) { DeliverCardEvent(0x0100U); return 0U; }
        if (sector >= MemCardMcrStore.SectorCount) { DeliverCardEvent(0x8000U); return 0U; }
        Span<byte> buf = stackalloc byte[MemCardMcrStore.SectorSize];
        MemCard.ReadSector(slot, (int)sector, buf);
        if (_cpu.Bus.TryGetRamSpan(dstAddr, MemCardMcrStore.SectorSize, out Span<byte> ram)) buf.CopyTo(ram);
        else for (int i = 0; i < MemCardMcrStore.SectorSize; i++) _cpu.Bus.Write8(dstAddr + (uint)i, buf[i]);
        _cpu.Bus.NotifyRamWriteRange(dstAddr, MemCardMcrStore.SectorSize);
        DeliverCardIoEnd();
        return 1U;
    }

    private uint NewCard()
    {
        MemCard.AcknowledgeNewCard();
        return 1U;
    }

    private uint CardInfo(uint chan)
    {
        if (!TryResolveCardChannel(chan, out int slot)) { DeliverCardEvent(0x0100U); return 0U; }
        if (MemCard.IsSlotNewCard(slot) && HasEnabledEvent(0xF4000001U, 0x2000U))
        {
            DeliverClassEvent(0xF0000011U, 0x0004U);
            DeliverClassEvent(0xF4000001U, 0x2000U);
        }
        else DeliverCardIoEnd();
        return 1U;
    }

    private uint CardLoadDirectory(uint chan) => CardInfo(chan);

    private uint FormatCard(string devName)
    {
        int slot = devName.StartsWith("bu10:", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        MemCard.Format(slot);
        MemCard.ClearSlotNewCardFlag(slot);
        return 1U;
    }

    private uint OpenCardFile(string name, uint flags)
    {
        bool asyncMode = (flags & 0x8000U) != 0U;
        bool create = (flags & 0x0200U) != 0U;
        bool write = (flags & 0x0202U) != 0U;
        var f = new OpenMemCardFile { Name = name, WriteMode = write, AsyncMode = asyncMode };
        byte[]? existing = MemCard.ReadFile(name);
        if (!create)
        {
            if (existing is null) { f.Dispose(); return 0xFFFFFFFFU; }
            f.Buffer.Write(existing, 0, existing.Length);
        }
        else
        {
            int blocks = (int)((flags >> 16) & 0xFFFFU);
            if (blocks > 0)
            {
                byte[] zeroBuf = new byte[blocks * MemCardMcrStore.BlockSize];
                f.Buffer.Write(zeroBuf, 0, zeroBuf.Length);
            }
            else if (existing is not null)
            {
                f.Buffer.Write(existing, 0, existing.Length);
            }
        }
        int fd = _nextFd++; _openFiles[fd] = f; return (uint)fd;
    }

    private uint SeekCardFile(int fd, int offset, int origin)
    {
        if (!_openFiles.TryGetValue(fd, out var f)) return 0xFFFFFFFFU;
        long basePos = origin switch { 1 => f.Cursor, 2 => f.Buffer.Length, _ => 0 };
        f.Cursor = (int)Math.Max(0, basePos + offset);
        return (uint)f.Cursor;
    }

    private uint ReadCardFile(int fd, uint dst, int len)
    {
        if (!_openFiles.TryGetValue(fd, out var f)) return 0xFFFFFFFFU;
        byte[] raw = f.Buffer.ToArray();
        int take = Math.Clamp(len, 0, Math.Max(0, raw.Length - f.Cursor));
        for (int i = 0; i < take; i++) _cpu.Bus.Write8(dst + (uint)i, raw[f.Cursor + i]);
        if (take > 0) _cpu.Bus.NotifyRamWriteRange(dst, (uint)take);
        f.Cursor += take;
        if (f.AsyncMode) { DeliverCardIoEnd(); return 0U; }
        return (uint)take;
    }

    private uint WriteCardFile(int fd, uint src, int len)
    {
        if (!_openFiles.TryGetValue(fd, out var f)) return 0xFFFFFFFFU;
        f.WriteMode = true;
        byte[] tmp = new byte[Math.Max(0, len)];
        for (int i = 0; i < tmp.Length; i++) tmp[i] = _cpu.Bus.Read8(src + (uint)i);
        f.Buffer.Position = f.Cursor; f.Buffer.Write(tmp, 0, tmp.Length); f.Cursor += tmp.Length;
        if (f.AsyncMode) { DeliverCardIoEnd(); return 0U; }
        return (uint)tmp.Length;
    }

    private uint CloseCardFile(int fd)
    {
        if (!_openFiles.Remove(fd, out var f)) return 0xFFFFFFFFU;
        if (f.WriteMode) MemCard.WriteFile(f.Name, f.Buffer.ToArray());
        f.Dispose();
        return (uint)fd;
    }

    private uint FirstFile(string pattern, uint dirEntryAddr)
    {
        if (_cpu.Bus.Read32(KernelToTDcbAddr) == DcbTableAddr)
            _cpu.Bus.Write32(DcbTableAddr + 0x34U, DcbFirstFileStubAddr);
        _dirSearch = MemCard.ListFiles(pattern);
        _dirCursor = 0;
        DeliverCardIoEnd();
        return NextFile(dirEntryAddr);
    }

    private uint NextFile(uint dirEntryAddr)
    {
        if (_dirCursor >= _dirSearch.Count) return 0;
        var item = _dirSearch[_dirCursor++];
        WriteCString(dirEntryAddr, item.Name, 20);
        _cpu.Bus.Write32(dirEntryAddr + 20, 0);
        _cpu.Bus.Write32(dirEntryAddr + 24, (uint)item.SizeBytes);
        _cpu.Bus.Write32(dirEntryAddr + 28, 0U);
        _cpu.Bus.Write32(dirEntryAddr + 32, (uint)item.FirstBlock);
        _cpu.Bus.NotifyRamWriteRange(dirEntryAddr, 40U);
        return dirEntryAddr;
    }
}
