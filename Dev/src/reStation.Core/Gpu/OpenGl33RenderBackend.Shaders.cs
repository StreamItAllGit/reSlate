// @module: reStation.Core.Gpu.OpenGl33RenderBackend.Shaders
// @purpose: GLSL #version 330 core rasterizer/present/24-bit shaders, shader compiler/linker, VAO/VBO/FBO allocation for OpenGl33RenderBackend.
// @depends-on: System.Runtime.InteropServices, System.Text, reStation.Core.Gpu.OpenGl33Bindings
// @exports: OpenGl33RenderBackend (partial)
// @owner: devv-gpu-gl (T4)

using System.Runtime.InteropServices;
using System.Text;

namespace reStation.Core.Gpu;

public sealed unsafe partial class OpenGl33RenderBackend
{
    [StructLayout(LayoutKind.Sequential)]
    private struct GlVertex
    {
        public float X, Y, W, U, V;
        public uint ColorRgba, TexInfo, TexWindow;
    }

    private const string DrawVertexGlsl = """
        #version 330 core
        layout(location = 0) in vec3 aPosW;
        layout(location = 1) in vec2 aUv;
        layout(location = 2) in vec4 aColor;
        layout(location = 3) in uint aTexInfo;
        layout(location = 4) in uint aTexWindow;
        out vec2 vUv;
        out vec4 vColor;
        flat out uint vTexInfo;
        flat out uint vTexWindow;
        void main() {
            float w = max(aPosW.z, 1.0);
            float ndcX = (aPosW.x / 512.0) - 1.0;
            float ndcY = (aPosW.y / 256.0) - 1.0;
            gl_Position = vec4(ndcX * w, ndcY * w, 0.0, w);
            vUv = aUv; vColor = aColor; vTexInfo = aTexInfo; vTexWindow = aTexWindow;
        }
        """;

    private const string DrawFragmentGlsl = """
        #version 330 core
        uniform usampler2D uVramTex;
        uniform int uStpPass;
        in vec2 vUv;
        in vec4 vColor;
        flat in uint vTexInfo;
        flat in uint vTexWindow;
        out vec4 fragColor;
        uint readVram16(int x, int y) { return texelFetch(uVramTex, ivec2(x & 1023, y & 511), 0).r; }
        void main() {
            bool textured = (vTexInfo & (1u << 22)) != 0u;
            bool semi = (vTexInfo & (1u << 24)) != 0u;
            uint semiMode = (vTexInfo >> 25) & 3u;
            if (!textured) {
                float a = semi ? (semiMode == 0u ? 0.5 : (semiMode == 3u ? 0.25 : 1.0)) : 1.0;
                fragColor = vec4(vColor.rgb, a);
                return;
            }
            int tu = int(floor(vUv.x + 1e-4)) & 255;
            int tv = int(floor(vUv.y + 1e-4)) & 255;
            int mx = int(vTexWindow & 31u), my = int((vTexWindow >> 5) & 31u);
            int ox = int((vTexWindow >> 10) & 31u), oy = int((vTexWindow >> 15) & 31u);
            tu = (tu & ~(mx << 3)) | ((ox & mx) << 3);
            tv = (tv & ~(my << 3)) | ((oy & my) << 3);
            int bx = int(vTexInfo & 15u) * 64, by = int((vTexInfo >> 4) & 1u) * 256;
            uint depth = (vTexInfo >> 5) & 3u;
            int cx = int((vTexInfo >> 7) & 63u) * 16, cy = int((vTexInfo >> 13) & 511u);
            uint texel;
            if (depth == 0u) {
                uint w16 = readVram16(bx + (tu >> 2), by + tv);
                texel = readVram16(cx + int((w16 >> uint((tu & 3) * 4)) & 15u), cy);
            } else if (depth == 1u) {
                uint w16 = readVram16(bx + (tu >> 1), by + tv);
                texel = readVram16(cx + int((w16 >> uint((tu & 1) * 8)) & 255u), cy);
            } else {
                texel = readVram16(bx + tu, by + tv);
            }
            if (texel == 0u) discard;
            bool stp = (texel & 0x8000u) != 0u;
            if ((uStpPass == 1 && stp) || (uStpPass == 2 && !stp)) discard;
            vec3 rgb = vec3(float(texel & 31u), float((texel >> 5) & 31u), float((texel >> 10) & 31u)) / 31.0;
            if ((vTexInfo & (1u << 23)) == 0u) rgb = clamp(rgb * vColor.rgb * (255.0 / 128.0), 0.0, 1.0);
            float a = (semi && stp) ? (semiMode == 0u ? 0.5 : (semiMode == 3u ? 0.25 : 1.0)) : 1.0;
            fragColor = vec4(rgb, a);
        }
        """;

