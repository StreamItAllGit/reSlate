// @module: reStation.Tests.V015EngineFixesTests
// @purpose: Verifies v0.1.5 additive fixes: digital joypad non-0x42 NACK, 4-arg BIOS B0:0x12 InitPAD, SPU XA/Main Vol & SPUCNT gating, and IOverlayRecompModule emission.
// @depends-on: Xunit, Foundation.Core, reStation.Contracts, reStation.Compiler, reStation.Core, reStation.Core.Audio, reStation.Core.Cpu, reStation.Core.Gpu, reStation.Core.Input
// @exports: V015EngineFixesTests
// @owner: devv-tests

using System.Buffers.Binary;
using System.Text;
using Foundation.Core;
using reStation.Compiler;
using reStation.Contracts;
using reStation.Core;
using reStation.Core.Audio;
using reStation.Core.Cpu;
using reStation.Core.Gpu;
using reStation.Core.Hardware;
using reStation.Core.Input;
using Xunit;

namespace reStation.Tests;

public sealed class V015EngineFixesTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"restation-v015-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true);
    }

    private PsxCpu CreateCpu()
    {
        string dataDir = Path.Combine(_tempRoot, "cpu-data");
        Directory.CreateDirectory(dataDir);
        return new PsxCpu(new RecompSettings(), dataDir, new SoftwareCpuRenderBackend());
    }

    [Fact]
    public void PsxJoypadPort_DigitalNon0x42Probe_NacksCleanlyWithoutAckOrIrq7ThenPolls0x42()
    {
        using PsxCpu cpu = CreateCpu();
        cpu.TimersAndDma.InterruptMask = 1U << PsxIrq.Controller;
        cpu.SetControllerState(0, (ushort)PsxPadButton.Cross, 0x80, 0x80, 0x80, 0x80);
        cpu.Joypad.AnalogMode = false;

        cpu.Joypad.Write16(0x1F80104AU, 0x1003); // TXEN | /JOY1 | RXIEN(bit12)
        cpu.Joypad.Write16(0x1F801040U, 0x0001); // Port 1 select -> ACK pending
        Assert.Equal((ushort)0xFF, cpu.Joypad.Read16(0x1F801040U));
        Assert.NotEqual(long.MaxValue, cpu.Joypad.NextWakeCycle);

        cpu.Joypad.TickTo(cpu.Joypad.NextWakeCycle + 1);
        Assert.NotEqual(0U, cpu.TimersAndDma.InterruptStatus & (1U << PsxIrq.Controller));
        cpu.TimersAndDma.InterruptStatus = 0;

        // DualShock config command 0x43 on a digital controller must NACK with 0xFF and no pending IRQ7 timer.
        cpu.Joypad.Write16(0x1F801040U, 0x0043);
        Assert.Equal((ushort)0xFF, cpu.Joypad.Read16(0x1F801040U));
        Assert.Equal(long.MaxValue, cpu.Joypad.NextWakeCycle);
        cpu.Joypad.TickTo(100_000);
        Assert.Equal(0U, cpu.TimersAndDma.InterruptStatus & (1U << PsxIrq.Controller));

        // Subsequent 0x01 + 0x42 read succeeds with digital ID 0x41, 0x5A, active-low Cross (0xBFFF).
        cpu.Joypad.Write16(0x1F80104AU, 0x0003);
        cpu.Joypad.Write16(0x1F801040U, 0x0001);
        Assert.Equal((ushort)0xFF, cpu.Joypad.Read16(0x1F801040U));
        cpu.Joypad.Write16(0x1F801040U, 0x0042);
        Assert.Equal((ushort)0x41, cpu.Joypad.Read16(0x1F801040U));
        cpu.Joypad.Write16(0x1F801040U, 0x0000);
        Assert.Equal((ushort)0x5A, cpu.Joypad.Read16(0x1F801040U));
        cpu.Joypad.Write16(0x1F801040U, 0x0000);
        byte lo = (byte)cpu.Joypad.Read16(0x1F801040U);
        cpu.Joypad.Write16(0x1F801040U, 0x0000);
        byte hi = (byte)cpu.Joypad.Read16(0x1F801040U);
        Assert.Equal((ushort)~PsxPadButton.Cross, (ushort)(lo | (hi << 8)));
    }

    [Fact]
    public void PsxBiosHle_InitPadFourArgSeparatesPortsAndPreservesRstxV1Compat()
    {
        using PsxCpu cpu = CreateCpu();
        const uint padDrBuf = 0x80020400U;
        const uint buf1 = 0x80021000U;
        const uint buf2 = 0x80021040U;

        // Prime _padType = 0x20000001 via PAD_init first, then call 4-arg InitPAD(buf1, 34, buf2, 34).
        cpu.R[9] = 0x15;
        cpu.R[4] = 0x20000001U;
        cpu.R[5] = padDrBuf;
        cpu.R[6] = 8;
        cpu.Bios.HandleVectorCall(0xB0U);

        cpu.SetControllerState(0, (ushort)PsxPadButton.Square, 0x80, 0x80, 0x80, 0x80);
        cpu.R[9] = 0x12;
        cpu.R[4] = buf1;
        cpu.R[5] = 34;
        cpu.R[6] = buf2;
        cpu.R[7] = 34;
        cpu.Bios.HandleVectorCall(0xB0U);
        Assert.Equal(1U, cpu.R[2]);
        Assert.Equal((ushort)0x4100, cpu.Bus.Read16(buf1));
        Assert.Equal((ushort)~PsxPadButton.Square, cpu.Bus.Read16(buf1 + 2));
        Assert.Equal((ushort)0xFFFF, cpu.Bus.Read16(buf2));
        Assert.Equal((ushort)0xFFFF, cpu.Bus.Read16(buf2 + 2));

        // Capture v1 BIOS chunk and verify restore keeps P2 buffer active + v0.1.4 short payload compat works.
        byte[] snapshot = cpu.Bios.CaptureSnapshot();
        using PsxCpu dst = CreateCpu();
        dst.Bios.RestoreSnapshot(snapshot);
        dst.Bus.Write32(buf2, 0U);
        dst.SetControllerState(0, (ushort)PsxPadButton.Triangle, 0x80, 0x80, 0x80, 0x80);
        dst.Bios.OnVBlank();
        Assert.Equal((ushort)~PsxPadButton.Triangle, dst.Bus.Read16(buf1 + 2));
        Assert.Equal((ushort)0xFFFF, dst.Bus.Read16(buf2));

        byte[] legacyV014Payload = snapshot[..^8];
        dst.Bios.RestoreSnapshot(legacyV014Payload);
        Assert.Equal(0x534F4942U, BinaryPrimitives.ReadUInt32LittleEndian(legacyV014Payload));
    }

    [Fact]
    public void PsxSpu_UnityDefaultsCdMuteVoiceOnlyMuteAndSweepIgnore_BehaveHardwareAccurately()
    {
        var spu = new PsxSpu(new RecompSettings { MasterVolume = 1.0 });
        Assert.Equal((ushort)0x3FFF, spu.Read16(0x1F801D80U));
        Assert.Equal((ushort)0x7FFF, spu.Read16(0x1F801DB0U));
        Assert.Equal((ushort)0x0001, spu.Read16(0x1F801DAEU));

        byte[] groups = BuildNonZeroXaSector();
        spu.SubmitXaSectors(groups, codingInfo: 0x01);
        spu.RenderFrames(2352);
        float[] unityDrain = new float[2352 * 2];
        spu.Output.Read(unityDrain.AsSpan());
        double unityPeak = unityDrain.Max(s => Math.Abs(s));
        Assert.True(unityPeak > 0.01);

        // Bit 0 clear (SPUCNT = 0xC000): CD Audio disabled -> XA queue drains completely cleanly with silence & zero stale ramp.
        spu.Write16(0x1F801DAAU, 0xC000);
        spu.SubmitXaSectors(groups, codingInfo: 0x01);
        spu.RenderFrames(2352);
        Assert.Equal(0, spu.PendingXaFrames);
        float[] mutedDrain = new float[2352 * 2];
        spu.Output.Read(mutedDrain.AsSpan());
        Assert.All(mutedDrain, s => Assert.Equal(0.0f, s));

        // Bit 14 clear, bit 15+0 set (SPUCNT = 0x8001): Voices muted, XA still plays.
        // Also test sweep bit-15 write on 0x180/0x182 is ignored while fixed 0x2000 scales by ~0.5.
        spu.Write16(0x1F801DAAU, 0x8001);
        spu.Write16(0x1F801D80U, 0x2000); // fixed 0.5 Left
        spu.Write16(0x1F801D82U, 0x2000); // fixed 0.5 Right
        spu.Write16(0x1F801D80U, 0x8000); // sweep flag set -> must keep latched 0.5
        spu.SubmitXaSectors(groups, codingInfo: 0x01);
        spu.RenderFrames(2352);
        float[] halfDrain = new float[2352 * 2];
        spu.Output.Read(halfDrain.AsSpan());
        double halfPeak = halfDrain.Max(s => Math.Abs(s));
        Assert.InRange(halfPeak, unityPeak * 0.45, unityPeak * 0.55);
    }

    [Fact]
    public void FunctionChunkEmitter_OverlaySpec_EmitsIOverlayRecompModuleAndGuardsInvalidSpecs()
    {
        string outDir = Path.Combine(_tempRoot, "emit");
        string validSha = new('A', 64);
        var overlaySpec = new GeneratedExeSpec(
            ExeId: 11, ExeName: "OVL_PART11.EXE", RelativePath: "A_FILE/PART11",
            FolderName: "Part11", ClassName: "Part11Overlay",
            TextStart: 0x800BBC34U, TextEnd: 0x800BBC40U, Functions: [])
        {
            IsOverlay = true,
            LoadRamAddress = 0x800BBC34U,
            ContentSizeBytes = 12,
            ContentSha256Hex = validSha
        };

        FunctionChunkEmitter.EmitModule(outDir, "TestGame.Recomp", overlaySpec, _ => 0U);
        string emitted = File.ReadAllText(Path.Combine(outDir, "Part11", "Part11Overlay.g.cs"), Encoding.UTF8);
        Assert.Contains(": IOverlayRecompModule", emitted, StringComparison.Ordinal);
        Assert.Contains("LoadRamAddress => 0x800BBC34U;", emitted, StringComparison.Ordinal);
        Assert.Contains("ContentSizeBytes => 12;", emitted, StringComparison.Ordinal);
        Assert.Contains($"ContentSha256Hex => \"{validSha}\";", emitted, StringComparison.Ordinal);

        var badShaSpec = overlaySpec with { ContentSha256Hex = "not-a-valid-sha" };
        Assert.Throws<ArgumentException>(() => FunctionChunkEmitter.EmitModule(outDir, "TestGame.Recomp", badShaSpec, _ => 0U));
        var zeroSizeSpec = overlaySpec with { ContentSizeBytes = 0 };
        Assert.Throws<ArgumentException>(() => FunctionChunkEmitter.EmitModule(outDir, "TestGame.Recomp", zeroSizeSpec, _ => 0U));
    }

    private static byte[] BuildNonZeroXaSector()
    {
        byte[] groups = new byte[XaAdpcmDecoder.SoundGroupsPerSector * XaAdpcmDecoder.SoundGroupSize];
        for (int g = 0; g < XaAdpcmDecoder.SoundGroupsPerSector; g++)
        {
            int o = g * XaAdpcmDecoder.SoundGroupSize;
            for (int u = 0; u < 8; u++) groups[o + 4 + u] = 0x10;
            for (int b = 16; b < XaAdpcmDecoder.SoundGroupSize; b++) groups[o + b] = 0x55;
        }
        return groups;
    }
}
