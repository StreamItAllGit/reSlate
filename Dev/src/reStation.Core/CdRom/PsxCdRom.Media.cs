// @module: reStation.Core.CdRom.PsxCdRom.Media
// @purpose: CD-ROM sector loading, XA-ADPCM subheader routing, asset streaming, BCD helpers, and snapshots.
// @depends-on: reStation.Core.DiscIngestion.DiscManifest, PsxCdRomTiming
// @exports: PsxCdRom (media partial)
// @owner: devv-cpu-cd (T2)

using reStation.Core.DiscIngestion;

namespace reStation.Core.CdRom;

public sealed partial class PsxCdRom
{
    private bool LoadCurrentLbaSector(out bool isXaOrForm2, out bool isRawMdec)
    {
        Array.Fill(_driveSectorBuf, (byte)0);
        LbaToBcd(_seekLba, out byte mBcd, out byte sBcd, out byte fBcd);
        _driveSectorBuf[0] = mBcd; _driveSectorBuf[1] = sBcd; _driveSectorBuf[2] = fBcd; _driveSectorBuf[3] = 0x02;

        isRawMdec = (_mode & PsxCdRomTiming.ModeWholeSector) != 0;
        isXaOrForm2 = (_mode & PsxCdRomTiming.ModeXaAdpcm) != 0;
        bool isRealtimeXa = false;

        DiscFileEntry? entry = _lastEntry;
        if (entry is null || _seekLba < entry.Lba || _seekLba >= entry.Lba + Math.Max(1U, entry.SectorCount))
        {
            entry = _manifest?.FindByLba(_seekLba);
            _lastEntry = entry;
        }

        if (entry is not null)
        {
            int stride = entry.IsForm2Raw ? 2336 : 2048;
            long fileOff = (long)(_seekLba - entry.Lba) * stride;
            int n = ReadCachedAsset(entry.RelativeFile, fileOff, _driveSectorBuf.AsSpan(entry.IsForm2Raw ? 4 : 12, stride));
            if (n >= 0 && entry.IsForm2Raw)
            {
                isXaOrForm2 = true;
                isRealtimeXa = HandleForm2Subheader(n);
            }
            else if (n >= 0) { _driveSectorBuf[6] = 0x08; _driveSectorBuf[10] = 0x08; }
        }
        else if (TryReadPrimaryImageSector(_seekLba, out bool rawForm2, out int bytesRead))
        {
            if (rawForm2) { isXaOrForm2 = true; isRealtimeXa = HandleForm2Subheader(bytesRead); }
            else { _driveSectorBuf[6] = 0x08; _driveSectorBuf[10] = 0x08; }
        }

        _driveSectorStartOffset = isRawMdec ? 0 : 12;
        _driveSectorLen = isRawMdec ? 2340 : 2060;
        _driveSectorUnlatched = true;
        return isRealtimeXa;
    }

    private bool HandleForm2Subheader(int n)
    {
        bool xaEnabled = (_mode & PsxCdRomTiming.ModeXaAdpcm) != 0;
        bool isXaAudioSector = (_driveSectorBuf[6] & 0x44) == 0x44;
        if (!xaEnabled || !isXaAudioSector) return false;
        bool filterMatch = (_mode & 0x08) == 0 || (_driveSectorBuf[4] == _filterFile && _driveSectorBuf[5] == _filterChannel);
        if (filterMatch && !_muted)
        {
            int soundBytes = (Math.Min(2304, Math.Max(0, n - 8)) / 128) * 128;
            if (soundBytes > 0)
            {
                _cpu.Spu.SubmitXaSectors(_driveSectorBuf.AsSpan(12, soundBytes), _driveSectorBuf[7]);
                XaSectorsDelivered++;
            }
        }
        return true;
    }

