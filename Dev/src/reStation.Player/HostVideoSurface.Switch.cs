// @module: reStation.Player.HostVideoSurface.Switch
// @purpose: Live frame-boundary backend hot-swap between OpenGL 3.3 Core and Software SDL_Renderer preserving window state.
// @depends-on: Foundation.Core, reStation.Contracts, reStation.Core, reStation.Core.Gpu
// @exports: HostVideoSurface (partial)

using reStation.Contracts;
using reStation.Core;
using reStation.Core.Gpu;

namespace reStation.Player;

internal sealed unsafe partial class HostVideoSurface
{
    /// <summary>Live frame-boundary switch between <c>opengl</c> and <c>software</c>, preserving window geometry, fullscreen, VSync, and cursor capture.</summary>
    public bool TrySwitchBackend(string backendId, RecompSettings settings, ReStationLogger logger, out string? error)
    {
        error = null;
        if (_disposed) { error = "Video surface disposed."; return false; }
        bool wantGl = string.Equals(backendId, PsxRenderBackendIds.OpenGl, StringComparison.OrdinalIgnoreCase)
            || string.Equals(backendId, "gl", StringComparison.OrdinalIgnoreCase);
        string targetId = wantGl ? PsxRenderBackendIds.OpenGl : PsxRenderBackendIds.Software;
        if (string.Equals(Backend.BackendId, targetId, StringComparison.OrdinalIgnoreCase)) return true;

        (int winW, int winH) = GetWindowSize();
        bool wasFullscreen = settings.IsFullscreen || (Window != IntPtr.Zero && (Sdl3Native.SDL_GetWindowFlags(Window) & Sdl3Native.WindowFullscreen) != 0);
        bool wasRelativeMouse = Window != IntPtr.Zero && Sdl3Native.SDL_GetWindowRelativeMouseMode(Window) != 0;
        int winX = 0, winY = 0;
        bool hasPos = Window != IntPtr.Zero && Sdl3Native.SDL_GetWindowPosition(Window, out winX, out winY) != 0;

        if (!SdlAvailable)
        {
            if (wantGl) { error = "OpenGL 3.3 unavailable in headless/offscreen mode."; return false; }
            var sw = new SoftwareCpuRenderBackend();
            sw.Initialize(winW, winH, settings.ResolutionScale);
            Backend.Dispose();
            Backend = sw;
            _surfW = winW; _surfH = winH; _surfScale = sw.ScaleFactor;
            return true;
        }

        ulong hiddenFlags = Sdl3Native.WindowResizable | Sdl3Native.WindowHidden | (wasFullscreen ? Sdl3Native.WindowFullscreen : 0UL);
        IntPtr newWin = IntPtr.Zero, newGl = IntPtr.Zero, newRen = IntPtr.Zero;
        IPsxRenderBackend? newBackend = null;
        if (wantGl)
        {
            if (!Sdl3Native.ConfigureGl33CoreAttributes(out string glErr)) { error = $"GL3.3 init failed: {glErr}"; return false; }
            newWin = Sdl3Native.CreateWindowUtf8(_title, winW, winH, hiddenFlags | Sdl3Native.WindowOpenGl);
            if (newWin == IntPtr.Zero) { error = $"GL3.3 window failed: {Sdl3Native.GetErrorString()}"; return false; }
            newGl = Sdl3Native.TryCreateGlContext(newWin, out glErr);
            if (newGl == IntPtr.Zero) { Sdl3Native.SDL_DestroyWindow(newWin); error = glErr; return false; }
            try
            {
                var gl = new OpenGl33Bindings();
                gl.Initialize(Sdl3Native.GetGlProcAddress);
                newBackend = new OpenGl33RenderBackend(gl);
                _ = Sdl3Native.SDL_GL_SetSwapInterval(settings.IsVsyncEnabled ? 1 : 0);
                newBackend.Initialize(winW, winH, settings.ResolutionScale);
            }
            catch (Exception ex) when (ex is InvalidOperationException or EntryPointNotFoundException or NotSupportedException)
            {
                newBackend?.Dispose();
                _ = Sdl3Native.SDL_GL_DestroyContext(newGl);
                Sdl3Native.SDL_DestroyWindow(newWin);
                error = $"OpenGL 3.3 fallback: {ex.Message}";
                return false;
            }
        }
        else
        {
            newWin = Sdl3Native.CreateWindowUtf8(_title, winW, winH, hiddenFlags);
            if (newWin == IntPtr.Zero) { error = $"Software window failed: {Sdl3Native.GetErrorString()}"; return false; }
            newRen = Sdl3Native.SDL_CreateRenderer(newWin, null);
            if (newRen != IntPtr.Zero) _ = Sdl3Native.SDL_SetRenderVSync(newRen, settings.IsVsyncEnabled ? 1 : 0);
            newBackend = new SoftwareCpuRenderBackend();
            newBackend.Initialize(winW, winH, settings.ResolutionScale);
        }

        if (!wasFullscreen && hasPos) _ = Sdl3Native.SDL_SetWindowPosition(newWin, winX, winY);
        if (!wasFullscreen) _ = Sdl3Native.SDL_SetWindowSize(newWin, winW, winH);
        if (wasFullscreen) _ = Sdl3Native.SDL_SetWindowFullscreen(newWin, 1);
        Sdl3Native.BringWindowToForeground(newWin, _alwaysOnTop);
        if (wasRelativeMouse) _ = Sdl3Native.SDL_SetWindowRelativeMouseMode(newWin, 1);

        if (_texture != IntPtr.Zero) Sdl3Native.SDL_DestroyTexture(_texture);
        if (_uiTexture != IntPtr.Zero) Sdl3Native.SDL_DestroyTexture(_uiTexture);
        if (Renderer != IntPtr.Zero) Sdl3Native.SDL_DestroyRenderer(Renderer);
        Backend.Dispose();
        if (GlContext != IntPtr.Zero) _ = Sdl3Native.SDL_GL_DestroyContext(GlContext);
        if (Window != IntPtr.Zero) Sdl3Native.SDL_DestroyWindow(Window);

        _texture = _uiTexture = IntPtr.Zero;
        _texW = _texH = _uiTexW = _uiTexH = 0;
        Window = newWin; GlContext = newGl; Renderer = newRen; Backend = newBackend;
        _surfW = winW; _surfH = winH; _surfScale = newBackend.ScaleFactor;
        FallbackReason = wantGl ? null : "backend=software requested";
        logger.Info("VIDEO", $"Live backend hot-swap -> {Backend.BackendId} ({winW}x{winH}, scale={Backend.ScaleFactor}x, vsync={settings.IsVsyncEnabled}).");
        return true;
    }
}
