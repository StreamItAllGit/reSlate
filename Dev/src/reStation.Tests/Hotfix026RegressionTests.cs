// @module: reStation.Tests.Hotfix026RegressionTests
// @purpose: Regression coverage for v0.2.6 data/gamedata AOT media resolution, TryAdvanceToNextVBlank CD-ROM sector wakes, and AOT compiler fallback.
// @depends-on: Xunit, System.IO.Compression, System.Security.Cryptography, Foundation.Retro, reStation.Contracts, reStation.Core, reStation.Player
// @exports: Hotfix026RegressionTests

using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Foundation.Retro;
using reStation.Contracts;
using reStation.Core;
using reStation.Core.Aot;
using reStation.Core.CdRom;
using reStation.Core.Cpu;
using reStation.Core.DiscIngestion;
using reStation.Core.Gpu;
using reStation.Player;
using Xunit;

namespace reStation.Tests;

public sealed class Hotfix026RegressionTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"restation-hf026-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    [Fact]
    public void PlayerMediaResolver_ResolvesIngestedGameDataDir_WhenRomDirIsEmpty()
    {
        string installRoot = Path.Combine(_tempRoot, "game");
        string appDir = Path.Combine(installRoot, "app");
        string romDir = Path.Combine(installRoot, "data", "rom");
        string gamedataDir = Path.Combine(installRoot, "data", "gamedata");
        Directory.CreateDirectory(appDir);
        Directory.CreateDirectory(romDir);
        Directory.CreateDirectory(gamedataDir);

        byte[] exeBytes = BuildMinimalPsxExe();
        File.WriteAllBytes(Path.Combine(gamedataDir, "SLUS_001.58"), exeBytes);
        string exeSha = Convert.ToHexString(SHA256.HashData(exeBytes)).ToLowerInvariant();

        var recipe = new AotRecipeSpec
        {
            ModuleId = "kf-test",
            Executables =
            [
                new AotRecipeExecutableSpec
                {
                    BootExePath = "SLUS_001.58",
                    Sha256 = exeSha,
                    LoadAddress = 0x80010000U,
                    EntryAddress = 0x80010000U,
                    TextSizeBytes = 2048
                }
            ]
        };

        ResolvedPlayerMedia fromAppDir = PlayerMediaResolver.ResolveAndVerifyMedia(recipe, appDir);
        Assert.Equal(MediaInputKind.ExeFile, fromAppDir.Kind);
        Assert.Equal(exeSha, fromAppDir.RomSha256Hex);
        Assert.Equal(Path.GetFullPath(gamedataDir), Path.GetFullPath(fromAppDir.MediaRootDir));

        ResolvedPlayerMedia fromDataDir = PlayerMediaResolver.ResolveAndVerifyMedia(
            recipe, installRoot, new LocalAotLoadOptions(DataDir: Path.Combine(installRoot, "data")));
        Assert.Equal(exeSha, fromDataDir.RomSha256Hex);
    }

    [Fact]
    public void TryAdvanceToNextVBlank_PreservesIntermediateCdRomSectorWakeups()
    {
        Directory.CreateDirectory(_tempRoot);
        string binPath = Path.Combine(_tempRoot, "test.bin");
        File.WriteAllBytes(binPath, new byte[2352 * 8]);

        using var cpu = new PsxCpu(new RecompSettings(), _tempRoot, new SoftwareCpuRenderBackend());
        var manifest = new DiscManifest
        {
            ModuleId = "cd-wake-test",
            SerialNumber = "SLUS-00001",
            PrimaryImagePath = binPath,
            Files = [new DiscFileEntry { RelativeFile = "OP/OP0.S;1", Lba = 0, SectorCount = 8, ByteLength = 8 * 2336U, IsForm2Raw = true }]
        };
        cpu.AttachDisc(manifest);
        cpu.TimersAndDma.InterruptMask = 1U << 0; // VBlank IRQ enabled (also keeps Timer/VBlank gate active)
        cpu.CdRom.WriteRegister(0, 1); cpu.CdRom.WriteRegister(3, 0x1F); // ACK any initial flags
        cpu.CdRom.WriteRegister(0, 0);
        cpu.CdRom.WriteRegister(2, 0x80); // 2x speed SetMode
        cpu.CdRom.WriteRegister(1, 0x0E);
        cpu.TotalCycles += 4000;
        cpu.PollEvents();
        cpu.CdRom.WriteRegister(0, 1); cpu.CdRom.WriteRegister(3, 0x1F); cpu.CdRom.WriteRegister(0, 0);

        cpu.CdRom.WriteRegister(2, 0x00); cpu.CdRom.WriteRegister(2, 0x02); cpu.CdRom.WriteRegister(2, 0x00); // SetLoc 00:02:00
        cpu.CdRom.WriteRegister(1, 0x02);
        cpu.TotalCycles += 4000;
        cpu.PollEvents();
        cpu.CdRom.WriteRegister(0, 1); cpu.CdRom.WriteRegister(3, 0x1F); cpu.CdRom.WriteRegister(0, 0);

        long beforeDelivered = cpu.CdRom.SectorsDelivered;
        cpu.CdRom.WriteRegister(1, 0x1B); // ReadS without data IRQ blocking? Drain INT3 + INT1 during VBlank
        for (int i = 0; i < 6; i++)
        {
            Assert.True(cpu.TryAdvanceToNextVBlank());
            cpu.CdRom.WriteRegister(0, 1);
            if ((cpu.CdRom.ReadRegister(3) & 0x1F) != 0)
            {
                cpu.CdRom.WriteRegister(3, 0x1F);
                _ = cpu.CdRom.ReadRegister(1);
            }
            cpu.CdRom.WriteRegister(0, 0);
        }

        // At 2x speed (225,792 cycles/sec) over 6 VBlanks (3,386,880 cycles), multiple sectors must arrive!
        Assert.True(cpu.CdRom.SectorsDelivered - beforeDelivered >= 3,
            $"Expected >=3 sectors across 6 VBlanks, saw {cpu.CdRom.SectorsDelivered - beforeDelivered}");
    }

    [Fact]
    public void ModuleAssemblyLoader_FallsBackToEntryAssembly_WhenExternalCompilerUnavailableInAotWithJitFallbackMode()
    {
        string installRoot = Path.Combine(_tempRoot, "fallback-install");
        string appDir = Path.Combine(installRoot, "app");
        string romDir = Path.Combine(installRoot, "data", "rom");
        Directory.CreateDirectory(appDir);
        Directory.CreateDirectory(romDir);

        byte[] exeBytes = BuildMinimalPsxExe();
        File.WriteAllBytes(Path.Combine(romDir, "SLUS_001.58"), exeBytes);
        string exeSha = Convert.ToHexString(SHA256.HashData(exeBytes)).ToLowerInvariant();

        var recipe = new AotRecipeSpec
        {
            ModuleId = "fallback-mod",
            ExecutionTierMode = ReStationExecutionTierModes.AotWithJitFallback,
            Executables =
            [
                new AotRecipeExecutableSpec
                {
                    BootExePath = "SLUS_001.58",
                    Sha256 = exeSha,
                    LoadAddress = 0x80010000U,
                    EntryAddress = 0x80010000U,
                    TextSizeBytes = 2048
                }
            ]
        };
        string pkgPath = Path.Combine(appDir, "Fallback.recomp");
        var manifest = new ReStationModuleManifest
        {
            SchemaVersion = 1,
            ModuleId = "fallback-mod",
            Title = "Fallback Test",
            DisplayName = "Fallback Test",
            PlatformId = "ps1",
            Version = "0.2.6",
            EngineId = "reStation",
            EngineVersion = "0.2.6",
            CompatibleEngineTrack = "0.2.x",
            EntryAssembly = "reStation.Tests.dll",
            DefaultBootExecutable = "SLUS_001.58",
            ModuleTypeName = typeof(FallbackDummyGameModule).FullName!,
            AotRecipe = "aot-recipe.json",
            ExecutionTierMode = ReStationExecutionTierModes.AotWithJitFallback
        };

        using (ZipArchive zip = ZipFile.Open(pkgPath, ZipArchiveMode.Create))
        {
            using (var sw = new StreamWriter(zip.CreateEntry("module.recomp.json").Open(), new UTF8Encoding(false)))
                sw.Write(JsonSerializer.Serialize(manifest, ReStationModuleManifest.JsonOptions));
            using (var sw = new StreamWriter(zip.CreateEntry("aot-recipe.json").Open(), new UTF8Encoding(false)))
                sw.Write(JsonSerializer.Serialize(recipe, ReStationModuleManifest.JsonOptions));
            zip.CreateEntryFromFile(typeof(FallbackDummyGameModule).Assembly.Location, "reStation.Tests.dll");
        }

        var missingCompiler = new PlayerLocalAotCompiler(Path.Combine(_tempRoot, "missing", "reStation.Compiler.exe"));
        var loadOpts = new LocalAotLoadOptions(DataDir: Path.Combine(installRoot, "data"), PreloadRomDir: romDir);

        IRecompGameModule loaded = ModuleAssemblyLoader.LoadGameModule(
            ReStationModuleManifest.LoadFromFile(pkgPath), installRoot, pkgPath, loadOpts, out ModuleAssemblyLoader alc, missingCompiler);
        Assert.NotNull(loaded);
        Assert.False(alc.LoadedFromLocalAotCache);
        Assert.False(string.IsNullOrWhiteSpace(alc.AotFallbackWarning));
        alc.Unload();

        var strictOpts = loadOpts with { ExecutionTierModeOverride = ReStationExecutionTierModes.StaticAotOnly };
        Assert.Throws<FileNotFoundException>(() =>
            ModuleAssemblyLoader.LoadGameModule(ReStationModuleManifest.LoadFromFile(pkgPath), installRoot, pkgPath, strictOpts, out _, missingCompiler));
    }

    private static byte[] BuildMinimalPsxExe()
    {
        byte[] bytes = new byte[0x800 + 8];
        Encoding.ASCII.GetBytes("PS-X EXE").CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x10, 4), 0x80010000U);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x18, 4), 0x80010000U);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x1C, 4), 8U);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x800, 4), 0x03E00008U);
        return bytes;
    }

    public sealed class FallbackDummyGameModule : IRecompGameModule
    {
        public IRecompModuleManifest Manifest => new ReStationModuleManifest { ModuleId = "fallback-mod" };
        public IReadOnlyList<RecompPatchDescriptor> TimingPatches => Array.Empty<RecompPatchDescriptor>();
        public Task InitializeAsync(IRecompHost host, CancellationToken ct = default) => Task.CompletedTask;
        public Task StepFrameAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task LoadSaveStateAsync(string saveStatePath, CancellationToken ct = default) => Task.CompletedTask;
    }
}
