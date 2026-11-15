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
        if (Directory.Exists(_tempRoot))
        {
            try { Directory.Delete(_tempRoot, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
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
        Assert.False(doc.RootElement.GetProperty("isR4PromotionReady").GetBoolean());
        Assert.Equal(1, doc.RootElement.GetProperty("discoveredFunctionCount").GetInt32());
    }
}
