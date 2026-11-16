// @module: reStation.Player.Sdl3Native
// @purpose: SDL3 C-ABI P/Invoke surface (bool-as-byte returns: != 0 success) for video, renderer, events, audio, gamepad, plus resolver + comdlg32 picker.
// @depends-on: System.Reflection, System.Runtime.InteropServices, System.Text
// @exports: SdlAudioSpec, SdlFRect, Sdl3Native
// @owner: devv-player-ui (T6)

using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace reStation.Player;

/// <summary>SDL3 <c>SDL_AudioSpec</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SdlAudioSpec
{
    public uint Format;
    public int Channels;
    public int Freq;
}

/// <summary>SDL3 <c>SDL_FRect</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SdlFRect
{
    public float X;
    public float Y;
    public float W;
    public float H;
}

/// <summary>
/// Minimal SDL3 binding. SDL3 returns C <c>bool</c> (1 byte) where SDL2 returned <c>int</c> (0 = success):
/// every <see cref="byte"/> return here means <c>!= 0</c> = success, <c>0</c> = failure.
/// </summary>
internal static unsafe partial class Sdl3Native
{
    private const string Lib = "SDL3";
    private static readonly object ResolverSync = new();
    private static bool _resolverInstalled;

    public const uint InitAudio = 0x10U, InitVideo = 0x20U, InitGamepad = 0x2000U;
    public const uint PixelFormatAbgr8888 = 0x16762004U; // byte order R,G,B,A on little-endian
    public const uint AudioS16Le = 0x8010U;
    public const uint DefaultAudioPlaybackDevice = 0xFFFFFFFFU;
    public const int TextureAccessStreaming = 1;
    public const int ScaleModeNearest = 0, ScaleModeLinear = 1;
    public const uint BlendModeNone = 0U, BlendModeBlend = 1U;

    public const uint EventQuit = 0x100U, EventKeyDown = 0x300U, EventMouseMotion = 0x400U;
    public const uint EventMouseButtonDown = 0x401U, EventMouseButtonUp = 0x402U, EventMouseWheel = 0x403U;
    public const uint EventGamepadButtonDown = 0x651U, EventGamepadAdded = 0x653U, EventGamepadRemoved = 0x654U, EventDropFile = 0x1000U;

    public const int ScA = 4, ScD = 7, ScS = 22, ScW = 26, ScRet = 40, ScEsc = 41, ScBackspace = 42, ScTab = 43, ScSpace = 44;
    public const int ScF1 = 58, ScF3 = 60, ScF5 = 62, ScF9 = 66, ScPgUp = 75, ScDelete = 76, ScPgDn = 78;
    public const int ScRight = 79, ScLeft = 80, ScDown = 81, ScUp = 82;
    public const int MouseBtnLeft = 1, MouseBtnMiddle = 2, MouseBtnRight = 3, MouseBtnX1 = 4, MouseBtnX2 = 5;
    public const ulong WindowInputFocus = 0x0000000000000200UL;

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern byte SDL_SetHint(byte* name, byte* value);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern byte SDL_Init(uint flags);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void SDL_Quit();
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern byte* SDL_GetError();
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr SDL_CreateWindow(byte* title, int w, int h, ulong flags);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void SDL_DestroyWindow(IntPtr window);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern byte SDL_ShowWindow(IntPtr window);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern byte SDL_RaiseWindow(IntPtr window);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern byte SDL_SetWindowAlwaysOnTop(IntPtr window, byte onTop);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint SDL_GetWindowProperties(IntPtr window);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr SDL_GetPointerProperty(uint props, byte* name, IntPtr defaultValue);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern byte SDL_SetWindowFullscreen(IntPtr window, byte fullscreen);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern byte SDL_SetWindowRelativeMouseMode(IntPtr window, byte enabled);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern byte SDL_GetWindowRelativeMouseMode(IntPtr window);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern ulong SDL_GetWindowFlags(IntPtr window);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern byte SDL_GetWindowPosition(IntPtr window, out int x, out int y);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern byte SDL_SetWindowPosition(IntPtr window, int x, int y);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern byte SDL_SetWindowSize(IntPtr window, int w, int h);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern byte SDL_GetWindowSize(IntPtr window, out int w, out int h);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern int SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern int SetForegroundWindow(IntPtr hWnd);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr SDL_CreateRenderer(IntPtr window, byte* name);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void SDL_DestroyRenderer(IntPtr renderer);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr SDL_CreateTexture(IntPtr renderer, uint format, int access, int w, int h);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void SDL_DestroyTexture(IntPtr texture);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern byte SDL_UpdateTexture(IntPtr texture, IntPtr rect, void* pixels, int pitch);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern byte SDL_SetTextureScaleMode(IntPtr texture, int scaleMode);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern byte SDL_SetTextureBlendMode(IntPtr texture, uint blendMode);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern byte SDL_SetRenderDrawColor(IntPtr renderer, byte r, byte g, byte b, byte a);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern byte SDL_RenderClear(IntPtr renderer);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern byte SDL_RenderTexture(IntPtr renderer, IntPtr texture, SdlFRect* src, SdlFRect* dst);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern byte SDL_RenderPresent(IntPtr renderer);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern byte SDL_SetRenderVSync(IntPtr renderer, int vsync);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern byte SDL_PollEvent(byte* ev);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern byte* SDL_GetKeyboardState(out int numkeys);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint SDL_GetMouseState(float* x, float* y);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr SDL_OpenAudioDeviceStream(uint devid, SdlAudioSpec* spec, IntPtr callback, IntPtr userdata);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern byte SDL_ResumeAudioStreamDevice(IntPtr stream);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern byte SDL_PutAudioStreamData(IntPtr stream, void* buf, int len);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int SDL_GetAudioStreamQueued(IntPtr stream);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern byte SDL_ClearAudioStream(IntPtr stream);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void SDL_DestroyAudioStream(IntPtr stream);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern uint* SDL_GetGamepads(out int count);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr SDL_OpenGamepad(uint instanceId);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void SDL_CloseGamepad(IntPtr gamepad);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern byte SDL_GetGamepadButton(IntPtr gamepad, int button);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern short SDL_GetGamepadAxis(IntPtr gamepad, int axis);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void SDL_free(void* mem);

