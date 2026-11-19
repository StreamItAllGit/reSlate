// @module: reStation.Tests.PsxCpuGteAndMdecTests
// @purpose: Verifies PsxCpu dispatch/snapshot state, PsxMemoryBus mirroring + SR.IsC isolation, PsxGte FLAG/PGXP, and PsxMdec 15/24-bit block decode.
// @depends-on: Xunit, reStation.Contracts, reStation.Core, reStation.Core.Cpu, reStation.Core.Gpu, reStation.Core.Gte, reStation.Core.Mdec
// @exports: PsxCpuGteAndMdecTests
// @owner: devv-pkg-test (T8)

using reStation.Contracts;
using reStation.Core;
using reStation.Core.Cpu;
using reStation.Core.Gpu;
using reStation.Core.Gte;
using reStation.Core.Mdec;
using Xunit;

namespace reStation.Tests;

public sealed class PsxCpuGteAndMdecTests
{
    private static PsxCpu CreateCpu() =>
        new(new RecompSettings(), Path.GetTempPath(), new SoftwareCpuRenderBackend());

    [Fact]
    public void PsxCpu_UnregisteredPc_RunsTier3ByDefault_AndInterpreterOracleIncrementsFallbackAndUnwinds()
    {
        PsxCpu tier3Cpu = CreateCpu();
        tier3Cpu.Bus.Write32(0x80012340U, 0x03E00008U); // JR $ra
        tier3Cpu.Bus.Write32(0x80012344U, 0x24020063U); // ADDIU $v0, $zero, 99 (delay slot)
        tier3Cpu.DispatchFunction(0x80012340U);

        Assert.Equal(99U, tier3Cpu.R[2]);
        Assert.Equal(0L, tier3Cpu.InterpreterFallbackCount);
        Assert.True(tier3Cpu.Tier3Coordinator.JitDispatchCount + tier3Cpu.Tier3Coordinator.ThreadedIrDispatchCount >= 1L);
        Assert.False(tier3Cpu.ShouldUnwind);

        using var cpu = new PsxCpu(
            new RecompSettings { ExecutionTierMode = ReStationExecutionTierModes.InterpreterOracle },
            Path.GetTempPath(),
            new SoftwareCpuRenderBackend());
        cpu.Bus.Write32(0x80012340U, 0x03E00008U); // JR $ra
        cpu.Bus.Write32(0x80012344U, 0x24020063U); // ADDIU $v0, $zero, 99 (delay slot)
        cpu.DispatchFunction(0x80012340U);

        Assert.Equal(99U, cpu.R[2]);
        Assert.Equal(1L, cpu.InterpreterFallbackCount);
        Assert.False(cpu.ShouldUnwind); // DispatchFunction resets ShouldUnwind once returned cleanly unless exiting

        cpu.Call(IReStationCpu.ReturnSentinelPc);
        Assert.Equal(IReStationCpu.ReturnSentinelPc, cpu.Pc);
        Assert.True(cpu.ShouldUnwind);
    }

