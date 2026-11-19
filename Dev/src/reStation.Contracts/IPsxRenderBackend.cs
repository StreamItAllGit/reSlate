// @module: reStation.Contracts.IPsxRenderBackend
// @purpose: Locked pluggable PS1 GPU render backend contract (OpenGL 3.3 Core + deterministic SoftwareCpu oracle).
// @depends-on: Foundation.Retro
// @exports: PsxRenderBackendIds, IPsxRenderBackend

using Foundation.Retro;

namespace reStation.Contracts;

/// <summary>Canonical <c>backendId</c> values (Standard 18 settings key).</summary>
public static class PsxRenderBackendIds
{
    /// <summary>Hardware OpenGL 3.3 Core backend (default).</summary>
    public const string OpenGl = "opengl";
    /// <summary>Deterministic CPU rasterizer oracle (offscreen CPU fallback).</summary>
    public const string Software = "software";
    /// <summary>Hardware Vulkan backend (deferred to v0.3.1 shared reEngine.Host GPU layer; fails closed with ExitCode = 6 on --require-backend).</summary>
    public const string Vulkan = "vulkan";
    /// <summary>Deterministic tile/band ParallelCpu backend (deferred to v0.3.1 shared reEngine.Host GPU layer).</summary>
    public const string ParallelCpu = "parallelCpu";
}

/// <summary>
/// PS1 GPU primitive sink. <see cref="PsxRenderBackendIds"/> values identify implementations. Coordinates are native
/// PS1 VRAM pixels; backends scale internally by <see cref="IRecompRenderBackend.ScaleFactor"/> (1, 2, 4, 8).
/// </summary>
public interface IPsxRenderBackend : IRecompRenderBackend, IDisposable
{
    /// <summary>Allocates VRAM mirror and scaled output targets.</summary>
    void Initialize(int initialWidth, int initialHeight, int scaleFactor);

    /// <summary>Resizes the presentation surface and/or internal scale.</summary>
    void Resize(int width, int height, int scaleFactor);

    /// <summary>GP0 A0h: uploads 15-bit pixels into the VRAM rectangle.</summary>
    void UploadVramRect(int x, int y, int w, int h, ReadOnlySpan<ushort> pixels15);

    /// <summary>GP0 80h: VRAM-to-VRAM rectangle copy.</summary>
    void CopyVramRect(int srcX, int srcY, int dstX, int dstY, int w, int h);

    /// <summary>GP0 02h: fills a VRAM rectangle with a 24-bit BGR color (ignores draw area/mask).</summary>
    void FillDisplayRect(int x, int y, int w, int h, uint bgr24);

    /// <summary>
    /// GP0 20h-7Fh: submits a polygon/line/rectangle command (<paramref name="words"/> includes the command word)
    /// with the current texpage, drawing offset, and inclusive drawing-area clip rectangle.
    /// </summary>
    void SubmitPrimitive(uint gp0Opcode, ReadOnlySpan<uint> words, ushort texPage, int drawOffsetX, int drawOffsetY,
        int clipLeft, int clipTop, int clipRight, int clipBottom);

    /// <summary>Resolves the display area (15-bit or 24-bit MDEC mode) to the presentation surface.</summary>
    void EndFrameAndPresent(int dispX, int dispY, int dispW, int dispH, bool is24Bit, bool widescreen16By9);

    /// <summary>Reads back the last presented frame as tightly packed RGBA8 (lazy GPU readback).</summary>
    byte[] CaptureRgba(out int width, out int height);
}
