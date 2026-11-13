// @module: reStation.Core.Gpu.OpenGl33Bindings
// @purpose: Window-system-free OpenGL 3.3 Core entry-point loader using unsafe delegate* unmanaged[Stdcall] initialized strictly via getProcAddress.
// @depends-on: System
// @exports: OpenGl33Bindings, GlConst
// @owner: devv-gpu-gl (T4) — preserves T1-locked RequiredEntryPoints, IsInitialized, Initialize(Func<string, IntPtr>), GetProc.

namespace reStation.Core.Gpu;

internal static class GlConst
{
    public const uint False = 0, True = 1;
    public const uint UnsignedByte = 0x1401, UnsignedShort = 0x1403, UnsignedInt = 0x1405, Float = 0x1406;
    public const uint Triangles = 0x0004, ColorBufferBit = 0x00004000;
    public const uint Blend = 0x0BE2, ScissorTest = 0x0C11;
    public const uint Zero = 0, One = 1, SrcAlpha = 0x0302, OneMinusSrcAlpha = 0x0303;
    public const uint FuncAdd = 0x8006, FuncReverseSubtract = 0x800B;
    public const uint Texture2D = 0x0DE1, Texture0 = 0x84C0, Texture1 = 0x84C1;
    public const uint TextureMagFilter = 0x2800, TextureMinFilter = 0x2801, TextureWrapS = 0x2802, TextureWrapT = 0x2803;
    public const int Nearest = 0x2600, Linear = 0x2601, Repeat = 0x2901, ClampToEdge = 0x812F;
    public const uint Rgba = 0x1908, RedInteger = 0x8D94;
    public const int Rgba8 = 0x8058, R16Ui = 0x8234;
    public const uint PackAlignment = 0x0D05, UnpackAlignment = 0x0CF5, UnpackRowLength = 0x0CF2;
    public const uint Framebuffer = 0x8D40, ReadFramebuffer = 0x8CA8, DrawFramebuffer = 0x8CA9;
    public const uint ColorAttachment0 = 0x8CE0, FramebufferComplete = 0x8CD5;
    public const uint ArrayBuffer = 0x8892, DynamicDraw = 0x88E8, StaticDraw = 0x88E4;
    public const uint FragmentShader = 0x8B30, VertexShader = 0x8B31, CompileStatus = 0x8B81, LinkStatus = 0x8B82;
}

public sealed unsafe class OpenGl33Bindings
{
    /// <summary>Entry points the OpenGL 3.3 Core backend requires.</summary>
    public static readonly IReadOnlyList<string> RequiredEntryPoints = new[]
    {
        "glGetString", "glGetError", "glViewport", "glClearColor", "glClear", "glEnable", "glDisable", "glBlendFunc",
        "glBlendEquation", "glGenTextures", "glDeleteTextures", "glBindTexture", "glTexImage2D", "glTexSubImage2D",
        "glTexParameteri", "glActiveTexture", "glGenFramebuffers", "glDeleteFramebuffers", "glBindFramebuffer",
        "glFramebufferTexture2D", "glCheckFramebufferStatus", "glBlitFramebuffer", "glReadPixels", "glPixelStorei",
        "glGenVertexArrays", "glDeleteVertexArrays", "glBindVertexArray", "glGenBuffers", "glDeleteBuffers", "glBindBuffer",
        "glBufferData", "glBufferSubData", "glVertexAttribPointer", "glVertexAttribIPointer", "glEnableVertexAttribArray",
        "glCreateShader", "glShaderSource", "glCompileShader", "glGetShaderiv", "glGetShaderInfoLog", "glDeleteShader",
        "glCreateProgram", "glAttachShader", "glLinkProgram", "glGetProgramiv", "glGetProgramInfoLog", "glUseProgram",
        "glDeleteProgram", "glGetUniformLocation", "glUniform1i", "glUniform2f", "glUniform4f", "glDrawArrays", "glScissor"
    };

    private readonly Dictionary<string, IntPtr> _procs = new(StringComparer.Ordinal);