    /// <summary>Installs the SDL3 resolver probing <c>&lt;base&gt;\SDL3.dll</c> then <c>&lt;base&gt;\runtimes\win-x64\native\SDL3.dll</c>.</summary>
    public static void InstallResolver()
    {
        lock (ResolverSync)
        {
            if (_resolverInstalled) return;
            NativeLibrary.SetDllImportResolver(typeof(Sdl3Native).Assembly, ResolveSdl3);
            _resolverInstalled = true;
        }
    }

    private static IntPtr ResolveSdl3(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, Lib, StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;
        string baseDir = AppContext.BaseDirectory;
        foreach (string candidate in new[] { Path.Combine(baseDir, "SDL3.dll"), Path.Combine(baseDir, "runtimes", "win-x64", "native", "SDL3.dll") })
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out IntPtr handle)) return handle;
        }
        return NativeLibrary.TryLoad(libraryName, assembly, searchPath, out IntPtr fallback) ? fallback : IntPtr.Zero;
    }

    /// <summary>Initializes SDL; returns false (with reason) when SDL3.dll is missing or <c>SDL_Init</c> fails.</summary>
    public static bool TryInit(uint flags, out string error)
    {
        try
        {
            if (SDL_Init(flags) != 0) { error = string.Empty; return true; }
            error = GetErrorString();
            return false;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    public static string GetErrorString()
    {
        byte* p = SDL_GetError();
        return p == null ? string.Empty : Marshal.PtrToStringUTF8((IntPtr)p) ?? string.Empty;
    }

    public static void SetHintUtf8(string name, string value)
    {
        byte[] n = Encoding.UTF8.GetBytes(name + "\0");
        byte[] v = Encoding.UTF8.GetBytes(value + "\0");
        fixed (byte* np = n, vp = v) _ = SDL_SetHint(np, vp);
    }

    public static IntPtr CreateWindowUtf8(string title, int w, int h, ulong flags)
    {
        byte[] t = Encoding.UTF8.GetBytes(title + "\0");
        fixed (byte* tp = t) return SDL_CreateWindow(tp, w, h, flags);
    }

    public static uint GetMouseButtonsMask() => SDL_GetMouseState(null, null);

    public static int SdlButtonToBindingCode(byte sdlButton) => sdlButton switch
    {
        MouseBtnLeft => 1001,
        MouseBtnRight => 1002,
        MouseBtnMiddle => 1003,
        MouseBtnX1 => 1004,
        MouseBtnX2 => 1005,
        _ => 0
    };

    public static void BringWindowToForeground(IntPtr window, bool alwaysOnTop)
    {
        if (window == IntPtr.Zero) return;
        _ = SDL_ShowWindow(window);
        if (alwaysOnTop) _ = SDL_SetWindowAlwaysOnTop(window, 1);
        _ = SDL_RaiseWindow(window);
        if (!OperatingSystem.IsWindows()) return;
        uint props = SDL_GetWindowProperties(window);
        if (props == 0) return;
        byte[] propName = Encoding.UTF8.GetBytes("SDL.window.win32.hwnd\0");
        IntPtr hwnd;
        fixed (byte* pn = propName) hwnd = SDL_GetPointerProperty(props, pn, IntPtr.Zero);
        if (hwnd == IntPtr.Zero) return;
        if (alwaysOnTop) _ = SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0043U);
        _ = SetForegroundWindow(hwnd);
    }

    /// <summary>Reads the SDL event type at offset 0.</summary>
    public static uint EventType(byte* ev) => *(uint*)ev;

    /// <summary>SDL_KeyboardEvent.scancode (offset 24).</summary>
    public static int EventScancode(byte* ev) => *(int*)(ev + 24);

    /// <summary>SDL_MouseButtonEvent.button (offset 24).</summary>
    public static byte EventMouseButton(byte* ev) => *(ev + 24);

    /// <summary>Mouse motion/button window coordinates (offsets 28/32).</summary>
    public static (float X, float Y) EventMousePosition(byte* ev) => (*(float*)(ev + 28), *(float*)(ev + 32));

    /// <summary>SDL_MouseMotionEvent.xrel/yrel (offsets 36/40).</summary>
    public static (float Dx, float Dy) EventMouseRelative(byte* ev) => (*(float*)(ev + 36), *(float*)(ev + 40));

    /// <summary>SDL_MouseWheelEvent vertical scroll normalized for flipped direction (offsets 28/32).</summary>
    public static float EventMouseWheelY(byte* ev) => (*(uint*)(ev + 32)) == 1U ? -(*(float*)(ev + 28)) : *(float*)(ev + 28);

    /// <summary>SDL_GamepadButtonEvent.button (offset 20).</summary>
    public static int EventGamepadButton(byte* ev) => *(ev + 20);

    /// <summary>SDL_DropEvent.data UTF-8 path (offset 40).</summary>
    public static string? EventDropPath(byte* ev) => Marshal.PtrToStringUTF8(*(IntPtr*)(ev + 40));

    [StructLayout(LayoutKind.Sequential)]
    private struct OpenFileNameW
    {
        public int lStructSize;
        public IntPtr hwndOwner, hInstance;
        public char* lpstrFilter, lpstrCustomFilter;
        public int nMaxCustFilter, nFilterIndex;
        public char* lpstrFile;
        public int nMaxFile;
        public char* lpstrFileTitle;
        public int nMaxFileTitle;
        public char* lpstrInitialDir, lpstrTitle;
        public int Flags;
        public short nFileOffset, nFileExtension;
        public char* lpstrDefExt;
        public IntPtr lCustData, lpfnHook;
        public char* lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved, FlagsEx;
    }

    [DllImport("comdlg32.dll", ExactSpelling = true)]
    private static extern int GetOpenFileNameW(OpenFileNameW* ofn);

    public const string DiscImageFilter = "PlayStation Disc Images (*.cue;*.bin;*.iso;*.chd;*.7z;*.zip)\0*.cue;*.bin;*.iso;*.chd;*.7z;*.zip\0All Files (*.*)\0*.*\0\0";
    public const string ModuleFilter = "reStation Modules (*.recomp;*.recomp.json)\0*.recomp;*.recomp.json\0All Files (*.*)\0*.*\0\0";

    /// <summary>Native Win32 open-file picker (no WinForms); returns null when cancelled.</summary>
    public static string? ShowOpenFileDialog(string title, string filter)
    {
        char[] fileBuf = new char[1024];
        string titleZ = title + "\0";
        fixed (char* fp = fileBuf, fltp = filter, tp = titleZ)
        {
            var ofn = new OpenFileNameW
            {
                lStructSize = sizeof(OpenFileNameW),
                lpstrFilter = fltp,
                nFilterIndex = 1,
                lpstrFile = fp,
                nMaxFile = fileBuf.Length,
                lpstrTitle = tp,
                Flags = 0x00081808 // OFN_EXPLORER | OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR
            };
            if (GetOpenFileNameW(&ofn) == 0) return null;
            string picked = new string(fp).TrimEnd('\0');
            return string.IsNullOrWhiteSpace(picked) ? null : picked;
        }
    }
}
