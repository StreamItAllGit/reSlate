// @module: reStation.Player.HostVideoSurface
// @purpose: Owns the SDL3 window + OpenGL/Vulkan/ParallelCpu/Software presenter & IPsxRenderBackend; presents composited RGBA frames.
// @depends-on: Foundation.Core, reEngine.Host.Contracts, reEngine.Host.Core, reEngine.Host.Rendering, reStation.Contracts, reStation.Core, reStation.Core.Gpu
// @exports: HostVideoSurface

using reEngine.Host.Contracts;
using reEngine.Host.Core;
using reEngine.Host.Rendering;
using reStation.Contracts;
using reStation.Core;
using reStation.Core.Gpu;

namespace reStation.Player;

/// <summary>Video output: OpenGL 3.3 Core, Vulkan, ParallelCpu, SDL software renderer fallback, or offscreen.</summary>
internal sealed unsafe partial class HostVideoSurface : IDisposable
{
    public const int DefaultWidth = 1280, DefaultHeight = 720;
    public const int NativeWidth = 320, NativeHeight = 240;

    private IntPtr _texture;
    private IntPtr _uiTexture;
    private int _texW, _texH, _uiTexW, _uiTexH, _surfW, _surfH, _surfScale;
    private string _title = "reSlate";
    private bool _alwaysOnTop;
    private bool _disposed;

    private HostVideoSurface(IPsxRenderBackend backend) => Backend = backend;

    public IPsxRenderBackend Backend { get; private set; }
    public IHostSurfacePresenter? Presenter { get; private set; }
    public IntPtr Window { get; private set; }
    public IntPtr GlContext { get; private set; }
    public IntPtr Renderer { get; private set; }
    public bool SdlAvailable { get; private set; }
    public bool UsesOpenGl => GlContext != IntPtr.Zero;
    public bool UsesPresenter => Presenter is not null;
    public bool HasWindow => Window != IntPtr.Zero;
    public bool CanPresent => UsesOpenGl || UsesPresenter || Renderer != IntPtr.Zero;
    public string? FallbackReason { get; private set; }

    internal static string NormalizeBackendId(string? backendId)
    {
        string norm = (backendId ?? string.Empty).Trim().ToLowerInvariant();
        return norm switch
        {
            "gl" or PsxRenderBackendIds.OpenGl => PsxRenderBackendIds.OpenGl,
            PsxRenderBackendIds.Vulkan => PsxRenderBackendIds.Vulkan,
            "parallelcpu" or "parallel-cpu" or "parallel_cpu" => PsxRenderBackendIds.ParallelCpu,
            PsxRenderBackendIds.Software => PsxRenderBackendIds.Software,
            _ => PsxRenderBackendIds.OpenGl
        };
    }

    /// <summary>Software-backend surface with no SDL at all (--verify, offscreen/tray, or CI without SDL3.dll).</summary>
    public static HostVideoSurface CreateOffscreen(RecompSettings settings, ReStationLogger logger, string reason)
    {
        var surface = new HostVideoSurface(new SoftwareCpuRenderBackend()) { FallbackReason = reason };
        surface.Backend.Initialize(NativeWidth, NativeHeight, settings.ResolutionScale);
        logger.Info("VIDEO", $"Offscreen surface backend={surface.Backend.BackendId} scale={surface.Backend.ScaleFactor}x reason={reason}");
        return surface;
    }

