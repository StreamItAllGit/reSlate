// @module: reStation.Core.Audio.PsxSpu
// @purpose: PS1 SPU unit (512 KB RAM, 24 ADSR voices, DMA4, XA intake queue) mixing 44.1 kHz stereo into Foundation.Core.AudioRingBuffer(65536, 2) with XA drop telemetry and a zero-ramp XA underrun guard (KFR-PARITY-02b fix).
// @depends-on: Foundation.Core, reStation.Core
// @exports: PsxSpu
// @owner: devv-audio-mdec (T3) — T1 public members are cross-slice LOCKED (PsxCpu bus/CD-ROM/Player audio consume them).

using Foundation.Core;

namespace reStation.Core.Audio;

public sealed class PsxSpu
{
    public const int SampleRate = 44_100;
    public const int VoiceCount = 24;
    public const int RamSize = 512 * 1024;
    /// <summary>Ring capacity in interleaved shorts (32,768 stereo frames ≈ 743 ms @ 44.1 kHz).</summary>
    public const int RingCapacitySamples = 65_536;
    /// <summary>Pending decoded XA stereo frames held ahead of the mixer (≈ 743 ms).</summary>
    public const int XaQueueCapacityFrames = 32_768;
    private const int XaRampFrames = 32;
    private const uint SnapshotMarker = 0x32505352U; // "RSP2"

    private readonly ushort[] _regs = new ushort[0x200];
    private readonly short[] _frame = new short[2];
    private readonly SpuVoice[] _voices = new SpuVoice[VoiceCount];
    private readonly XaAdpcmDecoder _xaDecoder = new();
    private readonly short[] _xaQueue = new short[XaQueueCapacityFrames * 2];
    private short[] _xaScratch = new short[XaAdpcmDecoder.GetMaxOutputFrames(XaAdpcmDecoder.SoundGroupsPerSector, 0x05) * 2];
    private int _xaHead, _xaCount;
    private int _xaRampLeft;
    private short _lastXaL, _lastXaR;
    private uint _transferAddr;
    private uint _endx;
    private long _xaDrops, _outputOverflows, _xaFramesDecoded;
    private int _mainVolL = 0x3FFF, _mainVolR = 0x3FFF;
    private double _cdScaleL = 1.0, _cdScaleR = 1.0, _mainScaleL = 1.0, _mainScaleR = 1.0;
    private bool? _fastForwardOverride;
    private double _fastForwardMultiplierOverride = 2.0;
    private double _ffSampleAccum;
    private float[]? _ffScratch;

    public PsxSpu(RecompSettings settings)
    {
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Output = new AudioRingBuffer(RingCapacitySamples, 2);
        for (int i = 0; i < VoiceCount; i++) _voices[i] = new SpuVoice();
        _regs[0x180 >> 1] = 0x3FFF;
        _regs[0x182 >> 1] = 0x3FFF;
        _regs[0x1B0 >> 1] = 0x7FFF;
        _regs[0x1B2 >> 1] = 0x7FFF;
        _regs[0x1AA >> 1] = 0xC001;
        RefreshCachedScales();
    }

    public RecompSettings Settings { get; }
    public byte[] Ram { get; } = new byte[RamSize];
    /// <summary>Mixed host output consumed by the Player audio stream (SDL_PutAudioStreamData).</summary>
    public AudioRingBuffer Output { get; }
    /// <summary>XA stereo frames lost to a full XA queue or full output ring (must stay 0 in healthy playback).</summary>
    public long XaDrops => Interlocked.Read(ref _xaDrops);
    public double AudioFillRatio => Output.FillRatio;
    public long AudioUnderruns => Output.UnderrunCount;
    public int ActiveVoiceCount { get; private set; }
    public IReadOnlyList<SpuVoice> Voices => _voices;
    /// <summary>Decoded XA frames waiting for the mixer.</summary>
    public int PendingXaFrames => _xaCount;
    /// <summary>Mixed frames (with or without XA) that could not be written because <see cref="Output"/> was full.</summary>
    public long OutputOverflows => Interlocked.Read(ref _outputOverflows);
    public long XaFramesDecoded => Interlocked.Read(ref _xaFramesDecoded);