    internal delegate* unmanaged[Stdcall]<uint, byte*> GetString;
    internal delegate* unmanaged[Stdcall]<uint> GetError;
    internal delegate* unmanaged[Stdcall]<int, int, int, int, void> Viewport;
    internal delegate* unmanaged[Stdcall]<float, float, float, float, void> ClearColor;
    internal delegate* unmanaged[Stdcall]<uint, void> Clear;
    internal delegate* unmanaged[Stdcall]<uint, void> Enable;
    internal delegate* unmanaged[Stdcall]<uint, void> Disable;
    internal delegate* unmanaged[Stdcall]<uint, uint, void> BlendFunc;
    internal delegate* unmanaged[Stdcall]<uint, void> BlendEquation;
    internal delegate* unmanaged[Stdcall]<int, uint*, void> GenTextures;
    internal delegate* unmanaged[Stdcall]<int, uint*, void> DeleteTextures;
    internal delegate* unmanaged[Stdcall]<uint, uint, void> BindTexture;
    internal delegate* unmanaged[Stdcall]<uint, int, int, int, int, int, uint, uint, void*, void> TexImage2D;
    internal delegate* unmanaged[Stdcall]<uint, int, int, int, int, int, uint, uint, void*, void> TexSubImage2D;
    internal delegate* unmanaged[Stdcall]<uint, uint, int, void> TexParameteri;
    internal delegate* unmanaged[Stdcall]<uint, void> ActiveTexture;
    internal delegate* unmanaged[Stdcall]<int, uint*, void> GenFramebuffers;
    internal delegate* unmanaged[Stdcall]<int, uint*, void> DeleteFramebuffers;
    internal delegate* unmanaged[Stdcall]<uint, uint, void> BindFramebuffer;
    internal delegate* unmanaged[Stdcall]<uint, uint, uint, uint, int, void> FramebufferTexture2D;
    internal delegate* unmanaged[Stdcall]<uint, uint> CheckFramebufferStatus;
    internal delegate* unmanaged[Stdcall]<int, int, int, int, int, int, int, int, uint, uint, void> BlitFramebuffer;
    internal delegate* unmanaged[Stdcall]<int, int, int, int, uint, uint, void*, void> ReadPixels;
    internal delegate* unmanaged[Stdcall]<uint, int, void> PixelStorei;
    internal delegate* unmanaged[Stdcall]<int, uint*, void> GenVertexArrays;
    internal delegate* unmanaged[Stdcall]<int, uint*, void> DeleteVertexArrays;
    internal delegate* unmanaged[Stdcall]<uint, void> BindVertexArray;
    internal delegate* unmanaged[Stdcall]<int, uint*, void> GenBuffers;
    internal delegate* unmanaged[Stdcall]<int, uint*, void> DeleteBuffers;
    internal delegate* unmanaged[Stdcall]<uint, uint, void> BindBuffer;
    internal delegate* unmanaged[Stdcall]<uint, nint, void*, uint, void> BufferData;
    internal delegate* unmanaged[Stdcall]<uint, nint, nint, void*, void> BufferSubData;
    internal delegate* unmanaged[Stdcall]<uint, int, uint, byte, int, void*, void> VertexAttribPointer;
    internal delegate* unmanaged[Stdcall]<uint, int, uint, int, void*, void> VertexAttribIPointer;
    internal delegate* unmanaged[Stdcall]<uint, void> EnableVertexAttribArray;
    internal delegate* unmanaged[Stdcall]<uint, uint> CreateShader;
    internal delegate* unmanaged[Stdcall]<uint, int, byte**, int*, void> ShaderSource;
    internal delegate* unmanaged[Stdcall]<uint, void> CompileShader;
    internal delegate* unmanaged[Stdcall]<uint, uint, int*, void> GetShaderiv;
    internal delegate* unmanaged[Stdcall]<uint, int, int*, byte*, void> GetShaderInfoLog;
    internal delegate* unmanaged[Stdcall]<uint, void> DeleteShader;
    internal delegate* unmanaged[Stdcall]<uint> CreateProgram;
    internal delegate* unmanaged[Stdcall]<uint, uint, void> AttachShader;
    internal delegate* unmanaged[Stdcall]<uint, void> LinkProgram;
    internal delegate* unmanaged[Stdcall]<uint, uint, int*, void> GetProgramiv;
    internal delegate* unmanaged[Stdcall]<uint, int, int*, byte*, void> GetProgramInfoLog;
    internal delegate* unmanaged[Stdcall]<uint, void> UseProgram;
    internal delegate* unmanaged[Stdcall]<uint, void> DeleteProgram;
    internal delegate* unmanaged[Stdcall]<uint, byte*, int> GetUniformLocation;
    internal delegate* unmanaged[Stdcall]<int, int, void> Uniform1i;
    internal delegate* unmanaged[Stdcall]<int, float, float, void> Uniform2f;
    internal delegate* unmanaged[Stdcall]<int, float, float, float, float, void> Uniform4f;
    internal delegate* unmanaged[Stdcall]<uint, int, int, void> DrawArrays;
    internal delegate* unmanaged[Stdcall]<int, int, int, int, void> Scissor;

