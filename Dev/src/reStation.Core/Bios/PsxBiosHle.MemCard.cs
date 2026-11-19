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
        public int Cursor;
        public MemoryStream Buffer = new();
        public void Dispose() => Buffer.Dispose();
    }

    private readonly Dictionary<int, OpenMemCardFile> _openFiles = new();
    private List<(string Name, int SizeBytes, int FirstBlock)> _dirSearch = new();
    private int _dirCursor, _nextFd = 2;

    internal void SetCardIdentity(string? moduleId, string? serialFallback = null) =>
        MemCard.SetCardIdentity(!string.IsNullOrWhiteSpace(moduleId) ? moduleId : serialFallback);

    internal void FlushMemCardsIfDirty() => MemCard.Flush();

    private uint OpenCardFile(string name, uint flags)
    {
        bool write = (flags & 0x0202U) != 0;
        var f = new OpenMemCardFile { Name = name, WriteMode = write };
        if (!write)
        {
            byte[]? data = MemCard.ReadFile(name);
            if (data is null) { f.Dispose(); return 0xFFFFFFFFU; }
            f.Buffer.Write(data, 0, data.Length);
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
        f.Cursor += take;
        return (uint)take;
    }

    private uint WriteCardFile(int fd, uint src, int len)
    {
        if (!_openFiles.TryGetValue(fd, out var f)) return 0xFFFFFFFFU;
        byte[] tmp = new byte[Math.Max(0, len)];
        for (int i = 0; i < tmp.Length; i++) tmp[i] = _cpu.Bus.Read8(src + (uint)i);
        f.Buffer.Position = f.Cursor; f.Buffer.Write(tmp, 0, tmp.Length); f.Cursor += tmp.Length;
        return (uint)tmp.Length;
    }

    private uint CloseCardFile(int fd)
    {
        if (!_openFiles.Remove(fd, out var f)) return 0xFFFFFFFFU;
        if (f.WriteMode) MemCard.WriteFile(f.Name, f.Buffer.ToArray());
        f.Dispose();
        return (uint)fd;
    }

    private uint FirstFile(string pattern, uint dirEntryAddr) { _dirSearch = MemCard.ListFiles(pattern); _dirCursor = 0; return NextFile(dirEntryAddr); }

    private uint NextFile(uint dirEntryAddr)
    {
        if (_dirCursor >= _dirSearch.Count) return 0;
        var item = _dirSearch[_dirCursor++];
        WriteCString(dirEntryAddr, item.Name, 20);
        _cpu.Bus.Write32(dirEntryAddr + 20, 0);
        _cpu.Bus.Write32(dirEntryAddr + 24, (uint)item.SizeBytes);
        return dirEntryAddr;
    }
}
