// @module: reStation.Core.Gpu.OpenGl33Bindings
// @purpose: SDL3-free OpenGL 3.3 Core entry-point loader initialized strictly through an injected getProcAddress delegate.
// @depends-on: System
// @exports: OpenGl33Bindings
// @owner: devv-gpu-gl (T4) — Initialize(Func<string, IntPtr>) contract T1-locked; T4 converts resolved pointers to delegate* unmanaged[Stdcall].

namespace reStation.Core.Gpu;

public sealed class OpenGl33Bindings
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

    public bool IsInitialized { get; private set; }

    /// <summary>
    /// Resolves every <see cref="RequiredEntryPoints"/> symbol via <paramref name="getProcAddress"/> (e.g. SDL_GL_GetProcAddress);
    /// throws <see cref="InvalidOperationException"/> listing missing symbols so the host can fall back to the software backend.
    /// </summary>
    public void Initialize(Func<string, IntPtr> getProcAddress)
    {
        ArgumentNullException.ThrowIfNull(getProcAddress);
        _procs.Clear();
        var missing = new List<string>();
        foreach (string name in RequiredEntryPoints)
        {
            IntPtr p = getProcAddress(name);
            if (p == IntPtr.Zero) missing.Add(name);
            else _procs[name] = p;
        }
        if (missing.Count > 0)
        {
            IsInitialized = false;
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
