// @module: reStation.Core.CdRom.PsxCdRom
// @purpose: Hardware-timed PS1 CD-ROM controller (0x1F801800-1803) with KFR-PARITY-01 cadence, XA-SPU streaming, and MDEC 2340-byte reads.
// @depends-on: reStation.Core.Cpu.PsxCpu, reStation.Core.DiscIngestion.DiscManifest, reStation.Core.Hardware.PsxIrq, PsxCdRomTiming
// @exports: PsxCdRom
// @owner: devv-cpu-cd (T2)

using reStation.Core.Cpu;
using reStation.Core.DiscIngestion;
using reStation.Core.Hardware;

namespace reStation.Core.CdRom;

public sealed partial class PsxCdRom : IDisposable
{
    private const long MaxPreloadBytes = 4 * 1024 * 1024;
    private const uint SnapshotMarker = 0x55444352U; // "RCDU"

    private readonly PsxCpu _cpu;
    private readonly Queue<byte> _paramFifo = new();
    private readonly Queue<byte> _respFifo = new();
    private readonly Queue<(byte Irq, byte[] Resp)> _pendingIrqs = new();
    private readonly Queue<long> _sectorDeliveryCycles = new();
    private readonly byte[] _driveSectorBuf = new byte[2352];
    private readonly byte[] _dataFifoBuf = new byte[2352];
    private readonly Dictionary<string, byte[]> _preloadedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileStream> _openStreams = new(StringComparer.OrdinalIgnoreCase);

    private DiscManifest? _manifest;
    private string _gameDataDir = string.Empty;
    private DiscFileEntry? _lastEntry;
    private FileStream? _primaryImageStream;
    private int _primaryImageStride;

    private byte _index, _irqMask = 0x1F, _irqFlag, _mode, _filterFile, _filterChannel;
    private uint _seekLba = 150;
    private bool _reading, _muted, _driveSectorUnlatched, _lastSectorXaOrForm2;
    private int _driveSectorStartOffset, _driveSectorLen, _sectorCursor, _sectorStartOffset, _sectorLen;
    private long _nextReadCycle, _nextIrqCycle, _lastLatencyCycles = PsxCdRomTiming.CyclesPerSectorDoubleSpeed;
    private long _lastDeliveryCycle = -1;

    public PsxCdRom(PsxCpu cpu) => _cpu = cpu ?? throw new ArgumentNullException(nameof(cpu));

    public long SectorsDelivered { get; private set; }
    public long XaSectorsDelivered { get; private set; }
    public byte Mode => _mode;

    /// <summary>Rolling sectors/sec over the last virtual second (0 when idle > 1s).</summary>
    public double SectorsPerSec
    {
        get
        {
            PruneThroughput(_cpu.TotalCycles);
            if (_sectorDeliveryCycles.Count == 0) return 0.0;
            if (_sectorDeliveryCycles.Count == 1)
                return _reading ? PsxCdRomTiming.SectorsPerSecond(_lastLatencyCycles) : 1.0;
            long span = _lastDeliveryCycle - _sectorDeliveryCycles.Peek();
            return span > 0
                ? (_sectorDeliveryCycles.Count - 1) * (double)PsxCdRomTiming.CpuClockHz / span
                : PsxCdRomTiming.SectorsPerSecond(_lastLatencyCycles);
        }
    }

