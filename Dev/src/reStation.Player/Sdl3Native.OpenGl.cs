// @module: reStation.Player.Sdl3Native.OpenGl
// @purpose: SDL3 OpenGL 3.3 Core context bindings (attributes, create/make-current/swap/destroy, GetProcAddress) wired to OpenGl33Bindings.
// @depends-on: System.Runtime.InteropServices, System.Text
// @exports: Sdl3Native (OpenGL partial)
// @owner: devv-player-ui (T6)

using System.Runtime.InteropServices;
using System.Text;

namespace reStation.Player;

internal static unsafe partial class Sdl3Native
{
    public const ulong WindowFullscreen = 0x01UL, WindowOpenGl = 0x02UL, WindowHidden = 0x08UL, WindowResizable = 0x20UL;

    public const int GlRedSize = 0, GlGreenSize = 1, GlBlueSize = 2, GlAlphaSize = 3, GlDoubleBuffer = 5, GlDepthSize = 6;
    public const int GlContextMajorVersion = 17, GlContextMinorVersion = 18, GlContextProfileMask = 20;
    public const int GlContextProfileCore = 0x0001;

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern byte SDL_GL_SetAttribute(int attr, int value);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void SDL_GL_ResetAttributes();
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr SDL_GL_CreateContext(IntPtr window);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern byte SDL_GL_MakeCurrent(IntPtr window, IntPtr context);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern byte SDL_GL_SwapWindow(IntPtr window);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern byte SDL_GL_SetSwapInterval(int interval);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern byte SDL_GL_DestroyContext(IntPtr context);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr SDL_GL_GetProcAddress(byte* proc);

    /// <summary>Requests an RGBA8, double-buffered OpenGL 3.3 Core profile context (must precede window creation).</summary>
    public static bool ConfigureGl33CoreAttributes(out string error)
    {
        SDL_GL_ResetAttributes();
        bool ok = SDL_GL_SetAttribute(GlContextMajorVersion, 3) != 0
               && SDL_GL_SetAttribute(GlContextMinorVersion, 3) != 0
               && SDL_GL_SetAttribute(GlContextProfileMask, GlContextProfileCore) != 0
               && SDL_GL_SetAttribute(GlDoubleBuffer, 1) != 0
               && SDL_GL_SetAttribute(GlRedSize, 8) != 0
               && SDL_GL_SetAttribute(GlGreenSize, 8) != 0
               && SDL_GL_SetAttribute(GlBlueSize, 8) != 0
               && SDL_GL_SetAttribute(GlAlphaSize, 8) != 0
               && SDL_GL_SetAttribute(GlDepthSize, 0) != 0;
        error = ok ? string.Empty : GetErrorString();
        return ok;
    }

    /// <summary>Creates and binds an OpenGL context for <paramref name="window"/>; returns <see cref="IntPtr.Zero"/> with a reason on failure.</summary>
    public static IntPtr TryCreateGlContext(IntPtr window, out string error)
    {
        error = string.Empty;
        if (window == IntPtr.Zero) { error = "no window"; return IntPtr.Zero; }
        IntPtr ctx = SDL_GL_CreateContext(window);
        if (ctx == IntPtr.Zero) { error = $"SDL_GL_CreateContext failed: {GetErrorString()}"; return IntPtr.Zero; }
        if (SDL_GL_MakeCurrent(window, ctx) == 0)
        {
            error = $"SDL_GL_MakeCurrent failed: {GetErrorString()}";
            _ = SDL_GL_DestroyContext(ctx);
            return IntPtr.Zero;
        }
        return ctx;
    }

    /// <summary>
    /// <c>Func&lt;string, IntPtr&gt;</c>-compatible loader passed to <c>OpenGl33Bindings.Initialize</c> so <c>reStation.Core</c> stays SDL-free.
    /// </summary>
    public static IntPtr GetGlProcAddress(string name)
    {
        if (string.IsNullOrEmpty(name)) return IntPtr.Zero;
        byte[] utf8 = Encoding.ASCII.GetBytes(name + "\0");
        fixed (byte* p = utf8) return SDL_GL_GetProcAddress(p);
    }
}
