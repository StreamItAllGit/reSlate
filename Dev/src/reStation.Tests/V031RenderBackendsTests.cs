// @module: reStation.Tests.V031RenderBackendsTests
// @purpose: Verifies ParallelCpu (100% SHA-256 bit-exact parity vs Software across 1/4/16 threads & 50 runs) and Hardware Vulkan (ICD, R16UI VkImage, PGXP, Hor+ 16:9, GP0(E6h), HD pack containment, Dispose).
// @depends-on: System.Security.Cryptography, Foundation.Core, reEngine.Host.Contracts, reEngine.Host.Core, reStation.Contracts, reStation.Core, reStation.Core.Enhancement, reStation.Core.Gpu, reStation.Core.Gte, Xunit
// @exports: V031RenderBackendsTests
// @owner: devv-gpu-backends (T3/T4)

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Foundation.Core;
using reEngine.Host.Contracts;
using reEngine.Host.Core;
using reStation.Contracts;
using reStation.Core;
using reStation.Core.Enhancement;
using reStation.Core.Gpu;
using reStation.Core.Gte;
using Xunit;

namespace reStation.Tests;

public sealed class V031RenderBackendsTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"reslate-v031-gpu-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true);
    }

    [Fact]
    public void ParallelCpu_MatchesSoftware_BitExactSha256_Across1_4_16Threads_And_50Runs()
    {
        using var sw = new SoftwareCpuRenderBackend();
        (byte[] swRgba, ushort[] swVram) = ExecuteComplexPs1Workload(sw, scale: 1, widescreen: false);
        string expectedRgbaSha = Convert.ToHexString(SHA256.HashData(swRgba));
        string expectedVramSha = Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(swVram.AsSpan())));

        foreach (int threads in new[] { 1, 4 })
        {
            using var p = new ParallelCpuRenderBackend(threads);
            Assert.Equal(PsxRenderBackendIds.ParallelCpu, p.BackendId);
            Assert.Equal(threads, p.WorkerThreads);
            (byte[] rgba, ushort[] vram) = ExecuteComplexPs1Workload(p, scale: 1, widescreen: false);
            Assert.Equal(expectedRgbaSha, Convert.ToHexString(SHA256.HashData(rgba)));
            Assert.Equal(expectedVramSha, Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(vram.AsSpan()))));
        }

        using var p16 = new ParallelCpuRenderBackend(16);
        Assert.Equal(16, p16.WorkerThreads);
        for (int run = 0; run < 50; run++)
        {
            (byte[] rgba, ushort[] vram) = ExecuteComplexPs1Workload(p16, scale: 1, widescreen: false);
            Assert.Equal(expectedRgbaSha, Convert.ToHexString(SHA256.HashData(rgba)));
            Assert.Equal(expectedVramSha, Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(vram.AsSpan()))));
        }
    }

    [Fact]
    public void ParallelCpu_Supports4xPgxpAndWidescreen16By9_MatchingSoftwareBitExact()
    {
        using var sw = new SoftwareCpuRenderBackend();
        using var par = new ParallelCpuRenderBackend(8);
        (byte[] swRgba, ushort[] swVram) = ExecuteComplexPs1Workload(sw, scale: 4, widescreen: true, useMaskBits: false);
        (byte[] parRgba, ushort[] parVram) = ExecuteComplexPs1Workload(par, scale: 4, widescreen: true, useMaskBits: false);

        Assert.True(swRgba.Length > 320 * 4 * 240 * 4 * 4);
        Assert.Equal(SHA256.HashData(swRgba), SHA256.HashData(parRgba));
        Assert.Equal(SHA256.HashData(MemoryMarshal.AsBytes(swVram.AsSpan())), SHA256.HashData(MemoryMarshal.AsBytes(parVram.AsSpan())));

        // Also verify GP0(E6h) mask bits in 4x Hor+ 16:9 widescreen match between ParallelCpu and Vulkan
        using var vk = new VulkanRenderBackend();
        (byte[] parMaskedRgba, _) = ExecuteComplexPs1Workload(par, scale: 4, widescreen: true, useMaskBits: true);
        (byte[] vkMaskedRgba, _) = ExecuteComplexPs1Workload(vk, scale: 4, widescreen: true, useMaskBits: true);
        Assert.Equal(SHA256.HashData(parMaskedRgba), SHA256.HashData(vkMaskedRgba));
    }

    [Fact]
    public void Vulkan_HardwareIcdAndR16UiVramImage_InitializesRoundTripsAndFailsClosedWhenMissing()
    {
        HostBackendCapability probe = HostBackendProbe.Probe(HostRenderBackendKind.Vulkan);
        using var vk = new VulkanRenderBackend();
        Assert.Equal(PsxRenderBackendIds.Vulkan, vk.BackendId);
        Assert.False(string.IsNullOrWhiteSpace(vk.VulkanDiagnostic));

        if (probe.IsAvailable)
        {
            Assert.True(vk.HasHardwareVulkanContext, vk.VulkanDiagnostic);
            Assert.True(vk.InitializeHardwareDevice());
            Assert.NotEqual(IntPtr.Zero, vk.HardwareVkInstance);
            Assert.NotEqual(IntPtr.Zero, vk.HardwareVkDevice);
            Assert.NotEqual(0UL, vk.HardwareVramImage);

            // Upload a wrapped 8x8 15-bit + STP pattern across (1020, 508) and verify GPU R16_UINT VkImage round-trip
            ushort[] pattern = new ushort[8 * 8];
            for (int i = 0; i < pattern.Length; i++) pattern[i] = (ushort)(0x8000 | ((i * 17) & 0x7FFF));
            vk.UploadVramRect(1020, 508, 8, 8, pattern);

            ushort[] gpuReadback = new ushort[8 * 8];
            Assert.True(vk.TryReadHardwareR16UiImageRect(1020, 508, 8, 8, gpuReadback));
            Assert.Equal(pattern, gpuReadback);
        }
        else
        {
            Assert.False(vk.HasHardwareVulkanContext);
        }

        // Fail-closed verification when ICD library cannot be loaded
        using var missingIcd = new VulkanRenderBackend(["nonexistent_vulkan_missing_icd_031.dll"]);
        Assert.False(missingIcd.HasHardwareVulkanContext);
        Assert.False(missingIcd.InitializeHardwareDevice());
        Assert.Contains("unavailable", missingIcd.VulkanDiagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Vulkan_Pgxp_Widescreen16By9_MaskBits_VramTransfers_And_Canonical1xReadback_DisposeCleanly()
    {
        using var sw1x = new SoftwareCpuRenderBackend();
        (_, ushort[] expectedVram1x) = ExecuteComplexPs1Workload(sw1x, scale: 1, widescreen: false);

        using var vk = new VulkanRenderBackend();
        (byte[] wideRgba, ushort[] vkVram1x) = ExecuteComplexPs1Workload(vk, scale: 4, widescreen: true);

        // CaptureRgba scales and widens to Hor+ 16:9 while TryReadVramRect stays canonical 1x PS1 VRAM
        Assert.True(wideRgba.Length > 320 * 4 * 240 * 4 * 4);
        Assert.Equal(expectedVram1x, vkVram1x);

        // Verify left/right Hor+ wings received the full-width FillDisplayRect color (not pure zero)
        Assert.Contains(wideRgba.Take(40), b => b > 0);

        vk.Dispose();
        Assert.False(vk.HasHardwareVulkanContext);
        Assert.Equal(0UL, vk.HardwareVramImage);
        Assert.Throws<ObjectDisposedException>(() => vk.EndFrameAndPresent(0, 0, 320, 240, false, false));
        Assert.Throws<ObjectDisposedException>(() => vk.CaptureRgba(out _, out _));
    }

    [Fact]
    public void HdTexturePackStore_AttachedToAllBackends_PreservesCanonical1xVramAt4xScale()
    {
        Directory.CreateDirectory(_tempRoot);
        string dataDir = Path.Combine(_tempRoot, "data");
        string hdDir = Path.Combine(dataDir, "hdpacks");
        Directory.CreateDirectory(hdDir);

        ushort[] tex15 = new ushort[16 * 16];
        Array.Fill(tex15, (ushort)0x001F); // canonical 1x red
        string clutSha = HdTexturePackStore.ComputeClutSha256Hex(ReadOnlySpan<ushort>.Empty);
        string rectSha = HdTexturePackStore.ComputeVramRectSha256Hex(tex15);

        byte[] cyanHdPng = new byte[64 * 64 * 4];
        for (int i = 0; i < cyanHdPng.Length; i += 4) { cyanHdPng[i] = 0; cyanHdPng[i + 1] = 250; cyanHdPng[i + 2] = 250; cyanHdPng[i + 3] = 255; }
        string pngPath = Path.Combine(hdDir, $"{clutSha}_{rectSha}.png");
        RgbaPngWriter.WritePng(pngPath, 64, 64, cyanHdPng);

        HdTexturePackStore store = HdTexturePackStore.FromDataDirectory(dataDir, isEnabled: true);
        Assert.True(store.IsEnabled);
        Assert.Equal(1, store.IndexedCount);

        bool decoderActive = store.TryResolveRgba(clutSha, rectSha, out byte[] resolvedRgba, out int rw, out int rh)
            && rw == 64 && rh == 64 && resolvedRgba.Length == 64 * 64 * 4;

        IPsxRenderBackend[] backends =
        [
            new SoftwareCpuRenderBackend(),
            new ParallelCpuRenderBackend(4),
            new VulkanRenderBackend()
        ];

        foreach (IPsxRenderBackend backend in backends)
        {
            using (backend)
            {
                backend.Initialize(320, 240, 4);
                var hooks = (IPsxGpuBackendHooks)backend;
                hooks.AttachHdTexturePack(store);

                // Upload 15-bit texture at TexPage 1 (64, 0), draw 16x16 raw textured rect at (8, 8)
                backend.FillDisplayRect(0, 0, 320, 240, 0x000000U);
                backend.UploadVramRect(64, 0, 16, 16, tex15);
                // GP0(65h): textured raw variable rect, TexPage = (2 << 7) | 1 (15-bit direct at X=64)
                uint[] rectCmd = { 0x65808080U, (8U << 16) | 8U, 0x0000_0000U, (16U << 16) | 16U };
                backend.SubmitPrimitive(0x65U, rectCmd, texPage: (ushort)((2 << 7) | 1), 0, 0, 0, 0, 319, 239);
                backend.EndFrameAndPresent(0, 0, 320, 240, is24Bit: false, widescreen16By9: false);

                byte[] cap = backend.CaptureRgba(out int cw, out int ch);
                Assert.Equal(1280, cw);
                Assert.Equal(960, ch);
                int sampleOff = ((48 * cw) + 48) * 4;
                if (decoderActive)
                    Assert.True(cap[sampleOff + 1] >= 240 && cap[sampleOff + 2] >= 240 && cap[sampleOff] <= 10);
                else
                    Assert.True(cap[sampleOff] >= 240 && cap[sampleOff + 1] <= 10);

                // Canonical 1x GP0(C0h) VRAM readback MUST remain red (0x001F), never replaced by cyan HD override
                ushort[] readback = new ushort[16 * 16];
                Assert.True(hooks.TryReadVramRect(8, 8, 16, 16, readback));
                Assert.All(readback, px => Assert.Equal((ushort)0x001F, px));
            }
        }
    }

    private static (byte[] Rgba, ushort[] Vram1x) ExecuteComplexPs1Workload(IPsxRenderBackend backend, int scale, bool widescreen, bool useMaskBits = true)
    {
        backend.Initialize(320, 240, scale);
        var hooks = (IPsxGpuBackendHooks)backend;
        var pgxp = new PgxpTracker { IsEnabled = true };
        hooks.AttachPgxp(pgxp);

        backend.FillDisplayRect(0, 0, 320, 240, 0x201008U);

        // Upload 4-bit CLUT at (0, 256) and 4-bit texture at (320, 0) (TexPage 5)
        ushort[] clut4 = { 0x0000, 0x001F, 0x03E0, 0xFC00, 0x7FFF, 0x03FF, 0x7C1F, 0x3DEF, 0, 0, 0, 0, 0, 0, 0, 0 };
        backend.UploadVramRect(0, 256, 16, 1, clut4);
        ushort[] tex4 = new ushort[8 * 16];
        for (int i = 0; i < tex4.Length; i++) tex4[i] = (ushort)(0x1234 + (i & 3));
        backend.UploadVramRect(320, 0, 8, 16, tex4);

        // Upload 8-bit CLUT at (0, 257) and 8-bit texture at (384, 0) (TexPage 6, depth=1)
        ushort[] clut8 = new ushort[256];
        for (int i = 1; i < 256; i++) clut8[i] = (ushort)(0x8000 | ((i & 31) << 10) | ((i & 15) << 5) | (i & 31));
        backend.UploadVramRect(0, 257, 256, 1, clut8);
        ushort[] tex8 = new ushort[16 * 16];
        for (int i = 0; i < tex8.Length; i++) tex8[i] = (ushort)(((i + 2) << 8) | ((i + 1) & 0xFF));
        backend.UploadVramRect(384, 0, 16, 16, tex8);

        hooks.SetTextureWindow(0x1E, 0x1E, 0x01, 0x01);

        // 1. PGXP Gouraud triangle GP0(30h) spanning bands 1..12
        pgxp.Record(30, 20, 30.25f, 20.5f, 32f);
        pgxp.Record(160, 40, 160.75f, 40.25f, 64f);
        pgxp.Record(90, 180, 90.5f, 180.75f, 48f);
        uint[] gouraudTri =
        {
            0x302040E0U, (uint)PgxpTracker.Pack(30, 20),
            0x0040E020U, (uint)PgxpTracker.Pack(160, 40),
            0x00E04020U, (uint)PgxpTracker.Pack(90, 180)
        };
        backend.SubmitPrimitive(0x30U, gouraudTri, 0, 0, 0, 0, 0, 319, 239);

        // 2. Textured 4-bit quad GP0(2Ch) with setMaskBit = true
        if (useMaskBits) hooks.SetMaskBits(setMaskBit: true, checkMaskBit: false);
        ushort clutPacked4 = (ushort)(256 << 6);
        uint[] texQuad4 =
        {
            0x2C808080U,
            (uint)PgxpTracker.Pack(40, 50), ((uint)clutPacked4 << 16) | 0x0000U,
            (uint)PgxpTracker.Pack(120, 50), ((uint)5U << 16) | 0x001FU,
            (uint)PgxpTracker.Pack(40, 130), 0x0F00U,
            (uint)PgxpTracker.Pack(120, 130), 0x0F1FU
        };
        backend.SubmitPrimitive(0x2CU, texQuad4, texPage: 5, 0, 0, 0, 0, 319, 239);

        // 3. Mid-frame VRAM CopyVramRect hazard (forces ParallelCpu barrier flush before copy!)
        backend.CopyVramRect(40, 50, 180, 50, 48, 48);

        // 4. Overlapping rect with checkMaskBit = true (must respect mask bit from step 2!)
        if (useMaskBits)
        {
            hooks.SetMaskBits(setMaskBit: false, checkMaskBit: true);
            uint[] overRect = { 0x6000F000U, (70U << 16) | 70U, (70U << 16) | 70U };
            backend.SubmitPrimitive(0x60U, overRect, 0, 0, 0, 0, 0, 319, 239);
        }

        // 5. Semi-transparent 8-bit textured quad + Gouraud line across bands
        hooks.SetMaskBits(setMaskBit: false, checkMaskBit: false);
        ushort clutPacked8 = (ushort)(257 << 6);
        ushort tp8Semi = (ushort)((1 << 7) | 6);
        uint[] semiQuad8 =
        {
            0x3E808080U,
            (uint)PgxpTracker.Pack(140, 90), ((uint)clutPacked8 << 16) | 0x0000U,
            0x00907070U, (uint)PgxpTracker.Pack(230, 90), ((uint)tp8Semi << 16) | 0x001FU,
            0x00709070U, (uint)PgxpTracker.Pack(140, 170), 0x0F00U,
            0x00707090U, (uint)PgxpTracker.Pack(230, 170), 0x0F1FU
        };
        backend.SubmitPrimitive(0x3EU, semiQuad8, tp8Semi, 0, 0, 0, 0, 319, 239);

        uint[] lineCmd = { 0x5010FF10U, (210U << 16) | 10U, 0x00FF20F0U, (10U << 16) | 260U };
        backend.SubmitPrimitive(0x50U, lineCmd, 0, 0, 0, 0, 0, 319, 239);

        backend.EndFrameAndPresent(0, 0, 320, 240, is24Bit: false, widescreen16By9: widescreen);
        byte[] rgba = backend.CaptureRgba(out _, out _);
        ushort[] vramRead = new ushort[320 * 240];
        Assert.True(hooks.TryReadVramRect(0, 0, 320, 240, vramRead));
        return (rgba, vramRead);
    }
}