    /// <summary>MMIO 0x1F801C00-0x1F801FFF halfword read.</summary>
    public ushort Read16(uint address)
    {
        uint reg = address & 0x3FFU;
        if (reg < 0x180 && (reg & 0xFU) == 0xC) return (ushort)_voices[reg >> 4].Envelope;
        return reg switch
        {
            0x19C => (ushort)_endx,
            0x19E => (ushort)(_endx >> 16),
            0x1AE => (ushort)(_regs[0x1AA >> 1] & 0x3F),
            _ => _regs[reg >> 1]
        };
    }

    /// <summary>MMIO 0x1F801C00-0x1F801FFF halfword write.</summary>
    public void Write16(uint address, ushort value)
    {
        uint reg = address & 0x3FFU;
        _regs[reg >> 1] = value;
        if (reg < 0x180)
        {
            WriteVoiceRegister(_voices[reg >> 4], reg & 0xFU, value);
            return;
        }
        switch (reg)
        {
            case 0x180: if ((value & 0x8000) == 0) { _mainVolL = DecodeSigned15(value); _mainScaleL = ScaleMainVol(_mainVolL); } break;
            case 0x182: if ((value & 0x8000) == 0) { _mainVolR = DecodeSigned15(value); _mainScaleR = ScaleMainVol(_mainVolR); } break;
            case 0x1B0: _cdScaleL = ScaleCdVol(value); break;
            case 0x1B2: _cdScaleR = ScaleCdVol(value); break;
            case 0x188: KeyBits(0, value, true); break;
            case 0x18A: KeyBits(16, value, true); break;
            case 0x18C: KeyBits(0, value, false); break;
            case 0x18E: KeyBits(16, value, false); break;
            case 0x1A6: _transferAddr = ((uint)value << 3) & (RamSize - 1); break;
            case 0x1A8: WriteRam16(value); break;
        }
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
        int needed = XaAdpcmDecoder.GetMaxOutputFrames(soundGroups.Length / 128, codingInfo) * 2;
        if (_xaScratch.Length < needed) _xaScratch = new short[needed];

        int frames = _xaDecoder.DecodeSector(soundGroups, codingInfo, _xaScratch);
        Interlocked.Add(ref _xaFramesDecoded, frames);
        int free = XaQueueCapacityFrames - _xaCount;
        if (frames > free && IsFastForwardDecimationActive)
        {
            int discardOldest = Math.Min(_xaCount, frames - free);
            _xaHead = (_xaHead + discardOldest) % XaQueueCapacityFrames;
            _xaCount -= discardOldest;
            free = XaQueueCapacityFrames - _xaCount;
        }
        int accepted = Math.Min(frames, free);
        int tail = (_xaHead + _xaCount) % XaQueueCapacityFrames;
        for (int i = 0; i < accepted; i++)
        {
            int slot = ((tail + i) % XaQueueCapacityFrames) * 2;
            _xaQueue[slot] = _xaScratch[i * 2];
            _xaQueue[slot + 1] = _xaScratch[i * 2 + 1];
        }
        _xaCount += accepted;
        if (accepted < frames) Interlocked.Add(ref _xaDrops, frames - accepted);
    }

    /// <summary>Clears XA decoder history and pending XA samples (disc stop / quick-load only).</summary>
    public void ResetXaState()
    {
        _xaDecoder.Reset();
        _xaHead = _xaCount = 0;
        _xaRampLeft = 0;
        _lastXaL = _lastXaR = 0;
        _ffSampleAccum = 0.0;
    }

    /// <summary>True when SPU Fast-Forward audio decimation and queue-overflow protection are active.</summary>
    public bool IsFastForwardDecimationActive => _fastForwardOverride ?? Settings.IsFastForwardEnabled;

