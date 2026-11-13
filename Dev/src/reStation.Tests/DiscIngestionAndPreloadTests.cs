// @module: reStation.Tests.DiscIngestionAndPreloadTests
// @purpose: Verifies data\rom\ preload discovery priority, ReStationModuleManifest JSON parity, fail-closed checksum & Zip-Slip rejection, and ISO-9660/CUE parsing.
// @depends-on: Xunit, System.IO.Compression, System.Buffers.Binary, Foundation.Core, reStation.Contracts, reStation.Core.DiscIngestion, reStation.Player
// @exports: DiscIngestionAndPreloadTests
// @owner: devv-pkg-test (T8)

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Foundation.Core;
using Foundation.Retro;
using reEngine.Core.Manifests;
using reStation.Contracts;
using reStation.Core.DiscIngestion;
using reStation.Player;
using Xunit;

namespace reStation.Tests;

public sealed class DiscIngestionAndPreloadTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"restation-disc-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true);
    }

    [Fact]
    public void PreloadRomDetector_DiscoversCueOverIsoAndZipInDataRom()
    {
        string modDir = Path.Combine(_tempRoot, "mod_a");
        string romDir = Path.Combine(modDir, "data", "rom");
        Directory.CreateDirectory(romDir);
        File.WriteAllText(Path.Combine(romDir, "notes.txt"), "ignore");
        File.WriteAllBytes(Path.Combine(romDir, "game.zip"), new byte[] { 0x50, 0x4B });
        File.WriteAllBytes(Path.Combine(romDir, "game.iso"), new byte[] { 0x01, 0x02 });
        File.WriteAllText(Path.Combine(romDir, "game.cue"), "FILE \"game.bin\" BINARY\nTRACK 01 MODE2/2352\nINDEX 01 00:00:00\n");

        PreloadRomDiscoveryResult res = PreloadRomDetector.Detect(modDir);

        Assert.True(res.DirectoryExists);
        Assert.Equal(3, res.DiscoveredMediaFiles.Count);
        Assert.EndsWith("game.cue", res.PrimaryMediaPath!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PreloadRomDetector_EmptyOrMissingDataRom_ReturnsEmptyWithoutThrow()
    {
        string modDir = Path.Combine(_tempRoot, "mod_empty");
        Directory.CreateDirectory(modDir);

        PreloadRomDiscoveryResult res = PreloadRomDetector.Detect(modDir);

        Assert.False(res.DirectoryExists);
        Assert.Null(res.PrimaryMediaPath);
        Assert.Empty(res.DiscoveredMediaFiles);
    }

    [Fact]
    public void ReStationModuleManifest_JsonRoundTrip_PreservesChecksumsAndBootExe()
    {
        Directory.CreateDirectory(_tempRoot);
        string manifestPath = Path.Combine(_tempRoot, "module.recomp.json");
        var original = new ReStationModuleManifest
        {
            ModuleId = "sample_ps1",
            Title = "Sample PS1",
            DisplayName = "Sample PS1",
            SerialNumber = "SLUS-00999",
            EntryAssembly = "sample_ps1.recomp.dll",
            ModuleTypeName = "sample_ps1.recomp.GameModule",
            DefaultBootExecutable = "SLUS_009.99",
            DefaultFramerateMode = RecompTimingMode.LockedCap,
            RequiredDiscChecksums = new[]
            {
                new DiscChecksumEntry("disc1.iso", new string('a', 64))
            }
        };

        original.SaveToFile(manifestPath);
        ReStationModuleManifest loaded = ReStationModuleManifest.LoadFromFile(manifestPath);

        Assert.Equal("sample_ps1", loaded.ModuleId);
        Assert.Equal("SLUS-00999", loaded.SerialNumber);
        Assert.Equal("SLUS_009.99", loaded.DefaultBootExecutable);
        Assert.Single(loaded.RequiredDiscChecksums);
        Assert.Equal("disc1.iso", loaded.RequiredDiscChecksums[0].RelativePath);
        Assert.Equal(new string('a', 64), loaded.RequiredDiscChecksums[0].ExpectedSha256);
        Assert.Equal(RecompTimingMode.LockedCap, loaded.DefaultFramerateMode);
    }

    [Fact]
    public async Task DiscVerifierAndExtractor_MismatchedChecksum_FailsClosed()
    {
        Directory.CreateDirectory(_tempRoot);
        string dataDir = Path.Combine(_tempRoot, "data");
        string isoPath = Path.Combine(_tempRoot, "game.iso");
        await File.WriteAllBytesAsync(isoPath, new byte[4096]);

        var manifest = new ReStationModuleManifest
        {
            ModuleId = "bad_sum",
            SerialNumber = "SLUS-12345",
            RequiredDiscChecksums = new[] { new DiscChecksumEntry("game.iso", new string('f', 64)) }
        };

        DiscIngestResult result = await DiscVerifierAndExtractor.IngestAsync(isoPath, dataDir, manifest);
        Assert.False(result.Success);
        Assert.Null(result.Manifest);
        Assert.Null(DiscManifest.TryLoad(dataDir));
    }

    [Fact]
    public async Task DiscVerifierAndExtractor_ZipSlipTraversalEntry_FailsClosed()
    {
        Directory.CreateDirectory(_tempRoot);
        string dataDir = Path.Combine(_tempRoot, "zipslip_data");
        string zipPath = Path.Combine(_tempRoot, "malicious.zip");
        string escapedTarget = Path.Combine(_tempRoot, "escaped_pwned.txt");

        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            ZipArchiveEntry entry = archive.CreateEntry("../../escaped_pwned.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("evil");
        }

        var manifest = new ReStationModuleManifest { ModuleId = "zipslip_mod", SerialNumber = "SLUS-00002" };
        DiscIngestResult result = await DiscVerifierAndExtractor.IngestAsync(zipPath, dataDir, manifest);

        Assert.False(result.Success);
        Assert.False(File.Exists(escapedTarget));
    }

    [Fact]
    public void Iso9660RawReader_AndCueSheetReader_ParseValidDiscAndCueTrack()
    {
        Directory.CreateDirectory(_tempRoot);
        string isoPath = Path.Combine(_tempRoot, "synthetic.iso");
        byte[] img = new byte[20 * Iso9660RawReader.UserSectorSize];

        // Sector 16: Primary Volume Descriptor ("CD001", volumeId "RESTATION", root dir LBA 18)
        int pvd = 16 * Iso9660RawReader.UserSectorSize;
        img[pvd] = 1;
        Encoding.ASCII.GetBytes("CD001").CopyTo(img, pvd + 1);
        img[pvd + 6] = 1;
        Encoding.ASCII.GetBytes("RESTATION").CopyTo(img, pvd + 40);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(pvd + 158, 4), 18U);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(pvd + 166, 4), 2048U);

        // Sector 18: Root directory containing ".", "..", and "SYSTEM.CNF;1" (LBA 19)
        int dir = 18 * Iso9660RawReader.UserSectorSize;
        WriteIsoDirRecord(img, dir, lba: 18, length: 2048, isDir: true, "\0");
        WriteIsoDirRecord(img, dir + 34, lba: 18, length: 2048, isDir: true, "\u0001");
        byte[] sysCnf = Encoding.ASCII.GetBytes("BOOT = cdrom:\\SLUS_000.01;1\r\n");
        sysCnf.CopyTo(img, 19 * Iso9660RawReader.UserSectorSize);
        WriteIsoDirRecord(img, dir + 68, lba: 19, length: (uint)sysCnf.Length, isDir: false, "SYSTEM.CNF;1");
        File.WriteAllBytes(isoPath, img);

        using (Iso9660RawReader reader = Iso9660RawReader.Open(isoPath))
        {
            Assert.Equal("RESTATION", reader.VolumeId);
            Assert.Equal(Iso9660RawReader.UserSectorSize, reader.SectorStride);
            Iso9660Entry? entry = reader.FindEntry("SYSTEM.CNF;1");
            Assert.NotNull(entry);
            string content = Encoding.ASCII.GetString(reader.ReadForm1Bytes(entry!.Lba, entry.ByteLength));
            Assert.Contains("SLUS_000.01", content);
        }

        IReadOnlyList<CueTrack> tracks = CueSheetReader.Parse("FILE \"disc.bin\" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\n");
        Assert.Single(tracks);
        Assert.Equal("disc.bin", tracks[0].FileName);
        Assert.Equal(1, tracks[0].TrackNumber);
    }

    private static void WriteIsoDirRecord(byte[] img, int offset, uint lba, uint length, bool isDir, string name)
    {
        byte[] nameBytes = Encoding.ASCII.GetBytes(name);
        byte recordLen = (byte)(33 + nameBytes.Length + ((33 + nameBytes.Length) & 1));
        img[offset] = recordLen;
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(offset + 2, 4), lba);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(offset + 10, 4), length);
        img[offset + 25] = isDir ? (byte)0x02 : (byte)0x00;
        img[offset + 32] = (byte)nameBytes.Length;
        nameBytes.CopyTo(img, offset + 33);
    }
}