    /// <summary>
    /// Initializes SDL3 and selects OpenGL 3.3 Core, Vulkan, ParallelCpu, or falls back to
    /// <c>SDL_CreateRenderer</c> + <see cref="SoftwareCpuRenderBackend"/> with a structured warning.
    /// </summary>
    public static HostVideoSurface Create(
        string title,
        RecompSettings settings,
        bool foreground,
        ReStationLogger logger,
        bool alwaysOnTop = false,
        bool hidden = false,
        string? backendOverride = null)
    {
        Sdl3Native.InstallResolver();
        string requested = NormalizeBackendId(backendOverride ?? settings.BackendId);

        if (!Sdl3Native.TryInit(Sdl3Native.InitVideo | Sdl3Native.InitAudio | Sdl3Native.InitGamepad, out string initError))
        {
            string cat = requested == PsxRenderBackendIds.Vulkan ? "VK_FALLBACK" : "GL_FALLBACK";
            logger.Warn(cat, $"stage=SDL_Init reason=\"{initError}\" forced=software mode=offscreen");
            return CreateOffscreen(settings, logger, "sdl-init-failed");
        }

        ulong baseFlags = Sdl3Native.WindowResizable
            | (hidden ? Sdl3Native.WindowHidden : 0UL)
            | (settings.IsFullscreen && !foreground && !hidden ? Sdl3Native.WindowFullscreen : 0UL);
        string? fallback = requested == PsxRenderBackendIds.Software ? "backend=software requested" : null;

        if (requested == PsxRenderBackendIds.OpenGl)
        {
            if (TryCreateGlResources(title, DefaultWidth, DefaultHeight, baseFlags, settings, logger,
                    out IntPtr glWin, out IntPtr glCtx, out IPsxRenderBackend? glBackend, out string glErr) && glBackend is not null)
            {
                return CompleteSurface(glWin, glCtx, IntPtr.Zero, null, glBackend, null, title, foreground && !hidden, alwaysOnTop && !hidden, settings, logger);
            }
            fallback = glErr;
            logger.Warn("GL_FALLBACK", $"stage=GL33Core reason=\"{glErr}\" forced=software renderer=SDL_CreateRenderer");
        }
        else if (requested == PsxRenderBackendIds.Vulkan)
        {
            if (TryCreateVulkanResources(title, DefaultWidth, DefaultHeight, baseFlags, settings, logger,
                    out IntPtr vkWin, out IHostSurfacePresenter? vkPres, out IPsxRenderBackend? vkBackend, out string vkErr) && vkBackend is not null)
            {
                return CompleteSurface(vkWin, IntPtr.Zero, IntPtr.Zero, vkPres, vkBackend, null, title, foreground && !hidden, alwaysOnTop && !hidden, settings, logger);
            }
            fallback = vkErr;
            logger.Warn("VK_FALLBACK", $"[VK_FALLBACK] stage=Vulkan reason=\"{vkErr}\" forced=software renderer=SDL_CreateRenderer");
        }
        else if (requested == PsxRenderBackendIds.ParallelCpu)
        {
            if (TryCreateParallelCpuResources(title, DefaultWidth, DefaultHeight, baseFlags, settings, logger,
                    out IntPtr pcWin, out IHostSurfacePresenter? pcPres, out IPsxRenderBackend? pcBackend, out string pcErr) && pcBackend is not null)
            {
                return CompleteSurface(pcWin, IntPtr.Zero, IntPtr.Zero, pcPres, pcBackend, null, title, foreground && !hidden, alwaysOnTop && !hidden, settings, logger);
            }
            fallback = pcErr;
            logger.Warn("VIDEO", $"[PARALLEL_CPU_FALLBACK] reason=\"{pcErr}\" forced=software renderer=SDL_CreateRenderer");
        }

        IntPtr swWin = Sdl3Native.CreateWindowUtf8(title, DefaultWidth, DefaultHeight, baseFlags);
        IntPtr swRen = IntPtr.Zero;
        if (swWin != IntPtr.Zero)
        {
            swRen = Sdl3Native.SDL_CreateRenderer(swWin, null);
            if (swRen != IntPtr.Zero) _ = Sdl3Native.SDL_SetRenderVSync(swRen, settings.IsVsyncEnabled ? 1 : 0);
            else logger.Warn("VIDEO", $"[RENDERER_FALLBACK] reason=\"{Sdl3Native.GetErrorString()}\" mode=offscreen");
        }
        else
        {
            logger.Warn("VIDEO", $"[WINDOW_FALLBACK] reason=\"{Sdl3Native.GetErrorString()}\" mode=offscreen");
        }

        var swSurface = new HostVideoSurface(new SoftwareCpuRenderBackend())
        {
            SdlAvailable = true,
            Window = swWin,
            Renderer = swRen,
            FallbackReason = fallback,
            _title = title,
            _alwaysOnTop = alwaysOnTop
        };
        (int initW, int initH) = GetWindowSize(swWin);
        swSurface.InitializeBackend(initW, initH, settings.ResolutionScale);
        swSurface.FinishWindow(foreground && !hidden, alwaysOnTop && !hidden, logger);
        return swSurface;
    }