    /// <summary>Overrides runtime Fast-Forward audio decimation (<c>2x..4x</c>) independently of persisted settings.</summary>
    public void SetFastForwardDecimation(bool enabled, double multiplier = 2.0)
    {
        _fastForwardOverride = enabled;
        _fastForwardMultiplierOverride = NormalizeFastForwardMultiplier(multiplier, 2.0);
        if (!enabled) _ffSampleAccum = 0.0;
    }

    /// <summary>Clears any transient Fast-Forward decimation override, reverting to <see cref="RecompSettings.IsFastForwardEnabled"/>.</summary>
    public void ClearFastForwardDecimationOverride()
    {
        _fastForwardOverride = null;
        _ffSampleAccum = 0.0;
    }

    /// <summary>
    /// Discards excess queued XA frames during Fast-Forward without incrementing <see cref="XaDrops"/>.
    /// Returns the number of stereo frames discarded.
    /// </summary>
    public int DiscardExcessXaFramesForFastForward(int maxRetainedFrames = 1470)
    {
        int limit = Math.Clamp(maxRetainedFrames, 0, XaQueueCapacityFrames);
        if (_xaCount <= limit) return 0;
        int discard = _xaCount - limit;
        _xaHead = (_xaHead + discard) % XaQueueCapacityFrames;
        _xaCount = limit;
        if (_xaCount == 0)
        {
            _xaRampLeft = 0;
            _lastXaL = _lastXaR = 0;
        }
        return discard;
    }

    /// <summary>
    /// Decimates buffered <see cref="Output"/> audio and trims excess pending XA frames after a <paramref name="multiplier"/>
    /// Fast-Forward burst (<c>2x..4x</c>) so host playback stays near real-time with <c>XaDrops == 0</c>.
    /// </summary>
    public int ResampleOrTrimFastForwardAudio(double multiplier)
    {
        double mult = double.IsFinite(multiplier) ? Math.Clamp(multiplier, 1.0, 4.0) : 2.0;
        _ = DiscardExcessXaFramesForFastForward(Math.Max(9408, XaQueueCapacityFrames / 2));
        if (mult <= 1.0) return 0;

        int evenSamples = Output.BufferedSamples & ~1;
        int totalFrames = evenSamples / 2;
        if (totalFrames <= 1) return 0;

        _ffScratch ??= new float[RingCapacitySamples];
        int readCount = Math.Min(evenSamples, _ffScratch.Length) & ~1;
        int got = Output.Read(_ffScratch.AsSpan(0, readCount));
        int readFrames = got / 2;
        if (readFrames <= 0) return 0;

        int targetFrames = IsFastForwardDecimationActive
            ? Math.Min(readFrames, 1470)
            : Math.Clamp((int)Math.Round(readFrames / mult), 1, 1470);
        if (targetFrames >= readFrames)
        {
            _ = Output.Write(_ffScratch.AsSpan(0, readFrames * 2));
            return 0;
        }

        double step = readFrames / (double)targetFrames;
        for (int i = 0; i < targetFrames; i++)
        {
            int srcIdx = Math.Min(readFrames - 1, (int)(i * step)) * 2;
            _ffScratch[i * 2] = _ffScratch[srcIdx];
            _ffScratch[i * 2 + 1] = _ffScratch[srcIdx + 1];
        }
        _ = Output.Write(_ffScratch.AsSpan(0, targetFrames * 2));
        return readFrames - targetFrames;
    }

