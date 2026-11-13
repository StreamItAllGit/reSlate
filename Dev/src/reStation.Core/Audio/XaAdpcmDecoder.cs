// @module: reStation.Core.Audio.XaAdpcmDecoder
// @purpose: CD-ROM XA-ADPCM (4-bit/8-bit, mono/stereo) sound-group decoder with persistent filter history across sectors and exact 3:7 / 6:7 linear resampling to 44.1 kHz (KFR-PARITY-02c fix).
// @depends-on: System, System.IO
// @exports: XaAdpcmDecoder
// @owner: devv-audio-mdec (T3)

namespace reStation.Core.Audio;

/// <summary>
/// Decodes 128-byte XA sound groups into interleaved 44.1 kHz stereo. Filter history (<c>_old1L/_old2L/_old1R/_old2R</c>)
/// and the resampler phase persist across consecutive sectors; only <see cref="Reset"/> (disc stop / quick-load) clears them.
/// 18.9 kHz input is resampled 3:7 and 37.8 kHz input 6:7, so N source samples always yield exactly N*7/3 or N*7/6 frames.
/// </summary>
public sealed class XaAdpcmDecoder
{
    public const int SoundGroupSize = 128;
    public const int SoundGroupsPerSector = 18;
    public const int SamplesPerUnit = 28;
    private const int PhaseDenominator = 7;

    private static readonly int[] FilterK0 = { 0, 60, 115, 98 };
    private static readonly int[] FilterK1 = { 0, 0, -52, -55 };

    private int _old1L, _old2L, _old1R, _old2R;
    private short _prevL, _prevR, _curL, _curR;
    private int _phase;

    public int Old1L => _old1L;
    public int Old2L => _old2L;
    public int Old1R => _old1R;
    public int Old2R => _old2R;

    /// <summary>Clears filter history and resampler phase (disc stop / quick-load only).</summary>
    public void Reset()
    {
        _old1L = _old2L = _old1R = _old2R = 0;
        _prevL = _prevR = _curL = _curR = 0;
        _phase = 0;
    }

    public static bool IsStereo(byte codingInfo) => (codingInfo & 0x03) == 1;
    public static bool Is8Bit(byte codingInfo) => (codingInfo & 0x30) == 0x10;
    public static int GetSourceSampleRate(byte codingInfo) => (codingInfo & 0x0C) == 0x04 ? 18_900 : 37_800;

    /// <summary>Upper bound of stereo output frames produced by <paramref name="soundGroupCount"/> groups.</summary>
    public static int GetMaxOutputFrames(int soundGroupCount, byte codingInfo)
    {
        int samplesPerGroup = (Is8Bit(codingInfo) ? 4 : 8) * SamplesPerUnit;
        int perChannel = soundGroupCount * samplesPerGroup / (IsStereo(codingInfo) ? 2 : 1);
        int step = GetSourceSampleRate(codingInfo) == 18_900 ? 3 : 6;
        return (perChannel * PhaseDenominator + step - 1) / step + 1;
    }