    private static HostVideoSurface CompleteSurface(
        IntPtr window, IntPtr glCtx, IntPtr renderer, IHostSurfacePresenter? presenter, IPsxRenderBackend backend,
        string? fallback, string title, bool foreground, bool alwaysOnTop, RecompSettings settings, ReStationLogger logger)
    {
        var surface = new HostVideoSurface(backend)
        {
            SdlAvailable = true,
            Window = window,
            GlContext = glCtx,
            Renderer = renderer,
            Presenter = presenter,
            FallbackReason = fallback,
            _title = title,
            _alwaysOnTop = alwaysOnTop
        };
        (int initW, int initH) = GetWindowSize(window);
        surface._surfW = initW;
        surface._surfH = initH;
        surface._surfScale = backend.ScaleFactor;
        _ = settings;
        surface.FinishWindow(foreground, alwaysOnTop, logger);
        return surface;
    }

    private void FinishWindow(bool foreground, bool alwaysOnTop, ReStationLogger logger)
    {
        if (Window == IntPtr.Zero) return;
        if (foreground || alwaysOnTop)
            Sdl3Native.BringWindowToForeground(Window, alwaysOnTop);
        logger.Info("VIDEO", $"Window {DefaultWidth}x{DefaultHeight} backend={Backend.BackendId} gl={UsesOpenGl} presenter={UsesPresenter} renderer={Renderer != IntPtr.Zero} topmost={alwaysOnTop}");
    }

    /// <summary>Presents a tightly packed RGBA8 frame (composited game + UI) and swaps/presents the window.</summary>
    public void PresentRgba(int width, int height, ReadOnlySpan<byte> rgba)
    {
        if (_disposed || width <= 0 || height <= 0 || rgba.Length < checked(width * height * 4)) return;
        if (UsesOpenGl)
        {
            SyncViewport(Backend.ScaleFactor);
            Backend.PresentRgba(width, height, rgba);
            _ = Sdl3Native.SDL_GL_SwapWindow(Window);
            return;
        }
        if (Presenter is not null)
        {
            (int renderW, int renderH) = GetWindowPixelSize();
            HostViewportRect destRect = HostViewportGeometry.ComputeDestinationRect(width, height, renderW, renderH, HostAspectRatioMode.NativeFit);
            HostViewportRect srcCrop = new(0, 0, width, height);
            Presenter.PresentRgba(width, height, rgba, destRect, srcCrop);
            return;
        }
        if (Renderer == IntPtr.Zero) return;
        EnsureGameTexture(width, height, Sdl3Native.ScaleModeLinear, Sdl3Native.BlendModeNone);
        if (_texture == IntPtr.Zero) return;
        fixed (byte* p = rgba) _ = Sdl3Native.SDL_UpdateTexture(_texture, IntPtr.Zero, p, width * 4);
        _ = Sdl3Native.SDL_SetRenderDrawColor(Renderer, 0x0E, 0x0E, 0x10, 0xFF);
        _ = Sdl3Native.SDL_RenderClear(Renderer);
        _ = Sdl3Native.SDL_RenderTexture(Renderer, _texture, null, null);
        _ = Sdl3Native.SDL_RenderPresent(Renderer);
    }

