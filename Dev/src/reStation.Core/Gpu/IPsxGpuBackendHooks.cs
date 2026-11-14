// @module: reStation.Core.Gpu.IPsxGpuBackendHooks
// @purpose: Optional additive backend hooks PsxGpu drives when present: PGXP tracker binding, GP0(E2h) texture window, VRAM readback for GP0(C0h) parity.
// @depends-on: reStation.Core.Gte
// @exports: IPsxGpuBackendHooks
// @owner: devv-gpu-gl (T4) — additive companion to the T1-locked reStation.Contracts.IPsxRenderBackend (contract untouched).

using reStation.Core.Gte;

namespace reStation.Core.Gpu;

/// <summary>
/// Engine-internal extension surface implemented by <see cref="SoftwareCpuRenderBackend"/> and <see cref="OpenGl33RenderBackend"/>.
/// <see cref="PsxGpu"/> discovers it via a type test so third-party <c>IPsxRenderBackend</c> implementations keep working without it.
/// </summary>
public interface IPsxGpuBackendHooks
{
    /// <summary>Binds the per-frame PGXP vertex cache (null disables sub-pixel/perspective resolution).</summary>
    void AttachPgxp(PgxpTracker? tracker);

    /// <summary>GP0(E2h) texture window in 8-texel units (mask/offset are 5-bit fields).</summary>
    void SetTextureWindow(int maskX, int maskY, int offsetX, int offsetY);

    /// <summary>Copies native 15-bit VRAM (including rendered pixels) into <paramref name="destination"/>; false when unsupported.</summary>
    bool TryReadVramRect(int x, int y, int w, int h, Span<ushort> destination);
    void RenderUiOverlay(ReadOnlySpan<byte> rgbaPixels, int width, int height, bool uploadPixels);
}
