// @module: reStation.Player.HostVideoSurface
// @purpose: Owns the SDL3 window + OpenGL 3.3 Core context (or SDL_Renderer/offscreen fallback) and the matching IPsxRenderBackend; presents RGBA frames.
// @depends-on: Foundation.Core, reStation.Contracts, reStation.Core, reStation.Core.Gpu
// @exports: HostVideoSurface
// @owner: devv-player-ui (T6)

using reStation.Contracts;
using reStation.Core;
using reStation.Core.Gpu;

namespace reStation.Player;

/// <summary>Video output: GL window (default), SDL software renderer fallback, or pure offscreen (no SDL / headless).</summary>
internal sealed unsafe class HostVideoSurface : IDisposable
{
    public const int DefaultWidth = 1280, DefaultHeight = 720;
    public const int NativeWidth = 320, NativeHeight = 240;

    private IntPtr _texture;
    private int _texW, _texH, _surfW, _surfH, _surfScale;
    private bool _disposed;

    private HostVideoSurface(IPsxRenderBackend backend) => Backend = backend;

    public IPsxRenderBackend Backend { get; private set; }
    public IntPtr Window { get; private set; }
    public IntPtr GlContext { get; private set; }
    public IntPtr Renderer { get; private set; }
    public bool SdlAvailable { get; private set; }
    public bool UsesOpenGl => GlContext != IntPtr.Zero;
    public bool HasWindow => Window != IntPtr.Zero;
    public bool CanPresent => UsesOpenGl || Renderer != IntPtr.Zero;
    public string? FallbackReason { get; private set; }

    /// <summary>Software-backend surface with no SDL at all (fixtures, --verify, CI without SDL3.dll).</summary>
    public static HostVideoSurface CreateOffscreen(RecompSettings settings, ReStationLogger logger, string reason)
    {
        var surface = new HostVideoSurface(new SoftwareCpuRenderBackend()) { FallbackReason = reason };
        surface.Backend.Initialize(NativeWidth, NativeHeight, settings.ResolutionScale);
        logger.Info("VIDEO", $"Offscreen surface backend={surface.Backend.BackendId} scale={surface.Backend.ScaleFactor}x reason={reason}");
        return surface;
    }

    /// <summary>
    /// Initializes SDL3 and selects OpenGL 3.3 Core (when requested and not headless) or falls back to
    /// <c>SDL_CreateRenderer</c> + <see cref="SoftwareCpuRenderBackend"/> with a structured warning.
    /// </summary>
    public static HostVideoSurface Create(string title, RecompSettings settings, bool headless, bool foreground, ReStationLogger logger)
    {
        Sdl3Native.InstallResolver();
        bool requestedGl = string.Equals(settings.BackendId, PsxRenderBackendIds.OpenGl, StringComparison.OrdinalIgnoreCase)
            || string.Equals(settings.BackendId, "gl", StringComparison.OrdinalIgnoreCase);
        bool loggedHeadlessFallback = false;
        if (headless && requestedGl)
        {
            logger.Warn("GL_FALLBACK", "reason=headless forced=software");
            loggedHeadlessFallback = true;
        }
        if (headless)
        {
            Environment.SetEnvironmentVariable("SDL_VIDEODRIVER", "dummy");
            Environment.SetEnvironmentVariable("SDL_AUDIODRIVER", "dummy");
        }

        if (!Sdl3Native.TryInit(Sdl3Native.InitVideo | Sdl3Native.InitAudio | Sdl3Native.InitGamepad, out string initError))
        {
            if (!loggedHeadlessFallback)
                logger.Warn("GL_FALLBACK", $"stage=SDL_Init reason=\"{initError}\" forced=software mode=offscreen");
            return CreateOffscreen(settings, logger, headless && requestedGl ? "headless" : "sdl-init-failed");
        }

        ulong baseFlags = Sdl3Native.WindowResizable | (settings.IsFullscreen && !headless && !foreground ? Sdl3Native.WindowFullscreen : 0UL);
        bool wantGl = !headless && requestedGl;
        string? fallback = wantGl ? null : headless ? "headless" : "backend=software requested";

        if (wantGl)
        {
            if (TryCreateGlSurface(title, baseFlags, settings, logger, out HostVideoSurface? glSurface, out string glError) && glSurface is not null)
            {
                glSurface.FinishWindow(foreground, logger);
                return glSurface;
            }
            fallback = glError;
            logger.Warn("GL_FALLBACK", $"stage=GL33Core reason=\"{glError}\" forced=software renderer=SDL_CreateRenderer");
        }

        var surface = new HostVideoSurface(new SoftwareCpuRenderBackend()) { SdlAvailable = true, FallbackReason = fallback };
        surface.Window = Sdl3Native.CreateWindowUtf8(title, DefaultWidth, DefaultHeight, baseFlags);
        if (surface.Window != IntPtr.Zero)
        {
            surface.Renderer = Sdl3Native.SDL_CreateRenderer(surface.Window, null);
            if (surface.Renderer != IntPtr.Zero) _ = Sdl3Native.SDL_SetRenderVSync(surface.Renderer, settings.IsVsyncEnabled && !headless ? 1 : 0);
            else logger.Warn("VIDEO", $"[RENDERER_FALLBACK] reason=\"{Sdl3Native.GetErrorString()}\" mode=offscreen");
        }
        else
        {
            logger.Warn("VIDEO", $"[WINDOW_FALLBACK] reason=\"{Sdl3Native.GetErrorString()}\" mode=offscreen");
        }
        (int initW, int initH) = GetWindowSize(surface.Window);
        surface.InitializeBackend(initW, initH, settings.ResolutionScale);
        surface.FinishWindow(foreground && !headless, logger);
        return surface;
    }