    /// <summary>Advances one 44.1 kHz sample tick: steps envelopes, mixes voices + XA, and writes one stereo frame.</summary>
    public void TickSample()
    {
        int mixL = 0, mixR = 0, active = 0;
        for (int v = 0; v < VoiceCount; v++)
        {
            SpuVoice voice = _voices[v];
            if (!voice.Active) continue;
            bool endedBefore = voice.ReachedEnd;
            voice.StepSample(Ram, out int vl, out int vr);
            mixL += vl;
            mixR += vr;
            if (!endedBefore && voice.ReachedEnd) _endx |= 1U << v;
            if (voice.Active) active++;
        }
        ActiveVoiceCount = active;

        ushort cnt = _regs[0x1AA >> 1];
        bool spuEnabled = (cnt & 0x8000) != 0;
        bool voicesUnmuted = (cnt & 0x4000) != 0;
        bool cdEnabled = (cnt & 0x0001) != 0;
        if (!spuEnabled || !voicesUnmuted) { mixL = 0; mixR = 0; }

        double cdScaleL = _cdScaleL;
        double cdScaleR = _cdScaleR;
        bool cdAudible = spuEnabled && cdEnabled && (cdScaleL != 0.0 || cdScaleR != 0.0);
        bool carriesXa = false;
        if (_xaCount > 0)
        {
            int slot = _xaHead * 2;
            short rawL = _xaQueue[slot];
            short rawR = _xaQueue[slot + 1];
            _xaHead = (_xaHead + 1) % XaQueueCapacityFrames;
            _xaCount--;
            if (cdAudible)
            {
                _lastXaL = rawL;
                _lastXaR = rawR;
                _xaRampLeft = XaRampFrames;
                mixL += ScaleSample(rawL, cdScaleL);
                mixR += ScaleSample(rawR, cdScaleR);
                carriesXa = true;
            }
            else
            {
                _xaRampLeft = 0;
                _lastXaL = _lastXaR = 0;
            }
        }
        else if (_xaRampLeft > 0)
        {
            if (cdAudible)
            {
                // Zero-ramp underrun guard: fade the last XA sample to silence instead of stepping to 0 (no pop/click).
                _xaRampLeft--;
                mixL += ScaleSample(_lastXaL * _xaRampLeft / XaRampFrames, cdScaleL);
                mixR += ScaleSample(_lastXaR * _xaRampLeft / XaRampFrames, cdScaleR);
                if (_xaRampLeft == 0) _lastXaL = _lastXaR = 0;
            }
            else
            {
                _xaRampLeft = 0;
                _lastXaL = _lastXaR = 0;
            }
        }

        if (!spuEnabled) { mixL = 0; mixR = 0; }
        else
        {
            mixL = ScaleSample(mixL, _mainScaleL);
            mixR = ScaleSample(mixR, _mainScaleR);
        }

        double vol = Settings.MasterVolume;
        _frame[0] = (short)Math.Clamp((int)(mixL * vol), short.MinValue, short.MaxValue);
        _frame[1] = (short)Math.Clamp((int)(mixR * vol), short.MinValue, short.MaxValue);
        if (IsFastForwardDecimationActive)
        {
            double mult = _fastForwardOverride.HasValue
                ? _fastForwardMultiplierOverride
                : NormalizeFastForwardMultiplier(Settings.FastForwardMultiplier, 2.0);
            _ffSampleAccum += 1.0;
            if (_ffSampleAccum + 1e-9 < mult) return;
            _ffSampleAccum -= mult;
            EnsureFastForwardOutputRoom();
        }
        if (Output.Write(_frame) < 2)
        {
            Interlocked.Increment(ref _outputOverflows);
            if (carriesXa) Interlocked.Increment(ref _xaDrops);
        }
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
        w.Write(_endx);
        foreach (SpuVoice v in _voices) v.WriteState(w);
        _xaDecoder.WriteState(w);
    }

