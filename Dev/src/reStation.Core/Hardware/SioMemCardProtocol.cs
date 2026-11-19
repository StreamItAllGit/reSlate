// @module: reStation.Core.Hardware.SioMemCardProtocol
// @purpose: SIO0 (0x81) PS1 Memory Card Slot 1+2 packet protocol (0x52 Read, 0x57 Write, 0x53 ID, FLAG 0x08, 0x58..0x5F mini dock NACK).
// @depends-on: reStation.Core.Bios.MemCardMcrStore
// @exports: SioMemCardProtocol

using reStation.Core.Bios;

namespace reStation.Core.Hardware;

/// <summary>Implements the PS1 SIO0 <c>0x81</c> Memory Card serial protocol across Slot 1 and Slot 2 backed by <see cref="MemCardMcrStore"/>.</summary>
public sealed class SioMemCardProtocol
{
    public const byte AddressSelect = 0x81;
    public const byte CmdRead = 0x52;
    public const byte CmdGetId = 0x53;
    public const byte CmdWrite = 0x57;
    public const byte EndGood = 0x47;
    public const byte EndBadChecksum = 0x4E;
    public const byte EndBadSector = 0xFF;

    private readonly Func<MemCardMcrStore> _getStore;
    private readonly byte[] _sectorBuf = new byte[MemCardMcrStore.SectorSize];
    private bool _active;
    private int _slot;
    private byte _cmd;
    private int _step;
    private byte _addrMsb;
    private byte _addrLsb;
    private ushort _sector;
    private byte _xor;
    private byte _writeStatus = EndGood;

    public SioMemCardProtocol(Func<MemCardMcrStore> getStore) =>
        _getStore = getStore ?? throw new ArgumentNullException(nameof(getStore));

    public bool IsActive => _active;

    public void ResetSession()
    {
        _active = false;
        _cmd = 0;
        _step = 0;
    }

    public (byte Rx, bool Ack) BeginSession(int slot)
    {
        _active = true;
        _slot = slot is 1 ? 1 : 0;
        _cmd = 0;
        _step = 0;
        _addrMsb = 0;
        _addrLsb = 0;
        _sector = 0;
        _xor = 0;
        _writeStatus = EndGood;
        return (0xFF, true);
    }

    public (byte Rx, bool Ack) TransferByte(byte tx)
    {
        if (!_active) return (0xFF, false);
        MemCardMcrStore store = _getStore();
        if (_cmd == 0)
        {
            if (tx is >= 0x58 and <= 0x5F)
            {
                ResetSession();
                return (0xFF, false);
            }
            if (tx is CmdRead or CmdGetId or CmdWrite)
            {
                byte flag = store.GetSlotFlag(_slot);
                _cmd = tx;
                _step = 1;
                return (flag, true);
            }
            ResetSession();
            return (0xFF, false);
        }

        return _cmd switch
        {
            CmdGetId => HandleGetId(),
            CmdRead => HandleRead(tx, store),
            CmdWrite => HandleWrite(tx, store),
            _ => FailReset()
        };
    }

    private (byte Rx, bool Ack) HandleGetId()
    {
        return (_step++) switch
        {
            1 => (0x5A, true),
            2 => (0x5D, true),
            3 => (0x5C, true),
            4 => (0x5D, true),
            5 => (0x04, true),
            6 => (0x00, true),
            7 => (0x00, true),
            8 => EndTransfer(0x80),
            _ => FailReset()
        };
    }

    private (byte Rx, bool Ack) HandleRead(byte tx, MemCardMcrStore store)
    {
        int s = _step++;
        switch (s)
        {
            case 1: return (0x5A, true);
            case 2: return (0x5D, true);
            case 3: _addrMsb = tx; return (0x00, true);
            case 4:
                _addrLsb = tx;
                _sector = (ushort)((_addrMsb << 8) | _addrLsb);
                return (0x00, true);
            case 5: return (0x5C, true);
            case 6: return (0x5D, true);
            case 7:
                if (_sector >= MemCardMcrStore.SectorCount) return FailReset();
                _ = store.ReadSector(_slot, _sector, _sectorBuf);
                _xor = (byte)(_addrMsb ^ _addrLsb);
                return (_addrMsb, true);
            case 8: return (_addrLsb, true);
            case >= 9 and <= 136:
                byte d = _sectorBuf[s - 9];
                _xor ^= d;
                return (d, true);
            case 137: return (_xor, true);
            case 138: return EndTransfer(EndGood);
            default: return FailReset();
        }
    }

    private (byte Rx, bool Ack) HandleWrite(byte tx, MemCardMcrStore store)
    {
        int s = _step++;
        switch (s)
        {
            case 1: return (0x5A, true);
            case 2: return (0x5D, true);
            case 3: _addrMsb = tx; return (0x00, true);
            case 4:
                _addrLsb = tx;
                _sector = (ushort)((_addrMsb << 8) | _addrLsb);
                _xor = (byte)(_addrMsb ^ _addrLsb);
                return (0x00, true);
            case >= 5 and <= 132:
                _sectorBuf[s - 5] = tx;
                _xor ^= tx;
                return (0x00, true);
            case 133:
                if (_sector >= MemCardMcrStore.SectorCount) _writeStatus = EndBadSector;
                else if (tx != _xor) _writeStatus = EndBadChecksum;
                else
                {
                    _ = store.WriteSector(_slot, _sector, _sectorBuf);
                    _writeStatus = EndGood;
                }
                return (0x00, true);
            case 134: return (0x5C, true);
            case 135: return (0x5D, true);
            case 136: return EndTransfer(_writeStatus);
            default: return FailReset();
        }
    }

    private (byte Rx, bool Ack) EndTransfer(byte rx) { ResetSession(); return (rx, false); }
    private (byte Rx, bool Ack) FailReset() { ResetSession(); return (0xFF, false); }

    internal void WriteSnapshotState(BinaryWriter w)
    {
        w.Write(_active);
        w.Write(_slot);
        w.Write(_cmd);
        w.Write(_step);
        w.Write(_addrMsb);
        w.Write(_addrLsb);
        w.Write(_sector);
        w.Write(_xor);
        w.Write(_writeStatus);
        w.Write(_sectorBuf);
    }

    internal void ReadSnapshotState(BinaryReader r)
    {
        _active = r.ReadBoolean();
        _slot = r.ReadInt32();
        _cmd = r.ReadByte();
        _step = r.ReadInt32();
        _addrMsb = r.ReadByte();
        _addrLsb = r.ReadByte();
        _sector = r.ReadUInt16();
        _xor = r.ReadByte();
        _writeStatus = r.ReadByte();
        r.BaseStream.ReadExactly(_sectorBuf);
    }
}
