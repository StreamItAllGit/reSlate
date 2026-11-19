// @module: reStation.Core.Hardware.PsxJoypadPort
// @purpose: SIO0 joypad/memory-card serial port (0x1F801040-0x1F80104F) speaking digital/DualShock pad and 0x81 Memory Card protocols.
// @depends-on: reStation.Core.Cpu.PsxCpu, reStation.Core.Hardware.PsxTimersAndDma, reStation.Core.Hardware.SioMemCardProtocol
// @exports: PsxJoypadPort
// @owner: devv-cpu-cd (T2)

using reStation.Core.Cpu;

namespace reStation.Core.Hardware;

/// <summary>Direct-I/O controller port for titles that bypass the BIOS pad driver (libpad / custom SIO drivers).</summary>
public sealed class PsxJoypadPort
{
    /// <summary>Approximate byte transfer + /ACK latency at the default 250 kHz baud (in CPU cycles).</summary>
    public const long AckDelayCycles = 1088;
    /// <summary>Finite /ACK low pulse width in CPU cycles (~2.8 us at 33.8688 MHz).</summary>
    public const long AckPulseCycles = 96;
    private const uint SnapshotMarker = 0x55534A52U; // "RJSU"

    private readonly PsxCpu _cpu;
    private readonly SioMemCardProtocol _memCard;
    private readonly byte[] _response = new byte[9];
    private int _responseLength;
    private int _sequence = -1;
    private byte _cmd;
    private byte _rxData = 0xFF;
    private bool _rxPending;
    private bool _ackLine;
    private bool _irqPending;
    private bool _configMode;
    private long _ackAtCycle = long.MaxValue;
    private long _ackEndCycle = long.MaxValue;
    private ushort _mode, _control, _baud;

    public PsxJoypadPort(PsxCpu cpu)
    {
        _cpu = cpu ?? throw new ArgumentNullException(nameof(cpu));
        _memCard = new SioMemCardProtocol(() => _cpu.Bios.MemCard);
    }

    internal SioMemCardProtocol MemCardProtocol => _memCard;

    /// <summary>When true, port 1 answers as a DualShock in analog mode (ID 0x73) instead of a digital pad (0x41).</summary>
    public bool AnalogMode { get; set; }
    public long BytesTransferred { get; private set; }

    public long NextWakeCycle => Math.Min(_ackAtCycle, _ackEndCycle);

    public byte Read8(uint address) => (byte)Read16(address);

    public ushort Read16(uint address)
    {
        if (_ackAtCycle != long.MaxValue || _ackEndCycle != long.MaxValue) TickTo(_cpu.TotalCycles);
        switch (address & 0xFU)
        {
            case 0x0:
                byte value = _rxPending ? _rxData : (byte)0xFF;
                _rxPending = false;
                return value;
            case 0x4: return (ushort)(0x0005 | (_rxPending ? 0x0002 : 0) | (_ackLine ? 0x0080 : 0) | (_irqPending ? 0x0200 : 0));
            case 0x8: return _mode;
            case 0xA: return _control;
            case 0xE: return _baud;
            default: return 0;
        }
    }

    public void Write8(uint address, byte value) => Write16(address, value);

    public void Write16(uint address, ushort value)
    {
        if (_ackAtCycle != long.MaxValue || _ackEndCycle != long.MaxValue) TickTo(_cpu.TotalCycles);
        switch (address & 0xFU)
        {
            case 0x0: Transfer((byte)value); break;
            case 0x8: _mode = value; break;
            case 0xA:
                _control = (ushort)(value & ~0x0050);
                if ((value & 0x0010) != 0) _irqPending = false; // ACK: reset IRQ7 latch (bit 9)
                if ((value & 0x0040) != 0) Reset();
                if ((value & 0x0002) == 0) { _sequence = -1; _memCard.ResetSession(); _ackAtCycle = long.MaxValue; _ackEndCycle = long.MaxValue; _ackLine = false; } // /JOYn deasserted ends the packet
                break;
            case 0xE: _baud = value; break;
        }
    }

    /// <summary>Raises IRQ7 and starts the finite /ACK pulse, then deasserts /ACK after <see cref="AckPulseCycles"/>.</summary>
    public void TickTo(long totalCycles)
    {
        if (totalCycles >= _ackAtCycle)
        {
            long pulseEnd = _ackAtCycle + AckPulseCycles;
            _ackAtCycle = long.MaxValue;
            _ackLine = true;
            _ackEndCycle = pulseEnd;
            _irqPending = true;
            if ((_control & 0x1000) != 0) _cpu.TimersAndDma.RaiseIrq(PsxIrq.Controller);
        }
        if (totalCycles >= _ackEndCycle)
        {
            _ackEndCycle = long.MaxValue;
            _ackLine = false;
        }
    }

