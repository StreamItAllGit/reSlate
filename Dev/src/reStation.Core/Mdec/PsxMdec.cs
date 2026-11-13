// @module: reStation.Core.Mdec.PsxMdec
// @purpose: PS1 MDEC unit: command/parameter FIFO (DMA0 in), output FIFO (DMA1 out), status/control registers, quant/scale table upload, incremental macroblock decode and macroblocks/sec telemetry.
// @depends-on: System, System.Diagnostics
// @exports: PsxMdec
// @owner: devv-audio-mdec (T3) — DmaIn/DmaOut/ReadStatus/WriteControl/MacroblocksDecoded are cross-slice LOCKED.

using System.Diagnostics;

namespace reStation.Core.Mdec;

public sealed partial class PsxMdec
{
    private const uint EmptyOutputWord = 0x80008000U;

    private uint[] _outBuf = new uint[4096];
    private int _outHead, _outCount;
    private readonly List<ushort> _in = new();
    private int _parse;
    private uint _cmdWord, _command;
    private int _wordsRemaining;
    private bool _dma0Enabled, _dma1Enabled;
    private long _rateWindowStart = Stopwatch.GetTimestamp();
    private long _rateWindowCount;
    private double _macroblocksPerSec;

    /// <summary>Total macroblocks decoded since power-on (telemetry derives macroblocks/sec).</summary>
    public long MacroblocksDecoded { get; private set; }

    /// <summary>Rolling decode rate over the most recent ≥ 1 s window.</summary>
    public double MacroblocksPerSec
    {
        get
        {
            UpdateRate();
            return _macroblocksPerSec;
        }
    }

    /// <summary>Decoded words waiting in the output FIFO.</summary>
    public int OutputWordsAvailable => _outCount;

    /// <summary>
    /// MMIO 0x1F801824 read: bit31 out-FIFO empty, bit29 busy, bit28 data-in request, bit27 data-out request,
    /// bits 26-23 output depth/signed/bit15, bits 18-16 current block, bits 15-0 parameter words remaining - 1.
    /// </summary>
    public uint ReadStatus()
    {
        uint s = 0;
        if (_outCount == 0) s |= 1U << 31;
        if (_wordsRemaining > 0) s |= 1U << 29;
        if (_dma0Enabled) s |= 1U << 28;
        if (_dma1Enabled && _outCount > 0) s |= 1U << 27;
        s |= ((_cmdWord >> 25) & 0xFU) << 23;
        s |= (uint)CurrentBlockStatus << 16;
        s |= (uint)(_wordsRemaining - 1) & 0xFFFFU;
        return s;
    }

    /// <summary>MMIO 0x1F801824 write: bit31 resets the decoder, bit30 enables DMA0 requests, bit29 enables DMA1 requests.</summary>
    public void WriteControl(uint value)
    {
        if ((value & 0x80000000U) != 0)
        {
            _outHead = _outCount = 0;
            _cmdWord = _command = 0;
            _wordsRemaining = 0;
            _in.Clear();
            _parse = 0;
            ResetMacroblockState();
        }
        _dma0Enabled = (value & 0x40000000U) != 0;
        _dma1Enabled = (value & 0x20000000U) != 0;
    }

    /// <summary>MMIO 0x1F801820 / DMA0 write: command word or parameter/RLE data word.</summary>
    public void DmaIn(uint word)
    {
        if (_wordsRemaining == 0)
        {
            StartCommand(word);
            return;
        }
        ushort lo = (ushort)word, hi = (ushort)(word >> 16);
        _in.Add(lo);
        _in.Add(hi);
        _wordsRemaining--;
        if (_command == 1)
        {
            if (_wordsRemaining == 0) FinishDecodeCommand();
            else if (lo == 0xFE00 || hi == 0xFE00) DecodeAvailableBlocks();
        }
        else if (_wordsRemaining == 0)
        {
            FinishTableCommand();
        }
    }

    /// <summary>Bulk DMA0 transfer.</summary>
    public void DmaInBlock(ReadOnlySpan<uint> words)
    {
        foreach (uint w in words) DmaIn(w);
    }

    /// <summary>MMIO 0x1F801820 / DMA1 read: next decoded output word (mono4/mono8/RGB24/RGB15).</summary>
    public uint DmaOut()
    {
        if (_outCount == 0) return EmptyOutputWord;
        uint w = _outBuf[_outHead];
        _outHead = (_outHead + 1) & (_outBuf.Length - 1);
        _outCount--;
        return w;
    }

    /// <summary>Bulk DMA1 transfer; returns the number of decoded words copied (remaining slots are left untouched).</summary>
    public int DmaOutBlock(Span<uint> destination)
    {
        int n = Math.Min(destination.Length, _outCount);
        for (int i = 0; i < n; i++) destination[i] = DmaOut();
        return n;
    }

    private void StartCommand(uint word)
    {
        _cmdWord = word;
        _command = word >> 29;
        _wordsRemaining = _command switch
        {
            1 => (int)(word & 0xFFFFU),
            2 => (word & 1U) != 0 ? 32 : 16,
            3 => 32,
            _ => 0
        };
        _in.Clear();
        _parse = 0;
        ResetMacroblockState();
    }

    private void FinishTableCommand()
    {
        if (_command == 2)
        {
            bool color = (_cmdWord & 1U) != 0;
            for (int i = 0; i < 64; i++) LumQuant[i] = HalfByte(i);
            if (color)
            {
                for (int i = 0; i < 64; i++) ChrQuant[i] = HalfByte(64 + i);
            }
        }
        else if (_command == 3)
        {
            for (int i = 0; i < 64; i++) ScaleTable[i] = (short)_in[i];
        }
        _in.Clear();
        _parse = 0;
    }

    private byte HalfByte(int byteIndex)
    {
        ushort h = _in[byteIndex >> 1];
        return (byte)((byteIndex & 1) == 0 ? h : h >> 8);
    }

    private void FinishDecodeCommand()
    {
        DecodeAvailableBlocks();
        _in.Clear();
        _parse = 0;
        ResetMacroblockState();
    }

    private void DecodeAvailableBlocks()
    {
        while (TryDecodeNextBlock())
        {
        }
        if (_parse > 8192)
        {
            _in.RemoveRange(0, _parse);
            _parse = 0;
        }
    }

    private void EnqueueOutWord(uint w)
    {
        if (_outCount == _outBuf.Length) GrowOutBuf();
        _outBuf[(_outHead + _outCount++) & (_outBuf.Length - 1)] = w;
    }

    private void GrowOutBuf()
    {
        uint[] next = new uint[_outBuf.Length * 2];
        int first = Math.Min(_outCount, _outBuf.Length - _outHead);
        Array.Copy(_outBuf, _outHead, next, 0, first);
        if (_outCount > first) Array.Copy(_outBuf, 0, next, first, _outCount - first);
        _outBuf = next;
        _outHead = 0;
    }

    private void OnMacroblockDecoded()
    {
        MacroblocksDecoded++;
        UpdateRate();
    }

    private void UpdateRate()
    {
        long now = Stopwatch.GetTimestamp();
        double elapsed = (now - _rateWindowStart) / (double)Stopwatch.Frequency;
        if (elapsed < 1.0) return;
        _macroblocksPerSec = (MacroblocksDecoded - _rateWindowCount) / elapsed;
        _rateWindowStart = now;
        _rateWindowCount = MacroblocksDecoded;
    }
}