    /// <summary>Software GPU layer compositing: uploads game RGBA once + streaming blend UI texture only when dirty.</summary>
    public void PresentLayeredSoftware(
        int gameW, int gameH, ReadOnlySpan<byte> gameRgba, RecompSettings settings, bool effectiveWidescreen,
        bool hasOverlay, int uiW, int uiH, ReadOnlySpan<byte> uiRgba, int uiDstX, int uiDstY, bool uiUpload)
    {
        if (_disposed || Renderer == IntPtr.Zero) return;
        (int winW, int winH) = GetWindowPixelSize();
        _ = Sdl3Native.SDL_SetRenderDrawColor(Renderer, 0x0E, 0x0E, 0x10, 0xFF);
        _ = Sdl3Native.SDL_RenderClear(Renderer);

        if (gameW > 0 && gameH > 0 && gameRgba.Length >= checked(gameW * gameH * 4))
        {
            EnsureGameTexture(gameW, gameH, Sdl3Native.ScaleModeLinear, Sdl3Native.BlendModeNone);
            if (_texture != IntPtr.Zero)
            {
                fixed (byte* gp = gameRgba) _ = Sdl3Native.SDL_UpdateTexture(_texture, IntPtr.Zero, gp, gameW * 4);
                HostViewportRect srcCrop = HostViewportGeometry.ComputeSourceCropRect(gameW, gameH, settings.OverscanCropPx);
                HostAspectRatioMode mode = UiOverlayPresenter.ResolveEffectiveAspectMode(settings, effectiveWidescreen);
                HostViewportRect dstRect = HostViewportGeometry.ComputeDestinationRect(srcCrop.Width, srcCrop.Height, winW, winH, mode);
                var srcF = new SdlFRect { X = srcCrop.X, Y = srcCrop.Y, W = srcCrop.Width, H = srcCrop.Height };
                var dstF = new SdlFRect { X = dstRect.X, Y = dstRect.Y, W = dstRect.Width, H = dstRect.Height };
                _ = Sdl3Native.SDL_RenderTexture(Renderer, _texture, &srcF, &dstF);
            }
        }

        if (hasOverlay && uiW > 0 && uiH > 0 && uiRgba.Length >= checked(uiW * uiH * 4))
        {
            if (_uiTexture == IntPtr.Zero || _uiTexW != uiW || _uiTexH != uiH)
            {
                if (_uiTexture != IntPtr.Zero) Sdl3Native.SDL_DestroyTexture(_uiTexture);
                _uiTexture = Sdl3Native.SDL_CreateTexture(Renderer, Sdl3Native.PixelFormatAbgr8888, Sdl3Native.TextureAccessStreaming, uiW, uiH);
                _ = Sdl3Native.SDL_SetTextureScaleMode(_uiTexture, Sdl3Native.ScaleModeLinear);
                _ = Sdl3Native.SDL_SetTextureBlendMode(_uiTexture, Sdl3Native.BlendModeBlend);
                _uiTexW = uiW; _uiTexH = uiH;
                uiUpload = true;
            }
            if (_uiTexture != IntPtr.Zero)
            {
                if (uiUpload) { fixed (byte* up = uiRgba) _ = Sdl3Native.SDL_UpdateTexture(_uiTexture, IntPtr.Zero, up, uiW * 4); }
                float sx = winW / (float)UiOverlayPresenter.OverlayWidth, sy = winH / (float)UiOverlayPresenter.OverlayHeight;
                var uiDst = new SdlFRect { X = uiDstX * sx, Y = uiDstY * sy, W = uiW * sx, H = uiH * sy };
                _ = Sdl3Native.SDL_RenderTexture(Renderer, _uiTexture, null, &uiDst);
            }
        }
        _ = Sdl3Native.SDL_RenderPresent(Renderer);
    }

    private void EnsureGameTexture(int width, int height, int scaleMode, uint blendMode)
    {
        if (_texture != IntPtr.Zero && _texW == width && _texH == height) return;
        if (_texture != IntPtr.Zero) Sdl3Native.SDL_DestroyTexture(_texture);
        _texture = Sdl3Native.SDL_CreateTexture(Renderer, Sdl3Native.PixelFormatAbgr8888, Sdl3Native.TextureAccessStreaming, width, height);
        if (_texture != IntPtr.Zero)
        {
            _ = Sdl3Native.SDL_SetTextureScaleMode(_texture, scaleMode);
            _ = Sdl3Native.SDL_SetTextureBlendMode(_texture, blendMode);
        }
        _texW = width;
        _texH = height;
    }