    private const string BlitVertexGlsl = """
        #version 330 core
        layout(location = 0) in vec2 aPos;
        layout(location = 1) in vec2 aUv;
        uniform vec4 uDstRect; // ndc x0, y0, w, h
        uniform vec4 uSrcRect; // uv  u0, v0, w, h
        out vec2 vUv;
        void main() {
            gl_Position = vec4(uDstRect.xy + aPos * uDstRect.zw, 0.0, 1.0);
            vUv = uSrcRect.xy + aUv * uSrcRect.zw;
        }
        """;

    private const string BlitFragmentGlsl = """
        #version 330 core
        uniform usampler2D uVramTex;
        uniform sampler2D uColorTex;
        uniform int uMode; // 0: 15-bit VRAM->FBO, 1/3: ColorTex->FBO, 2: 24-bit MDEC->FBO
        uniform vec4 uDispInfo; // dispX, dispY, dispW, dispH
        in vec2 vUv;
        out vec4 fragColor;
        uint readByte(int bp, int y) {
            uint w = texelFetch(uVramTex, ivec2((bp >> 1) & 1023, y & 511), 0).r;
            return (w >> uint((bp & 1) * 8)) & 255u;
        }
        void main() {
            if (uMode == 0) {
                uint px = texelFetch(uVramTex, ivec2(int(floor(vUv.x)) & 1023, int(floor(vUv.y)) & 511), 0).r;
                fragColor = vec4(float(px & 31u) / 31.0, float((px >> 5) & 31u) / 31.0, float((px >> 10) & 31u) / 31.0, 1.0);
            } else if (uMode == 2) {
                int px = int(clamp(floor(vUv.x * uDispInfo.z), 0.0, uDispInfo.z - 1.0));
                int py = int(clamp(floor(vUv.y * uDispInfo.w), 0.0, uDispInfo.w - 1.0));
                int bp = int(uDispInfo.x) * 2 + px * 3, vy = (int(uDispInfo.y) + py) & 511;
                fragColor = vec4(float(readByte(bp, vy)), float(readByte(bp + 1, vy)), float(readByte(bp + 2, vy)), 255.0) / 255.0;
            } else {
                fragColor = texture(uColorTex, vUv);
            }
        }
        """;