    /// <summary>
    /// Decodes every complete 128-byte sound group in <paramref name="soundGroups"/> and writes interleaved L/R
    /// 44.1 kHz samples into <paramref name="stereoOut"/>. Returns the number of stereo frames written.
    /// </summary>
    public int DecodeSector(ReadOnlySpan<byte> soundGroups, byte codingInfo, Span<short> stereoOut)
    {
        int groups = soundGroups.Length / SoundGroupSize;
        if (stereoOut.Length < GetMaxOutputFrames(groups, codingInfo) * 2)
            throw new ArgumentException("Output span is smaller than GetMaxOutputFrames(...) * 2.", nameof(stereoOut));

        bool stereo = IsStereo(codingInfo);
        bool eightBit = Is8Bit(codingInfo);
        int step = GetSourceSampleRate(codingInfo) == 18_900 ? 3 : 6;
        int units = eightBit ? 4 : 8;
        Span<int> shift = stackalloc int[8];
        Span<int> k0 = stackalloc int[8];
        Span<int> k1 = stackalloc int[8];
        int written = 0;

        for (int g = 0; g < groups; g++)
        {
            ReadOnlySpan<byte> sg = soundGroups.Slice(g * SoundGroupSize, SoundGroupSize);
            for (int u = 0; u < units; u++) ParseParam(sg[4 + u], eightBit, out shift[u], out k0[u], out k1[u]);

            if (stereo)
            {
                for (int u = 0; u < units; u += 2)
                {
                    for (int s = 0; s < SamplesPerUnit; s++)
                    {
                        short l = DecodeSample(sg, eightBit, u, s, shift[u], k0[u], k1[u], ref _old1L, ref _old2L);
                        short r = DecodeSample(sg, eightBit, u + 1, s, shift[u + 1], k0[u + 1], k1[u + 1], ref _old1R, ref _old2R);
                        written = Emit(l, r, step, stereoOut, written);
                    }
                }
            }
            else
            {
                for (int u = 0; u < units; u++)
                {
                    for (int s = 0; s < SamplesPerUnit; s++)
                    {
                        short m = DecodeSample(sg, eightBit, u, s, shift[u], k0[u], k1[u], ref _old1L, ref _old2L);
                        written = Emit(m, m, step, stereoOut, written);
                    }
                }
            }
        }
        return written;
    }

    public void WriteState(BinaryWriter w)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.Write(_old1L); w.Write(_old2L); w.Write(_old1R); w.Write(_old2R);
        w.Write(_prevL); w.Write(_prevR); w.Write(_curL); w.Write(_curR); w.Write(_phase);
    }

    public void ReadState(BinaryReader r)
    {
        ArgumentNullException.ThrowIfNull(r);
        _old1L = r.ReadInt32(); _old2L = r.ReadInt32(); _old1R = r.ReadInt32(); _old2R = r.ReadInt32();
        _prevL = r.ReadInt16(); _prevR = r.ReadInt16(); _curL = r.ReadInt16(); _curR = r.ReadInt16();
        _phase = Math.Clamp(r.ReadInt32(), 0, PhaseDenominator - 1);
    }

    /// <summary>Unit parameters live at header bytes 4..11 (bytes 0..3 and 12..15 are copies).</summary>
    private static void ParseParam(byte param, bool eightBit, out int shift, out int k0, out int k1)
    {
        shift = param & 0x0F;
        if (shift > (eightBit ? 8 : 12)) shift = 9 - (eightBit ? 1 : 0);
        int filter = (param >> 4) & 0x03;
        k0 = FilterK0[filter];
        k1 = FilterK1[filter];
    }

    private static short DecodeSample(ReadOnlySpan<byte> sg, bool eightBit, int unit, int s, int shift, int k0, int k1, ref int old1, ref int old2)
    {
        int raw;
        if (eightBit)
        {
            raw = ((sbyte)sg[16 + s * 4 + unit] << 8) >> shift;
        }
        else
        {
            byte b = sg[16 + s * 4 + (unit >> 1)];
            int nib = (unit & 1) == 0 ? (b & 0x0F) : (b >> 4);
            raw = ((nib << 28) >> 16) >> shift;
        }
        int sample = Math.Clamp(raw + ((old1 * k0 + old2 * k1 + 32) >> 6), short.MinValue, short.MaxValue);
        old2 = old1;
        old1 = sample;
        return (short)sample;
    }

    /// <summary>Consumes one source frame and emits every output frame whose phase falls before it (exact 3:7 / 6:7).</summary>
    private int Emit(short l, short r, int step, Span<short> output, int written)
    {
        _prevL = _curL;
        _prevR = _curR;
        _curL = l;
        _curR = r;
        while (_phase < PhaseDenominator)
        {
            int inv = PhaseDenominator - _phase;
            output[written * 2] = (short)((_prevL * inv + _curL * _phase) / PhaseDenominator);
            output[written * 2 + 1] = (short)((_prevR * inv + _curR * _phase) / PhaseDenominator);
            written++;
            _phase += step;
        }
        _phase -= PhaseDenominator;
        return written;
    }
}