    public bool IsInitialized { get; private set; }

    /// <summary>
    /// Resolves every <see cref="RequiredEntryPoints"/> symbol via <paramref name="getProcAddress"/> (e.g. SDL_GL_GetProcAddress);
    /// throws <see cref="InvalidOperationException"/> listing missing symbols so the host can fall back to the software backend.
    /// </summary>
    public void Initialize(Func<string, IntPtr> getProcAddress)
    {
        ArgumentNullException.ThrowIfNull(getProcAddress);
        _procs.Clear();
        IsInitialized = false;
        var missing = new List<string>();
        IntPtr L(string name)
        {
            IntPtr p = getProcAddress(name);
            if (p == IntPtr.Zero) missing.Add(name);
            else _procs[name] = p;
            return p;
        }

        GetString = (delegate* unmanaged[Stdcall]<uint, byte*>)L("glGetString");
        GetError = (delegate* unmanaged[Stdcall]<uint>)L("glGetError");
        Viewport = (delegate* unmanaged[Stdcall]<int, int, int, int, void>)L("glViewport");
        ClearColor = (delegate* unmanaged[Stdcall]<float, float, float, float, void>)L("glClearColor");
        Clear = (delegate* unmanaged[Stdcall]<uint, void>)L("glClear");
        Enable = (delegate* unmanaged[Stdcall]<uint, void>)L("glEnable");
        Disable = (delegate* unmanaged[Stdcall]<uint, void>)L("glDisable");
        BlendFunc = (delegate* unmanaged[Stdcall]<uint, uint, void>)L("glBlendFunc");
        BlendEquation = (delegate* unmanaged[Stdcall]<uint, void>)L("glBlendEquation");
        GenTextures = (delegate* unmanaged[Stdcall]<int, uint*, void>)L("glGenTextures");
        DeleteTextures = (delegate* unmanaged[Stdcall]<int, uint*, void>)L("glDeleteTextures");
        BindTexture = (delegate* unmanaged[Stdcall]<uint, uint, void>)L("glBindTexture");
        TexImage2D = (delegate* unmanaged[Stdcall]<uint, int, int, int, int, int, uint, uint, void*, void>)L("glTexImage2D");
        TexSubImage2D = (delegate* unmanaged[Stdcall]<uint, int, int, int, int, int, uint, uint, void*, void>)L("glTexSubImage2D");
        TexParameteri = (delegate* unmanaged[Stdcall]<uint, uint, int, void>)L("glTexParameteri");
        ActiveTexture = (delegate* unmanaged[Stdcall]<uint, void>)L("glActiveTexture");
        GenFramebuffers = (delegate* unmanaged[Stdcall]<int, uint*, void>)L("glGenFramebuffers");
        DeleteFramebuffers = (delegate* unmanaged[Stdcall]<int, uint*, void>)L("glDeleteFramebuffers");
        BindFramebuffer = (delegate* unmanaged[Stdcall]<uint, uint, void>)L("glBindFramebuffer");
        FramebufferTexture2D = (delegate* unmanaged[Stdcall]<uint, uint, uint, uint, int, void>)L("glFramebufferTexture2D");
        CheckFramebufferStatus = (delegate* unmanaged[Stdcall]<uint, uint>)L("glCheckFramebufferStatus");
        BlitFramebuffer = (delegate* unmanaged[Stdcall]<int, int, int, int, int, int, int, int, uint, uint, void>)L("glBlitFramebuffer");
        ReadPixels = (delegate* unmanaged[Stdcall]<int, int, int, int, uint, uint, void*, void>)L("glReadPixels");
        PixelStorei = (delegate* unmanaged[Stdcall]<uint, int, void>)L("glPixelStorei");
        GenVertexArrays = (delegate* unmanaged[Stdcall]<int, uint*, void>)L("glGenVertexArrays");
        DeleteVertexArrays = (delegate* unmanaged[Stdcall]<int, uint*, void>)L("glDeleteVertexArrays");
        BindVertexArray = (delegate* unmanaged[Stdcall]<uint, void>)L("glBindVertexArray");
        GenBuffers = (delegate* unmanaged[Stdcall]<int, uint*, void>)L("glGenBuffers");
        DeleteBuffers = (delegate* unmanaged[Stdcall]<int, uint*, void>)L("glDeleteBuffers");
        BindBuffer = (delegate* unmanaged[Stdcall]<uint, uint, void>)L("glBindBuffer");
        BufferData = (delegate* unmanaged[Stdcall]<uint, nint, void*, uint, void>)L("glBufferData");
        BufferSubData = (delegate* unmanaged[Stdcall]<uint, nint, nint, void*, void>)L("glBufferSubData");
        VertexAttribPointer = (delegate* unmanaged[Stdcall]<uint, int, uint, byte, int, void*, void>)L("glVertexAttribPointer");
        VertexAttribIPointer = (delegate* unmanaged[Stdcall]<uint, int, uint, int, void*, void>)L("glVertexAttribIPointer");
        EnableVertexAttribArray = (delegate* unmanaged[Stdcall]<uint, void>)L("glEnableVertexAttribArray");
        CreateShader = (delegate* unmanaged[Stdcall]<uint, uint>)L("glCreateShader");
        ShaderSource = (delegate* unmanaged[Stdcall]<uint, int, byte**, int*, void>)L("glShaderSource");
        CompileShader = (delegate* unmanaged[Stdcall]<uint, void>)L("glCompileShader");
        GetShaderiv = (delegate* unmanaged[Stdcall]<uint, uint, int*, void>)L("glGetShaderiv");
        GetShaderInfoLog = (delegate* unmanaged[Stdcall]<uint, int, int*, byte*, void>)L("glGetShaderInfoLog");
        DeleteShader = (delegate* unmanaged[Stdcall]<uint, void>)L("glDeleteShader");
        CreateProgram = (delegate* unmanaged[Stdcall]<uint>)L("glCreateProgram");
        AttachShader = (delegate* unmanaged[Stdcall]<uint, uint, void>)L("glAttachShader");
        LinkProgram = (delegate* unmanaged[Stdcall]<uint, void>)L("glLinkProgram");
        GetProgramiv = (delegate* unmanaged[Stdcall]<uint, uint, int*, void>)L("glGetProgramiv");
        GetProgramInfoLog = (delegate* unmanaged[Stdcall]<uint, int, int*, byte*, void>)L("glGetProgramInfoLog");
        UseProgram = (delegate* unmanaged[Stdcall]<uint, void>)L("glUseProgram");
        DeleteProgram = (delegate* unmanaged[Stdcall]<uint, void>)L("glDeleteProgram");
        GetUniformLocation = (delegate* unmanaged[Stdcall]<uint, byte*, int>)L("glGetUniformLocation");
        Uniform1i = (delegate* unmanaged[Stdcall]<int, int, void>)L("glUniform1i");
        Uniform2f = (delegate* unmanaged[Stdcall]<int, float, float, void>)L("glUniform2f");
        Uniform4f = (delegate* unmanaged[Stdcall]<int, float, float, float, float, void>)L("glUniform4f");
        DrawArrays = (delegate* unmanaged[Stdcall]<uint, int, int, void>)L("glDrawArrays");
        Scissor = (delegate* unmanaged[Stdcall]<int, int, int, int, void>)L("glScissor");

        if (missing.Count > 0)
        {
            _procs.Clear();
            throw new InvalidOperationException($"OpenGL 3.3 Core unavailable; missing {missing.Count} entry point(s): {string.Join(", ", missing.Take(8))}.");
        }
        IsInitialized = true;
    }

    /// <summary>Returns a resolved entry point; throws when not initialized or unknown.</summary>
    public IntPtr GetProc(string name) =>
        IsInitialized && _procs.TryGetValue(name, out IntPtr p)
            ? p
            : throw new InvalidOperationException($"OpenGL entry point '{name}' is not loaded.");
}