    private void EnsureGlResources()
    {
        if (_glReady) return;
        _drawProg = LinkProgram(DrawVertexGlsl, DrawFragmentGlsl);
        _uDrawVramTex = GetUniformLoc(_drawProg, "uVramTex");
        _uDrawStpPass = GetUniformLoc(_drawProg, "uStpPass");

        _blitProg = LinkProgram(BlitVertexGlsl, BlitFragmentGlsl);
        _uBlitVramTex = GetUniformLoc(_blitProg, "uVramTex");
        _uBlitColorTex = GetUniformLoc(_blitProg, "uColorTex");
        _uBlitMode = GetUniformLoc(_blitProg, "uMode");
        _uBlitDstRect = GetUniformLoc(_blitProg, "uDstRect");
        _uBlitSrcRect = GetUniformLoc(_blitProg, "uSrcRect");
        _uBlitDispInfo = GetUniformLoc(_blitProg, "uDispInfo");

        uint vao = 0, vbo = 0;
        Gl.GenVertexArrays(1, &vao); _drawVao = vao;
        Gl.GenBuffers(1, &vbo); _drawVbo = vbo;
        Gl.BindVertexArray(_drawVao);
        Gl.BindBuffer(GlConst.ArrayBuffer, _drawVbo);
        Gl.BufferData(GlConst.ArrayBuffer, MaxBatchVertices * sizeof(GlVertex), null, GlConst.DynamicDraw);
        int stride = sizeof(GlVertex);
        Gl.EnableVertexAttribArray(0); Gl.VertexAttribPointer(0, 3, GlConst.Float, (byte)GlConst.False, stride, (void*)0);
        Gl.EnableVertexAttribArray(1); Gl.VertexAttribPointer(1, 2, GlConst.Float, (byte)GlConst.False, stride, (void*)12);
        Gl.EnableVertexAttribArray(2); Gl.VertexAttribPointer(2, 4, GlConst.UnsignedByte, (byte)GlConst.True, stride, (void*)20);
        Gl.EnableVertexAttribArray(3); Gl.VertexAttribIPointer(3, 1, GlConst.UnsignedInt, stride, (void*)24);
        Gl.EnableVertexAttribArray(4); Gl.VertexAttribIPointer(4, 1, GlConst.UnsignedInt, stride, (void*)28);

        Gl.GenVertexArrays(1, &vao); _blitVao = vao;
        Gl.GenBuffers(1, &vbo); _blitVbo = vbo;
        Span<float> quad = stackalloc float[] { 0,0,0,0, 1,0,1,0, 1,1,1,1, 0,0,0,0, 1,1,1,1, 0,1,0,1 };
        Gl.BindVertexArray(_blitVao);
        Gl.BindBuffer(GlConst.ArrayBuffer, _blitVbo);
        fixed (float* qPtr = quad) Gl.BufferData(GlConst.ArrayBuffer, quad.Length * sizeof(float), qPtr, GlConst.StaticDraw);
        Gl.EnableVertexAttribArray(0); Gl.VertexAttribPointer(0, 2, GlConst.Float, (byte)GlConst.False, 16, (void*)0);
        Gl.EnableVertexAttribArray(1); Gl.VertexAttribPointer(1, 2, GlConst.Float, (byte)GlConst.False, 16, (void*)8);
        Gl.BindVertexArray(0);

        _vramTex = CreateTexture2D(GlConst.R16Ui, PsxGpu.VramWidth, PsxGpu.VramHeight, GlConst.RedInteger, GlConst.UnsignedShort, GlConst.Nearest, GlConst.Repeat);
        fixed (ushort* vp = _vramMirror)
        {
            Gl.BindTexture(GlConst.Texture2D, _vramTex);
            Gl.PixelStorei(GlConst.UnpackAlignment, 2);
            Gl.PixelStorei(GlConst.UnpackRowLength, 0);
            Gl.TexSubImage2D(GlConst.Texture2D, 0, 0, 0, PsxGpu.VramWidth, PsxGpu.VramHeight, GlConst.RedInteger, GlConst.UnsignedShort, vp);
        }
        EnsureVramFboSize();
        EnsureDisplayFboSize(320, 240);
        _glReady = true;
    }

    private void EnsureVramFboSize()
    {
        if (_vramFbo != 0 && _vramFboScale == _scale) return;
        _vramFboScale = _scale;
        int w = PsxGpu.VramWidth * _scale, h = PsxGpu.VramHeight * _scale;
        RecreateFbo(ref _vramFbo, ref _vramColorTex, w, h, GlConst.Repeat);
        BlitVramRegionToFbo(0, 0, PsxGpu.VramWidth, PsxGpu.VramHeight);
    }

    private void EnsureDisplayFboSize(int w, int h)
    {
        if (_displayFbo != 0 && _dispFboW == w && _dispFboH == h) return;
        _dispFboW = w; _dispFboH = h;
        RecreateFbo(ref _displayFbo, ref _displayTex, w, h, GlConst.ClampToEdge);
    }

    private void EnsureScratchFboSize(int w, int h)
    {
        if (_scratchFbo != 0 && _scratchW >= w && _scratchH >= h) return;
        _scratchW = Math.Max(w, _scratchW); _scratchH = Math.Max(h, _scratchH);
        RecreateFbo(ref _scratchFbo, ref _scratchTex, _scratchW, _scratchH, GlConst.ClampToEdge);
    }