    private static bool TryCreateGlSurface(string title, ulong baseFlags, RecompSettings settings, ReStationLogger logger,
        out HostVideoSurface? surface, out string error)
    {
        surface = null;
        if (!Sdl3Native.ConfigureGl33CoreAttributes(out error)) { error = $"SDL_GL_SetAttribute failed: {error}"; return false; }
        IntPtr window = Sdl3Native.CreateWindowUtf8(title, DefaultWidth, DefaultHeight, baseFlags | Sdl3Native.WindowOpenGl);
        if (window == IntPtr.Zero) { error = $"SDL_CreateWindow(OPENGL) failed: {Sdl3Native.GetErrorString()}"; return false; }
        IntPtr ctx = Sdl3Native.TryCreateGlContext(window, out error);
        if (ctx == IntPtr.Zero) { Sdl3Native.SDL_DestroyWindow(window); return false; }

        try
        {
            var gl = new OpenGl33Bindings();
            gl.Initialize(Sdl3Native.GetGlProcAddress);
            var backend = new OpenGl33RenderBackend(gl);
            (int initW, int initH) = GetWindowSize(window);
            _ = Sdl3Native.SDL_GL_SetSwapInterval(settings.IsVsyncEnabled ? 1 : 0);
            surface = new HostVideoSurface(backend) { SdlAvailable = true, Window = window, GlContext = ctx };
            surface.InitializeBackend(initW, initH, settings.ResolutionScale);
            logger.Info("VIDEO", $"OpenGL 3.3 Core context ready backend={backend.BackendId} scale={backend.ScaleFactor}x vsync={settings.IsVsyncEnabled}");
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or EntryPointNotFoundException or NotSupportedException)
        {
            error = ex.Message;
            _ = Sdl3Native.SDL_GL_DestroyContext(ctx);
            Sdl3Native.SDL_DestroyWindow(window);
            return false;
        }
    }

    private void FinishWindow(bool foreground, ReStationLogger logger)
    {
        if (Window == IntPtr.Zero) return;
        if (foreground)
        {
            _ = Sdl3Native.SDL_ShowWindow(Window);
            _ = Sdl3Native.SDL_RaiseWindow(Window);
        }
        logger.Info("VIDEO", $"Window {DefaultWidth}x{DefaultHeight} backend={Backend.BackendId} gl={UsesOpenGl} renderer={Renderer != IntPtr.Zero}");
    }

    /// <summary>Presents a tightly packed RGBA8 frame (composited game + UI) and swaps/presents the window.</summary>
    public void PresentRgba(int width, int height, ReadOnlySpan<byte> rgba)
    {
        if (_disposed || width <= 0 || height <= 0 || rgba.Length < width * height * 4) return;
        if (UsesOpenGl)
        {
            SyncViewport(Backend.ScaleFactor);
            Backend.PresentRgba(width, height, rgba);
            _ = Sdl3Native.SDL_GL_SwapWindow(Window);
            return;
        }
        if (Renderer == IntPtr.Zero) return;
        if (_texture == IntPtr.Zero || _texW != width || _texH != height)
        {
            if (_texture != IntPtr.Zero) Sdl3Native.SDL_DestroyTexture(_texture);
            _texture = Sdl3Native.SDL_CreateTexture(Renderer, Sdl3Native.PixelFormatAbgr8888, Sdl3Native.TextureAccessStreaming, width, height);
            _ = Sdl3Native.SDL_SetTextureScaleMode(_texture, Sdl3Native.ScaleModeLinear);
            _texW = width;
            _texH = height;
        }
        if (_texture == IntPtr.Zero) return;
        fixed (byte* p = rgba) _ = Sdl3Native.SDL_UpdateTexture(_texture, IntPtr.Zero, p, width * 4);
        _ = Sdl3Native.SDL_SetRenderDrawColor(Renderer, 0x0E, 0x0E, 0x10, 0xFF);
        _ = Sdl3Native.SDL_RenderClear(Renderer);
        _ = Sdl3Native.SDL_RenderTexture(Renderer, _texture, null, null);
        _ = Sdl3Native.SDL_RenderPresent(Renderer);
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
        if (Renderer != IntPtr.Zero) Sdl3Native.SDL_DestroyRenderer(Renderer);
        Backend.Dispose(); // GL backend frees GPU objects while its context is still current.
        if (GlContext != IntPtr.Zero) _ = Sdl3Native.SDL_GL_DestroyContext(GlContext);
        if (Window != IntPtr.Zero) Sdl3Native.SDL_DestroyWindow(Window);
        if (SdlAvailable) Sdl3Native.SDL_Quit();
        _texture = Renderer = GlContext = Window = IntPtr.Zero;
    }
}
