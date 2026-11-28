// @module: reStation.Tests.Tier3IrAndJitTests
// @purpose: Validates D2.1 Tier 3 16-byte IrInsn, MipsIrLowerer, RamPageEpochTable, --force-zero-rwx/SMC demotion, and JitBringupRecorder watchdog.

using System.Runtime.CompilerServices;
using System.Text.Json;
using reStation.Contracts;
using reStation.Core;
using reStation.Core.Bringup;
using reStation.Core.Cpu;
using reStation.Core.Gpu;
using reStation.Core.Ir;
using reStation.Core.Jit;
using Xunit;

namespace reStation.Tests;

public sealed class Tier3IrAndJitTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "restation-t3-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        TestTempCleanup.TryDeleteDirectory(_tempRoot, nameof(Tier3IrAndJitTests));
    }

    [Fact]
    public void IrInsn_IsStrictly16Bytes_AndMipsIrLowererHandlesAluMemoryBranchAndCop2()
    {
        Assert.Equal(IrInsn.ExpectedByteSize, Unsafe.SizeOf<IrInsn>());

        var words = new Dictionary<uint, uint>
        {
            [0x80020000U] = 0x2402002AU, // addiu v0, zero, 42
            [0x80020004U] = 0x24430008U, // addiu v1, v0, 8
            [0x80020008U] = 0xAC031000U, // sw v1, 0x1000(zero)
            [0x8002000CU] = 0x4A000001U, // COP2 command
            [0x80020010U] = 0x03E00008U, // jr ra
            [0x80020014U] = 0x00000000U  // nop (delay slot)
        };
        IrBasicBlock block = MipsIrLowerer.LowerBlock(0x80020000U, pc => words.GetValueOrDefault(pc));

        Assert.Equal(0x80020000U, block.EntryPc);
        Assert.Equal(6, block.Instructions.Length);
        Assert.True(block.EndsWithTerminalTransfer);
        Assert.Equal(IrOp.Addiu, block.Instructions[0].Op);
        Assert.Equal(IrOp.Sw, block.Instructions[2].Op);
        Assert.Equal(IrOp.Cop2Cmd, block.Instructions[3].Op);
        Assert.Equal(IrOp.Jr, block.Instructions[4].Op);
        Assert.True(block.Instructions[4].HasFlag(IrInsnFlags.HasDelaySlot));
        Assert.True(block.Instructions[5].HasFlag(IrInsnFlags.InDelaySlot));
    }

    [Fact]
    public void RamPageEpochTable_DetectsKusegKseg0Kseg1Aliasing_AndDualPageBoundarySmc()
    {
        var table = new RamPageEpochTable();
        Assert.Equal(512, RamPageEpochTable.PageCount);
        Assert.Equal(64, RamPageEpochTable.CachelinesPerPage);

        var words = new Dictionary<uint, uint>
        {
            [0x8004FFFCU] = 0x24020007U, // page 79, last word
            [0x80050000U] = 0x03E00008U, // page 80, first word
            [0x80050004U] = 0x00000000U  // delay nop
        };
        IrBasicBlock spanning = MipsIrLowerer.LowerBlock(0x8004FFFCU, pc => words.GetValueOrDefault(pc));
        Assert.True(spanning.SpansTwoPages);

        var stamp = table.CaptureStamp(spanning);
        Assert.True(table.IsStampValid(ref stamp));

        // Write via uncached KSEG1 alias touching second page boundary.
        table.InvalidateRamRange(0xA0050000U, 4);
        Assert.False(table.IsStampValid(ref stamp));

        // Unrelated page write leaves unrelated KUSEG stamp intact.
        IrBasicBlock local = MipsIrLowerer.LowerBlock(0x80030000U, _ => 0x03E00008U);
        var localStamp = table.CaptureStamp(local);
        table.InvalidateRamRange(0x00038000U, 4);
        Assert.True(table.IsStampValid(ref localStamp));

        table.InvalidateRamRange(0x00030000U, 4);
        Assert.False(table.IsStampValid(ref localStamp));
    }

    [Fact]
    public void Tier3Coordinator_ZeroRwxThreadedIrAndSmcDemotion_MatchParityAndThreshold8()
    {
        Directory.CreateDirectory(_tempRoot);
        var epochs = new RamPageEpochTable();
        var zeroRwxCoord = new Tier3ExecutionCoordinator(
            epochs, ReStationExecutionTierModes.JitBringup, forceZeroRwx: true);

        using var cpuA = new PsxCpu(new RecompSettings(), _tempRoot, new SoftwareCpuRenderBackend(), new ReStationLogger(_tempRoot, false));
        cpuA.R[31] = IReStationCpu.ReturnSentinelPc;
        cpuA.Bus.Write32(0x80021000U, 0x24020019U); // addiu v0, zero, 25
        cpuA.Bus.Write32(0x80021004U, 0x24420011U); // addiu v0, v0, 17 -> 42
        cpuA.Bus.Write32(0x80021008U, 0x03E00008U); // jr ra
        cpuA.Bus.Write32(0x8002100CU, 0x00000000U); // nop

        uint nextA = zeroRwxCoord.ExecuteBlock(cpuA, 0x80021000U, addr => cpuA.Bus.Read32(addr));
        Assert.Equal(IReStationCpu.ReturnSentinelPc, nextA);
        Assert.Equal(42U, cpuA.R[2]);
        Assert.Equal(0L, zeroRwxCoord.JitDispatchCount);
        Assert.True(zeroRwxCoord.ThreadedIrDispatchCount > 0);

        // RyuJIT parity (when dynamic code supported) + SMC demotion at threshold = 8.
        var epochsB = new RamPageEpochTable();
        var jitCoord = new Tier3ExecutionCoordinator(epochsB, ReStationExecutionTierModes.JitBringup, forceZeroRwx: false);
        Assert.Equal(8, Tier3ExecutionCoordinator.SmcDemotionThreshold);

        using var cpuB = new PsxCpu(new RecompSettings(), _tempRoot, new SoftwareCpuRenderBackend(), new ReStationLogger(_tempRoot, false));
        cpuB.R[31] = IReStationCpu.ReturnSentinelPc;
        cpuB.Bus.Write32(0x80022000U, 0x24020019U);
        cpuB.Bus.Write32(0x80022004U, 0x24420011U);
        cpuB.Bus.Write32(0x80022008U, 0x03E00008U);
        cpuB.Bus.Write32(0x8002200CU, 0x00000000U);

        for (int i = 0; i < Tier3ExecutionCoordinator.SmcDemotionThreshold + 2; i++)
        {
            cpuB.R[2] = 0U;
            _ = jitCoord.ExecuteBlock(cpuB, 0x80022000U, addr => cpuB.Bus.Read32(addr));
            Assert.Equal(42U, cpuB.R[2]);
            epochsB.InvalidateRamRange(0x80022000U, 4);
        }

        Assert.True(jitCoord.IsBlockDemotedToThreadedIr(0x80022000U));
    }

    [Fact]
    public void JitBringupRecorder_EnforcesSpinWatchdogExitCode5_AndWritesCleanProfileJson()
    {
        Directory.CreateDirectory(_tempRoot);
        Assert.Equal(5, JitBringupRecorder.SpinWatchdogExitCode);
        Assert.Equal(5_644_800L, JitBringupRecorder.DefaultSpinWatchdogCycleThreshold);

        string profilePath = Path.Combine(_tempRoot, "profile", "bringup.profile.json");
        var recorder = new JitBringupRecorder(
            moduleId: "t3clean",
            executionTierMode: ReStationExecutionTierModes.JitBringup,
            isDynamicCodeSupported: true,
            bringupProfilePath: profilePath);

        recorder.RecordDiscoveredBlock("deadbeefcafebabe", 0x80010000U, 16U, overlayId: 0);
        recorder.RecordIndirectJumpTarget(0x80010008U, 0x80010200U);
        Assert.False(recorder.AdvanceCyclesSinceVBlank(100_000L));
        recorder.ResetVBlankSpinCounter();
        Assert.Equal(0L, recorder.NonVBlankSpinCycles);

        Assert.True(recorder.AdvanceCyclesSinceVBlank(JitBringupRecorder.DefaultSpinWatchdogCycleThreshold));
        Assert.True(recorder.IsSpinWatchdogTripped);

        string written = recorder.WriteProfileAtomic(compiledDispatchCount: 12L, interpreterFallbackCount: 1L);
        Assert.Equal(Path.GetFullPath(profilePath), Path.GetFullPath(written));
        string json = File.ReadAllText(written);
        Assert.DoesNotContain(".bin", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".iso", json, StringComparison.OrdinalIgnoreCase);

        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal("t3clean", doc.RootElement.GetProperty("moduleId").GetString());
        Assert.True(doc.RootElement.GetProperty("isSpinWatchdogTripped").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("isPureStaticReady").GetBoolean());
        Assert.False(doc.RootElement.TryGetProperty("isBringupCaptureClean", out _));
        Assert.False(recorder.IsBringupCaptureClean); // Spin watchdog tripped -> false
        Assert.Equal(1, doc.RootElement.GetProperty("discoveredFunctionCount").GetInt32());
    }

    [Fact]
    public void PlayerLoopExitCodeAndLauncherSyncWait_PreserveJitBringupExitCode5()
    {
        Assert.Equal(JitBringupRecorder.SpinWatchdogExitCode, reStation.Player.Program.ResolveLoopExitCode(preservedExitCode: JitBringupRecorder.SpinWatchdogExitCode));
        Assert.Equal(0, reStation.Player.Program.ResolveLoopExitCode(preservedExitCode: 0));
        Assert.True(reStation.Launcher.LauncherArgsHelper.RequiresSyncWait(["--jit-bringup"]));
    }

    [Fact]
    public void MipsIrLowerer_JalrRdZero_PreservesZeroDestinationAcrossBothTier3Backends()
    {
        Directory.CreateDirectory(_tempRoot);
        const uint jalrZeroA0 = (4U << 21) | 0x09U; // jalr $zero, $a0
        IrInsn lowered = MipsIrLowerer.LowerInstruction(0x80020000U, jalrZeroA0);
        Assert.Equal(IrOp.Jalr, lowered.Op);
        Assert.Equal(0, lowered.Rd);
        Assert.False(lowered.HasFlag(IrInsnFlags.LinksRa));

        foreach (bool forceZeroRwx in new[] { false, true })
        {
            var coord = new Tier3ExecutionCoordinator(new RamPageEpochTable(), ReStationExecutionTierModes.JitBringup, forceZeroRwx);
            using var cpu = new PsxCpu(new RecompSettings(), _tempRoot, new SoftwareCpuRenderBackend());
            cpu.R[4] = 0x80020200U;
            cpu.R[31] = 0xCAFEBABEU;
            cpu.Bus.Write32(0x80020000U, jalrZeroA0);
            cpu.Bus.Write32(0x80020004U, 0x00000000U); // nop

            uint nextPc = coord.ExecuteBlock(cpu, 0x80020000U, cpu.Read32);
            Assert.Equal(0x80020200U, nextPc);
            Assert.Equal(0xCAFEBABEU, cpu.R[31]);
            Assert.Equal(0U, cpu.R[0]);
        }
    }

    [Fact]
    public void RyuJitAndThreadedIr_ExecuteRfeLwc2Swc2AndMfc0Regs8_13_14_WithParity()
    {
        Directory.CreateDirectory(_tempRoot);
        const uint mfc0BadVAddr = (0x10U << 26) | (8U << 16) | (8U << 11);   // mfc0 $t0, $8
        const uint mfc0Cause = (0x10U << 26) | (9U << 16) | (13U << 11);     // mfc0 $t1, $13
        const uint mfc0Epc = (0x10U << 26) | (10U << 16) | (14U << 11);      // mfc0 $t2, $14
        const uint lwc2Data6 = (0x32U << 26) | (29U << 21) | (6U << 16);     // lwc2 $6 (RGBC, 32-bit), 0($sp)
        const uint swc2Data6 = (0x3AU << 26) | (29U << 21) | (6U << 16) | 4U;// swc2 $6, 4($sp)
        const uint rfeInsn = (0x10U << 26) | (0x10U << 21) | 0x10U;          // rfe

        foreach (bool forceZeroRwx in new[] { false, true })
        {
            var coord = new Tier3ExecutionCoordinator(new RamPageEpochTable(), ReStationExecutionTierModes.JitBringup, forceZeroRwx);
            using var cpu = new PsxCpu(new RecompSettings(), _tempRoot, new SoftwareCpuRenderBackend());
            cpu.Cop0Sr = 0x0000040CU;
            cpu.Cop0BadVAddr = 0x80041234U;
            cpu.Cop0Cause = 0x00000024U;
            cpu.Cop0Epc = 0x80015550U;
            cpu.R[29] = 0x80050000U;
            cpu.Bus.Write32(0x80050000U, 0x11223344U);

            cpu.Bus.Write32(0x80021000U, mfc0BadVAddr);
            cpu.Bus.Write32(0x80021004U, mfc0Cause);
            cpu.Bus.Write32(0x80021008U, mfc0Epc);
            cpu.Bus.Write32(0x8002100CU, lwc2Data6);
            cpu.Bus.Write32(0x80021010U, swc2Data6);
            cpu.Bus.Write32(0x80021014U, rfeInsn);

            uint nextPc = coord.ExecuteBlock(cpu, 0x80021000U, cpu.Read32);
            Assert.Equal(0x80021018U, nextPc);
            Assert.Equal(0x80041234U, cpu.R[8]);
            Assert.Equal(0x00000024U, cpu.R[9]);
            Assert.Equal(0x80015550U, cpu.R[10]);
            Assert.Equal(0x11223344U, cpu.GteReadData(6));
            Assert.Equal(0x11223344U, cpu.Bus.Read32(0x80050004U));
            Assert.Equal(0x00000403U, cpu.Cop0Sr);
        }
    }

    [Fact]
    public void PsxCpu_LiveNativeAotAndTier3Coherence_EnforcesStaticFailClosedAndInterpreterQuarantine()
    {
        Directory.CreateDirectory(_tempRoot);
        string logDir = Path.Combine(_tempRoot, "logs");
        using var logger = new ReStationLogger(logDir, enabled: true);

        // Compiled static AOT entry at 0x80010000 (returns 77 in $v0)
        var compiledMod = new InlineCompiledModule(0x80010000U, 0x80010100U, c => c.R[2] = 77U);

        // 1. Default aotWithJitFallback: compiled first, unlifted calls Tier 3 (NOT interpreter!), Bus write invalidates RAM cache
        using (var cpu = new PsxCpu(new RecompSettings(), _tempRoot, new SoftwareCpuRenderBackend(), logger))
        {
            cpu.RegisterModule(compiledMod);
            // Unlifted caller at 0x80020000: calls compiled 0x80010000, adds 3, returns to caller $ra
            cpu.Bus.Write32(0x80020000U, 0x27BDFFF0U); // addiu $sp, $sp, -16
            cpu.Bus.Write32(0x80020004U, 0xAFBF0000U); // sw $ra, 0($sp)
            cpu.Bus.Write32(0x80020008U, (3U << 26) | (0x00010000U >> 2)); // jal 0x80010000
            cpu.Bus.Write32(0x8002000CU, 0x00000000U); // nop
            cpu.Bus.Write32(0x80020010U, 0x24420003U); // addiu $v0, $v0, 3 (-> 80)
            cpu.Bus.Write32(0x80020014U, 0x8FBF0000U); // lw $ra, 0($sp)
            cpu.Bus.Write32(0x80020018U, 0x27BD0010U); // addiu $sp, $sp, 16
            cpu.Bus.Write32(0x8002001CU, 0x03E00008U); // jr $ra
            cpu.Bus.Write32(0x80020020U, 0x00000000U); // nop

            cpu.R[29] = 0x80040000U;
            cpu.R[31] = 0x80010840U; // simulated compiled caller RA
            cpu.Call(0x80020000U);
            Assert.Equal(80U, cpu.R[2]);
            Assert.Equal(1L, cpu.CompiledDispatchCount);
            Assert.Equal(0L, cpu.InterpreterFallbackCount);
            Assert.True(cpu.Tier3Coordinator.JitDispatchCount + cpu.Tier3Coordinator.ThreadedIrDispatchCount >= 2);
            Assert.False(cpu.ShouldUnwind);

            // Patch addiu $v0, $v0, 3 -> addiu $v0, $v0, 23 via Bus RAM write (SMC coherence via OnRamWriteRange)
            long invBefore = cpu.Tier3Coordinator.RamPageInvalidationCount;
            cpu.Bus.Write32(0x80020010U, 0x24420017U);
            Assert.True(cpu.Tier3Coordinator.RamPageInvalidationCount > invBefore);
            cpu.R[2] = 0U;
            cpu.R[31] = 0x80010840U;
            cpu.Call(0x80020000U);
            Assert.Equal(100U, cpu.R[2]);
            Assert.Equal(0L, cpu.InterpreterFallbackCount);
        }

        // 2. staticAotOnly: compiled entry succeeds, unlifted entry throws InvalidOperationException
        using (var strictCpu = new PsxCpu(new RecompSettings { ExecutionTierMode = ReStationExecutionTierModes.StaticAotOnly }, _tempRoot, new SoftwareCpuRenderBackend(), logger))
        {
            strictCpu.RegisterModule(compiledMod);
            strictCpu.DispatchFunction(0x80010000U);
            Assert.Equal(77U, strictCpu.R[2]);

            strictCpu.Bus.Write32(0x80030000U, 0x03E00008U);
            strictCpu.Bus.Write32(0x80030004U, 0x00000000U);
            Assert.Throws<InvalidOperationException>(() => strictCpu.Call(0x80030000U));
            strictCpu.R[31] = 0x80030020U;
            Assert.Throws<InvalidOperationException>(() => strictCpu.DispatchFunction(0x80030000U));
        }

        // 3. interpreterOracle: quarantined strictly to interpreterOracle with [WARN]
        using (var oracleCpu = new PsxCpu(new RecompSettings { ExecutionTierMode = ReStationExecutionTierModes.InterpreterOracle }, _tempRoot, new SoftwareCpuRenderBackend(), logger))
        {
            oracleCpu.Bus.Write32(0x80030000U, 0x03E00008U);
            oracleCpu.Bus.Write32(0x80030004U, 0x2402002AU); // addiu $v0, $zero, 42
            oracleCpu.DispatchFunction(0x80030000U);
            Assert.Equal(42U, oracleCpu.R[2]);
            Assert.True(oracleCpu.InterpreterFallbackCount > 0L);
            Assert.Contains(logger.GetRecentLines(), l => l.Contains("[WARN]", StringComparison.Ordinal) && l.Contains("interpreterOracle", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void JitBringupRecorder_IsPureStaticReady_RejectsActiveJitBringupAndAcceptsZeroFallbackStaticRun()
    {
        Directory.CreateDirectory(_tempRoot);
        string profilePath = Path.Combine(_tempRoot, "bringup", "bringup.profile.json");
        using var cpu = new PsxCpu(new RecompSettings { ExecutionTierMode = ReStationExecutionTierModes.JitBringup }, _tempRoot, new SoftwareCpuRenderBackend());
        var recorder = new JitBringupRecorder("pure-check", ReStationExecutionTierModes.JitBringup, bringupProfilePath: profilePath);
        cpu.BringupRecorder = recorder;

        cpu.Bus.Write32(0x80025000U, 0x03E00008U);
        cpu.Bus.Write32(0x80025004U, 0x24020007U);
        cpu.DispatchFunction(0x80025000U);

        // Even when WriteProfileAtomic is called without explicit coordinator arg (FrameLoop pattern), attached Tier3 counts are included.
        string activeJsonPath = recorder.WriteProfileAtomic(compiledDispatchCount: cpu.CompiledDispatchCount, interpreterFallbackCount: cpu.InterpreterFallbackCount);
        string activeJson = File.ReadAllText(activeJsonPath);
        using (JsonDocument doc = JsonDocument.Parse(activeJson))
        {
            Assert.False(doc.RootElement.GetProperty("isPureStaticReady").GetBoolean());
            Assert.True(doc.RootElement.GetProperty("jitDispatchCount").GetInt64() + doc.RootElement.GetProperty("threadedIrDispatchCount").GetInt64() > 0L);
            Assert.False(doc.RootElement.TryGetProperty("isBringupCaptureClean", out _));
            Assert.False(doc.RootElement.TryGetProperty("isR4PromotionReady", out _));
        }
        Assert.False(recorder.IsPureStaticReady);
        Assert.True(recorder.IsBringupCaptureClean);

        // Active --jit-bringup must stay false even when compiled dispatches ran with zero dynamic JIT/IR fallbacks
        var bringupCoord = new Tier3ExecutionCoordinator(new RamPageEpochTable(), ReStationExecutionTierModes.JitBringup);
        bringupCoord.RecordCompiledDispatch(5L);
        var bringupRec = new JitBringupRecorder("bringup-pass", ReStationExecutionTierModes.JitBringup, bringupProfilePath: Path.Combine(_tempRoot, "bringup-zero-fallback", "bringup.profile.json"));
        BringupProfileDocument bringupDoc = bringupRec.BuildSnapshot(bringupCoord);
        Assert.False(bringupDoc.IsPureStaticReady);
        Assert.False(bringupRec.IsPureStaticReady);

        // Pure static run: CompiledDispatchCount > 0 && zero JIT/IR/interpreter && !spinTripped
        var pureCoord = new Tier3ExecutionCoordinator(new RamPageEpochTable(), ReStationExecutionTierModes.StaticAotOnly);
        pureCoord.RecordCompiledDispatch(5L);
        var pureRec = new JitBringupRecorder("pure-pass", ReStationExecutionTierModes.StaticAotOnly, bringupProfilePath: Path.Combine(_tempRoot, "pure", "bringup.profile.json"));
        BringupProfileDocument pureDoc = pureRec.BuildSnapshot(pureCoord);
        Assert.True(pureDoc.IsPureStaticReady);
        Assert.True(pureRec.IsPureStaticReady);
    }

    [Fact]
    public void JitBringupRecorder_LegacyIsR4PromotionReady_BackwardCompatAndDeserializeFallback()
    {
#pragma warning disable CS0618
        var legacyDoc = JsonSerializer.Deserialize<BringupProfileDocument>(
            """{"moduleId":"legacy","discoveredFunctionCount":2,"isR4PromotionReady":true}""",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.True(legacyDoc.IsPureStaticReady);
        Assert.True(legacyDoc.IsR4PromotionReady);

        var canonicalWins = JsonSerializer.Deserialize<BringupProfileDocument>(
            """{"moduleId":"new","isPureStaticReady":false,"isR4PromotionReady":true}""",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.False(canonicalWins.IsPureStaticReady);
        Assert.False(canonicalWins.IsR4PromotionReady);

        var recorder = new JitBringupRecorder("compat");
        Assert.Equal(recorder.IsPureStaticReady, recorder.IsR4PromotionReady);
#pragma warning restore CS0618
    }

    private sealed class InlineCompiledModule(uint textStart, uint textEnd, Action<IReStationCpu> body) : IRecompModule, reStation.Core.Aot.IRecompCompiledEntryLookup
    {
        public int ExeId => 0;
        public string ExeName => "INLINE.EXE";
        public uint TextStart => textStart;
        public uint TextEnd => textEnd;
        public bool ContainsCompiledEntry(uint pc) => pc == textStart;
        public bool TryExecuteFunction(IReStationCpu cpu, uint pc)
        {
            if (pc != textStart) return false;
            body(cpu);
            return true;
        }
    }
}