    private bool TryReadPrimaryImageSector(uint lba, out bool isForm2, out int n)
    {
        isForm2 = false; n = 0;
        if (_manifest is null || string.IsNullOrWhiteSpace(_manifest.PrimaryImagePath) || !File.Exists(_manifest.PrimaryImagePath)) return false;
        if (_primaryImageStream is null)
        {
            _primaryImageStream = new FileStream(_manifest.PrimaryImagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
            bool raw = _primaryImageStream.Length % 2352 == 0;
            _primaryImageStride = raw ? 2352 : 2048;
        }
        long pos = (long)lba * _primaryImageStride;
        if (pos + _primaryImageStride > _primaryImageStream.Length) return false;
        _primaryImageStream.Seek(pos, SeekOrigin.Begin);
        if (_primaryImageStride == 2352)
        {
            _primaryImageStream.Seek(pos + 16, SeekOrigin.Begin);
            n = _primaryImageStream.Read(_driveSectorBuf, 4, 2336);
            isForm2 = (_driveSectorBuf[6] & 0x20) != 0;
            return n == 2336;
        }
        n = _primaryImageStream.Read(_driveSectorBuf, 12, 2048);
        return n == 2048;
    }

    private int ReadCachedAsset(string relativeFile, long fileOff, Span<byte> dst)
    {
        if (_preloadedFiles.TryGetValue(relativeFile, out byte[]? data))
        {
            if (fileOff >= data.Length) return -1;
            int count = (int)Math.Min(dst.Length, data.Length - fileOff);
            data.AsSpan((int)fileOff, count).CopyTo(dst);
            return count;
        }
        if (!_openStreams.TryGetValue(relativeFile, out FileStream? fs))
        {
            string fullPath = Path.Combine(_gameDataDir, relativeFile.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath)) return -1;
            if (new FileInfo(fullPath).Length <= MaxPreloadBytes)
            {
                byte[] bytes = File.ReadAllBytes(fullPath);
                _preloadedFiles[relativeFile] = bytes;
                if (fileOff >= bytes.Length) return -1;
                int count = (int)Math.Min(dst.Length, bytes.Length - fileOff);
                bytes.AsSpan((int)fileOff, count).CopyTo(dst);
                return count;
            }
            fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
            _openStreams[relativeFile] = fs;
        }
        if (fileOff >= fs.Length) return -1;
        if (fs.Position != fileOff) fs.Seek(fileOff, SeekOrigin.Begin);
        return fs.Read(dst);
    }

    private static void LbaToBcd(uint lba, out byte mBcd, out byte sBcd, out byte fBcd)
    {
        int total = (int)lba + 150;
        mBcd = ToBcd(total / (75 * 60)); sBcd = ToBcd((total / 75) % 60); fBcd = ToBcd(total % 75);
    }

    private static byte ToBcd(int v) => (byte)(((v / 10) << 4) | (v % 10));

    private static uint BcdToLba(byte mBcd, byte sBcd, byte fBcd)
    {
        int m = (mBcd >> 4) * 10 + (mBcd & 0xF), s = (sBcd >> 4) * 10 + (sBcd & 0xF), f = (fBcd >> 4) * 10 + (fBcd & 0xF);
        return (uint)Math.Max(0, (m * 60 + s) * 75 + f - 150);
    }

    public void WriteSnapshotState(BinaryWriter w)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.Write(SnapshotMarker);
        w.Write(_index); w.Write(_irqMask); w.Write(_irqFlag); w.Write(_mode); w.Write(_filterFile); w.Write(_filterChannel);
        w.Write(_seekLba); w.Write(_reading); w.Write(_muted); w.Write(_driveSectorUnlatched); w.Write(_lastSectorXaOrForm2);
        w.Write(_driveSectorStartOffset); w.Write(_driveSectorLen); w.Write(_sectorCursor); w.Write(_sectorStartOffset); w.Write(_sectorLen);
        w.Write(_nextReadCycle); w.Write(_nextIrqCycle);
        w.Write(_driveSectorBuf); w.Write(_dataFifoBuf);
    }

    public void ReadSnapshotState(BinaryReader r)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (r.ReadUInt32() != SnapshotMarker) throw new InvalidDataException("Snapshot CD-ROM marker mismatch.");
        _index = r.ReadByte(); _irqMask = r.ReadByte(); _irqFlag = r.ReadByte(); _mode = r.ReadByte(); _filterFile = r.ReadByte(); _filterChannel = r.ReadByte();
        _seekLba = r.ReadUInt32(); _reading = r.ReadBoolean(); _muted = r.ReadBoolean(); _driveSectorUnlatched = r.ReadBoolean(); _lastSectorXaOrForm2 = r.ReadBoolean();
        _driveSectorStartOffset = r.ReadInt32(); _driveSectorLen = r.ReadInt32(); _sectorCursor = r.ReadInt32(); _sectorStartOffset = r.ReadInt32(); _sectorLen = r.ReadInt32();
        _nextReadCycle = r.ReadInt64(); _nextIrqCycle = r.ReadInt64();
        r.BaseStream.ReadExactly(_driveSectorBuf); r.BaseStream.ReadExactly(_dataFifoBuf);
        _paramFifo.Clear(); _respFifo.Clear(); _pendingIrqs.Clear(); _sectorDeliveryCycles.Clear();
    }

    private void CloseStreams()
    {
        foreach (FileStream fs in _openStreams.Values) fs.Dispose();
        _openStreams.Clear();
        _primaryImageStream?.Dispose();
        _primaryImageStream = null;
    }
}