    /// <summary>GL fast path: the backend already resolved the frame in EndFrameAndPresent; only swap.</summary>
    public void SwapGlOnly()
    {
        if (UsesOpenGl && !_disposed) _ = Sdl3Native.SDL_GL_SwapWindow(Window);
    }

    /// <summary>Synchronizes the render backend viewport dimensions and scale factor with the live window.</summary>
    public void SyncViewport(int scaleFactor)
    {
        if (_disposed) return;
        (int winW, int winH) = GetWindowSize();
        if (_surfW != winW || _surfH != winH || _surfScale != scaleFactor)
        {
            Backend.Resize(winW, winH, scaleFactor);
            _surfW = winW;
            _surfH = winH;
            _surfScale = Backend.ScaleFactor;
        }
    }

    public (int Width, int Height) GetWindowSize() => GetWindowSize(Window);

    public (int Width, int Height) GetWindowPixelSize()
    {
        if (Window != IntPtr.Zero && Sdl3Native.SDL_GetWindowSizeInPixels(Window, out int pw, out int ph) != 0 && pw > 0 && ph > 0)
            return (pw, ph);
        return GetWindowSize(Window);
    }

    private void InitializeBackend(int width, int height, int scaleFactor)
    {
        Backend.Initialize(width, height, scaleFactor);
        _surfW = width;
        _surfH = height;
        _surfScale = Backend.ScaleFactor;
    }

    private static (int Width, int Height) GetWindowSize(IntPtr window)
    {
        if (window == IntPtr.Zero || Sdl3Native.SDL_GetWindowSize(window, out int w, out int h) == 0 || w <= 0 || h <= 0)
            return (DefaultWidth, DefaultHeight);
        return (w, h);
    }

    public void SetVsync(bool enabled)
    {
        if (UsesOpenGl) _ = Sdl3Native.SDL_GL_SetSwapInterval(enabled ? 1 : 0);
        else if (Renderer != IntPtr.Zero) _ = Sdl3Native.SDL_SetRenderVSync(Renderer, enabled ? 1 : 0);
        else if (Presenter?.BackendKind == HostRenderBackendKind.ParallelCpu && Presenter.GraphicsContext?.Handle.NativeDeviceHandle is { } ren && ren != IntPtr.Zero)
            _ = Sdl3Native.SDL_SetRenderVSync(ren, enabled ? 1 : 0);
    }

    public void SetFullscreen(bool enabled)
    {
        if (Window != IntPtr.Zero) _ = Sdl3Native.SDL_SetWindowFullscreen(Window, (byte)(enabled ? 1 : 0));
    }

    public void SetRelativeMouse(bool enabled)
    {
        if (Window != IntPtr.Zero) _ = Sdl3Native.SDL_SetWindowRelativeMouseMode(Window, (byte)(enabled ? 1 : 0));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_texture != IntPtr.Zero) Sdl3Native.SDL_DestroyTexture(_texture);
        if (_uiTexture != IntPtr.Zero) Sdl3Native.SDL_DestroyTexture(_uiTexture);
        if (GlContext != IntPtr.Zero && Window != IntPtr.Zero) _ = Sdl3Native.SDL_GL_MakeCurrent(Window, GlContext);
        Backend.Dispose();
        Presenter?.Dispose();
        if (Renderer != IntPtr.Zero) Sdl3Native.SDL_DestroyRenderer(Renderer);
        if (GlContext != IntPtr.Zero) _ = Sdl3Native.SDL_GL_DestroyContext(GlContext);
        if (Window != IntPtr.Zero) Sdl3Native.SDL_DestroyWindow(Window);
        if (SdlAvailable) Sdl3Native.SDL_Quit();
        _texture = _uiTexture = Renderer = GlContext = Window = IntPtr.Zero;
        Presenter = null;
    }
}
