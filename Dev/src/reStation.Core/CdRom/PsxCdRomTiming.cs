// @module: reStation.Core.CdRom.PsxCdRomTiming
// @purpose: KFR-PARITY-01 fix — hardware-accurate CD-ROM sector cadence (2x 150 sectors/s, 1x 75 sectors/s, Form1-only fast load).
// @depends-on: System
// @exports: PsxCdRomTiming
// @owner: devv-cpu-cd (T2) — constants and ComputeReadLatencyCycles signature are T1-locked.

namespace reStation.Core.CdRom;

/// <summary>Pure timing math for CD-ROM ReadN/ReadS sector delivery on the 33,868,800 Hz PS1 clock.</summary>
public static class PsxCdRomTiming
{
    public const int CpuClockHz = 33_868_800;
    /// <summary>2x speed: 33,868,800 / 150 sectors/s.</summary>
    public const long CyclesPerSectorDoubleSpeed = 225_792;
    /// <summary>1x speed: 33,868,800 / 75 sectors/s.</summary>
    public const long CyclesPerSectorSingleSpeed = 451_584;
    /// <summary>Accelerated Form1 2048-byte data reads (~1,033 sectors/s) when shouldFastLoadData is enabled.</summary>
    public const long CyclesPerSectorFastLoadForm1 = 32_768;

    /// <summary>Setmode bit 7: double speed.</summary>
    public const byte ModeDoubleSpeed = 0x80;
    /// <summary>Setmode bit 6: XA-ADPCM streaming enabled.</summary>
    public const byte ModeXaAdpcm = 0x40;
    /// <summary>Setmode bit 5: whole-sector (2340-byte) reads, used by raw MDEC streams.</summary>
    public const byte ModeWholeSector = 0x20;

    /// <summary>
    /// Returns CPU cycles between delivered sectors. Fast load applies ONLY to non-XA, non-Form2, non-raw Form1 data
    /// sectors; XA/Form2 audio, raw 2340-byte MDEC streams, and fast-load-disabled reads keep hardware cadence.
    /// </summary>
    public static long ComputeReadLatencyCycles(byte mode, bool isXaOrForm2, bool isRawMdec, bool shouldFastLoadData)
    {
        bool streaming = isXaOrForm2 || isRawMdec || (mode & ModeXaAdpcm) != 0 || (mode & ModeWholeSector) != 0;
        if (shouldFastLoadData && !streaming)
        {
            return CyclesPerSectorFastLoadForm1;
        }
        return (mode & ModeDoubleSpeed) != 0 ? CyclesPerSectorDoubleSpeed : CyclesPerSectorSingleSpeed;
    }

    /// <summary>Sectors per emulated second for a given per-sector latency.</summary>
    public static double SectorsPerSecond(long cyclesPerSector) =>
        cyclesPerSector > 0 ? CpuClockHz / (double)cyclesPerSector : 0.0;
}