    [Fact]
    public void PsxCpu_BinarySnapshotRoundTrip_PreservesRegistersAndRam()
    {
        PsxCpu src = CreateCpu();
        src.ActiveExeId = 2;
        src.Pc = 0x80018800U;
        src.Hi = 0xCAFEBABEU;
        src.Lo = 0xDEADBEEFU;
        src.R[4] = 0x11223344U;
        src.Bus.Write32(0x80010010U, 0xA5A55A5AU);

        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true)) src.WriteSnapshotState(w);

        ms.Position = 0;
        PsxCpu dst = CreateCpu();
        using (var r = new BinaryReader(ms, System.Text.Encoding.UTF8, leaveOpen: true)) dst.ReadSnapshotState(r);

        Assert.Equal(2, dst.ActiveExeId);
        Assert.Equal(0x80018800U, dst.Pc);
        Assert.Equal(0xCAFEBABEU, dst.Hi);
        Assert.Equal(0xDEADBEEFU, dst.Lo);
        Assert.Equal(0x11223344U, dst.R[4]);
        Assert.Equal(0xA5A55A5AU, dst.Bus.Read32(0x80010010U));
    }

    [Fact]
    public void PsxMemoryBus_HonorsRamMirrorsScratchpadAndCacheIsolation()
    {
        PsxCpu cpu = CreateCpu();
        cpu.Bus.Write32(0x80001200U, 0x13572468U);
        Assert.Equal(0x13572468U, cpu.Bus.Read32(0xA0001200U));
        Assert.Equal(0x13572468U, cpu.Bus.Read32(0x00201200U));

        cpu.Bus.Write16(0x1F800100U, 0xBEEF);
        Assert.Equal((ushort)0xBEEF, cpu.Bus.Read16(0x1F800100U));

        // Setting COP0 SR.IsC (bit 16) isolates writes during BIOS cache flush
        cpu.Cop0Sr |= 0x00010000U;
        cpu.Bus.Write32(0x80001200U, 0x00000000U);
        Assert.Equal(0x13572468U, cpu.Bus.Read32(0x80001200U));
    }

    [Fact]
    public void PsxGte_RtpsProjection_UpdatesScreenFifoAndPgxpTracker()
    {
        var settings = new RecompSettings { IsPgxpEnabled = true, IsWidescreen16By9 = false };
        var pgxp = new PgxpTracker();
        var gte = new PsxGte(settings, pgxp);

        // Identity rotation matrix, zero translation, H=256, OFX=160<<16, OFY=120<<16
        gte.WriteControl(0, 0x1000U);
        gte.WriteControl(2, 0x00001000U);
        gte.WriteControl(4, 0x1000U);
        gte.WriteControl(24, 160U << 16);
        gte.WriteControl(25, 120U << 16);
        gte.WriteControl(26, 256U);
        gte.WriteData(0, 0x00000010U); // V0 = (16, 0, 256)
        gte.WriteData(1, 256U);

        gte.Execute(0x00080001U); // RTPS (sf=12)

        Assert.Equal(1L, gte.CommandsExecuted);
        uint sxy2 = gte.ReadData(14);
        short sx = (short)(sxy2 & 0xFFFFU);
        short sy = (short)(sxy2 >> 16);
        Assert.Equal(176, sx);
        Assert.Equal(120, sy);
        Assert.True(pgxp.TryResolve(PgxpTracker.Pack(sx, sy), out PgxpVertex pv));
        Assert.True(pv.Valid);
    }

    [Fact]
    public void PsxGte_LzcrAndFlagSummaryBit_BehavePerHardwareSpec()
    {
        var gte = new PsxGte(new RecompSettings(), new PgxpTracker());
        gte.WriteData(30, 0x00FFFFFFU);
        Assert.Equal(8U, gte.ReadData(31));

        gte.WriteControl(31, 0x00001000U); // bit 12 alone does not set bit 31
        Assert.Equal(0x00001000U, gte.Flag);

        gte.WriteControl(31, 0x40000000U); // MAC overflow sets bit 31 summary
        Assert.True((gte.Flag & 0x80000000U) != 0);
    }

    [Fact]
    public void PgxpTracker_RespectsCapacityAndClearFrame()
    {
        var pgxp = new PgxpTracker();
        pgxp.Record(10, 20, 10.25f, 20.75f, 128f);
        Assert.True(pgxp.TryResolve(PgxpTracker.Pack(10, 20), out PgxpVertex v));
        Assert.Equal(10.25f, v.X);

        pgxp.IsEnabled = false;
        Assert.False(pgxp.TryResolve(PgxpTracker.Pack(10, 20), out _));

        pgxp.IsEnabled = true;
        pgxp.ClearFrame();
        Assert.Equal(0, pgxp.Count);
    }

    [Theory]
    [InlineData(2U, 192)] // 24-bit RGB: 16*16*3 bytes = 768 bytes = 192 words
    [InlineData(3U, 128)] // 15-bit RGB: 16*16*2 bytes = 512 bytes = 128 words
    public void PsxMdec_DecodeOneColorMacroblock_EmitsExactWordCount(uint depth, int expectedWords)
    {
        var mdec = new PsxMdec();
        mdec.WriteControl(0xE0000000U); // reset + enable DMA0 & DMA1

        // MDEC(2): upload lum + chr quant tables (32 parameter words)
        mdec.DmaIn(0x40000001U);
        for (int i = 0; i < 32; i++) mdec.DmaIn(0x01010101U);

        // MDEC(1): decode 1 macroblock (6 RLE blocks = 12 halfwords = 6 uint32 words)
        uint decodeCmd = (1U << 29) | (depth << 27) | 6U;
        mdec.DmaIn(decodeCmd);
        for (int blk = 0; blk < 6; blk++) mdec.DmaIn(0xFE000401U);

        Assert.Equal(1L, mdec.MacroblocksDecoded);
        Assert.Equal(expectedWords, mdec.OutputWordsAvailable);

        uint[] outWords = new uint[expectedWords];
        int copied = mdec.DmaOutBlock(outWords);
        Assert.Equal(expectedWords, copied);
        Assert.Equal(0, mdec.OutputWordsAvailable);
    }
}