    public void SetManifest(DiscManifest manifest, string gameDataDir)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        CloseStreams();
        _preloadedFiles.Clear();
        _lastEntry = null;
        _manifest = manifest;
        _gameDataDir = gameDataDir ?? string.Empty;
    }

    public long GetNextWakeCycle()
    {
        long next = long.MaxValue;
        if (_pendingIrqs.Count > 0 && _irqFlag == 0) next = _nextIrqCycle;
        if (_reading && _irqFlag == 0 && _nextReadCycle < next) next = _nextReadCycle;
        return next;
    }

    public void TickTo(long totalCycles)
    {
        if (_pendingIrqs.Count > 0 && _irqFlag == 0 && totalCycles >= _nextIrqCycle)
        {
            var next = _pendingIrqs.Dequeue();
            if (next.Irq == 0x01 && _driveSectorUnlatched && _sectorCursor >= _sectorLen) LatchDriveSectorToFifo();
            _respFifo.Clear();
            foreach (byte b in next.Resp) _respFifo.Enqueue(b);
            _irqFlag = next.Irq;
            _nextIrqCycle = totalCycles + 256;
            if ((_irqMask & _irqFlag) != 0) _cpu.TimersAndDma.RaiseIrq(PsxIrq.CdRom);
            return;
        }

        if (_reading && _irqFlag == 0 && totalCycles >= _nextReadCycle)
        {
            bool isRealtimeXa = LoadCurrentLbaSector(out bool isXaOrForm2, out bool isRawMdec);
            _lastSectorXaOrForm2 = isXaOrForm2;
            _seekLba++;
            SectorsDelivered++;
            RecordDelivery(totalCycles);
            _lastLatencyCycles = PsxCdRomTiming.ComputeReadLatencyCycles(_mode, isXaOrForm2, isRawMdec, _cpu.Settings.ShouldFastLoadData);
            _nextReadCycle = totalCycles + _lastLatencyCycles;
            if (!isRealtimeXa) QueueIrq(0x01, new[] { BuildStat() });
        }
    }

    public byte ReadRegister(int offset) => (offset & 3, _index & 3) switch
    {
        (0, _) => (byte)(0x18 | (_paramFifo.Count == 0 ? 0x08 : 0) | (_respFifo.Count > 0 ? 0x20 : 0) | (_sectorCursor < _sectorLen ? 0x40 : 0)),
        (1, _) => _respFifo.Count > 0 ? _respFifo.Dequeue() : (byte)0,
        (2, _) => ReadDataByte(),
        (3, 0 or 2) => _irqMask,
        (3, _) => (byte)(0xE0 | _irqFlag),
        _ => 0
    };

    public void WriteRegister(int offset, byte value)
    {
        if ((offset & 3) == 0) { _index = (byte)(value & 3); return; }
        switch ((offset & 3, _index & 3))
        {
            case (1, 0): ExecuteCommand(value); break;
            case (2, 0): _paramFifo.Enqueue(value); break;
            case (3, 0):
                if ((value & 0x80) != 0)
                {
                    if (_driveSectorUnlatched) LatchDriveSectorToFifo();
                    else if (_sectorCursor >= _sectorLen) _sectorCursor = _sectorStartOffset;
                }
                else _sectorCursor = _sectorLen;
                break;
            case (2, 1): _irqMask = (byte)(value & 0x1F); break;
            case (3, 1):
                _irqFlag &= (byte)~(value & 0x1F);
                if ((value & 0x40) != 0) _paramFifo.Clear();
                if (_reading && _irqFlag == 0)
                {
                    long latency = PsxCdRomTiming.ComputeReadLatencyCycles(_mode, _lastSectorXaOrForm2, (_mode & PsxCdRomTiming.ModeWholeSector) != 0, _cpu.Settings.ShouldFastLoadData);
                    _nextReadCycle = Math.Max(_nextReadCycle, _cpu.TotalCycles + latency);
                }
                break;
        }
    }

    public uint ReadDataWord()
    {
        uint b0 = ReadDataByte(), b1 = ReadDataByte(), b2 = ReadDataByte(), b3 = ReadDataByte();
        return b0 | (b1 << 8) | (b2 << 16) | (b3 << 24);
    }

    private byte ReadDataByte() => _sectorCursor < _sectorLen ? _dataFifoBuf[_sectorCursor++] : (byte)0;

    private void LatchDriveSectorToFifo()
    {
        Buffer.BlockCopy(_driveSectorBuf, 0, _dataFifoBuf, 0, 2352);
        _sectorStartOffset = _driveSectorStartOffset;
        _sectorLen = _driveSectorLen;
        _sectorCursor = _sectorStartOffset;
        _driveSectorUnlatched = false;
    }

    private void ExecuteCommand(byte cmd)
    {
        byte p0 = _paramFifo.Count > 0 ? _paramFifo.Dequeue() : (byte)0;
        byte p1 = _paramFifo.Count > 0 ? _paramFifo.Dequeue() : (byte)0;
        byte p2 = _paramFifo.Count > 0 ? _paramFifo.Dequeue() : (byte)0;
        _paramFifo.Clear();

        switch (cmd)
        {
            case 0x01 or 0x07 or 0x1C: QueueIrq(0x03, new[] { BuildStat() }); break;
            case 0x02: _seekLba = BcdToLba(p0, p1, p2); QueueIrq(0x03, new[] { BuildStat() }); break;
            case 0x06 or 0x1B:
                _reading = true;
                _lastLatencyCycles = PsxCdRomTiming.ComputeReadLatencyCycles(_mode, (_mode & PsxCdRomTiming.ModeXaAdpcm) != 0, (_mode & PsxCdRomTiming.ModeWholeSector) != 0, _cpu.Settings.ShouldFastLoadData);
                _nextReadCycle = _cpu.TotalCycles + _lastLatencyCycles;
                QueueIrq(0x03, new[] { BuildStat() });
                break;
            case 0x08 or 0x09:
                _reading = false;
                if (cmd == 0x08) _cpu.Spu.ResetXaState();
                QueueIrq(0x03, new[] { BuildStat() });
                QueueIrq(0x02, new[] { BuildStat() });
                break;
            case 0x0A or 0x1E:
                if (cmd == 0x0A) { _mode = 0; _reading = false; }
                QueueIrq(0x03, new[] { BuildStat() });
                QueueIrq(0x02, new[] { BuildStat() });
                break;
            case 0x0B: _muted = true; QueueIrq(0x03, new[] { BuildStat() }); break;
            case 0x0C: _muted = false; QueueIrq(0x03, new[] { BuildStat() }); break;
            case 0x0D: _filterFile = p0; _filterChannel = p1; QueueIrq(0x03, new[] { BuildStat() }); break;
            case 0x0E: _mode = p0; QueueIrq(0x03, new[] { BuildStat() }); break;
            case 0x0F: QueueIrq(0x03, new byte[] { BuildStat(), _mode, 0x00, _filterFile, _filterChannel }); break;
            case 0x10:
                LbaToBcd(_seekLba, out byte lm, out byte ls, out byte lf);
                QueueIrq(0x03, new[] { lm, ls, lf, _mode, _filterFile, _filterChannel, _driveSectorBuf[6], _driveSectorBuf[7] });
                break;
            case 0x11:
                LbaToBcd(_seekLba, out byte pm, out byte ps, out byte pf);
                QueueIrq(0x03, new byte[] { 0x01, 0x01, pm, ps, pf, pm, ps, pf });
                break;
            case 0x15 or 0x16: QueueIrq(0x03, new[] { BuildStat() }); QueueIrq(0x02, new[] { BuildStat() }); break;
            case 0x19:
                QueueIrq(0x03, p0 == 0x20 ? new byte[] { 0x95, 0x05, 0x16, 0xC1 } : new[] { BuildStat() });
                break;
            case 0x1A:
                QueueIrq(0x03, new[] { BuildStat() });
                QueueIrq(0x02, new byte[] { 0x02, 0x00, 0x20, 0x00, (byte)'S', (byte)'C', (byte)'E', (byte)'A' });
                break;
            default: QueueIrq(0x03, new[] { BuildStat() }); break;
        }
    }

    private void RecordDelivery(long totalCycles)
    {
        _lastDeliveryCycle = totalCycles;
        _sectorDeliveryCycles.Enqueue(totalCycles);
        PruneThroughput(totalCycles);
    }

    private void PruneThroughput(long totalCycles)
    {
        long cutoff = totalCycles - PsxCdRomTiming.CpuClockHz;
        while (_sectorDeliveryCycles.Count > 0 && _sectorDeliveryCycles.Peek() < cutoff) _sectorDeliveryCycles.Dequeue();
    }

    private void QueueIrq(byte irq, byte[] resp)
    {
        if (_pendingIrqs.Count == 0 && _irqFlag == 0) _nextIrqCycle = Math.Max(_nextIrqCycle, _cpu.TotalCycles + 128);
        _pendingIrqs.Enqueue((irq, resp));
    }

    private byte BuildStat() => (byte)(0x02 | (_reading ? 0x20 : 0));

    public void Dispose() => CloseStreams();
}
