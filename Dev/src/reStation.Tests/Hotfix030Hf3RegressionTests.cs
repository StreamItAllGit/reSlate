// @module: reStation.Tests.Hotfix030Hf3RegressionTests
// @purpose: Verifies v0.3.0-hf.3 CdlGetlocP/L split across minute rollover, Call/ResumeMidStack Pc alignment + intercept, Gpu SyncBackendToVramMirror snapshot preservation, ITextScopedOverlayRecompModule .data immunity, and MipsLifter loop MidEntries AOT re-entry.
// @depends-on: Xunit, reStation.Compiler, reStation.Contracts, reStation.Core, reStation.Core.Cpu, reStation.Core.Gpu
// @exports: Hotfix030Hf3RegressionTests

using System.Buffers.Binary;
using System.Security.Cryptography;
using reStation.Compiler;
using reStation.Contracts;
using reStation.Core;
using reStation.Core.Aot;
using reStation.Core.Cpu;
using reStation.Core.Gpu;
using Xunit;

namespace reStation.Tests;

public sealed class Hotfix030Hf3RegressionTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "reslate-hf3-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true);
    }

    private sealed class CapturingFramePatch(uint interceptPc, Action<IReStationCpu> onHit) : IGameFramePatch
    {
        public int InterceptCount { get; private set; }
        public void Reset() { }
        public bool TryInterceptCall(IReStationCpu cpu, uint pc)
        {
            if (pc != interceptPc) return false;
            InterceptCount++;
            onHit(cpu);
            return true;
        }
    }

    private sealed class MockTextScopedOverlay(
        int exeId, uint loadAddr, int totalSize, string contentSha,
        uint verifiedStart, int verifiedSize, string textSha,
        HashSet<uint> compiledEntries, Action<uint> onExec) : ITextScopedOverlayRecompModule, IRecompCompiledEntryLookup
    {
        public int ExeId => exeId;
        public string ExeName => $"OVL_{exeId}.EXE";
        public uint TextStart => loadAddr;
        public uint TextEnd => loadAddr + (uint)totalSize;
        public uint LoadRamAddress => loadAddr;
        public int ContentSizeBytes => totalSize;
        public string ContentSha256Hex => contentSha;
        public uint VerifiedTextStart => verifiedStart;
        public int VerifiedTextSizeBytes => verifiedSize;
        public string TextSha256Hex => textSha;
        public bool ContainsCompiledEntry(uint pc) => compiledEntries.Contains(pc);
        public bool TryExecuteFunction(IReStationCpu cpu, uint pc)
        {
            if (!compiledEntries.Contains(pc)) return false;
            onExec(pc);
            return true;
        }
    }

    private static int BcdPosToLba(byte m, byte s, byte f)
    {
        int dm = (m >> 4) * 10 + (m & 0xF);
        int ds = (s >> 4) * 10 + (s & 0xF);
        int df = (f >> 4) * 10 + (f & 0xF);
        return (dm * 60 + ds) * 75 + df - 150;
    }

    private static byte[] IssueCdCmd(PsxCpu cpu, byte cmd, params byte[] args)
    {
        cpu.CdRom.WriteRegister(0, 1);
        cpu.CdRom.WriteRegister(3, 0x1F); // ack prior IRQ
        cpu.CdRom.WriteRegister(0, 0);
        foreach (byte b in args) cpu.CdRom.WriteRegister(2, b);
        cpu.CdRom.WriteRegister(1, cmd);
        cpu.TotalCycles += 500;
        cpu.CdRom.TickTo(cpu.TotalCycles);
        var resp = new List<byte>(8);
        while ((cpu.CdRom.ReadRegister(0) & 0x20) != 0) resp.Add(cpu.CdRom.ReadRegister(1));
        return resp.ToArray();
    }

    [Fact]
    public void CdRom_GetlocP_And_GetlocL_ReturnDistinctFormatsAcrossMinuteRollover()
    {
        Directory.CreateDirectory(_tempRoot);
        using var cpu = new PsxCpu(new RecompSettings(), _tempRoot, new SoftwareCpuRenderBackend(), new ReStationLogger(_tempRoot, false));

        _ = IssueCdCmd(cpu, 0x0D, 0x01, 0x02); // Setfilter(file=1, chan=2)
        _ = IssueCdCmd(cpu, 0x0E, 0xC8);       // Setmode(0xC8 = double-speed + XA)

        // LBA 67349 = 14m 59s 74f -> LBA 67350 = 15m 00s 00f
        _ = IssueCdCmd(cpu, 0x02, 0x14, 0x59, 0x74);
        byte[] locBefore = IssueCdCmd(cpu, 0x11);
        Assert.Equal(8, locBefore.Length);
        Assert.Equal([0x01, 0x01, 0x14, 0x59, 0x74, 0x14, 0x59, 0x74], locBefore);

        _ = IssueCdCmd(cpu, 0x02, 0x15, 0x00, 0x00);
        byte[] locAfter = IssueCdCmd(cpu, 0x11);
        Assert.Equal([0x01, 0x01, 0x15, 0x00, 0x00, 0x15, 0x00, 0x00], locAfter);

        int lbaBefore = BcdPosToLba(locBefore[5], locBefore[6], locBefore[7]);
        int lbaAfter = BcdPosToLba(locAfter[5], locAfter[6], locAfter[7]);
        Assert.Equal(67349, lbaBefore);
        Assert.Equal(67350, lbaAfter);

        byte[] locL = IssueCdCmd(cpu, 0x10);
        Assert.Equal([0x15, 0x00, 0x00, 0xC8, 0x01, 0x02], locL[..6]);
    }

    [Fact]
    public void PsxCpu_CallAndResumeMidStack_AlignPcBeforeFramePatchIntercept()
    {
        Directory.CreateDirectory(_tempRoot);
        using var cpu = new PsxCpu(new RecompSettings(), _tempRoot, new SoftwareCpuRenderBackend(), new ReStationLogger(_tempRoot, false));

        uint capturedPc = 0U;
        var patch = new CapturingFramePatch(0x80032CFCU, c => capturedPc = c.Pc);
        cpu.FramePatch = patch;

        cpu.Pc = 0x800C157CU;
        cpu.R[31] = IReStationCpu.ReturnSentinelPc;
        cpu.Call(0x80032CFCU);
        Assert.Equal(0x80032CFCU, capturedPc);
        Assert.Equal(1, patch.InterceptCount);

        capturedPc = 0U;
        cpu.Pc = 0x800C157CU;
        cpu.R[31] = 0x80011D88U;
        cpu.UnwindTargetPc = 0x80011D88U;
        cpu.DispatchFunction(0x80032CFCU);
        Assert.Equal(0x80032CFCU, capturedPc);
        Assert.Equal(2, patch.InterceptCount);
        Assert.Equal(0x80011D88U, cpu.Pc);
    }

    [Fact]
    public void PsxGpu_SyncBackendToVramMirror_PreservesRenderedPixelsAcrossQuickSave()
    {
        Directory.CreateDirectory(_tempRoot);
        var settings = new RecompSettings();
        var sw = new SoftwareCpuRenderBackend();
        sw.Initialize(320, 240, 1);
        var gpu = new PsxGpu(settings, new reStation.Core.Gte.PgxpTracker(), sw);

        // Draw monochrome rect GP0(60h) at (12, 18) size 8x8 -> written into backend VRAM only
        gpu.WriteGp0(0x6010FF20U);
        gpu.WriteGp0((18U << 16) | 12U);
        gpu.WriteGp0((8U << 16) | 8U);

        int idx = (19 * PsxGpu.VramWidth) + 14;
        Assert.Equal(0, gpu.Vram[idx]);

        Assert.True(gpu.SyncBackendToVramMirror());
        ushort mirrored = gpu.Vram[idx];
        Assert.NotEqual(0, mirrored);

        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
            gpu.WriteSnapshotState(bw);

        ms.Position = 0;
        var sw2 = new SoftwareCpuRenderBackend();
        sw2.Initialize(320, 240, 1);
        var gpu2 = new PsxGpu(settings, new reStation.Core.Gte.PgxpTracker(), sw2);
        using (var br = new BinaryReader(ms, System.Text.Encoding.UTF8, leaveOpen: true))
            gpu2.ReadSnapshotState(br);

        Span<ushort> readback = stackalloc ushort[1];
        Assert.True(sw2.TryReadVramRect(14, 19, 1, 1, readback));
        Assert.Equal(mirrored, readback[0]);
    }

    [Fact]
    public void CompilerAndCpu_TextScopedOverlay_IgnoresDataWrites_AndLiftsLoopMidEntries()
    {
        Directory.CreateDirectory(_tempRoot);

        // 1. MipsLifter emits MidEntries for a looping function with calls (loop header + jal return PC)
        const uint fnStart = 0x80011D00U;
        uint[] words =
        [
            0x27BDFFE8U, // addiu sp, sp, -24
            0xAFBF0014U, // sw ra, 20(sp)
            0x0C004800U, // jal 0x80012000 (loop header target)
            0x00000000U, // nop
            0x08004742U, // j 0x80011D08 (back edge)
            0x00000000U  // nop
        ];
        uint ReadWord(uint a) => (a >= fnStart && a < fnStart + (uint)(words.Length * 4)) ? words[(a - fnStart) / 4U] : 0U;
        List<LiftedUnit> units = MipsLifter.LiftFunction("CoreLoop", fnStart, fnStart + (uint)(words.Length * 4), ReadWord, null, out List<uint> midEntries);
        Assert.Contains(fnStart + 8U, midEntries);  // loop header back edge
        Assert.Contains(fnStart + 16U, midEntries); // jal return site
        Assert.Contains(units, u => u.Lines.Any(l => l.Contains("CoreLoop_At", StringComparison.Ordinal)));

        // 2. RecompCompilerPipeline emits ITextScopedOverlayRecompModule and PsxCpu ignores .data writes
        string exeDir = Path.Combine(_tempRoot, "disc");
        Directory.CreateDirectory(exeDir);
        byte[] ovlSlice = new byte[256];
        BinaryPrimitives.WriteUInt32LittleEndian(ovlSlice.AsSpan(0, 4), 0x0C02F000U); // jal
        BinaryPrimitives.WriteUInt32LittleEndian(ovlSlice.AsSpan(4, 4), 0x00000000U);
        BinaryPrimitives.WriteUInt32LittleEndian(ovlSlice.AsSpan(8, 4), 0x03E00008U); // jr ra
        BinaryPrimitives.WriteUInt32LittleEndian(ovlSlice.AsSpan(12, 4), 0x00000000U);
        string containerPath = Path.Combine(exeDir, "OVL.BIN");
        File.WriteAllBytes(containerPath, ovlSlice);

        string fullSha = Convert.ToHexString(SHA256.HashData(ovlSlice)).ToLowerInvariant();
        string textSha = Convert.ToHexString(SHA256.HashData(ovlSlice.AsSpan(0, 16))).ToLowerInvariant();
        Assert.NotEqual(fullSha, textSha);

        var recipe = new AotRecipeSpec
        {
            SchemaVersion = 1, PlatformId = "ps1", ModuleId = "ovltest", DiscSerial = "SLUS-09999",
            PrimaryBootExecutable = "OVL.BIN#SLICE0",
            Executables =
            [
                new AotRecipeExecutableSpec
                {
                    BootExePath = "OVL.BIN#SLICE0", Sha256 = fullSha,
                    LoadAddress = 0x800BC000U, EntryAddress = 0x800BC000U, TextSizeBytes = ovlSlice.Length
                }
            ]
        };
        string recipePath = Path.Combine(_tempRoot, "aot-recipe.json");
        File.WriteAllText(recipePath, System.Text.Json.JsonSerializer.Serialize(recipe, ReStationModuleManifest.JsonOptions));
        string outDir = Path.Combine(_tempRoot, "lifted");
        var compileRes = new RecompCompilerPipeline(new RecompCompilerOptions(null, exeDir, string.Empty, outDir, "ovltest", "SLUS-09999", "Ovl Test")
        {
            AotRecipePath = recipePath
        }).Run();
        Assert.True(compileRes.Success, compileRes.Message);
        string modHeader = Directory.GetFiles(outDir, "*Module.g.cs", SearchOption.AllDirectories)
            .Select(File.ReadAllText).First(t => t.Contains("ITextScopedOverlayRecompModule", StringComparison.Ordinal));
        Assert.Contains("VerifiedTextStart", modHeader, StringComparison.Ordinal);
        Assert.Contains("TextSha256Hex", modHeader, StringComparison.Ordinal);

        using var cpu = new PsxCpu(new RecompSettings(), _tempRoot, new SoftwareCpuRenderBackend(), new ReStationLogger(_tempRoot, false));
        ovlSlice.CopyTo(cpu.Ram.Slice(0x000BC000, ovlSlice.Length));
        uint executedPc = 0U;
        cpu.RegisterModule(new MockTextScopedOverlay(
            3, 0x800BC000U, ovlSlice.Length, fullSha, 0x800BC000U, 16, textSha,
            [0x800BC008U], pc => { executedPc = pc; cpu.ShouldUnwind = true; }));

        long hashesBefore = cpu.OverlayHashComputations;
        cpu.Write32(0x800BC080U, 0x12345678U); // mutate trailing .data
        cpu.Write32(0x800BC084U, 0x9ABCDEF0U);
        Assert.Equal(hashesBefore, cpu.OverlayHashComputations);
        Assert.True(cpu.HasCompiledEntry(0x800BC008U));
        Assert.Equal(hashesBefore + 1L, cpu.OverlayHashComputations);

        cpu.Write32(0x800BC088U, 0x11111111U);
        cpu.R[31] = 0x80012000U;
        cpu.UnwindTargetPc = 0x80012000U;
        cpu.DispatchFunction(0x800BC008U);
        Assert.Equal(0x800BC008U, executedPc);
        Assert.Equal(hashesBefore + 1L, cpu.OverlayHashComputations);

        cpu.Write32(0x800BC000U, 0U); // tamper immutable .text -> must evict
        Assert.False(cpu.HasCompiledEntry(0x800BC008U));
    }
}
