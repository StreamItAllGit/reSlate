// @module: reStation.Core.Gpu.VulkanRenderBackend
// @purpose: Hardware Vulkan PS1 GPU backend ("vulkan") with dynamic ICD/R16UI VkImage device context, 1x-8x PGXP, Hor+ 16:9, mask bits, and HD pack overrides.
// @depends-on: System, reStation.Contracts, reStation.Core.Enhancement, reStation.Core.Gte
// @exports: VulkanRenderBackend
// @owner: devv-gpu-backends (T3)

using reStation.Contracts;
using reStation.Core.Enhancement;
using reStation.Core.Gte;

namespace reStation.Core.Gpu;

/// <summary>Hardware Vulkan PS1 GPU backend backed by dynamic <c>vulkan-1</c> ICD + <c>R16_UINT</c> VRAM image and shared <c>reEngine.Host.Rendering.VulkanPresenter</c>.</summary>
public sealed partial class VulkanRenderBackend : IPsxRenderBackend, IPsxGpuBackendHooks
{
    private readonly IReadOnlyList<string>? _libraryCandidates;
    private readonly ushort[] _vram = new ushort[PsxGpu.VramWidth * PsxGpu.VramHeight];
    private uint[] _hiVram = Array.Empty<uint>();
    private byte[] _hiMask = Array.Empty<byte>();
    private uint[]? _canonical1x;
    private byte[]? _canonicalMask1x;
    private byte[] _frame = new byte[320 * 240 * 4];
    private int _frameW = 320, _frameH = 240;
    private int _scale = 1, _hiScale;
    private PgxpTracker? _pgxp;
    private HdTexturePackStore? _hdPack;
    private readonly HdTextureLookupCache _hdLookup = new();
    private VulkanDeviceContext? _deviceCtx;
    private bool _deviceAttempted, _disposed;
    private bool _vramDirty, _vramDirtyFull;
    private int _dirtyX0, _dirtyY0, _dirtyX1, _dirtyY1;

    /// <summary>Constructs the Vulkan backend and probes/initializes the hardware Vulkan ICD + R16_UINT VRAM image without throwing.</summary>
    public VulkanRenderBackend() : this(libraryCandidates: null) { }

    internal VulkanRenderBackend(IReadOnlyList<string>? libraryCandidates)
    {
        _libraryCandidates = libraryCandidates;
        _ = InitializeHardwareDevice();
    }

    /// <inheritdoc/>
    public string BackendId => PsxRenderBackendIds.Vulkan;
    /// <inheritdoc/>
    public int ScaleFactor
    {
        get => _scale;
        set { _scale = PsxVramSurfaceOps.NormalizeScale(value); EnsureHiVram(); }
    }
    public int SurfaceWidth { get; private set; } = 320;
    public int SurfaceHeight { get; private set; } = 240;
    public long PrimitivesSubmitted { get; private set; }
    /// <summary>True when a physical/logical Vulkan device context and <c>R16_UINT</c> <c>1024x512</c> <c>VkImage</c> were created on the host ICD.</summary>
    public bool HasHardwareVulkanContext { get; private set; }
    /// <summary>Human-readable hardware Vulkan initialization diagnostic.</summary>
    public string VulkanDiagnostic { get; private set; } = "Vulkan not initialized.";

    internal IntPtr HardwareVkInstance => _deviceCtx?.Instance ?? IntPtr.Zero;
    internal IntPtr HardwareVkDevice => _deviceCtx?.Device ?? IntPtr.Zero;
    internal ulong HardwareVramImage => _deviceCtx?.VramImage ?? 0UL;

    /// <summary>Attempts once (idempotently) to create the hardware Vulkan instance, logical device, and <c>R16_UINT</c> VRAM image; fails closed cleanly if unavailable.</summary>
    public bool InitializeHardwareDevice()
    {
        if (_disposed) return false;
        if (_deviceAttempted) return HasHardwareVulkanContext;
        _deviceAttempted = true;

        if (VulkanDeviceContext.TryCreate(out VulkanDeviceContext? ctx, out string diag, _libraryCandidates) && ctx is not null)
        {
            _deviceCtx = ctx;
            HasHardwareVulkanContext = true;
            VulkanDiagnostic = diag;
            return true;
        }

        HasHardwareVulkanContext = false;
        VulkanDiagnostic = diag;
        return false;
    }

