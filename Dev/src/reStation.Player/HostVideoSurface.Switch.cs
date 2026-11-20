// @module: reStation.Player.HostVideoSurface.Switch
// @purpose: Live frame-boundary backend hot-swap across OpenGL 3.3 Core, Vulkan, ParallelCpu, and Software preserving window state.
// @depends-on: Foundation.Core, reEngine.Host.Contracts, reStation.Contracts, reStation.Core, reStation.Core.Gpu
// @exports: HostVideoSurface (partial)

using reEngine.Host.Contracts;
using reEngine.Host.Rendering;
using reStation.Contracts;
using reStation.Core;
using reStation.Core.Gpu;

namespace reStation.Player;

internal sealed unsafe partial class HostVideoSurface
{
    /// <summary>Live frame-boundary switch across all 4 backends (<c>opengl</c>, <c>vulkan</c>, <c>parallelCpu</c>, <c>software</c>).</summary>
    public bool TrySwitchBackend(string backendId, RecompSettings settings, ReStationLogger logger, out string? error)
    {
        error = null;
        if (_disposed) { error = "Video surface disposed."; return false; }
        string targetId = NormalizeBackendId(backendId);
        if (string.Equals(Backend.BackendId, targetId, StringComparison.OrdinalIgnoreCase)) return true;

        (int winW, int winH) = GetWindowSize();
        bool wasFullscreen = settings.IsFullscreen || (Window != IntPtr.Zero && (Sdl3Native.SDL_GetWindowFlags(Window) & Sdl3Native.WindowFullscreen) != 0);
        bool wasRelativeMouse = Window != IntPtr.Zero && Sdl3Native.SDL_GetWindowRelativeMouseMode(Window) != 0;
        int winX = 0, winY = 0;
        bool hasPos = Window != IntPtr.Zero && Sdl3Native.SDL_GetWindowPosition(Window, out winX, out winY) != 0;

        if (!SdlAvailable)
        {
            if (targetId is PsxRenderBackendIds.OpenGl or PsxRenderBackendIds.Vulkan)
            {
                error = $"{targetId} unavailable in offscreen mode.";
                return false;
            }
            IPsxRenderBackend nextCpu = targetId == PsxRenderBackendIds.ParallelCpu
                ? new ParallelCpuRenderBackend()
                : new SoftwareCpuRenderBackend();
            nextCpu.Initialize(winW, winH, settings.ResolutionScale);
            Backend.Dispose();
            Backend = nextCpu;
            _surfW = winW; _surfH = winH; _surfScale = nextCpu.ScaleFactor;
            return true;
        }

        ulong hiddenFlags = Sdl3Native.WindowResizable | Sdl3Native.WindowHidden | (wasFullscreen ? Sdl3Native.WindowFullscreen : 0UL);
        IntPtr newWin = IntPtr.Zero, newGl = IntPtr.Zero, newRen = IntPtr.Zero;
        IHostSurfacePresenter? newPres = null;
        IPsxRenderBackend? newBackend = null;

        if (targetId == PsxRenderBackendIds.OpenGl)
        {
            if (!TryCreateGlResources(_title, winW, winH, hiddenFlags, settings, logger, out newWin, out newGl, out newBackend, out string glErr) || newBackend is null)
            {
                error = $"OpenGL 3.3 fallback: {glErr}";
                return false;
            }
        }
        else if (targetId == PsxRenderBackendIds.Vulkan)
        {
            if (!TryCreateVulkanResources(_title, winW, winH, hiddenFlags, settings, logger, out newWin, out newPres, out newBackend, out string vkErr) || newBackend is null)
            {
                error = $"[VK_FALLBACK] {vkErr}";
                return false;
            }
        }
        else if (targetId == PsxRenderBackendIds.ParallelCpu)
        {
            if (!TryCreateParallelCpuResources(_title, winW, winH, hiddenFlags, settings, logger, out newWin, out newPres, out newBackend, out string pcErr) || newBackend is null)
            {
                error = $"ParallelCpu unavailable: {pcErr}";
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
        if (GlContext != IntPtr.Zero && Window != IntPtr.Zero) _ = Sdl3Native.SDL_GL_MakeCurrent(Window, GlContext);
        Backend.Dispose();
        Presenter?.Dispose();
        if (Renderer != IntPtr.Zero) Sdl3Native.SDL_DestroyRenderer(Renderer);
        if (GlContext != IntPtr.Zero) _ = Sdl3Native.SDL_GL_DestroyContext(GlContext);
        if (Window != IntPtr.Zero) Sdl3Native.SDL_DestroyWindow(Window);

        _texture = _uiTexture = IntPtr.Zero;
        _texW = _texH = _uiTexW = _uiTexH = 0;
        Window = newWin; GlContext = newGl; Renderer = newRen; Presenter = newPres; Backend = newBackend;
        _surfW = winW; _surfH = winH; _surfScale = newBackend.ScaleFactor;
        FallbackReason = targetId == PsxRenderBackendIds.Software ? "backend=software requested" : null;
        logger.Info("VIDEO", $"Live backend hot-swap -> {Backend.BackendId} ({winW}x{winH}, scale={Backend.ScaleFactor}x, vsync={settings.IsVsyncEnabled}).");
        return true;
    }

    internal static bool TryCreateGlResources(
        string title, int width, int height, ulong baseFlags, RecompSettings settings, ReStationLogger logger,
        out IntPtr window, out IntPtr ctx, out IPsxRenderBackend? backend, out string error)
    {
        window = ctx = IntPtr.Zero;
        backend = null;
        if (!Sdl3Native.ConfigureGl33CoreAttributes(out error)) { error = $"SDL_GL_SetAttribute failed: {error}"; return false; }
        window = Sdl3Native.CreateWindowUtf8(title, width, height, baseFlags | Sdl3Native.WindowOpenGl);
        if (window == IntPtr.Zero) { error = $"SDL_CreateWindow(OPENGL) failed: {Sdl3Native.GetErrorString()}"; return false; }
        ctx = Sdl3Native.TryCreateGlContext(window, out error);
        if (ctx == IntPtr.Zero) { Sdl3Native.SDL_DestroyWindow(window); window = IntPtr.Zero; return false; }

        try
        {
            var gl = new OpenGl33Bindings();
            gl.Initialize(Sdl3Native.GetGlProcAddress);
            var glBackend = new OpenGl33RenderBackend(gl);
            (int initW, int initH) = GetWindowSize(window);
            _ = Sdl3Native.SDL_GL_SetSwapInterval(settings.IsVsyncEnabled ? 1 : 0);
            glBackend.Initialize(initW, initH, settings.ResolutionScale);
            backend = glBackend;
            logger.Info("VIDEO", $"OpenGL 3.3 Core context ready backend={glBackend.BackendId} scale={glBackend.ScaleFactor}x vsync={settings.IsVsyncEnabled}");
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or EntryPointNotFoundException or NotSupportedException)
        {
            error = ex.Message;
            backend?.Dispose();
            backend = null;
            _ = Sdl3Native.SDL_GL_DestroyContext(ctx);
            Sdl3Native.SDL_DestroyWindow(window);
            ctx = window = IntPtr.Zero;
            return false;
        }
    }

    internal static bool TryCreateVulkanResources(
        string title, int width, int height, ulong baseFlags, RecompSettings settings, ReStationLogger logger,
        out IntPtr window, out IHostSurfacePresenter? presenter, out IPsxRenderBackend? backend, out string error)
    {
        window = IntPtr.Zero;
        presenter = null;
        backend = null;
        window = Sdl3Native.CreateWindowUtf8(title, width, height, baseFlags | Sdl3Native.WindowVulkan);
        if (window == IntPtr.Zero) { error = $"SDL_CreateWindow(VULKAN) failed: {Sdl3Native.GetErrorString()}"; return false; }

        (int initW, int initH) = GetWindowSize(window);
        var vkPresenter = new VulkanPresenter();
        if (!vkPresenter.TryInitialize(window, initW, initH, out string presDiag))
        {
            vkPresenter.Dispose();
            Sdl3Native.SDL_DestroyWindow(window);
            window = IntPtr.Zero;
            error = $"VulkanPresenter.TryInitialize failed: {presDiag}";
            return false;
        }

        var vkBackend = new VulkanRenderBackend();
        vkBackend.Initialize(initW, initH, settings.ResolutionScale);
        if (!vkBackend.HasHardwareVulkanContext)
        {
            error = $"Vulkan hardware context unavailable ({vkBackend.VulkanDiagnostic}).";
            vkBackend.Dispose();
            vkPresenter.Dispose();
            Sdl3Native.SDL_DestroyWindow(window);
            window = IntPtr.Zero;
            return false;
        }

        presenter = vkPresenter;
        backend = vkBackend;
        error = string.Empty;
        logger.Info("VIDEO", $"Vulkan hardware context ready backend={vkBackend.BackendId} scale={vkBackend.ScaleFactor}x presenter=\"{presDiag}\"");
        return true;
    }

    internal static bool TryCreateParallelCpuResources(
        string title, int width, int height, ulong baseFlags, RecompSettings settings, ReStationLogger logger,
        out IntPtr window, out IHostSurfacePresenter? presenter, out IPsxRenderBackend? backend, out string error)
    {
        window = IntPtr.Zero;
        presenter = null;
        backend = null;
        window = Sdl3Native.CreateWindowUtf8(title, width, height, baseFlags);
        if (window == IntPtr.Zero) { error = $"SDL_CreateWindow(ParallelCpu) failed: {Sdl3Native.GetErrorString()}"; return false; }

        (int initW, int initH) = GetWindowSize(window);
        var pcpuPresenter = new ParallelCpuPresenter(initW, initH, settings.ResolutionScale);
        if (!pcpuPresenter.TryInitialize(window, initW, initH, out string presDiag))
        {
            pcpuPresenter.Dispose();
            Sdl3Native.SDL_DestroyWindow(window);
            window = IntPtr.Zero;
            error = $"ParallelCpuPresenter.TryInitialize failed: {presDiag}";
            return false;
        }

        IntPtr sdlRen = pcpuPresenter.GraphicsContext?.Handle.NativeDeviceHandle ?? IntPtr.Zero;
        if (sdlRen != IntPtr.Zero) _ = Sdl3Native.SDL_SetRenderVSync(sdlRen, settings.IsVsyncEnabled ? 1 : 0);

        var pcpuBackend = new ParallelCpuRenderBackend();
        pcpuBackend.Initialize(initW, initH, settings.ResolutionScale);
        presenter = pcpuPresenter;
        backend = pcpuBackend;
        error = string.Empty;
        logger.Info("VIDEO", $"ParallelCpu presenter ready backend={pcpuBackend.BackendId} workers={pcpuBackend.WorkerThreads} scale={pcpuBackend.ScaleFactor}x ({presDiag})");
        return true;
    }
}
