// @module: reStation.Tests.RenderBackendParityTests
// @purpose: Verifies IPsxRenderBackend / SoftwareCpuRenderBackend / OpenGl33RenderBackend parity, PsxGpu VRAM + GP0 state + VBlank cadence, and PsxPrimitiveDecoder.
// @depends-on: Xunit, reStation.Contracts, reStation.Core, reStation.Core.Gpu, reStation.Core.Gte
// @exports: RenderBackendParityTests
// @owner: devv-pkg-test (T8)

using reStation.Contracts;
using reStation.Core;
using reStation.Core.Enhancement;
using reStation.Core.Gpu;
using reStation.Core.Gte;
using Xunit;

namespace reStation.Tests;

public sealed class RenderBackendParityTests
{
    [Fact]
    public void SoftwareBackend_VramFillCopyAndEndFrame_ProducesExpectedRgbaPixels()
    {
        var backend = new SoftwareCpuRenderBackend();
        backend.Initialize(320, 240, 1);

        // BGR24: R=0xF8, G=0x00, B=0x00 -> pure red 15-bit in VRAM, copied and displayed
        backend.FillDisplayRect(0, 0, 16, 16, 0x0000F8U);
        backend.CopyVramRect(0, 0, 16, 0, 16, 16);
        backend.EndFrameAndPresent(16, 0, 320, 240, is24Bit: false, widescreen16By9: false);

        byte[] rgba = backend.CaptureRgba(out int w, out int h);
        Assert.Equal(320, w);
        Assert.Equal(240, h);
        Assert.True(rgba[0] >= 240 && rgba[1] <= 8 && rgba[2] <= 8 && rgba[3] == 255);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void SoftwareBackend_ResolutionScale_NormalizesAllowedValuesAndBounds(int scale)
    {
        var backend = new SoftwareCpuRenderBackend();
        backend.Initialize(320, 240, scale);
        Assert.Equal(scale, backend.ScaleFactor);

        backend.Resize(640, 480, scale);
        Assert.Equal(640, backend.SurfaceWidth);
        Assert.Equal(480, backend.SurfaceHeight);
    }

    [Fact]
    public void OpenGlBackend_Constructor_FailsClosedWhenBindingsUninitialized()
    {
        var uninitializedBindings = new OpenGl33Bindings();
        Assert.False(uninitializedBindings.IsInitialized);
        Assert.Throws<InvalidOperationException>(() => new OpenGl33RenderBackend(uninitializedBindings));
    }

    [Fact]
    public void PsxGpu_Gp0AndGp1Registers_DecodeDisplayModeClipAndOffset()
    {
        var gpu = new PsxGpu(new RecompSettings(), new PgxpTracker(), new SoftwareCpuRenderBackend());

        gpu.WriteGp1(0x03000000U); // Enable display
        gpu.WriteGp1(0x05000040U); // DisplayX = 64, DisplayY = 0
        gpu.WriteGp1(0x08000014U); // 256px wide, 480px interlaced, 24-bit
        Assert.False(gpu.DisplayDisabled);
        Assert.Equal(64, gpu.DisplayX);
        Assert.Equal(256, gpu.DisplayWidth);
        Assert.Equal(480, gpu.DisplayHeight);
        Assert.True(gpu.Is24BitDisplay);

        gpu.WriteGp0(0xE1000015U); // TexPage
        gpu.WriteGp0(0xE3000000U | (10U << 10) | 20U); // DrawArea Top=10, Left=20
        gpu.WriteGp0(0xE4000000U | (200U << 10) | 300U); // DrawArea Bottom=200, Right=300
        gpu.WriteGp0(0xE5000000U | (5U << 11) | 7U); // DrawOffset X=7, Y=5
        Assert.Equal(20, gpu.DrawAreaLeft);
        Assert.Equal(10, gpu.DrawAreaTop);
        Assert.Equal(300, gpu.DrawAreaRight);
        Assert.Equal(200, gpu.DrawAreaBottom);
        Assert.Equal(7, gpu.DrawOffsetX);
        Assert.Equal(5, gpu.DrawOffsetY);
    }

    [Fact]
    public void PsxGpu_TickTo_FiresVBlankExactlyOncePer564480Cycles()
    {
        var gpu = new PsxGpu(new RecompSettings(), new PgxpTracker(), new SoftwareCpuRenderBackend());

        gpu.TickTo(PsxGpu.CyclesPerVBlank - 1, out bool beforeEdge);
        Assert.False(beforeEdge);
        Assert.Equal(0L, gpu.FramesPresented);

        gpu.TickTo(PsxGpu.CyclesPerVBlank, out bool atEdge);
        Assert.True(atEdge);
        gpu.PresentFrame();
        Assert.Equal(1L, gpu.FramesPresented);
    }

    [Fact]
    public void PsxPrimitiveDecoder_ShadedTexturedQuadAndPgxp_DecodesExactVertexStream()
    {
        var pgxp = new PgxpTracker();
        pgxp.Record(10, 20, 10.5f, 20.25f, 64.0f);
        pgxp.Record(50, 20, 50.0f, 20.0f, 64.0f);
        pgxp.Record(10, 60, 10.0f, 60.0f, 64.0f);
        pgxp.Record(50, 60, 50.0f, 60.0f, 64.0f);

        // GP0(0x3C): shaded, textured quad -> 1 cmd + 3 extra colors + 4*2 (xy+uv) = 12 words
        uint opcode = 0x3C;
        Assert.Equal(12, PsxPrimitiveDecoder.ExpectedWords(opcode));

        uint[] words =
        {
            0x3C102030U,
            (uint)PgxpTracker.Pack(10, 20), 0x0001_0408U, // v0 + CLUT(1) + UV(8,4)
            0x00405060U, (uint)PgxpTracker.Pack(50, 20), 0x0085_0420U, // v1 + TexPage(0x85)
            0x00708090U, (uint)PgxpTracker.Pack(10, 60), 0x0000_2008U, // v2
            0x00A0B0C0U, (uint)PgxpTracker.Pack(50, 60), 0x0000_2020U  // v3
        };
        Span<PsxVertex> verts = stackalloc PsxVertex[4];
        int count = PsxPrimitiveDecoder.DecodePolygon(opcode, words, texPage: 0, offsetX: 2, offsetY: 3, pgxp, verts, out PsxDrawState state);

        Assert.Equal(4, count);
        Assert.True(state.Textured);
        Assert.Equal(12.5f, verts[0].X);
        Assert.Equal(23.25f, verts[0].Y);
        Assert.Equal(64.0f, verts[0].W);
    }

    [Theory]
    [InlineData(PsxRenderBackendIds.Software)]
    [InlineData(PsxRenderBackendIds.ParallelCpu)]
    [InlineData(PsxRenderBackendIds.Vulkan)]
    public void PsxGpu_Gp0E6MaskBits_ForwardsToBackendAndProtectsMaskedPixels(string backendId)
    {
        using IPsxRenderBackend backend = backendId switch
        {
            PsxRenderBackendIds.ParallelCpu => new ParallelCpuRenderBackend(4),
            PsxRenderBackendIds.Vulkan => new VulkanRenderBackend(),
            _ => new SoftwareCpuRenderBackend()
        };
        backend.Initialize(320, 240, 1);
        var gpu = new PsxGpu(new RecompSettings(), new PgxpTracker(), backend);

        // Clear draw area and set GP0(E6h) setMaskBit=1, checkMaskBit=0
        gpu.WriteGp0(0x02000000U); gpu.WriteGp0(0x00000000U); gpu.WriteGp0((64U << 16) | 64U);
        gpu.WriteGp0(0xE6000001U);
        Assert.True(gpu.MaskSetBit);
        Assert.False(gpu.MaskCheckBit);
        Assert.NotEqual(0U, gpu.ReadGpuStat() & (1U << 11));

        // Draw red 8x8 rect at (16, 16) with mask bit set
        gpu.WriteGp0(0x600000F8U);
        gpu.WriteGp0((16U << 16) | 16U);
        gpu.WriteGp0((8U << 16) | 8U);

        // Enable checkMaskBit=1, setMaskBit=0, draw larger green 16x16 rect at (12, 12)
        gpu.WriteGp0(0xE6000002U);
        Assert.False(gpu.MaskSetBit);
        Assert.True(gpu.MaskCheckBit);
        Assert.NotEqual(0U, gpu.ReadGpuStat() & (1U << 12));

        gpu.WriteGp0(0x6000F800U);
        gpu.WriteGp0((12U << 16) | 12U);
        gpu.WriteGp0((16U << 16) | 16U);

        gpu.WriteGp1(0x03000000U);
        gpu.PresentFrame();
        byte[] rgba = backend.CaptureRgba(out int w, out int h);
        Assert.Equal(320, w);
        Assert.Equal(240, h);

        // (14, 14) should be green (unmasked); (18, 18) must remain red (protected by mask bit)
        int unmaskedOff = ((14 * w) + 14) * 4;
        int protectedOff = ((18 * w) + 18) * 4;
        Assert.True(rgba[unmaskedOff + 1] >= 240 && rgba[unmaskedOff] <= 8);
        Assert.True(rgba[protectedOff] >= 240 && rgba[protectedOff + 1] <= 8);

        Span<ushort> readback = stackalloc ushort[1];
        Assert.True(((IPsxGpuBackendHooks)backend).TryReadVramRect(18, 18, 1, 1, readback));
        Assert.NotEqual(0, readback[0] & 0x8000);
        Assert.True(((IPsxGpuBackendHooks)backend).TryReadVramRect(14, 14, 1, 1, readback));
        Assert.Equal(0, readback[0] & 0x8000);

        // Disable mask check & overwrite (18, 18) with blue
        gpu.WriteGp0(0xE6000000U);
        gpu.WriteGp0(0x60F80000U);
        gpu.WriteGp0((16U << 16) | 16U);
        gpu.WriteGp0((8U << 16) | 8U);
        Assert.True(((IPsxGpuBackendHooks)backend).TryReadVramRect(18, 18, 1, 1, readback));
        Assert.Equal(0, readback[0] & 0x8000);
        Assert.True((readback[0] >> 10) >= 28);
    }

    [Fact]
    public void PsxGpu_SetBackend_PreservesMaskBitsAndHdTexturePack()
    {
        using var sw = new SoftwareCpuRenderBackend();
        using var parallel = new ParallelCpuRenderBackend(2);
        var gpu = new PsxGpu(new RecompSettings(), new PgxpTracker(), sw);
        var pack = HdTexturePackStore.FromDataDirectory(Path.Combine(Path.GetTempPath(), $"restation-pack-nop-{Guid.NewGuid():N}"), isEnabled: false);

        gpu.AttachHdTexturePack(pack);
        gpu.WriteGp0(0xE6000003U);
        gpu.SetBackend(parallel);

        Assert.Same(pack, gpu.HdTexturePack);
        Assert.True(gpu.MaskSetBit);
        Assert.True(gpu.MaskCheckBit);

        // Protected pixel from GP0(A0h) upload with 0x8000 bit must not be overwritten after SetBackend
        gpu.WriteGp0(0xA0000000U);
        gpu.WriteGp0((10U << 16) | 10U);
        gpu.WriteGp0((2U << 16) | 2U);
        gpu.WriteGp0(0x801F_801FU);
        gpu.WriteGp0(0x801F_801FU);
        gpu.WriteGp0(0x6000FF00U);
        gpu.WriteGp0((10U << 16) | 10U);
        gpu.WriteGp0((2U << 16) | 2U);

        Span<ushort> px = stackalloc ushort[1];
        Assert.True(parallel.TryReadVramRect(10, 10, 1, 1, px));
        Assert.Equal((ushort)0x801F, px[0]);
    }
}
