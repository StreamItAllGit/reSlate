// @module: reStation.Tests.PsxDecompAndSlaTests
// @purpose: Validates reStation.Decomp clean-room .decomp pipeline, offline toolchain probe, SLEIGH R3000A_GTE, LP32/D5 parity, and asset extractors.

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using reEngine.Core.Decomp;
using reEngine.Core.Manifests;
using reStation.Contracts;
using reStation.Decomp;
using Xunit;

namespace reStation.Tests;

public sealed class PsxDecompAndSlaTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "restation-decomp-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            try { Directory.Delete(_tempRoot, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void ProviderMetadata_AndGteSleighSpec_EnforceCleanRoomR3000AGteContract()
    {
        var provider = new PsxDecompPlatformProvider();
        Assert.Equal("ps1", provider.PlatformId);
        Assert.Equal("reStation", provider.EngineId);
        Assert.Equal("0.2.6", provider.MinimumEngineVersion);
        Assert.Equal("MIPS:LE:32:R3000A_GTE", provider.SleighLanguageId);

        string slaSpec = PsxGteSlaExtension.LoadSlaSpecText();
        PsxSlaValidationResult ok = PsxGteSlaExtension.ValidateSlaSpec(slaSpec);
        Assert.True(ok.IsValid, ok.Summary);
        Assert.All(PsxGteSlaExtension.RequiredSpecTokens, tok => Assert.Contains(tok, slaSpec, StringComparison.Ordinal));

        string taintedSpec = slaSpec + "\n// GPL leak marker: GNU General Public License";
        PsxSlaValidationResult bad = PsxGteSlaExtension.ValidateSlaSpec(taintedSpec);
        Assert.False(bad.IsValid);
    }

    [Fact]
    public void OfflineToolchainLocator_LightweightProbesCleanly_FullFlagsMissingOfflineDir()
    {
        Directory.CreateDirectory(_tempRoot);
        string fakeReEngine = Path.Combine(_tempRoot, "reEngine");
        Directory.CreateDirectory(fakeReEngine);

        OfflineToolchainStatus lw = PsxCleanExeAndOverlayLoader.LocateOfflineToolchain(fakeReEngine, ReEngineInstallProfile.Lightweight);
        Assert.Equal(ReEngineInstallProfile.Lightweight, lw.Profile);
        Assert.False(lw.IsAvailable);

        string missingTc = Path.Combine(_tempRoot, "missing-toolchain");
        OfflineToolchainStatus fullMissing = PsxCleanExeAndOverlayLoader.LocateOfflineToolchain(fakeReEngine, ReEngineInstallProfile.Full, missingTc);
        Assert.Equal(ReEngineInstallProfile.Full, fullMissing.Profile);
        Assert.False(fullMissing.IsAvailable);
        Assert.Contains("offline", fullMissing.DiagnosticMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CleanExeLoader_AndD5ParityVerifier_ValidateBssOverlaysLp32AndRoundTrip()
    {
        Directory.CreateDirectory(_tempRoot);
        uint[] words =
        [
            0x24020001U, // addiu v0, zero, 1
            0x4A000001U, // COP2 GTE command
            0x03E00008U, // jr ra
            0x00000000U  // nop
        ];
        byte[] exeBytes = BuildPsxExe(0x80010000U, 0x80010000U, words, bssAddr: 0x80018000U, bssSize: 16U);
        PsxExeImage boot = PsxCleanExeAndOverlayLoader.LoadExeFromBytes(exeBytes, "SLUS_001.58", isBootExe: true, slotId: 0);

        byte[] ram = new byte[PsxCleanExeAndOverlayLoader.PsxMainRamBytes];
        ram[0x18000] = 0xAA;
        PsxCleanExeAndOverlayLoader.LoadExeIntoRam(boot, ram);
        Assert.Equal(0, ram[0x18000]);

        byte[] ovlBytes = BuildPsxExe(0x80040000U, 0x80040000U, [0x03E00008U, 0x00000000U], bssAddr: 0x80048000U, bssSize: 8U);
        PsxExeImage ovlImg = PsxCleanExeAndOverlayLoader.LoadExeFromBytes(ovlBytes, "OVL_01.BIN", isBootExe: false, slotId: 1);
        var ovlDesc = new PsxOverlayDescriptor(1, ovlImg.Name, ovlImg.RelativePath, ovlImg.Header.TextAddr, ovlImg.Header.Pc0, ovlImg.Header.TextSize, ovlImg.Header.BssAddr, ovlImg.Header.BssSize, ovlImg.Sha256Hex);
        ram[0x48000] = 0x55;
        PsxCleanExeAndOverlayLoader.LoadOverlayIntoRam(ovlDesc, ovlBytes, ram);
        Assert.Equal(0, ram[0x48000]);

        string outDir = Path.Combine(_tempRoot, "d5");
        var lp32 = PsxParityAndByteMatchVerifier.VerifyLp32Parity(customHeaderText: null, outputDir: outDir);
        Assert.True(lp32.Passed);
        Assert.Equal(12, lp32.ChecksPassed);
        Assert.Equal(12, lp32.TotalChecks);
        Assert.Equal(4, lp32.PointerSizeBytes);
        Assert.Equal(4, lp32.LongSizeBytes);

        PsxByteMatchReport d5 = PsxParityAndByteMatchVerifier.VerifyExeByteMatch(boot, strictInstructionStream: true, outputDir: outDir);
        Assert.True(d5.Passed, d5.Summary);
        Assert.Equal(d5.OriginalSha256Hex, d5.RoundTripSha256Hex);

        byte[] badWords = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(badWords, 0xFC000000U); // invalid primary opcode 63
        PsxByteMatchReport strictFail = PsxParityAndByteMatchVerifier.VerifyCodeBytesRoundTrip(badWords, 0x80010000U, "bad-op", strictInstructionStream: true);
        Assert.False(strictFail.Passed);
    }

    [Fact]
    public void PsxAssetExtractors_ExtractTimVabSeqToCacheOnly_AndRejectTrackedDirs()
    {
        Directory.CreateDirectory(_tempRoot);
        string dataDir = Path.Combine(_tempRoot, "data");
        string cacheDir = Path.Combine(dataDir, "cache", "assets");

        byte[] tim16 = new byte[20 + 8];
        BinaryPrimitives.WriteUInt32LittleEndian(tim16.AsSpan(0, 4), 0x10U);
        BinaryPrimitives.WriteUInt32LittleEndian(tim16.AsSpan(4, 4), 2U); // 16bpp, no CLUT
        BinaryPrimitives.WriteUInt32LittleEndian(tim16.AsSpan(8, 4), 20U);
        BinaryPrimitives.WriteUInt16LittleEndian(tim16.AsSpan(16, 2), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(tim16.AsSpan(18, 2), 2);

        byte[] vab = new byte[32];
        Encoding.ASCII.GetBytes("pBAV").CopyTo(vab, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(vab.AsSpan(4, 4), 7U);

        byte[] seq = new byte[20];
        Encoding.ASCII.GetBytes("pQES").CopyTo(seq, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(seq.AsSpan(4, 4), 1U);
        BinaryPrimitives.WriteUInt16BigEndian(seq.AsSpan(8, 2), 480);

        PsxExtractedAssetRecord timRec = PsxAssetExtractors.ExtractTim(tim16, cacheDir, dataDir, "font.tim");
        PsxExtractedAssetRecord vabRec = PsxAssetExtractors.ExtractVab(vab, cacheDir, dataDir, "sfx.vab");
        PsxExtractedAssetRecord seqRec = PsxAssetExtractors.ExtractSeq(seq, cacheDir, dataDir, "bgm.seq");

        Assert.True(File.Exists(timRec.CacheOutputFiles[1]));
        Assert.True(File.Exists(vabRec.CacheMetadataPath));
        Assert.True(File.Exists(seqRec.CacheMetadataPath));

        string fakeTrackedSrc = Path.Combine(_tempRoot, "Dev", "src", "leak");
        Assert.Throws<InvalidOperationException>(() => PsxAssetExtractors.ExtractTim(tim16, fakeTrackedSrc, dataDir, "bad.tim"));
    }

    [Fact]
    public async Task DecompProvider_BuildsCleanAotRecipe_AndRejectsDirtyPackagesAndMissingToolchain()
    {
        Directory.CreateDirectory(_tempRoot);
        string pkgDir = Path.Combine(_tempRoot, "pkg");
        string exportsDir = Path.Combine(_tempRoot, "exports");
        string dataDir = Path.Combine(_tempRoot, "data");
        string gamedataDir = Path.Combine(dataDir, "rom");
        Directory.CreateDirectory(pkgDir);
        Directory.CreateDirectory(exportsDir);
        Directory.CreateDirectory(gamedataDir);

        File.WriteAllText(Path.Combine(pkgDir, "module.decomp.json"), """
        {
          "schemaVersion": 1,
          "moduleId": "ps1sample",
          "platformId": "ps1",
          "engineId": "reStation",
          "compatibleEngineTrack": "0.2.x",
          "minimumEngineVersion": "0.2.0",
          "executionTier": "aot-primary",
          "sleighLanguageId": "MIPS:LE:32:R3000A_GTE"
        }
        """);
        File.WriteAllText(Path.Combine(exportsDir, "sample.c"), "undefined4 FUN_80010000(void) { gte_rtps(); return 0; }\n");
        File.WriteAllBytes(Path.Combine(gamedataDir, "GAME.EXE"), BuildPsxExe(0x80010000U, 0x80010000U, [0x03E00008U, 0x00000000U]));

        var provider = new PsxDecompPlatformProvider();
        string outDir = Path.Combine(_tempRoot, "out");
        var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["exportsDir"] = exportsDir,
            ["gamedataDir"] = gamedataDir,
            ["profile"] = "Lightweight"
        };
        var runOpts = new DecompRunOptions(pkgDir, outDir, dataDir, DecompStage.D3, PlatformArguments: args);
        DecompStageResult stageRes = await provider.RunDecompAsync(runOpts);
        Assert.True(stageRes.Success, stageRes.Message);
        AotRecipeSpec builtRecipe = await provider.BuildAotRecipeAsync(runOpts);
        Assert.Equal("ps1sample", builtRecipe.ModuleId);
        Assert.True(File.Exists(Path.Combine(outDir, "aot-recipe.json")));
        Assert.True(provider.ValidateCleanRoomPackage(outDir).IsValid);

        var badTcOpts = new DecompRunOptions(
            pkgDir,
            Path.Combine(_tempRoot, "bad-tc-out"),
            dataDir,
            DecompStage.D2,
            ToolchainDirectory: Path.Combine(_tempRoot, "absent-tc"),
            PlatformArguments: new Dictionary<string, string> { ["profile"] = "Full", ["invokeGhidra"] = "true" });
        DecompStageResult failTc = await provider.RunDecompAsync(badTcOpts);
        Assert.False(failTc.Success);
        Assert.Equal("PSX_GHIDRA_HEADLESS_FAILED", failTc.DiagnosticCode);
        Assert.Contains("offline", failTc.Message, StringComparison.OrdinalIgnoreCase);

        string dirtyZip = Path.Combine(_tempRoot, "dirty.decomp");
        using (ZipArchive zip = ZipFile.Open(dirtyZip, ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(Path.Combine(outDir, "aot-recipe.json"), "aot-recipe.json");
            using var sw = new StreamWriter(zip.CreateEntry("bad.Generated.dll").Open());
            sw.Write("binary");
        }
        DecompCleanRoomValidationResult badPkg = provider.ValidateCleanRoomPackage(dirtyZip);
        Assert.False(badPkg.IsValid);
        Assert.NotEmpty(badPkg.Violations);

        string gplDir = Path.Combine(_tempRoot, "gpl-dir");
        Directory.CreateDirectory(gplDir);
        File.WriteAllText(Path.Combine(gplDir, "ghidra_psx_ldr.py"), "# GNU General Public License");
        Assert.False(provider.ValidateCleanRoomPackage(gplDir).IsValid);
    }

    private static byte[] BuildPsxExe(uint textAddr, uint pc0, uint[] words, uint bssAddr = 0, uint bssSize = 0)
    {
        byte[] bytes = new byte[0x800 + (words.Length * 4)];
        Encoding.ASCII.GetBytes("PS-X EXE").CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x10, 4), pc0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x18, 4), textAddr);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x1C, 4), (uint)(words.Length * 4));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x28, 4), bssAddr);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x2C, 4), bssSize);
        for (int i = 0; i < words.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x800 + i * 4, 4), words[i]);
        return bytes;
    }
}
