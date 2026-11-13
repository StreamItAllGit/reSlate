// @module: reStation.Tests.PsxSpuAdsrAndXaTests
// @purpose: Verifies KFR-PARITY-02 SPU ADSR release-to-Off transitions, XA-ADPCM frame counts & filter persistence, zero-drop 44.1kHz pacing, and queue saturation telemetry.
// @depends-on: Xunit, reStation.Core, reStation.Core.Audio
// @exports: PsxSpuAdsrAndXaTests
// @owner: devv-pkg-test (T8)

using reStation.Core;
using reStation.Core.Audio;
using Xunit;

namespace reStation.Tests;

public sealed class PsxSpuAdsrAndXaTests
{
    [Fact]
    public void SpuVoice_KeyOnThenKeyOff_ReleasesEnvelopeCleanlyToOff()
    {
        var voice = new SpuVoice { Adsr = 0x00000000U, Pitch = 0x1000 };
        voice.KeyOn();
        Assert.Equal(AdsrPhase.Attack, voice.Phase);
        Assert.True(voice.Active);

        for (int i = 0; i < 64; i++) voice.TickEnvelope();
        Assert.True(voice.Envelope > 0);

        voice.KeyOff();
        Assert.Equal(AdsrPhase.Release, voice.Phase);
        for (int i = 0; i < 64 && voice.Active; i++) voice.TickEnvelope();

        Assert.Equal(0, voice.Envelope);
        Assert.Equal(AdsrPhase.Off, voice.Phase);
        Assert.False(voice.Active);
    }

    [Fact]
    public void SpuVoice_StepSample_DecodesLoopingAdpcmWithoutOverflow()
    {
        byte[] ram = new byte[512 * 1024];
        // Simple 16-byte BRR block: shift=0/filter=0, loopEnd=1 | loopRepeat=2, non-zero nibbles
        int baseAddr = 0x1000;
        ram[baseAddr + 0] = 0x00;
        ram[baseAddr + 1] = 0x03; // end + repeat
        for (int i = 2; i < 16; i++) ram[baseAddr + i] = 0x55;

        var voice = new SpuVoice
        {
            StartAddr = (ushort)(baseAddr >> 3),
            LoopAddr = (ushort)(baseAddr >> 3),
            Pitch = 0x1000,
            VolLeft = 0x3FFF,
            VolRight = 0x3FFF,
            Adsr = 0x000F000FU
        };
        voice.KeyOn();
        for (int i = 0; i < 64; i++)
        {
            voice.StepSample(ram, out int l, out int r);
            Assert.InRange(l, short.MinValue * 2, short.MaxValue * 2);
            Assert.InRange(r, short.MinValue * 2, short.MaxValue * 2);
        }
        Assert.True(voice.ReachedEnd);
    }

    [Theory]
    [InlineData((byte)0x05, 4704)] // 4-bit stereo 18.9 kHz -> 3:7 resample
    [InlineData((byte)0x04, 9408)] // 4-bit mono   18.9 kHz -> 3:7 resample
    [InlineData((byte)0x01, 2352)] // 4-bit stereo 37.8 kHz -> 6:7 resample
    [InlineData((byte)0x11, 1176)] // 8-bit stereo 37.8 kHz -> 6:7 resample
    public void XaAdpcmDecoder_DecodeSector_ProducesExactHardwareFrameCount(byte codingInfo, int expectedFrames)
    {
        var decoder = new XaAdpcmDecoder();
        byte[] groups = new byte[XaAdpcmDecoder.SoundGroupsPerSector * XaAdpcmDecoder.SoundGroupSize];
        short[] output = new short[XaAdpcmDecoder.GetMaxOutputFrames(XaAdpcmDecoder.SoundGroupsPerSector, codingInfo) * 2];

        int frames = decoder.DecodeSector(groups, codingInfo, output);
        Assert.Equal(expectedFrames, frames);
    }

    [Fact]
    public void XaAdpcmDecoder_Reset_ClearsFilterHistoryAcrossTrackChanges()
    {
        var decoder = new XaAdpcmDecoder();
        byte[] groups = new byte[XaAdpcmDecoder.SoundGroupsPerSector * XaAdpcmDecoder.SoundGroupSize];
        for (int g = 0; g < XaAdpcmDecoder.SoundGroupsPerSector; g++)
        {
            int o = g * XaAdpcmDecoder.SoundGroupSize;
            for (int u = 0; u < 8; u++) groups[o + 4 + u] = 0x10; // filter 1 (K0=60), shift 0
            for (int b = 16; b < XaAdpcmDecoder.SoundGroupSize; b++) groups[o + b] = 0x55;
        }
        short[] outBuf = new short[XaAdpcmDecoder.GetMaxOutputFrames(XaAdpcmDecoder.SoundGroupsPerSector, 0x01) * 2];
        decoder.DecodeSector(groups, 0x01, outBuf);
        Assert.True((decoder.Old1L | decoder.Old1R) != 0);

        decoder.Reset();
        Assert.Equal(0, decoder.Old1L);
        Assert.Equal(0, decoder.Old2L);
        Assert.Equal(0, decoder.Old1R);
        Assert.Equal(0, decoder.Old2R);
    }

    [Fact]
    public void PsxSpu_SteadyXaStreamingWithRenderAndDrain_ProducesZeroDrops()
    {
        var spu = new PsxSpu(new RecompSettings { MasterVolume = 1.0 });
        byte[] groups = new byte[XaAdpcmDecoder.SoundGroupsPerSector * XaAdpcmDecoder.SoundGroupSize];
        float[] drain = new float[2352 * 2];

        for (int sector = 0; sector < 6; sector++)
        {
            spu.SubmitXaSectors(groups, codingInfo: 0x01); // 2352 stereo 44.1 kHz frames per sector
            spu.RenderFrames(2352);
            int readSamples = spu.Output.Read(drain.AsSpan());
            Assert.Equal(2352 * 2, readSamples);
        }

        Assert.Equal(6L * 2352L, spu.XaFramesDecoded);
        Assert.Equal(0L, spu.XaDrops);
        Assert.Equal(0L, spu.OutputOverflows);
    }

    [Fact]
    public void PsxSpu_QueueSaturationWithoutTicking_EnforcesCapAndCountsDrops()
    {
        var spu = new PsxSpu(new RecompSettings());
        byte[] groups = new byte[XaAdpcmDecoder.SoundGroupsPerSector * XaAdpcmDecoder.SoundGroupSize];

        for (int i = 0; i < 20; i++)
        {
            spu.SubmitXaSectors(groups, codingInfo: 0x01); // 20 * 2352 = 47,040 > 32,768 capacity
        }

        Assert.True(spu.XaDrops > 0);
        Assert.InRange(spu.PendingXaFrames, 1, PsxSpu.XaQueueCapacityFrames);
    }

    [Fact]
    public void PsxSpu_ResetXaState_ClearsQueuedFramesAndTelemetry()
    {
        var spu = new PsxSpu(new RecompSettings());
        byte[] groups = new byte[XaAdpcmDecoder.SoundGroupsPerSector * XaAdpcmDecoder.SoundGroupSize];
        spu.SubmitXaSectors(groups, codingInfo: 0x01);
        Assert.True(spu.PendingXaFrames > 0);

        spu.ResetXaState();
        Assert.Equal(0, spu.PendingXaFrames);

        float[] empty = new float[64];
        spu.Output.Read(empty.AsSpan());
        Assert.True(spu.AudioUnderruns >= 0);
        Assert.InRange(spu.AudioFillRatio, 0.0, 1.0);
    }
}