    private void RecreateFbo(ref uint fbo, ref uint tex, int w, int h, int wrap)
    {
        if (fbo != 0) { uint f = fbo; Gl.DeleteFramebuffers(1, &f); fbo = 0; }
        if (tex != 0) { uint t = tex; Gl.DeleteTextures(1, &t); tex = 0; }
        tex = CreateTexture2D(GlConst.Rgba8, w, h, GlConst.Rgba, GlConst.UnsignedByte, GlConst.Nearest, wrap);
        uint nf = 0; Gl.GenFramebuffers(1, &nf); fbo = nf;
        Gl.BindFramebuffer(GlConst.Framebuffer, fbo);
        Gl.FramebufferTexture2D(GlConst.Framebuffer, GlConst.ColorAttachment0, GlConst.Texture2D, tex, 0);
        uint status = Gl.CheckFramebufferStatus(GlConst.Framebuffer);
        Gl.BindFramebuffer(GlConst.Framebuffer, 0);
        if (status != GlConst.FramebufferComplete)
            throw new InvalidOperationException($"OpenGL framebuffer incomplete: 0x{status:X4}.");
    }

    private uint CreateTexture2D(int internalFmt, int w, int h, uint fmt, uint type, int filter, int wrap)
    {
        uint t = 0; Gl.GenTextures(1, &t);
        Gl.BindTexture(GlConst.Texture2D, t);
        Gl.TexParameteri(GlConst.Texture2D, GlConst.TextureMinFilter, filter);
        Gl.TexParameteri(GlConst.Texture2D, GlConst.TextureMagFilter, filter);
        Gl.TexParameteri(GlConst.Texture2D, GlConst.TextureWrapS, wrap);
        Gl.TexParameteri(GlConst.Texture2D, GlConst.TextureWrapT, wrap);
        Gl.TexImage2D(GlConst.Texture2D, 0, internalFmt, w, h, 0, fmt, type, null);
        return t;
    }

    private uint LinkProgram(string vsSrc, string fsSrc)
    {
        uint vs = CompileShader(GlConst.VertexShader, vsSrc);
        uint fs = CompileShader(GlConst.FragmentShader, fsSrc);
        uint prog = Gl.CreateProgram();
        Gl.AttachShader(prog, vs); Gl.AttachShader(prog, fs);
        Gl.LinkProgram(prog);
        Gl.DeleteShader(vs); Gl.DeleteShader(fs);
        int ok = 0; Gl.GetProgramiv(prog, GlConst.LinkStatus, &ok);
        if (ok != 0) return prog;
        byte* buf = stackalloc byte[512]; int len = 0;
        Gl.GetProgramInfoLog(prog, 512, &len, buf);
        Gl.DeleteProgram(prog);
        throw new InvalidOperationException($"GLSL link error: {Encoding.UTF8.GetString(buf, Math.Clamp(len, 0, 511))}");
    }

    private uint CompileShader(uint type, string src)
    {
        uint sh = Gl.CreateShader(type);
        byte[] utf8 = Encoding.UTF8.GetBytes(src);
        int len = utf8.Length;
        fixed (byte* p = utf8) { byte* pp = p; Gl.ShaderSource(sh, 1, &pp, &len); }
        Gl.CompileShader(sh);
        int ok = 0; Gl.GetShaderiv(sh, GlConst.CompileStatus, &ok);
        if (ok != 0) return sh;
        byte* buf = stackalloc byte[512]; int logLen = 0;
        Gl.GetShaderInfoLog(sh, 512, &logLen, buf);
        Gl.DeleteShader(sh);
        throw new InvalidOperationException($"GLSL compile error: {Encoding.UTF8.GetString(buf, Math.Clamp(logLen, 0, 511))}");
    }

    private int GetUniformLoc(uint prog, string name)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(name + '\0');
        fixed (byte* p = bytes) return Gl.GetUniformLocation(prog, p);
    }
}
