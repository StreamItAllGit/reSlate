// @module: reStation.Tests.RenderBackendParityTests
// @purpose: Verifies IPsxRenderBackend / SoftwareCpuRenderBackend / OpenGl33RenderBackend parity, PsxGpu VRAM + GP0 state + VBlank cadence, and PsxPrimitiveDecoder.
// @depends-on: Xunit, reStation.Contracts, reStation.Core, reStation.Core.Gpu, reStation.Core.Gte
// @exports: RenderBackendParityTests
// @owner: devv-pkg-test (T8)

using reStation.Contracts;
using reStation.Core;
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
}
