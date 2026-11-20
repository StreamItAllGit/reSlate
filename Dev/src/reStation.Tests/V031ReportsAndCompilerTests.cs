// @module: reStation.Tests.V031ReportsAndCompilerTests
// @purpose: Verifies v0.3.1 T1/T2 deliverables: TryWaitForVBlankCounter, O(1) ContainerOffsetBytes + fallback, per-EXE EntryAddresses without Slot-A drops, and LoadFunctionsJson program filtering.
// @depends-on: Xunit, System.Buffers.Binary, System.Diagnostics, System.Security.Cryptography, System.Text.Json, reStation.Compiler, reStation.Contracts, reStation.Core
// @exports: V031ReportsAndCompilerTests

using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using reStation.Compiler;
using reStation.Contracts;
using reStation.Core;
using reStation.Core.Aot;
using reStation.Core.Cpu;
using reStation.Core.Gpu;
using Xunit;

namespace reStation.Tests;

public sealed class V031ReportsAndCompilerTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "reslate-v031-t1t2-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (!Directory.Exists(_tempRoot)) return;
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"[V031ReportsAndCompilerTests] Cleanup failed for '{_tempRoot}': {ex.Message}");
        }
    }

    private PsxCpu CreateCpu()
    {
        Directory.CreateDirectory(_tempRoot);
        return new PsxCpu(new RecompSettings(), _tempRoot, new SoftwareCpuRenderBackend(), new ReStationLogger(_tempRoot, false));
    }

    [Fact]
    public void TryWaitForVBlankCounter_ImmediateMatch_AndWaitEntryPcAlignment()
    {
        using PsxCpu cpu = CreateCpu();
        IReStationCpuEx cpuEx = cpu;
        const uint counterAddr = 0x800575D4U;

        cpu.Pc = 0x80011110U;
        cpu.Write32(counterAddr, 5U);
        Assert.True(cpuEx.TryWaitForVBlankCounter(counterAddr, targetValue: 3));
        Assert.Equal(0L, cpu.VBlankSequence);
        Assert.Equal(0x80011110U, cpu.Pc);

        // Signed negative comparison: -1 < 0 waits (fails if masked), -1 >= -2 matches immediately with Pc aligned.
        cpu.Write32(counterAddr, unchecked((uint)-1));
        Assert.True(cpuEx.TryWaitForVBlankCounter(counterAddr, targetValue: -2, waitEntryPc: 0x80032CFCU));
        Assert.Equal(0x80032CFCU, cpu.Pc);
        Assert.Equal(0L, cpu.VBlankSequence);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(17)]
    [InlineData(63)]
    public void TryWaitForVBlankCounter_MultiVBlankAdvance_AndSub64CycleClampSafety(int remainingCyclesToVBlank)
    {
        using PsxCpu cpu = CreateCpu();
        IReStationCpuEx cpuEx = cpu;
        const uint counterAddr = 0x800575D4U;
        const uint entryPc = 0x80032CFCU;

        cpu.TimersAndDma.InterruptMask = 1U;
        cpu.TotalCycles = PsxCpu.CyclesPerFrame - remainingCyclesToVBlank;
        cpu.Write32(counterAddr, 0U);

        uint observedPcAtVBlank = 0U;
        cpu.OnVBlankFrame = c =>
        {
            observedPcAtVBlank = c.Pc;
            c.Write32(counterAddr, c.Read32(counterAddr) + 1U);
        };

        bool ok = cpuEx.TryWaitForVBlankCounter(counterAddr, targetValue: 3, waitEntryPc: entryPc, maxVBlanks: 16);
        Assert.True(ok);
        Assert.Equal(3L, cpu.VBlankSequence);
        Assert.Equal(3U, cpu.Read32(counterAddr));
        Assert.Equal(entryPc, observedPcAtVBlank);
        Assert.Equal(entryPc, cpu.Pc);
    }

    [Fact]
    public void TryWaitForVBlankCounter_FailClosedGuards_AndMaxVBlanksBound()
    {
        using PsxCpu cpu = CreateCpu();
        IReStationCpuEx cpuEx = cpu;
        const uint counterAddr = 0x800575D4U;

        cpu.TimersAndDma.InterruptMask = 1U;
        cpu.Write32(counterAddr, 10U);

        cpu.InSubTickRender = true;
        cpu.Pc = 0x80010000U;
        Assert.False(cpuEx.TryWaitForVBlankCounter(counterAddr, targetValue: 5, waitEntryPc: 0x80032CFCU));
        Assert.Equal(0x80010000U, cpu.Pc);
        cpu.InSubTickRender = false;

        Assert.False(cpuEx.TryWaitForVBlankCounter(counterAddr, targetValue: 5, maxVBlanks: 0));
        Assert.False(cpuEx.TryWaitForVBlankCounter(counterAddr, targetValue: 5, maxVBlanks: -2));

        cpu.Write32(counterAddr, 0U);
        cpu.OnVBlankFrame = c => c.Write32(counterAddr, c.Read32(counterAddr) + 1U);
        Assert.False(cpuEx.TryWaitForVBlankCounter(counterAddr, targetValue: 10, maxVBlanks: 4));
        Assert.Equal(4L, cpu.VBlankSequence);
        Assert.Equal(4U, cpu.Read32(counterAddr));

        cpu.TimersAndDma.InterruptMask = 0U;
        Assert.False(cpuEx.TryWaitForVBlankCounter(counterAddr, targetValue: 10, maxVBlanks: 4));
        Assert.Equal(4L, cpu.VBlankSequence);

        cpu.TimersAndDma.InterruptMask = 1U;
        cpu.RequestExit();
        Assert.False(cpuEx.TryWaitForVBlankCounter(counterAddr, targetValue: 10, maxVBlanks: 4));
    }

    [Fact]
    public void ContainerOffsetBytes_O1HitAtUnalignedOffset_AndBadOffsetFallback_WorkEndToEnd()
    {
        Directory.CreateDirectory(_tempRoot);

        // Slice 1 sits at non-4-byte-aligned offset 17 (unreachable by 2048/4-byte scan -> proves O(1) hint path!).
        byte[] sliceUnaligned = BuildExecutableOverlaySlice(0x800B7E20U, returnVal: 7);
        byte[] packUnaligned = new byte[4096];
        sliceUnaligned.CopyTo(packUnaligned, 17);
        string shaUnaligned = Convert.ToHexString(SHA256.HashData(sliceUnaligned)).ToLowerInvariant();

        Assert.False(PlayerMediaResolver.TryFindContainerSliceOffset(packUnaligned, sliceUnaligned.Length, shaUnaligned, out _));
        Assert.True(PlayerMediaResolver.TryFindContainerSliceOffset(packUnaligned, sliceUnaligned.Length, shaUnaligned, 17L, out int o1Hit));
        Assert.Equal(17, o1Hit);

        // Slice 2 sits at sector-aligned offset 2048; test bad hint fallback + emitted containerOffsetBytes.
        byte[] sliceAligned = BuildExecutableOverlaySlice(0x800B7E20U, returnVal: 11);
        byte[] packAligned = new byte[8192];
        sliceAligned.CopyTo(packAligned, 2048);
        string shaAligned = Convert.ToHexString(SHA256.HashData(sliceAligned)).ToLowerInvariant();

        Assert.True(PlayerMediaResolver.TryFindContainerSliceOffset(packAligned, sliceAligned.Length, shaAligned, 999999L, out int badHiHit));
        Assert.Equal(2048, badHiHit);
        Assert.True(PlayerMediaResolver.TryFindContainerSliceOffset(packAligned, sliceAligned.Length, shaAligned, 0L, out int badZeroHit));
        Assert.Equal(2048, badZeroHit);

        string romDir = Path.Combine(_tempRoot, "gamedata");
        Directory.CreateDirectory(romDir);
        File.WriteAllBytes(Path.Combine(romDir, "UNALIGNED.BIN"), packUnaligned);
        File.WriteAllBytes(Path.Combine(romDir, "ALIGNED.BIN"), packAligned);

        var recipe = new AotRecipeSpec
        {
            SchemaVersion = 1,
            PlatformId = "ps1",
            ModuleId = "offsettest",
            DiscSerial = "SLUS-09911",
            PrimaryBootExecutable = "UNALIGNED.BIN#OVL_FAST",
            Executables =
            [
                new AotRecipeExecutableSpec
                {
                    BootExePath = "UNALIGNED.BIN#OVL_FAST",
                    Sha256 = shaUnaligned,
                    LoadAddress = 0x800B7E20U,
                    EntryAddress = 0x800B7E20U,
                    TextSizeBytes = sliceUnaligned.Length,
                    ContainerOffsetBytes = 17L
                },
                new AotRecipeExecutableSpec
                {
                    BootExePath = "ALIGNED.BIN#OVL_FALLBACK",
                    Sha256 = shaAligned,
                    LoadAddress = 0x800B7E20U,
                    EntryAddress = 0x800B7E20U,
                    TextSizeBytes = sliceAligned.Length,
                    ContainerOffsetBytes = 4096L // stale offset -> must fall back cleanly to 2048
                }
            ]
        };

        ResolvedPlayerMedia verified = PlayerMediaResolver.ResolveAndVerifyMedia(
            recipe,
            _tempRoot,
            new LocalAotLoadOptions(DataDir: _tempRoot, PreloadRomDir: romDir));
        Assert.Equal(MediaInputKind.GameDataDir, verified.Kind);

        string recipePath = Path.Combine(_tempRoot, "input-recipe.json");
        File.WriteAllText(recipePath, JsonSerializer.Serialize(recipe, ReStationModuleManifest.JsonOptions));
        string outDir = Path.Combine(_tempRoot, "out");
        RecompCompileResult res = new RecompCompilerPipeline(
            new RecompCompilerOptions(null, romDir, string.Empty, outDir, "offsettest", "SLUS-09911", "Offset Test")
            {
                AotRecipePath = recipePath,
                EmitCleanRoomOnly = true
            }).Run();

        Assert.True(res.Success, res.Message);
        AotRecipeSpec emitted = JsonSerializer.Deserialize<AotRecipeSpec>(
            File.ReadAllText(Path.Combine(outDir, "aot-recipe.json")),
            ReStationModuleManifest.JsonOptions)!;
        Assert.Equal(17L, emitted.Executables[0].ContainerOffsetBytes);
        Assert.Equal(2048L, emitted.Executables[1].ContainerOffsetBytes);
    }

    [Fact]
    public void PerExeEntryAddresses_MultiOverlaySharedRange_PreservesAllSlotASeedsThroughEmitAndRelift()
    {
        Directory.CreateDirectory(_tempRoot);
        string romDir = Path.Combine(_tempRoot, "gamedata");
        Directory.CreateDirectory(romDir);

        // Two Slot-A overlays both loaded at 0x800B7E20..0x800B7E60.
        // Offset +0x00 is a self-contained jr $ra; nop (so auto-scan stops at 0x800B7E28).
        // Seed +0x10 (0x800B7E30) exists ONLY in OVL_A; seed +0x20 (0x800B7E40) exists ONLY in OVL_B.
        byte[] ovlA = BuildSharedSlotOverlay(slotIndex: 0, extraOffsetBytes: 0x10, returnVal: 0x11);
        byte[] ovlB = BuildSharedSlotOverlay(slotIndex: 1, extraOffsetBytes: 0x20, returnVal: 0x22);
        byte[] container = new byte[4096];
        ovlA.CopyTo(container, 0);
        ovlB.CopyTo(container, 2048);
        File.WriteAllBytes(Path.Combine(romDir, "A_FILE.BIN"), container);

        string shaA = Convert.ToHexString(SHA256.HashData(ovlA)).ToLowerInvariant();
        string shaB = Convert.ToHexString(SHA256.HashData(ovlB)).ToLowerInvariant();

        var inputRecipe = new AotRecipeSpec
        {
            SchemaVersion = 1,
            PlatformId = "ps1",
            ModuleId = "slotaseeds",
            DiscSerial = "SLUS-09912",
            PrimaryBootExecutable = "A_FILE.BIN#OVL_A",
            Executables =
            [
                new AotRecipeExecutableSpec
                {
                    BootExePath = "A_FILE.BIN#OVL_A",
                    Sha256 = shaA,
                    LoadAddress = 0x800B7E20U,
                    EntryAddress = 0x800B7E20U,
                    TextSizeBytes = ovlA.Length,
                    ContainerOffsetBytes = 0L,
                    EntryAddresses = [0x800B7E20U, 0x800B7E30U]
                },
                new AotRecipeExecutableSpec
                {
                    BootExePath = "A_FILE.BIN#OVL_B",
                    Sha256 = shaB,
                    LoadAddress = 0x800B7E20U,
                    EntryAddress = 0x800B7E20U,
                    TextSizeBytes = ovlB.Length,
                    ContainerOffsetBytes = 2048L,
                    EntryAddresses = [0x800B7E20U, 0x800B7E40U]
                }
            ],
            EntryAddresses = [0x800B7E20U, 0x800B7E30U, 0x800B7E40U]
        };

        string recipePath = Path.Combine(_tempRoot, "slota-recipe.json");
        File.WriteAllText(recipePath, JsonSerializer.Serialize(inputRecipe, ReStationModuleManifest.JsonOptions));
        string outDir = Path.Combine(_tempRoot, "lifted");

        RecompCompileResult res = new RecompCompilerPipeline(
            new RecompCompilerOptions(null, romDir, string.Empty, outDir, "slotaseeds", "SLUS-09912", "Slot A Seeds")
            {
                AotRecipePath = recipePath
            }).Run();
        Assert.True(res.Success, res.Message);

        AotRecipeSpec emitted = JsonSerializer.Deserialize<AotRecipeSpec>(
            File.ReadAllText(Path.Combine(outDir, "aot-recipe.json")),
            ReStationModuleManifest.JsonOptions)!;
        Assert.Equal(2, emitted.Executables.Count);
        Assert.Contains(0x800B7E30U, emitted.Executables[0].EntryAddresses!);
        Assert.DoesNotContain(0x800B7E40U, emitted.Executables[0].EntryAddresses!);
        Assert.Contains(0x800B7E40U, emitted.Executables[1].EntryAddresses!);
        Assert.DoesNotContain(0x800B7E30U, emitted.Executables[1].EntryAddresses!);
        Assert.Contains(0x800B7E30U, emitted.EntryAddresses);
        Assert.Contains(0x800B7E40U, emitted.EntryAddresses);

        string ovlADispatch = File.ReadAllText(Path.Combine(outDir, "OvlA_0Exe", "Dispatch00.g.cs"));
        string ovlBDispatch = File.ReadAllText(Path.Combine(outDir, "OvlB_1Exe", "Dispatch00.g.cs"));
        Assert.Contains("Fn_800B7E30", ovlADispatch, StringComparison.Ordinal);
        Assert.DoesNotContain("Fn_800B7E40", ovlADispatch, StringComparison.Ordinal);
        Assert.Contains("Fn_800B7E40", ovlBDispatch, StringComparison.Ordinal);
        Assert.DoesNotContain("Fn_800B7E30", ovlBDispatch, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadFunctionsJson_FiltersProgramTags_PreservesLegacyUntagged_AndIsolatesCombinedProgramCatalog()
    {
        Directory.CreateDirectory(_tempRoot);

        // 1. Tagged functions.json across kind/category/type/segment -> keeps strictly "program" when present.
        string taggedJson = Path.Combine(_tempRoot, "tagged.functions.json");
        File.WriteAllText(taggedJson, """
        {
          "functions": [
            { "address": "0x80010000", "size": 8, "kind": "program" },
            { "address": "0x80010010", "size": 8, "category": "PROGRAM" },
            { "address": "0x80010020", "size": 8, "type": "program" },
            { "address": "0x80010030", "size": 8, "segment": "program" },
            { "address": "0x80010040", "size": 8, "kind": "bios" },
            { "address": "0x80010050", "size": 8, "category": "thunk" },
            { "address": "0x80010060", "size": 8, "type": "bios" },
            { "address": "0x80010070", "size": 8, "segment": "thunk" }
          ]
        }
        """);

        byte[] exeBytes = new byte[0x800 + 0x100];
        foreach (int addrOff in new[] { 0x00, 0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70 })
        {
            BinaryPrimitives.WriteUInt32LittleEndian(exeBytes.AsSpan(0x800 + addrOff, 4), 0x03E00008U);
            BinaryPrimitives.WriteUInt32LittleEndian(exeBytes.AsSpan(0x800 + addrOff + 4, 4), 0x00000000U);
        }
        var exe = new DiscoveredExe(0, "SLUS_099.13", "SLUS_099.13", "Slus09913Exe", "Slus09913Module", exeBytes,
            new PsxExeHeader(0x80010000U, 0U, 0x80010000U, 0x100U, 0U, 0U));
        uint ReadExeWord(uint a) => BinaryPrimitives.ReadUInt32LittleEndian(exeBytes.AsSpan(0x800 + (int)(a - 0x80010000U), 4));

        Assert.Equal([0x80010000U, 0x80010010U, 0x80010020U, 0x80010030U],
            ExeScanHelpers.LoadFunctionsJson(taggedJson, exe).Select(x => x.Addr).ToArray());
        (List<GeneratedFnSpec> taggedFuncs, _) = ExeScanHelpers.DiscoverFunctions(exe, ReadExeWord, taggedJson);
        Assert.Equal([0x80010000U, 0x80010010U, 0x80010020U, 0x80010030U], taggedFuncs.Select(f => f.Addr).ToArray());

        // 2. Legacy untagged functions.json -> preserves all entries cleanly.
        string legacyJson = Path.Combine(_tempRoot, "legacy.functions.json");
        File.WriteAllText(legacyJson, """
        [
          { "address": "0x80010000", "size": 8 },
          { "address": "0x80010040", "size": 8 }
        ]
        """);
        Assert.Equal([0x80010000U, 0x80010040U],
            ExeScanHelpers.LoadFunctionsJson(legacyJson, exe).Select(x => x.Addr).ToArray());
        (List<GeneratedFnSpec> legacyFuncs, _) = ExeScanHelpers.DiscoverFunctions(exe, ReadExeWord, legacyJson);
        Assert.Contains(0x80010000U, legacyFuncs.Select(f => f.Addr));
        Assert.Contains(0x80010040U, legacyFuncs.Select(f => f.Addr));

        // 3. Multi-overlay combined catalog with "program" property sharing [0x800B7E20, 0x800B7E60).
        string catalogJson = Path.Combine(_tempRoot, "functions_catalog.json");
        File.WriteAllText(catalogJson, """
        {
          "functions": [
            { "program": "OVL_part11_id06.EXE", "address": "0x800B7E20", "size": 8, "kind": "program" },
            { "program": "OVL_part11_id06.EXE", "address": "0x800B7E30", "size": 8, "kind": "program" },
            { "program": "OVL_part11_id06.EXE", "address": "0x800B7E38", "size": 8, "kind": "thunk" },
            { "program": "OVL_part12_id07.EXE", "address": "0x800B7E20", "size": 8, "kind": "program" },
            { "program": "OVL_part12_id07.EXE", "address": "0x800B7E40", "size": 8, "kind": "program" }
          ]
        }
        """);

        byte[] ovl11Bytes = BuildSharedSlotOverlay(0, 0x10, 1);
        byte[] ovl12Bytes = BuildSharedSlotOverlay(1, 0x20, 1);
        var ovl11 = new DiscoveredExe(1, "A_FILE.BIN#OVL_part11_id06", "A_FILE.BIN#OVL_part11_id06", "Ovl11Exe", "Ovl11Module",
            ovl11Bytes, new PsxExeHeader(0x800B7E20U, 0U, 0x800B7E20U, (uint)ovl11Bytes.Length, 0U, 0U)) { IsOverlay = true };
        var ovl12 = new DiscoveredExe(2, "A_FILE.BIN#OVL_part12_id07", "A_FILE.BIN#OVL_part12_id07", "Ovl12Exe", "Ovl12Module",
            ovl12Bytes, new PsxExeHeader(0x800B7E20U, 0U, 0x800B7E20U, (uint)ovl12Bytes.Length, 0U, 0U)) { IsOverlay = true };
        uint ReadOvl11Word(uint a) => BinaryPrimitives.ReadUInt32LittleEndian(ovl11Bytes.AsSpan((int)(a - 0x800B7E20U), 4));
        uint ReadOvl12Word(uint a) => BinaryPrimitives.ReadUInt32LittleEndian(ovl12Bytes.AsSpan((int)(a - 0x800B7E20U), 4));

        Assert.Equal([0x800B7E20U, 0x800B7E30U], ExeScanHelpers.LoadFunctionsJson(catalogJson, ovl11).Select(x => x.Addr).ToArray());
        Assert.Equal([0x800B7E20U, 0x800B7E40U], ExeScanHelpers.LoadFunctionsJson(catalogJson, ovl12).Select(x => x.Addr).ToArray());
        (List<GeneratedFnSpec> fn11, _) = ExeScanHelpers.DiscoverFunctions(ovl11, ReadOvl11Word, catalogJson);
        (List<GeneratedFnSpec> fn12, _) = ExeScanHelpers.DiscoverFunctions(ovl12, ReadOvl12Word, catalogJson);
        Assert.Equal([0x800B7E20U, 0x800B7E30U], fn11.Select(f => f.Addr).ToArray());
        Assert.Equal([0x800B7E20U, 0x800B7E40U], fn12.Select(f => f.Addr).ToArray());
    }

    private static byte[] BuildExecutableOverlaySlice(uint textAddr, ushort returnVal)
    {
        byte[] slice = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(slice.AsSpan(0, 4), (9U << 26) | (2U << 16) | returnVal); // addiu v0, zero, returnVal
        BinaryPrimitives.WriteUInt32LittleEndian(slice.AsSpan(4, 4), 0x03E00008U); // jr ra
        BinaryPrimitives.WriteUInt32LittleEndian(slice.AsSpan(8, 4), 0x00000000U); // nop
        _ = textAddr;
        return slice;
    }

    private static byte[] BuildSharedSlotOverlay(int slotIndex, int extraOffsetBytes, ushort returnVal)
    {
        byte[] slice = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(slice.AsSpan(0, 4), 0x03E00008U); // 0x00: jr ra
        BinaryPrimitives.WriteUInt32LittleEndian(slice.AsSpan(4, 4), 0x00000000U); // 0x04: nop
        BinaryPrimitives.WriteUInt32LittleEndian(slice.AsSpan(extraOffsetBytes, 4), (9U << 26) | (2U << 16) | (uint)(returnVal + slotIndex));
        BinaryPrimitives.WriteUInt32LittleEndian(slice.AsSpan(extraOffsetBytes + 4, 4), 0x03E00008U);
        BinaryPrimitives.WriteUInt32LittleEndian(slice.AsSpan(extraOffsetBytes + 8, 4), 0x00000000U);
        return slice;
    }
}
