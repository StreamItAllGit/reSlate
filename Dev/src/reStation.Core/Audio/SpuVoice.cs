// @module: reStation.Core.Audio.SpuVoice
// @purpose: One PS1 SPU voice: 28-sample ADPCM block decode, loop flags, pitch-stepped linear interpolation, and a hardware 4-stage ADSR envelope (KFR-PARITY-02a fix).
// @depends-on: System, System.IO
// @exports: AdsrPhase, SpuVoice
// @owner: devv-audio-mdec (T3)

namespace reStation.Core.Audio;

/// <summary>ADSR envelope phase of one SPU voice.</summary>
public enum AdsrPhase { Off, Attack, Decay, Sustain, Release }

/// <summary>
/// Single PS1 SPU ADPCM voice. The envelope steps once per 44.1 kHz tick using the hardware rate formula
/// (cycles = 1 &lt;&lt; max(0, shift-11), step = stepValue &lt;&lt; max(0, 11-shift), exponential decrease scales the step by
/// level/0x8000 with floor rounding). <see cref="KeyOff"/> enters <see cref="AdsrPhase.Release"/>; the voice turns off
/// (<see cref="Active"/> = false) only once the envelope has reached exactly 0.
/// </summary>
public sealed class SpuVoice
{
    public const int MaxEnvelope = 0x7FFF;
    public const int SamplesPerBlock = 28;
    public const int BlockBytes = 16;
    public const ushort MaxPitch = 0x3FFF;

    private static readonly int[] FilterK0 = { 0, 60, 115, 98, 122 };
    private static readonly int[] FilterK1 = { 0, 0, -52, -55, -60 };

    private readonly short[] _decoded = new short[SamplesPerBlock];
    private uint _curAddr;
    private int _sampleIdx = SamplesPerBlock;
    private int _old1, _old2;
    private uint _pitchCounter;
    private short _s0, _s1;
    private int _envCycles;
    private bool _pendingEnd, _pendingRepeat, _needsPrime;

    /// <summary>Voice volume left/right (signed 15-bit scaled to 16-bit).</summary>
    public short VolLeft { get; set; }
    public short VolRight { get; set; }
    /// <summary>Sample rate step (0x1000 = 44,100 Hz).</summary>
    public ushort Pitch { get; set; }
    /// <summary>ADPCM start address in 8-byte units.</summary>
    public ushort StartAddr { get; set; }
    /// <summary>ADPCM repeat address in 8-byte units (updated by loop-start flags).</summary>
    public ushort LoopAddr { get; set; }
    /// <summary>32-bit ADSR register (low half = 0x1F801C08, high half = 0x1F801C0A).</summary>
    public uint Adsr { get; set; }

    public AdsrPhase Phase { get; private set; } = AdsrPhase.Off;
    public bool Active => Phase != AdsrPhase.Off;
    /// <summary>Current ADSR level, 0..0x7FFF.</summary>
    public int Envelope { get; private set; }
    /// <summary>Set when a block with the loop-end flag was played (ENDX); cleared on KeyOn.</summary>
    public bool ReachedEnd { get; private set; }

    /// <summary>Sustain level ((N + 1) * 0x800, capped at 0x7FFF).</summary>
    public int SustainLevel => Math.Min(MaxEnvelope, (int)((Adsr & 0xFU) + 1U) * 0x800);

    /// <summary>Starts the voice from <see cref="StartAddr"/> in the Attack phase at level 0.</summary>
    public void KeyOn()
    {
        _curAddr = (uint)StartAddr << 3;
        _sampleIdx = SamplesPerBlock;
        _old1 = _old2 = 0;
        _pitchCounter = 0;
        _s0 = _s1 = 0;
        _envCycles = 0;
        _pendingEnd = _pendingRepeat = false;
        _needsPrime = true;
        ReachedEnd = false;
        Envelope = 0;
        Phase = AdsrPhase.Attack;
    }

    /// <summary>Enters the Release phase; the envelope then decays to 0 and the voice turns off.</summary>
    public void KeyOff()
    {
        if (Phase == AdsrPhase.Off) return;
        Phase = AdsrPhase.Release;
        _envCycles = 0;
    }

