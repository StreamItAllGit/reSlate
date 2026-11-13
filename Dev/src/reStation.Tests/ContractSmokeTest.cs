// @module: reStation.Tests.ContractSmokeTest
// @purpose: T1 baseline proving locked contracts compile, round-trip, and expose the KFR-PARITY-01 cadence constants.
// @depends-on: Xunit, Foundation.Retro, reStation.Contracts, reStation.Core
// @exports: ContractSmokeTest
// @owner: devv-pkg-test (T8) — expand into the >= 48-test matrix across 8 files.

using Foundation.Retro;
using reStation.Contracts;
using reStation.Core;
using reStation.Core.CdRom;
using reStation.Core.Cpu;
using reStation.Core.Gpu;

namespace reStation.Tests;

public sealed class ContractSmokeTest
{
    [Fact]
    public void ModuleManifest_RoundTripsStandard18KeysAndValidates()
    {
        string dir = Path.Combine(Path.GetTempPath(), "restation-tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "module.recomp.json");
        try
        {
            var manifest = new ReStationModuleManifest
            {
                ModuleId = "sample-game",
                Title = "Sample Game",
                SerialNumber = "TEST-00001",
                EntryAssembly = "sample-game.recomp.dll",
                ModuleTypeName = "SampleGame.GameModule",
                RequiredDiscChecksums = new[] { new DiscChecksumEntry("disc/game.bin", "ab", 2352) },
                SaveStateFixtures = new[] { new SaveStateFixtureSpec("boot", "Boot", "fixtures/boot.rst", 30) }
            };
            manifest.SaveToFile(path);
            string json = File.ReadAllText(path);
            Assert.Contains("\"moduleId\"", json);
            Assert.Contains("\"expectedSha256\"", json);
            Assert.Contains("\"defaultFramerateMode\": \"lockedCap\"", json);

            ReStationModuleManifest loaded = ReStationModuleManifest.LoadFromFile(path);
            Assert.Equal("Sample Game", loaded.DisplayName);
            Assert.Equal(ReStationEngineInfo.EngineId, loaded.EngineId);
            Assert.Equal(2352, loaded.RequiredDiscChecksums[0].ExpectedSizeBytes);
            Assert.Equal(30, loaded.SaveStateFixtures[0].FrameCount);
            Assert.IsAssignableFrom<IRecompModuleManifest>(loaded);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData((byte)0x80, false, false, false, 225_792L)]
    [InlineData((byte)0x00, false, false, false, 451_584L)]
    [InlineData((byte)0xC0, true, false, true, 225_792L)]
    [InlineData((byte)0xA0, false, true, true, 225_792L)]
    [InlineData((byte)0x80, false, false, true, 32_768L)]
    public void CdRomTiming_EnforcesHardwareCadence(byte mode, bool xa, bool raw, bool fastLoad, long expected)
    {
        Assert.Equal(expected, PsxCdRomTiming.ComputeReadLatencyCycles(mode, xa, raw, fastLoad));
    }

    [Fact]
    public void PsxCpu_ImplementsLockedCpuSurfaceWithSoftwareBackend()
    {
        using var backend = new SoftwareCpuRenderBackend();
        var cpu = new PsxCpu(new RecompSettings(), Path.GetTempPath(), backend);
        IReStationCpu surface = cpu;
        surface.Write32(0x80001000U, 0xDEADBEEFU);
        Assert.Equal(0xDEADBEEFU, surface.Read32(0xA0001000U));
        surface.Div(7, 0);
        Assert.Equal(0xFFFFFFFFU, surface.Lo);
        Assert.Equal(PsxRenderBackendIds.Software, cpu.Gpu.Backend.BackendId);
    }

    [Fact]
    public void Telemetry_HeartbeatCarriesRequiredKeys()
    {
        string line = PsxTelemetrySnapshot.Empty.ToHeartbeatLine();
        foreach (string key in new[] { "resolutionScale=", "backend=", "cpuMs=", "gpuMs=", "waitMs=", "sectorsPerSec=", "activeSpuVoices=", "xaDrops=0", "audioFillPct=" })
            Assert.Contains(key, line);
    }
}
