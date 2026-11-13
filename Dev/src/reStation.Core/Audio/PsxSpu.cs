// @module: reStation.Core.Audio.PsxSpu
// @purpose: PS1 SPU unit (512 KB RAM, register file, DMA4, XA intake) mixing 44.1 kHz stereo into Foundation.Core.AudioRingBuffer(65536, 2).
// @depends-on: Foundation.Core, reStation.Core
// @exports: PsxSpu
// @owner: devv-audio-mdec (T3) — T1 skeleton; public members are cross-slice LOCKED (PsxCpu bus/CD-ROM/Player audio consume them).

using Foundation.Core;

namespace reStation.Core.Audio;

public sealed class PsxSpu
{
    public const int SampleRate = 44_100;
    public const int VoiceCount = 24;
    public const int RamSize = 512 * 1024;
    /// <summary>Ring capacity in interleaved shorts (32,768 stereo frames ≈ 743 ms @ 44.1 kHz).</summary>
    public const int RingCapacitySamples = 65_536;
    private const uint SnapshotMarker = 0x55505352U; // "RSPU"

    private readonly ushort[] _regs = new ushort[0x200];
    private readonly short[] _frame = new short[2];
    private uint _transferAddr;
    private long _xaDrops;

    public PsxSpu(RecompSettings settings)
    {
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Output = new AudioRingBuffer(RingCapacitySamples, 2);
    }

    public RecompSettings Settings { get; }
    public byte[] Ram { get; } = new byte[RamSize];
    /// <summary>Mixed host output consumed by the Player audio stream (SDL_PutAudioStreamData).</summary>
    public AudioRingBuffer Output { get; }
    /// <summary>XA samples dropped because the ring was full (must stay 0 in healthy playback).</summary>
    public long XaDrops => Interlocked.Read(ref _xaDrops);
    public double AudioFillRatio => Output.FillRatio;
    public long AudioUnderruns => Output.UnderrunCount;
    public int ActiveVoiceCount { get; private set; }

    /// <summary>MMIO 0x1F801C00-0x1F801FFF halfword read.</summary>
    public ushort Read16(uint address) => _regs[(address & 0x3FFU) >> 1];

    /// <summary>MMIO 0x1F801C00-0x1F801FFF halfword write.</summary>
    public void Write16(uint address, ushort value)
    {
        uint reg = address & 0x3FFU;
        _regs[reg >> 1] = value;
        if (reg == 0x1A6) _transferAddr = ((uint)value << 3) & (RamSize - 1);
        else if (reg == 0x1A8) WriteRam16(value);
    }

    /// <summary>DMA4 write (CPU RAM -> SPU RAM).</summary>
    public void WriteDmaWord(uint word)
    {
        WriteRam16((ushort)word);
        WriteRam16((ushort)(word >> 16));
    }

    /// <summary>DMA4 read (SPU RAM -> CPU RAM).</summary>
    public uint ReadDmaWord()
    {
        uint a = _transferAddr;
        uint w = (uint)(Ram[a] | (Ram[(a + 1) & (RamSize - 1)] << 8) | (Ram[(a + 2) & (RamSize - 1)] << 16) | (Ram[(a + 3) & (RamSize - 1)] << 24));
        _transferAddr = (a + 4) & (RamSize - 1);
        return w;
    }

    /// <summary>
    /// Submits one or more contiguous 128-byte XA-ADPCM sound groups (18 groups = one Form2 sector) with the sector
    /// subheader coding-info byte. Decoder history persists across calls until <see cref="ResetXaState"/>.
    /// </summary>
    public void SubmitXaSectors(ReadOnlySpan<byte> soundGroups, byte codingInfo)
    {
        if (soundGroups.Length % 128 != 0)
            throw new ArgumentException("XA payload must be a multiple of 128-byte sound groups.", nameof(soundGroups));
    }

    /// <summary>Clears XA decoder history and pending XA samples (disc stop / quick-load only).</summary>
    public void ResetXaState() => Interlocked.Exchange(ref _xaDrops, 0);

    /// <summary>Advances one 44.1 kHz sample tick: steps envelopes, mixes voices + XA, and writes one stereo frame.</summary>
    public void TickSample()
    {
        _frame[0] = 0;
        _frame[1] = 0;
        if (Output.Write(_frame) < 2) Interlocked.Increment(ref _xaDrops);
    }

    /// <summary>Renders <paramref name="stereoFrames"/> consecutive sample ticks.</summary>
    public void RenderFrames(int stereoFrames)
    {
        for (int i = 0; i < stereoFrames; i++) TickSample();
    }

    public void WriteSnapshotState(BinaryWriter w)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.Write(SnapshotMarker);
        w.Write(Ram);
        foreach (ushort r in _regs) w.Write(r);
        w.Write(_transferAddr);
    }

    public void ReadSnapshotState(BinaryReader r)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (r.ReadUInt32() != SnapshotMarker) throw new InvalidDataException("Snapshot SPU marker mismatch.");
        r.BaseStream.ReadExactly(Ram);
        for (int i = 0; i < _regs.Length; i++) _regs[i] = r.ReadUInt16();
        _transferAddr = r.ReadUInt32() & (RamSize - 1);
        ActiveVoiceCount = 0;
        Output.Clear();
    }

    private void WriteRam16(ushort value)
    {
        Ram[_transferAddr] = (byte)value;
        Ram[(_transferAddr + 1) & (RamSize - 1)] = (byte)(value >> 8);
        _transferAddr = (_transferAddr + 2) & (RamSize - 1);
    }
}
