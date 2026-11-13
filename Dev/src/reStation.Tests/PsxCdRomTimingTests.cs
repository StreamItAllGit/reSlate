// @module: reStation.Tests.PsxCdRomTimingTests
// @purpose: Verifies KFR-PARITY-01 CD-ROM speed cadence (1x/2x/Form1 fast-load guard), DiscManifest schema, Root Counters, and IRQ flags.
// @depends-on: Xunit, reStation.Contracts, reStation.Core, reStation.Core.CdRom, reStation.Core.Cpu, reStation.Core.DiscIngestion, reStation.Core.Gpu, reStation.Core.Hardware
// @exports: PsxCdRomTimingTests
// @owner: devv-pkg-test (T8)

using reStation.Core;
using reStation.Core.CdRom;
using reStation.Core.Cpu;
using reStation.Core.DiscIngestion;
using reStation.Core.Gpu;
using reStation.Core.Hardware;
using Xunit;

namespace reStation.Tests;

public sealed class PsxCdRomTimingTests
{
    [Theory]
    [InlineData((byte)0x80, false, false, false, PsxCdRomTiming.CyclesPerSectorDoubleSpeed)]
    [InlineData((byte)0x00, false, false, false, PsxCdRomTiming.CyclesPerSectorSingleSpeed)]
    [InlineData((byte)0x80, true, false, true, PsxCdRomTiming.CyclesPerSectorDoubleSpeed)]
    [InlineData((byte)0xC0, false, false, true, PsxCdRomTiming.CyclesPerSectorDoubleSpeed)]
    [InlineData((byte)0xA0, false, false, true, PsxCdRomTiming.CyclesPerSectorDoubleSpeed)]
    [InlineData((byte)0x80, false, true, true, PsxCdRomTiming.CyclesPerSectorDoubleSpeed)]
    [InlineData((byte)0x80, false, false, true, PsxCdRomTiming.CyclesPerSectorFastLoadForm1)]
    public void ComputeReadLatencyCycles_RespectsHardwareTimingAndFastLoadGuard(
        byte mode, bool isXaOrForm2, bool isRawMdec, bool shouldFastLoad, long expectedCycles)
    {
        long actual = PsxCdRomTiming.ComputeReadLatencyCycles(mode, isXaOrForm2, isRawMdec, shouldFastLoad);
        Assert.Equal(expectedCycles, actual);
    }

    [Fact]
    public void SectorsPerSecond_MatchesHardwareSpeeds()
    {
        Assert.Equal(75.0, PsxCdRomTiming.SectorsPerSecond(PsxCdRomTiming.CyclesPerSectorSingleSpeed), 2);
        Assert.Equal(150.0, PsxCdRomTiming.SectorsPerSecond(PsxCdRomTiming.CyclesPerSectorDoubleSpeed), 2);
        Assert.True(PsxCdRomTiming.SectorsPerSecond(PsxCdRomTiming.CyclesPerSectorFastLoadForm1) > 1000.0);
        Assert.Equal(0.0, PsxCdRomTiming.SectorsPerSecond(0));
    }

    [Fact]
    public void SingleSpeedXaStream_NeverAcceleratesEvenWhenFastLoadEnabled()
    {
        long singleXa = PsxCdRomTiming.ComputeReadLatencyCycles(
            PsxCdRomTiming.ModeXaAdpcm, isXaOrForm2: true, isRawMdec: false, shouldFastLoadData: true);
        Assert.Equal(PsxCdRomTiming.CyclesPerSectorSingleSpeed, singleXa);
        Assert.Equal(75.0, PsxCdRomTiming.SectorsPerSecond(singleXa), 2);
    }

    [Fact]
    public void TimersAndDma_RootCounter1_TracksScanlineRateAccurately()
    {
        var cpu = new PsxCpu(new RecompSettings(), Path.GetTempPath(), new SoftwareCpuRenderBackend());
        cpu.TimersAndDma.WriteTimerReg(0x1F801114U, 0x0100U); // Timer1 clock source = HBlank scanline
        cpu.TimersAndDma.TickTo(PsxCpu.CyclesPerFrame);

        uint scanlines = cpu.TimersAndDma.ReadTimerReg(0x1F801110U);
        Assert.InRange(scanlines, 260U, 265U);
    }

    [Fact]
    public void TimersAndDma_RootCounter2_SyncStopModeFreezesCounter()
    {
        var cpu = new PsxCpu(new RecompSettings(), Path.GetTempPath(), new SoftwareCpuRenderBackend());
        cpu.TimersAndDma.WriteTimerReg(0x1F801124U, 0x0001U); // Timer2 sync enable + mode 0 (stop counter)
        cpu.TimersAndDma.TickTo(PsxTimersAndDma.CyclesPerScanline * 10);

        Assert.Equal(0U, cpu.TimersAndDma.ReadTimerReg(0x1F801120U));
    }

    [Fact]
    public void TimersAndDma_RaiseIrq_SignalsOnlyWhenMasked()
    {
        var cpu = new PsxCpu(new RecompSettings(), Path.GetTempPath(), new SoftwareCpuRenderBackend());
        cpu.TimersAndDma.InterruptMask = 1U << PsxIrq.CdRom;

        cpu.TimersAndDma.RaiseIrq(PsxIrq.VBlank);
        Assert.False(cpu.TimersAndDma.HasPendingInterrupt);

        cpu.TimersAndDma.RaiseIrq(PsxIrq.CdRom);
        Assert.True(cpu.TimersAndDma.HasPendingInterrupt);
    }

    [Fact]
    public void DiscManifest_SaveAndLoad_RoundTripsInsideDataDir()
    {
        string temp = Path.Combine(Path.GetTempPath(), $"restation-discmanifest-{Guid.NewGuid():N}");
        try
        {
            var manifest = new DiscManifest
            {
                ModuleId = "demo_mod",
                SerialNumber = "SLUS-00001",
                VolumeId = "DEMOVOL",
                PrimaryImagePath = Path.Combine(temp, "gamedata", "disc", "game.bin"),
                CueSheetPath = Path.Combine(temp, "gamedata", "disc", "game.cue"),
                VerifiedAtUtc = "2026-11-13T00:00:00.000Z",
                Files =
                {
                    new DiscFileEntry { RelativeFile = "XA/BGM01.XA;1", Lba = 500, SectorCount = 120, ByteLength = 120 * 2336U, IsForm2Raw = true }
                }
            };
            manifest.Save(temp);

            DiscManifest? loaded = DiscManifest.TryLoad(temp);
            Assert.NotNull(loaded);
            Assert.Equal("demo_mod", loaded!.ModuleId);
            Assert.Equal("SLUS-00001", loaded.SerialNumber);
            Assert.Equal("DEMOVOL", loaded.VolumeId);
            Assert.NotNull(loaded.FindByLba(550));
            Assert.True(loaded.FindByName("xa/bgm01.xa")!.IsForm2Raw);
        }
        finally
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        }
    }
}