    /// <summary>Silences the voice immediately (end flag without repeat, SPU reset).</summary>
    public void ForceOff()
    {
        Envelope = 0;
        Phase = AdsrPhase.Off;
        _envCycles = 0;
    }

    /// <summary>Advances the ADSR envelope by one 44.1 kHz tick.</summary>
    public void TickEnvelope()
    {
        if (Phase == AdsrPhase.Off) return;
        GetRate(out int shift, out int stepValue, out bool exponential, out bool decrease);

        int cycles = 1 << Math.Max(0, shift - 11);
        int step = stepValue << Math.Max(0, 11 - shift);
        if (exponential && !decrease && Envelope > 0x6000) cycles *= 4;
        if (exponential && decrease) step = (step * Envelope) >> 15; // arithmetic shift floors: >= 1 unit while level > 0

        if (++_envCycles < cycles) return;
        _envCycles = 0;
        Envelope = Math.Clamp(Envelope + step, 0, MaxEnvelope);

        switch (Phase)
        {
            case AdsrPhase.Attack when Envelope >= MaxEnvelope:
                Phase = AdsrPhase.Decay;
                break;
            case AdsrPhase.Decay when Envelope <= SustainLevel:
                Phase = AdsrPhase.Sustain;
                break;
            case AdsrPhase.Release when Envelope == 0:
                Phase = AdsrPhase.Off;
                break;
        }
    }

    /// <summary>Produces one 44.1 kHz output sample pair (pre-mix), stepping decode, pitch and envelope.</summary>
    public void StepSample(byte[] spuRam, out int outL, out int outR)
    {
        ArgumentNullException.ThrowIfNull(spuRam);
        outL = outR = 0;
        if (Phase == AdsrPhase.Off) return;

        if (_needsPrime)
        {
            _needsPrime = false;
            _s1 = NextSample(spuRam);
            if (Phase == AdsrPhase.Off) return;
        }

        int frac = (int)(_pitchCounter & 0xFFFU);
        int sample = _s0 + (((_s1 - _s0) * frac) >> 12);

        _pitchCounter += Math.Min(Pitch, MaxPitch);
        while (_pitchCounter >= 0x1000U)
        {
            _pitchCounter -= 0x1000U;
            _s0 = _s1;
            _s1 = NextSample(spuRam);
            if (Phase == AdsrPhase.Off) break;
        }

        int env = Envelope;
        TickEnvelope();
        int scaled = (sample * env) >> 15;
        outL = (scaled * VolLeft) >> 15;
        outR = (scaled * VolRight) >> 15;
    }

