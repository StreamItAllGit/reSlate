// @module: reStation.Core.Gpu.VulkanRenderBackend
// @purpose: Phase 2A contract compile anchor for hardware Vulkan PS1 GPU backend (implemented in full by devv-gpu-backends in Phase 2B).
// @depends-on: reStation.Contracts, reStation.Core.Enhancement, reStation.Core.Gte
// @exports: VulkanRenderBackend
// @owner: devv-gpu-backends (T3)

using reStation.Contracts;
using reStation.Core.Enhancement;
using reStation.Core.Gte;

namespace reStation.Core.Gpu;

/// <summary>Hardware Vulkan PS1 GPU backend backed by shared <c>reEngine.Host.Rendering.VulkanPresenter</c>.</summary>
public sealed partial class VulkanRenderBackend : IPsxRenderBackend, IPsxGpuBackendHooks
{
    private readonly SoftwareCpuRenderBackend _inner = new();

    /// <inheritdoc/>
    public string BackendId => PsxRenderBackendIds.Vulkan;
    /// <inheritdoc/>
    public int ScaleFactor { get => _inner.ScaleFactor; set => _inner.ScaleFactor = value; }
    /// <summary>True when a physical/logical Vulkan device context was created on the host ICD.</summary>
    public bool HasHardwareVulkanContext { get; private set; }
    /// <summary>Human-readable hardware Vulkan initialization diagnostic.</summary>
    public string VulkanDiagnostic { get; private set; } = "Vulkan baseline stub (Phase 2A)";

    /// <inheritdoc/>
    public void Initialize(int initialWidth, int initialHeight, int scaleFactor) => _inner.Initialize(initialWidth, initialHeight, scaleFactor);
    /// <inheritdoc/>
    public void BeginFrame(int width, int height) => _inner.BeginFrame(width, height);
    /// <inheritdoc/>
    public void PresentRgba(int width, int height, ReadOnlySpan<byte> rgba) => _inner.PresentRgba(width, height, rgba);
    /// <inheritdoc/>
    public void Resize(int width, int height, int scaleFactor) => _inner.Resize(width, height, scaleFactor);
    /// <inheritdoc/>
    public void UploadVramRect(int x, int y, int w, int h, ReadOnlySpan<ushort> pixels15) => _inner.UploadVramRect(x, y, w, h, pixels15);
    /// <inheritdoc/>
    public void CopyVramRect(int srcX, int srcY, int dstX, int dstY, int w, int h) => _inner.CopyVramRect(srcX, srcY, dstX, dstY, w, h);
    /// <inheritdoc/>
    public void FillDisplayRect(int x, int y, int w, int h, uint bgr24) => _inner.FillDisplayRect(x, y, w, h, bgr24);
    /// <inheritdoc/>
    public void SubmitPrimitive(uint gp0Opcode, ReadOnlySpan<uint> words, ushort texPage, int drawOffsetX, int drawOffsetY, int clipLeft, int clipTop, int clipRight, int clipBottom)
        => _inner.SubmitPrimitive(gp0Opcode, words, texPage, drawOffsetX, drawOffsetY, clipLeft, clipTop, clipRight, clipBottom);
    /// <inheritdoc/>
    public void EndFrameAndPresent(int dispX, int dispY, int dispW, int dispH, bool is24Bit, bool widescreen16By9)
        => _inner.EndFrameAndPresent(dispX, dispY, dispW, dispH, is24Bit, widescreen16By9);
    /// <inheritdoc/>
    public byte[] CaptureRgba(out int width, out int height) => _inner.CaptureRgba(out width, out height);
    /// <inheritdoc/>
    public void AttachPgxp(PgxpTracker? tracker) => _inner.AttachPgxp(tracker);
    /// <inheritdoc/>
    public void SetTextureWindow(int maskX, int maskY, int offsetX, int offsetY) => _inner.SetTextureWindow(maskX, maskY, offsetX, offsetY);
    /// <inheritdoc/>
    public void SetMaskBits(bool setMaskBit, bool checkMaskBit) => ((IPsxGpuBackendHooks)_inner).SetMaskBits(setMaskBit, checkMaskBit);
    /// <inheritdoc/>
    public void AttachHdTexturePack(HdTexturePackStore? packStore) => ((IPsxGpuBackendHooks)_inner).AttachHdTexturePack(packStore);
    /// <inheritdoc/>
    public bool TryReadVramRect(int x, int y, int w, int h, Span<ushort> destination) => _inner.TryReadVramRect(x, y, w, h, destination);
    /// <inheritdoc/>
    public void RenderUiOverlay(ReadOnlySpan<byte> rgbaPixels, int width, int height, bool uploadPixels) => _inner.RenderUiOverlay(rgbaPixels, width, height, uploadPixels);
    /// <inheritdoc/>
    public void Dispose() => _inner.Dispose();
}
