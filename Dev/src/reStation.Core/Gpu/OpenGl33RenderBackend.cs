// @module: reStation.Core.Gpu.OpenGl33RenderBackend
// @purpose: Hardware OpenGL 3.3 Core IPsxRenderBackend ("opengl"): R16UI VRAM atlas + scaled RGBA8 FBO, PGXP, CLUT, semi-transparency.
// @depends-on: reStation.Contracts, reStation.Core.Gpu.OpenGl33Bindings
// @exports: OpenGl33RenderBackend
// @owner: devv-gpu-gl (T4) — T1 skeleton; constructor + IPsxRenderBackend surface LOCKED; GL resource/shader pipeline lands in T4.

using reStation.Contracts;

namespace reStation.Core.Gpu;

public sealed class OpenGl33RenderBackend : IPsxRenderBackend
{
    private int _scale = 1;
    private int _outW = 320, _outH = 240;
    private bool _disposed;

    /// <summary>Requires bindings already initialized against a current GL 3.3 Core context.</summary>
    public OpenGl33RenderBackend(OpenGl33Bindings gl)
    {
        Gl = gl ?? throw new ArgumentNullException(nameof(gl));
        if (!gl.IsInitialized) throw new InvalidOperationException("OpenGl33Bindings must be initialized before creating the OpenGL backend.");
    }

    public OpenGl33Bindings Gl { get; }
    public string BackendId => PsxRenderBackendIds.OpenGl;
    public int ScaleFactor { get => _scale; set => _scale = value <= 1 ? 1 : value <= 2 ? 2 : value <= 4 ? 4 : 8; }
    public int SurfaceWidth { get; private set; } = 320;
    public int SurfaceHeight { get; private set; } = 240;
    public long PrimitivesSubmitted { get; private set; }

    public void Initialize(int initialWidth, int initialHeight, int scaleFactor) => Resize(initialWidth, initialHeight, scaleFactor);

    public void Resize(int width, int height, int scaleFactor)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SurfaceWidth = Math.Max(1, width);
        SurfaceHeight = Math.Max(1, height);
        ScaleFactor = scaleFactor;
    }

    public void BeginFrame(int width, int height) => Resize(width, height, _scale);

    public void PresentRgba(int width, int height, ReadOnlySpan<byte> rgba)
    {
        if (width <= 0 || height <= 0 || rgba.Length < width * height * 4) throw new ArgumentException("RGBA buffer too small.", nameof(rgba));
        _outW = width;
        _outH = height;
    }

    public void UploadVramRect(int x, int y, int w, int h, ReadOnlySpan<ushort> pixels15) => ObjectDisposedException.ThrowIf(_disposed, this);

    public void CopyVramRect(int srcX, int srcY, int dstX, int dstY, int w, int h) => ObjectDisposedException.ThrowIf(_disposed, this);

    public void FillDisplayRect(int x, int y, int w, int h, uint bgr24) => ObjectDisposedException.ThrowIf(_disposed, this);

    public void SubmitPrimitive(uint gp0Opcode, ReadOnlySpan<uint> words, ushort texPage, int drawOffsetX, int drawOffsetY,
        int clipLeft, int clipTop, int clipRight, int clipBottom) => PrimitivesSubmitted++;

    public void EndFrameAndPresent(int dispX, int dispY, int dispW, int dispH, bool is24Bit, bool widescreen16By9)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _outW = Math.Max(1, dispW) * (is24Bit ? 1 : _scale);
        _outH = Math.Max(1, dispH) * (is24Bit ? 1 : _scale);
    }

    public byte[] CaptureRgba(out int width, out int height)
    {
        width = _outW;
        height = _outH;
        return new byte[_outW * _outH * 4];
    }

    public void Dispose() => _disposed = true;
}