    private void Transfer(byte tx)
    {
        BytesTransferred++;
        _ackLine = false;
        _ackEndCycle = long.MaxValue;
        bool port2 = (_control & 0x2000) != 0;
        bool ack;
        if (_memCard.IsActive)
        {
            (_rxData, ack) = _memCard.TransferByte(tx);
        }
        else if (_sequence < 0)
        {
            if (tx == SioMemCardProtocol.AddressSelect)
            {
                (_rxData, ack) = _memCard.BeginSession(port2 ? 1 : 0);
            }
            else
            {
                ack = tx == 0x01 && !port2;
                _rxData = 0xFF;
                if (ack) _sequence = 0;
            }
        }
        else if (_sequence == 0)
        {
            _cmd = tx;
            if (TryBuildCommandResponse(tx))
            {
                _rxData = _response[0];
                _sequence = 1;
                ack = true;
            }
            else
            {
                _rxData = 0xFF;
                ack = false;
                _sequence = -1;
            }
        }
        else
        {
            if (_sequence == 2 && AnalogMode) ApplyConfigSubByte(tx);
            _rxData = _sequence < _responseLength ? _response[_sequence] : (byte)0xFF;
            _sequence++;
            ack = _sequence < _responseLength;
            if (!ack) _sequence = -1;
        }
        _rxPending = true;
        _ackAtCycle = ack ? _cpu.TotalCycles + AckDelayCycles : long.MaxValue;
    }

    private bool TryBuildCommandResponse(byte cmd)
    {
        if (!AnalogMode)
        {
            if (cmd != 0x42) return false;
            BuildDigitalResponse();
            return true;
        }

        switch (cmd)
        {
            case 0x42 or 0x43:
                if (_configMode) FillHeader(0xF3); else BuildAnalogResponse();
                return true;
            case 0x44:
                FillHeader(0xF3);
                return true;
            case 0x45:
                FillHeader(0xF3, 0x03, 0x02, AnalogMode ? (byte)0x01 : (byte)0x00, 0x02, 0x01, 0x00);
                return true;
            case 0x46:
                FillHeader(0xF3, 0x00, 0x00, 0x01, 0x02, 0x00, 0x0A);
                return true;
            case 0x47:
                FillHeader(0xF3, 0x00, 0x00, 0x02, 0x00, 0x01, 0x00);
                return true;
            case 0x4C:
                FillHeader(0xF3, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00);
                return true;
            case 0x4D:
                FillHeader(0xF3, 0x00, 0x01, 0xFF, 0xFF, 0xFF, 0xFF);
                return true;
            default:
                return false;
        }
    }

    private void ApplyConfigSubByte(byte sub)
    {
        if (_cmd == 0x43) _configMode = sub == 0x01;
        else if (_cmd == 0x46 && sub == 0x01) { _response[4] = 0x01; _response[5] = 0x01; _response[6] = 0x01; _response[7] = 0x14; }
        else if (_cmd == 0x4C && sub == 0x01) _response[5] = 0x07;
    }

    private void FillHeader(byte id, byte b2 = 0, byte b3 = 0, byte b4 = 0, byte b5 = 0, byte b6 = 0, byte b7 = 0)
    {
        _response[0] = id; _response[1] = 0x5A;
        _response[2] = b2; _response[3] = b3;
        _response[4] = b4; _response[5] = b5;
        _response[6] = b6; _response[7] = b7;
        _responseLength = 8;
    }

    private void BuildDigitalResponse()
    {
        ushort pressedLow = (ushort)~_cpu.GetControllerButtons(0);
        _response[0] = 0x41; _response[1] = 0x5A;
        _response[2] = (byte)pressedLow; _response[3] = (byte)(pressedLow >> 8);
        _responseLength = 4;
    }

    private void BuildAnalogResponse()
    {
        ushort pressedLow = (ushort)~_cpu.GetControllerButtons(0);
        (byte lx, byte ly, byte rx, byte ry) = _cpu.GetControllerAxes(0);
        FillHeader(0x73, (byte)pressedLow, (byte)(pressedLow >> 8), rx, ry, lx, ly);
    }

    private void Reset()
    {
        _sequence = -1;
        _memCard.ResetSession();
        _rxPending = false;
        _ackLine = false;
        _irqPending = false;
        _configMode = false;
        _ackAtCycle = long.MaxValue;
        _ackEndCycle = long.MaxValue;
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
        _sequence = -1; _memCard.ResetSession(); _rxPending = false; _ackLine = false; _irqPending = false; _configMode = false;
        _ackAtCycle = long.MaxValue; _ackEndCycle = long.MaxValue;
    }
}