    public void WriteState(BinaryWriter w)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.Write(VolLeft); w.Write(VolRight); w.Write(Pitch); w.Write(StartAddr); w.Write(LoopAddr); w.Write(Adsr);
        w.Write((byte)Phase); w.Write(Envelope); w.Write(ReachedEnd);
        w.Write(_curAddr); w.Write(_sampleIdx); w.Write(_old1); w.Write(_old2); w.Write(_pitchCounter);
        w.Write(_s0); w.Write(_s1); w.Write(_envCycles); w.Write(_pendingEnd); w.Write(_pendingRepeat); w.Write(_needsPrime);
        foreach (short s in _decoded) w.Write(s);
    }

    public void ReadState(BinaryReader r)
    {
        ArgumentNullException.ThrowIfNull(r);
        VolLeft = r.ReadInt16(); VolRight = r.ReadInt16(); Pitch = r.ReadUInt16(); StartAddr = r.ReadUInt16(); LoopAddr = r.ReadUInt16(); Adsr = r.ReadUInt32();
        byte phase = r.ReadByte();
        Phase = phase <= (byte)AdsrPhase.Release ? (AdsrPhase)phase : AdsrPhase.Off;
        Envelope = Math.Clamp(r.ReadInt32(), 0, MaxEnvelope); ReachedEnd = r.ReadBoolean();
        _curAddr = r.ReadUInt32(); _sampleIdx = Math.Clamp(r.ReadInt32(), 0, SamplesPerBlock); _old1 = r.ReadInt32(); _old2 = r.ReadInt32();
        _pitchCounter = r.ReadUInt32() & 0xFFFU;
        _s0 = r.ReadInt16(); _s1 = r.ReadInt16(); _envCycles = Math.Max(0, r.ReadInt32()); _pendingEnd = r.ReadBoolean(); _pendingRepeat = r.ReadBoolean(); _needsPrime = r.ReadBoolean();
        for (int i = 0; i < _decoded.Length; i++) _decoded[i] = r.ReadInt16();
    }

    /// <summary>Decodes the 28 BRR samples of one 16-byte SPU ADPCM block (shift 13-15 behave as 9).</summary>
    public static void DecodeBlock(ReadOnlySpan<byte> block, Span<short> dst, ref int old1, ref int old2)
    {
        int shift = block[0] & 0xF;
        if (shift > 12) shift = 9;
        int filter = Math.Min(4, (block[0] >> 4) & 0x7);
        int k0 = FilterK0[filter], k1 = FilterK1[filter];
        for (int i = 0; i < SamplesPerBlock; i++)
        {
            byte b = block[2 + (i >> 1)];
            int nib = (i & 1) == 0 ? (b & 0xF) : (b >> 4);
            int raw = ((nib << 28) >> 16) >> shift;
            int sample = Math.Clamp(raw + ((old1 * k0 + old2 * k1 + 32) >> 6), short.MinValue, short.MaxValue);
            dst[i] = (short)sample;
            old2 = old1;
            old1 = sample;
        }
    }

    private short NextSample(byte[] ram)
    {
        if (_sampleIdx >= SamplesPerBlock && !DecodeNextBlock(ram)) return 0;
        return _decoded[_sampleIdx++];
    }

    private bool DecodeNextBlock(byte[] ram)
    {
        if (_pendingEnd)
        {
            _pendingEnd = false;
            ReachedEnd = true;
            if (!_pendingRepeat)
            {
                ForceOff();
                return false;
            }
            _curAddr = (uint)LoopAddr << 3;
        }

        uint mask = (uint)ram.Length - 1U;
        uint addr = _curAddr & mask & ~0xFU;
        if (addr + BlockBytes > (uint)ram.Length)
        {
            ForceOff();
            return false;
        }

        byte flags = ram[addr + 1];
        if ((flags & 0x04) != 0) LoopAddr = (ushort)(addr >> 3);
        DecodeBlock(ram.AsSpan((int)addr, BlockBytes), _decoded, ref _old1, ref _old2);
        _sampleIdx = 0;

        if ((flags & 0x01) != 0)
        {
            _pendingEnd = true;
            _pendingRepeat = (flags & 0x02) != 0;
        }
        _curAddr = (addr + BlockBytes) & mask;
        return true;
    }

    private void GetRate(out int shift, out int stepValue, out bool exponential, out bool decrease)
    {
        uint lo = Adsr & 0xFFFFU, hi = Adsr >> 16;
        switch (Phase)
        {
            case AdsrPhase.Attack:
                exponential = (lo & 0x8000U) != 0;
                decrease = false;
                shift = (int)((lo >> 10) & 0x1FU);
                stepValue = 7 - (int)((lo >> 8) & 0x3U);
                break;
            case AdsrPhase.Decay:
                exponential = true;
                decrease = true;
                shift = (int)((lo >> 4) & 0xFU);
                stepValue = -8;
                break;
            case AdsrPhase.Sustain:
                exponential = (hi & 0x8000U) != 0;
                decrease = (hi & 0x4000U) != 0;
                shift = (int)((hi >> 8) & 0x1FU);
                int s = (int)((hi >> 6) & 0x3U);
                stepValue = decrease ? -8 + s : 7 - s;
                break;
            default:
                exponential = (hi & 0x20U) != 0;
                decrease = true;
                shift = (int)(hi & 0x1FU);
                stepValue = -8;
                break;
        }
    }
}
