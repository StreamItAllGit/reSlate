// @module: reStation.Core.Gpu.IPsxGpuBackendHooks
// @purpose: Optional additive backend hooks PsxGpu drives when present: PGXP tracker binding, GP0(E2h) texture window, VRAM readback for GP0(C0h) parity.
// @depends-on: reStation.Core.Gte
// @exports: IPsxGpuBackendHooks
// @owner: devv-gpu-gl (T4) — additive companion to the T1-locked reStation.Contracts.IPsxRenderBackend (contract untouched).

using reStation.Core.Enhancement;
using reStation.Core.Gte;

namespace reStation.Core.Gpu;

/// <summary>
/// Engine-internal extension surface implemented by <see cref="SoftwareCpuRenderBackend"/>, <see cref="OpenGl33RenderBackend"/>,
/// <see cref="VulkanRenderBackend"/>, and <see cref="ParallelCpuRenderBackend"/>.
/// <see cref="PsxGpu"/> discovers it via a type test so third-party <c>IPsxRenderBackend</c> implementations keep working without it.
/// </summary>
public interface IPsxGpuBackendHooks
{
    /// <summary>Binds the per-frame PGXP vertex cache (null disables sub-pixel/perspective resolution).</summary>
    void AttachPgxp(PgxpTracker? tracker);

    /// <summary>GP0(E2h) texture window in 8-texel units (mask/offset are 5-bit fields).</summary>
    void SetTextureWindow(int maskX, int maskY, int offsetX, int offsetY);

    /// <summary>GP0(E6h) mask-bit settings (set MSB on draw / check existing MSB before draw).</summary>
    void SetMaskBits(bool setMaskBit, bool checkMaskBit) { }

    /// <summary>Binds an optional path-contained <c>hdpacks\</c> texture override store without altering canonical 1x VRAM.</summary>
    void AttachHdTexturePack(HdTexturePackStore? packStore) { }

    /// <summary>Copies native 15-bit VRAM (including rendered pixels) into <paramref name="destination"/>; false when unsupported.</summary>
    bool TryReadVramRect(int x, int y, int w, int h, Span<ushort> destination);
    void RenderUiOverlay(ReadOnlySpan<byte> rgbaPixels, int width, int height, bool uploadPixels);

    void RenderUiOverlay(ReadOnlySpan<byte> rgbaPixels, int width, int height, int dstX, int dstY, int canvasW, int canvasH, bool uploadPixels)
        => RenderUiOverlay(rgbaPixels, width, height, uploadPixels);

    /// <summary>Re-blits the last presented display image onto the active default framebuffer (used by inter-VBlank extra swaps).</summary>
    void ReblitPresentedDisplay() { }
}