    /// <inheritdoc/>
    public void Initialize(int initialWidth, int initialHeight, int scaleFactor)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _ = InitializeHardwareDevice();
        Resize(initialWidth, initialHeight, scaleFactor);
    }

    /// <inheritdoc/>
    public void BeginFrame(int width, int height) => Resize(width, height, _scale);

    /// <inheritdoc/>
    public void Resize(int width, int height, int scaleFactor)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SurfaceWidth = Math.Max(1, width);
        SurfaceHeight = Math.Max(1, height);
        ScaleFactor = scaleFactor;
    }

    /// <inheritdoc/>
    public void PresentRgba(int width, int height, ReadOnlySpan<byte> rgba)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (width <= 0 || height <= 0 || rgba.Length < width * height * 4)
            throw new ArgumentException("RGBA buffer too small.", nameof(rgba));
        _wideOps.Clear();
        _wideRenderer.ResetCache();
        _frameW = width;
        _frameH = height;
        _frame = rgba[..(width * height * 4)].ToArray();
    }

    /// <inheritdoc/>
    public void UploadVramRect(int x, int y, int w, int h, ReadOnlySpan<ushort> pixels15)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (w <= 0 || h <= 0) return;
        EnsureHiVram();
        _hdLookup.RecordUpload(x, y, w, h);
        PsxVramSurfaceOps.UploadVramRect(_vram, _hiVram, _hiMask, _hiScale, _canonical1x, _canonicalMask1x, x, y, w, h, pixels15);
        MarkVramDirty(x, y, w, h);
    }

    /// <inheritdoc/>
    public void CopyVramRect(int srcX, int srcY, int dstX, int dstY, int w, int h)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (w <= 0 || h <= 0) return;
        EnsureHiVram();
        _hdLookup.Invalidate();
        PsxVramSurfaceOps.CopyVramRect(_vram, _hiVram, _hiMask, _hiScale, _canonical1x, _canonicalMask1x, srcX, srcY, dstX, dstY, w, h);
        MarkVramDirty(dstX, dstY, w, h);
        _wideOps.Add(new WideBandOp
        {
            FrameOp = new WideFrameOp { Kind = WideFrameOpKind.CopyVram, A = srcX & 1023, B = srcY & 511, C = dstX & 1023, D = dstY & 511, E = w, F = h }
        });
    }

    /// <inheritdoc/>
    public void FillDisplayRect(int x, int y, int w, int h, uint bgr24)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (w <= 0 || h <= 0) return;
        EnsureHiVram();
        _hdLookup.Invalidate();
        PsxVramSurfaceOps.FillDisplayRect(_vram, _hiVram, _hiMask, _hiScale, _canonical1x, _canonicalMask1x, x, y, w, h, bgr24);
        MarkVramDirty(x, y, w, h);
        _wideOps.Add(new WideBandOp { FrameOp = new WideFrameOp { Kind = WideFrameOpKind.FillRect, A = x & 1023, B = y & 511, C = w, D = h, RawU32 = bgr24 } });
    }

    /// <inheritdoc/>
    public void EndFrameAndPresent(int dispX, int dispY, int dispW, int dispH, bool is24Bit, bool widescreen16By9)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureHiVram();
        SyncDirtyVramToHardwareImage();
        int dw = Math.Clamp(dispW, 1, PsxGpu.VramWidth), dh = Math.Clamp(dispH, 1, PsxGpu.VramHeight);
        int ox = dispX & 1023, oy = dispY & 511;
        int s = is24Bit ? 1 : _hiScale;
        if (widescreen16By9 && !is24Bit)
        {
            PsxWidescreenGeometry.ComputeDimensions(dw, dh, out int wideW, out int leftPad);
            if (wideW > dw)
            {
                _wideRenderer.Render(_wideOps, _hiVram, _hiMask, _vram, s, ox, oy, dw, dh, wideW, leftPad,
                    Math.Clamp(Environment.ProcessorCount, 1, 16), _frameStartWinMaskX, _frameStartWinMaskY,
                    _frameStartWinOffX, _frameStartWinOffY, ref _frame, out _frameW, out _frameH);
                ResetWideOps();
                return;
            }
        }
        ResetWideOps();
        _wideRenderer.ResetCache();
        PsxVramSurfaceOps.PresentStandardFrame(_vram, _hiVram, _hiScale, dispX, dispY, dw, dh, is24Bit, ref _frame, out _frameW, out _frameH);
    }

    /// <inheritdoc/>
    public byte[] CaptureRgba(out int width, out int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        width = _frameW;
        height = _frameH;
        return (byte[])_frame.Clone();
    }

    /// <inheritdoc/>
    public void AttachPgxp(PgxpTracker? tracker) => _pgxp = tracker;

    /// <inheritdoc/>
    public void SetTextureWindow(int maskX, int maskY, int offsetX, int offsetY)
    {
        _texWinMaskX = maskX & 0x1F; _texWinMaskY = maskY & 0x1F;
        _texWinOffX = offsetX & 0x1F; _texWinOffY = offsetY & 0x1F;
        uint packed = (uint)(_texWinMaskX | (_texWinMaskY << 5) | (_texWinOffX << 10) | (_texWinOffY << 15));
        _wideOps.Add(new WideBandOp { FrameOp = new WideFrameOp { Kind = WideFrameOpKind.SetTextureWindow, RawU32 = packed } });
    }

    /// <inheritdoc/>
    public void SetMaskBits(bool setMaskBit, bool checkMaskBit) { _maskSetBit = setMaskBit; _maskCheckBit = checkMaskBit; }

    /// <inheritdoc/>
    public void AttachHdTexturePack(HdTexturePackStore? packStore)
    {
        _hdPack = packStore;
        _hdLookup.Invalidate();
        if (packStore is { IsEnabled: true })
            PsxVramSurfaceOps.EnsureCanonical1x(_vram, _hiVram, _hiMask, _hiScale, ref _canonical1x, ref _canonicalMask1x);
    }

    /// <inheritdoc/>
    public bool TryReadVramRect(int x, int y, int w, int h, Span<ushort> destination)
    {
        if (_disposed) return false;
        EnsureHiVram();
        return PsxVramSurfaceOps.TryReadVramRect(_vram, _hiVram, _hiMask, _hiScale, _canonical1x, _canonicalMask1x, x, y, w, h, destination);
    }

    internal bool TryReadHardwareR16UiImageRect(int x, int y, int w, int h, Span<ushort> destination)
    {
        if (_disposed || _deviceCtx is not { IsValid: true } ctx) return false;
        SyncDirtyVramToHardwareImage();
        return ctx.TryReadVramRect(x, y, w, h, destination);
    }

    /// <inheritdoc/>
    public void RenderUiOverlay(ReadOnlySpan<byte> rgbaPixels, int width, int height, bool uploadPixels) { }
    /// <inheritdoc/>
    public void RenderUiOverlay(ReadOnlySpan<byte> rgbaPixels, int width, int height, int dstX, int dstY, int canvasW, int canvasH, bool uploadPixels) { }
    /// <inheritdoc/>
    public void ReblitPresentedDisplay() { }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _deviceCtx?.Dispose();
        _deviceCtx = null;
        HasHardwareVulkanContext = false;
        _wideOps.Clear();
        PrimitivesSubmitted = 0;
    }

    private void EnsureHiVram()
    {
        PsxVramSurfaceOps.EnsureHiVram(_vram, ref _hiVram, ref _hiMask, ref _hiScale, _scale);
        PsxVramSurfaceOps.EnsureCanonical1x(_vram, _hiVram, _hiMask, _hiScale, ref _canonical1x, ref _canonicalMask1x);
    }
}