    public void ReadSnapshotState(BinaryReader r)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (r.ReadUInt32() != SnapshotMarker) throw new InvalidDataException("Snapshot SPU marker mismatch.");
        r.BaseStream.ReadExactly(Ram);
        for (int i = 0; i < _regs.Length; i++) _regs[i] = r.ReadUInt16();
        _transferAddr = r.ReadUInt32() & (RamSize - 1);
        _endx = r.ReadUInt32() & 0xFFFFFFU;
        int active = 0;
        foreach (SpuVoice v in _voices)
        {
            v.ReadState(r);
            if (v.Active) active++;
        }
        _xaDecoder.ReadState(r);
        _xaHead = _xaCount = 0;
        _xaRampLeft = 0;
        _lastXaL = _lastXaR = 0;
        _ffSampleAccum = 0.0;
        if ((_regs[0x180 >> 1] & 0x8000) == 0) _mainVolL = DecodeSigned15(_regs[0x180 >> 1]);
        if ((_regs[0x182 >> 1] & 0x8000) == 0) _mainVolR = DecodeSigned15(_regs[0x182 >> 1]);
        RefreshCachedScales();
        ActiveVoiceCount = active;
        Output.Clear();
    }

    private void RefreshCachedScales()
    {
        _mainScaleL = ScaleMainVol(_mainVolL);
        _mainScaleR = ScaleMainVol(_mainVolR);
        _cdScaleL = ScaleCdVol(_regs[0x1B0 >> 1]);
        _cdScaleR = ScaleCdVol(_regs[0x1B2 >> 1]);
    }

    private void EnsureFastForwardOutputRoom()
    {
        if (Output.AvailableWriteSamples >= 2) return;
        Span<float> discard = stackalloc float[Math.Min(512, RingCapacitySamples)];
        int readCount = Math.Min(discard.Length, Output.BufferedSamples) & ~1;
        if (readCount > 0) _ = Output.Read(discard[..readCount]);
    }

    private static double NormalizeFastForwardMultiplier(double multiplier, double minValue) =>
        double.IsFinite(multiplier) ? Math.Clamp(multiplier, minValue, 4.0) : 2.0;

    private static int DecodeSigned15(ushort v) => (v & 0x4000) != 0 ? (int)(v | 0xFFFFC000U) : (int)(v & 0x3FFF);
    private static double ScaleMainVol(int v) => v == 0x3FFF ? 1.0 : Math.Clamp(v / 16384.0, -1.0, 1.0);
    private static double ScaleCdVol(ushort raw) => raw == 0x7FFF ? 1.0 : Math.Clamp(unchecked((short)raw) / 32768.0, -1.0, 1.0);
    private static int ScaleSample(int sample, double scale) => scale == 1.0 ? sample : (int)Math.Round(sample * scale);

    private static void WriteVoiceRegister(SpuVoice voice, uint offset, ushort value)
    {
        switch (offset)
        {
            // Bit 15 clear = fixed volume (bits 14-0 = volume / 2); sweep mode keeps the current level.
            case 0x0: if ((value & 0x8000) == 0) voice.VolLeft = (short)(value << 1); break;
            case 0x2: if ((value & 0x8000) == 0) voice.VolRight = (short)(value << 1); break;
            case 0x4: voice.Pitch = value; break;
            case 0x6: voice.StartAddr = value; break;
            case 0x8: voice.Adsr = (voice.Adsr & 0xFFFF0000U) | value; break;
            case 0xA: voice.Adsr = (voice.Adsr & 0x0000FFFFU) | ((uint)value << 16); break;
            case 0xE: voice.LoopAddr = value; break;
        }
    }

    private void KeyBits(int offset, ushort mask, bool on)
    {
        for (int i = 0; i < 16 && offset + i < VoiceCount; i++)
        {
            if ((mask & (1 << i)) == 0) continue;
            int v = offset + i;
            if (on)
            {
                _voices[v].KeyOn();
                _endx &= ~(1U << v);
            }
            else
            {
                _voices[v].KeyOff();
            }
        }
    }

    private void WriteRam16(ushort value)
    {
        Ram[_transferAddr] = (byte)value;
        Ram[(_transferAddr + 1) & (RamSize - 1)] = (byte)(value >> 8);
        _transferAddr = (_transferAddr + 2) & (RamSize - 1);
    }
}
