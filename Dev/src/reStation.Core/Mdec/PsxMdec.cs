// @module: reStation.Core.Mdec.PsxMdec
// @purpose: PS1 MDEC macroblock decoder unit (DMA0 in / DMA1 out, status/control registers, macroblock telemetry).
// @depends-on: System
// @exports: PsxMdec
// @owner: devv-audio-mdec (T3) — T1 skeleton; DmaIn/DmaOut/ReadStatus/WriteControl/MacroblocksDecoded are cross-slice LOCKED.

namespace reStation.Core.Mdec;

public sealed class PsxMdec
{
    private const uint StatusResetValue = 0x80040000U;
    private uint _status = StatusResetValue;
    private uint _command;
    private int _wordsRemaining;

    /// <summary>Total macroblocks decoded since power-on (telemetry derives macroblocks/sec).</summary>
    public long MacroblocksDecoded { get; private set; }

    /// <summary>MMIO 0x1F801824 read: MDEC status (bit 31 = output FIFO empty, bits 0-15 = words remaining - 1).</summary>
    public uint ReadStatus() => (_status & 0xFFFF0000U) | (uint)(Math.Max(0, _wordsRemaining - 1) & 0xFFFF);

    /// <summary>MMIO 0x1F801824 write: bit 31 resets the decoder.</summary>
    public void WriteControl(uint value)
    {
        if ((value & 0x80000000U) == 0) return;
        _status = StatusResetValue;
        _command = 0;
        _wordsRemaining = 0;
    }

    /// <summary>MMIO 0x1F801820 / DMA0 write: command word or parameter/RLE data word.</summary>
    public void DmaIn(uint word)
    {
        if (_wordsRemaining == 0)
        {
            _command = word >> 29;
            _wordsRemaining = _command switch { 1 => (int)(word & 0xFFFFU), 2 => (word & 1U) != 0 ? 32 : 16, 3 => 32, _ => 0 };
            return;
        }
        _wordsRemaining--;
    }

    /// <summary>MMIO 0x1F801820 / DMA1 read: next decoded output word (RGB15/RGB24/mono).</summary>
    public uint DmaOut() => 0x80008000U;
}
