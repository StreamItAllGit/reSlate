// @module: reStation.Core.Hardware.PsxJoypadPort
// @purpose: SIO0 joypad/memory-card serial port (0x1F801040-0x1F80104F) speaking the digital (0x41) / DualShock analog (0x73) pad protocol.
// @depends-on: reStation.Core.Cpu.PsxCpu (controller state), reStation.Core.Hardware.PsxTimersAndDma (IRQ7)
// @exports: PsxJoypadPort
// @owner: devv-cpu-cd (T2)

using reStation.Core.Cpu;

namespace reStation.Core.Hardware;

/// <summary>Direct-I/O controller port for titles that bypass the BIOS pad driver (libpad / custom SIO drivers).</summary>
public sealed class PsxJoypadPort
{
    /// <summary>Approximate byte transfer + /ACK latency at the default 250 kHz baud (in CPU cycles).</summary>
    public const long AckDelayCycles = 1088;
    private const uint SnapshotMarker = 0x55534A52U; // "RJSU"

    private readonly PsxCpu _cpu;
    private readonly byte[] _response = new byte[9];
    private int _responseLength;
    private int _sequence = -1;
    private byte _rxData = 0xFF;
    private bool _rxPending;
    private bool _ackLine;
    private long _ackAtCycle = long.MaxValue;
    private ushort _mode, _control, _baud;

    public PsxJoypadPort(PsxCpu cpu) => _cpu = cpu ?? throw new ArgumentNullException(nameof(cpu));

    /// <summary>When true, port 1 answers as a DualShock in analog mode (ID 0x73) instead of a digital pad (0x41).</summary>
    public bool AnalogMode { get; set; }
    public long BytesTransferred { get; private set; }

    public long NextWakeCycle => _ackAtCycle;

    public byte Read8(uint address) => (byte)Read16(address);

    public ushort Read16(uint address)
    {
        switch (address & 0xFU)
        {
            case 0x0:
                byte value = _rxPending ? _rxData : (byte)0xFF;
                _rxPending = false;
                return value;
            case 0x4: return (ushort)(0x0005 | (_rxPending ? 0x0002 : 0) | (_ackLine ? 0x0080 : 0) | ((_control & 0x10) != 0 ? 0x0200 : 0));
            case 0x8: return _mode;
            case 0xA: return _control;
            case 0xE: return _baud;
            default: return 0;
        }
    }

    public void Write16(uint address, ushort value)
    {
        switch (address & 0xFU)
        {
            case 0x0: Transfer((byte)value); break;
            case 0x8: _mode = value; break;
            case 0xA:
                _control = (ushort)(value & ~0x0050);
                if ((value & 0x0010) != 0) _ackLine = false; // ACK: reset IRQ latch
                if ((value & 0x0040) != 0) Reset();
                if ((value & 0x0002) == 0) _sequence = -1;   // /JOYn deasserted ends the packet
                break;
            case 0xE: _baud = value; break;
        }
    }

    /// <summary>Raises IRQ7 once the pending /ACK pulse elapses.</summary>
    public void TickTo(long totalCycles)
    {
        if (totalCycles < _ackAtCycle) return;
        _ackAtCycle = long.MaxValue;
        _ackLine = true;
        if ((_control & 0x1000) != 0) _cpu.TimersAndDma.RaiseIrq(PsxIrq.Controller);
    }

    private void Transfer(byte tx)
    {
        BytesTransferred++;
        bool port2 = (_control & 0x2000) != 0;
        bool ack;
        if (_sequence < 0)
        {
            ack = tx == 0x01 && !port2;
            _rxData = 0xFF;
            if (ack) { BuildResponse(); _sequence = 0; }
        }
        else
        {
            _rxData = _sequence < _responseLength ? _response[_sequence] : (byte)0xFF;
            _sequence++;
            ack = _sequence < _responseLength;
            if (!ack) _sequence = -1;
        }
        _rxPending = true;
        if (ack) _ackAtCycle = _cpu.TotalCycles + AckDelayCycles;
    }

    private void BuildResponse()
    {
        ushort pressedLow = (ushort)~_cpu.GetControllerButtons(0);
        _response[0] = AnalogMode ? (byte)0x73 : (byte)0x41;
        _response[1] = 0x5A;
        _response[2] = (byte)pressedLow;
        _response[3] = (byte)(pressedLow >> 8);
        _responseLength = 4;
        if (!AnalogMode) return;
        (byte lx, byte ly, byte rx, byte ry) = _cpu.GetControllerAxes(0);
        _response[4] = rx; _response[5] = ry; _response[6] = lx; _response[7] = ly;
        _responseLength = 8;
    }

    private void Reset()
    {
        _sequence = -1;
        _rxPending = false;
        _ackLine = false;
        _ackAtCycle = long.MaxValue;
        _mode = 0;
        _control = 0;
        _baud = 0;
    }

    public void WriteSnapshotState(BinaryWriter w)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.Write(SnapshotMarker);
        w.Write(_mode); w.Write(_control); w.Write(_baud); w.Write(AnalogMode);
    }

    public void ReadSnapshotState(BinaryReader r)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (r.ReadUInt32() != SnapshotMarker) throw new InvalidDataException("Snapshot joypad marker mismatch.");
        _mode = r.ReadUInt16(); _control = r.ReadUInt16(); _baud = r.ReadUInt16(); AnalogMode = r.ReadBoolean();
        _sequence = -1; _rxPending = false; _ackLine = false; _ackAtCycle = long.MaxValue;
    }
}
