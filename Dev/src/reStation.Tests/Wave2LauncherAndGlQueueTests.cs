// @module: reStation.Tests.Wave2LauncherAndGlQueueTests
// @purpose: Verifies LauncherArgsHelper (--wait, --preload-media, JobObjectGuard), OpenGl33RenderBackend off-thread sequence & lifecycle, and build_release.ps1 manifest parity.
// @depends-on: Xunit, System.Diagnostics, System.Runtime.InteropServices, System.Text.Json, Foundation.Core, reStation.Contracts, reStation.Core.Gpu, reStation.Launcher
// @exports: Wave2LauncherAndGlQueueTests
// @owner: devv-pkg (Wave 2 Slice 3)

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Foundation.Core;
using reStation.Contracts;
using reStation.Core.Gpu;
using reStation.Launcher;
using Xunit;

namespace reStation.Tests;

public sealed class Wave2LauncherAndGlQueueTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"restation-w2-pkg-{Guid.NewGuid():N}");

    public void Dispose() { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true); }

    [Theory]
    [InlineData(new[] { "--wait" }, true)]
    [InlineData(new[] { " --WAIT ", "--preload-media", "rom.cue" }, true)]
    [InlineData(new[] { "--verify" }, true)]
    [InlineData(new[] { "--headless" }, true)]
    [InlineData(new[] { "--help" }, true)]
    [InlineData(new[] { "-h" }, true)]
    [InlineData(new[] { "/?" }, true)]
    [InlineData(new[] { "--module", "game.recomp.json", "--preload-media", "data\\rom\\disc.cue" }, false)]
    [InlineData(new string[0], false)]
    public void LauncherArgsHelper_RequiresSyncWait_MatchesWaitAndCliFlags(string[] args, bool expected)
    {
        Assert.Equal(expected, LauncherArgsHelper.RequiresSyncWait(args));
        Assert.False(LauncherArgsHelper.RequiresSyncWait(null));
    }

    [Fact]
    public void LauncherArgsHelper_ResolveRelativePathArgs_ResolvesPreloadMediaAndPreservesRooted()
    {
        string baseDir = Path.Combine(_tempRoot, "caller");
        string rooted = Path.Combine(_tempRoot, "abs", "disc.cue");
        string[] raw = { "--wait", "--preload-media", "\"mods\\game.cue\"", "--module", rooted, "--scale", "4" };

        string[] resolved = LauncherArgsHelper.ResolveRelativePathArgs(raw, baseDir);

        Assert.Equal("--wait", resolved[0]);
        Assert.Equal(Path.GetFullPath(Path.Combine(baseDir, "mods", "game.cue")), resolved[2]);
        Assert.Equal(rooted, resolved[4]);
        Assert.Equal("4", resolved[6]);
        Assert.Empty(LauncherArgsHelper.ResolveRelativePathArgs(null, baseDir));
    }

    [Fact]
    public void LauncherArgsHelper_AttachJobGuard_TerminatesChildOnDispose()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c ping -n 20 127.0.0.1 >nul",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using Process? proc = Process.Start(psi);
        Assert.NotNull(proc);
        try
        {
            JobObjectGuard? guard = LauncherArgsHelper.AttachJobGuard(proc!);
            Assert.NotNull(guard);
            Assert.True(guard!.IsSupported);
            guard.Dispose();
            Assert.True(proc!.WaitForExit(3000), "Disposing JobObjectGuard should terminate the guarded child process.");
        }
        finally { if (!proc!.HasExited) proc.Kill(entireProcessTree: true); }
    }

    [Fact]
    public void OpenGl33Bindings_Initialize_FailsClosedOnMissingProcAndSucceedsWhenComplete()
    {
        using var stubs = new StubGlProcTable();
        var missing = new OpenGl33Bindings();
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            missing.Initialize(name => name == "glDrawArrays" ? IntPtr.Zero : stubs.GetProc(name)));
        Assert.Contains("glDrawArrays", ex.Message, StringComparison.Ordinal);

        var ok = new OpenGl33Bindings();
        ok.Initialize(stubs.GetProc);
        Assert.True(ok.IsInitialized);
        Assert.NotEqual(IntPtr.Zero, ok.GetProc("glDrawArrays"));
    }

    [Fact]
    public async Task OpenGl33RenderBackend_OffThreadSubmissionsAndOwnerPresent_CompletesLifecycleCleanly()
    {
        using var stubs = new StubGlProcTable();
        var gl = new OpenGl33Bindings();
        gl.Initialize(stubs.GetProc);

        using var backend = new OpenGl33RenderBackend(gl);
        backend.Initialize(320, 240, 2);
        Assert.Equal(2, backend.ScaleFactor);
        backend.ScaleFactor = 3;
        Assert.Equal(4, backend.ScaleFactor);
        backend.ScaleFactor = 2;
        Assert.False(backend.TryReadVramRect(0, 0, 4, 4, new ushort[16]));
        backend.AttachPgxp(null);
        backend.EndFrameAndPresent(0, 0, 320, 240, is24Bit: false, widescreen16By9: false);

        await Task.Run(() =>
        {
            backend.SetTextureWindow(0x1F, 0x1F, 0x04, 0x08);
            ushort[] tex = { 0x7FFF, 0x03E0, 0x001F, 0x7C00 };
            backend.UploadVramRect(0, 0, 2, 2, tex);
            backend.CopyVramRect(0, 0, 16, 0, 2, 2);
            backend.FillDisplayRect(32, 0, 8, 8, 0x00FF00U);
            uint[] tri = { 0x20FF0000U, 10U | (10U << 16), 60U | (10U << 16), 10U | (60U << 16) };
            backend.SubmitPrimitive(0x20U, tri, 0, 0, 0, 0, 0, 319, 239);
        });

        backend.EndFrameAndPresent(0, 0, 320, 240, is24Bit: false, widescreen16By9: false);
        byte[] rgba = backend.CaptureRgba(out int w, out int h);
        Assert.Equal(1L, backend.PrimitivesSubmitted);
        Assert.Equal(640, w);
        Assert.Equal(480, h);
        Assert.Equal(640 * 480 * 4, rgba.Length);

        backend.PresentRgba(320, 240, new byte[320 * 240 * 4]);
        backend.Dispose();
        Assert.Throws<ObjectDisposedException>(() => backend.EndFrameAndPresent(0, 0, 320, 240, false, false));
    }

    [Fact]
    public void OpenGl33RenderBackend_OffThreadSameRectUploads_CoalesceViaArrayPool()
    {
        using var stubs = new StubGlProcTable();
        var gl = new OpenGl33Bindings();
        gl.Initialize(stubs.GetProc);

        using var backend = new OpenGl33RenderBackend(gl);
        backend.Initialize(320, 240, 1);

        Exception? workerEx = null;
        var worker = new Thread(() =>
        {
            try
            {
                ushort[] first = { 0x001F, 0x001F, 0x001F, 0x001F };
                ushort[] latest = { 0x7C00, 0x7C00, 0x7C00, 0x7C00 };
                backend.UploadVramRect(8, 8, 2, 2, first);
                backend.UploadVramRect(8, 8, 2, 2, latest);
                Assert.Equal(1, backend.QueuedOpCount);
            }
            catch (Exception ex)
            {
                workerEx = ex;
            }
        }) { IsBackground = true };
        worker.Start();
        worker.Join();
        Assert.Null(workerEx);

        backend.EndFrameAndPresent(0, 0, 320, 240, is24Bit: false, widescreen16By9: false);
        Assert.Equal(0, backend.QueuedOpCount);
    }

    [Fact]
    public void BuildReleaseScript_AndMetadataFiles_Match020DualManifestContract()
    {
        string devDir = FindDevRoot();
        string script = File.ReadAllText(Path.Combine(devDir, "scripts", "build_release.ps1"));
        Assert.True(File.ReadAllLines(Path.Combine(devDir, "scripts", "build_release.ps1")).Length <= 400);
        Assert.True(File.ReadAllLines(Path.Combine(devDir, "scripts", "sanitize_release_docs.ps1")).Length <= 400);
        Assert.DoesNotContain("reEngine-Setup-v0.1.3-win-x64.exe", script, StringComparison.Ordinal);
        Assert.Contains("reStation.Decomp.dll", script, StringComparison.Ordinal);
        Assert.Contains("settings.example.json", script, StringComparison.Ordinal);
        Assert.DoesNotContain(@"data\rom\.keep", script, StringComparison.Ordinal);
        Assert.Contains("LICENSE", script, StringComparison.Ordinal);
        Assert.Contains("--preload-media", script, StringComparison.Ordinal);
        Assert.Contains("--wait", script, StringComparison.Ordinal);
        Assert.Contains("--version", script, StringComparison.Ordinal);
        Assert.Contains("Remove-Item -LiteralPath $sdkDir", script, StringComparison.Ordinal);
        Assert.Contains("engine.manifest.json", script, StringComparison.Ordinal);
        Assert.Contains("sdk-manifest.json", script, StringComparison.Ordinal);
        Assert.Contains("supportedEngineTracks", script, StringComparison.Ordinal);
        Assert.Contains("README.md", script, StringComparison.Ordinal);

        Assert.Equal("0.2.1", File.ReadAllText(Path.Combine(devDir, "version.txt")).Trim());
        using JsonDocument appManifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(devDir, "app.manifest.json")));
        Assert.Equal("0.2.1", appManifest.RootElement.GetProperty("version").GetString());
    }

    private static string FindDevRoot()
    {
        for (string? cur = AppContext.BaseDirectory; !string.IsNullOrEmpty(cur); cur = Path.GetDirectoryName(cur))
        {
            if (File.Exists(Path.Combine(cur, "Dev", "version.txt"))) return Path.Combine(cur, "Dev");
            if (File.Exists(Path.Combine(cur, "version.txt"))) return cur;
        }
        throw new DirectoryNotFoundException("Could not locate Dev root from test base directory.");
    }

    private sealed class StubGlProcTable : IDisposable
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr FnPtr();
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint FnU0();
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint FnU1(uint a);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int FnI2P(uint a, IntPtr b);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void FnV1(uint a);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void FnV2(uint a, uint b);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void FnV3(uint a, int b, int c);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void FnV4(int a, int b, int c, int d);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void FnV4F(float a, float b, float c, float d);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void FnV2F(int a, float b, float c);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void FnV5F(int a, float b, float c, float d, float e);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void FnGen(int n, IntPtr ptr);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void FnBufData(uint t, nint s, IntPtr p, uint u);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void FnBufSub(uint t, nint o, nint s, IntPtr p);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void FnQuery(uint id, uint p, IntPtr ptr);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void FnLog(uint id, int m, IntPtr l, IntPtr b);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void FnShaderSrc(uint s, int c, IntPtr st, IntPtr l);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void FnTexImg(uint t, int l, int i, int w, int h, int b, uint f, uint ty, IntPtr p);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void FnTexSub(uint t, int l, int x, int y, int w, int h, uint f, uint ty, IntPtr p);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void FnFboTex(uint t, uint a, uint tt, uint tx, int l);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void FnBlit(int sx0, int sy0, int sx1, int sy1, int dx0, int dy0, int dx1, int dy1, uint m, uint f);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void FnReadPx(int x, int y, int w, int h, uint f, uint t, IntPtr p);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void FnAttr(uint i, int s, uint t, byte n, int st, IntPtr p);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void FnAttrI(uint i, int s, uint t, int st, IntPtr p);

        private readonly List<Delegate> _keepAlive = new();
        private readonly Dictionary<string, IntPtr> _map = new(StringComparer.Ordinal);
        private int _nextId;

        public StubGlProcTable()
        {
            IntPtr Reg(Delegate d) { _keepAlive.Add(d); return Marshal.GetFunctionPointerForDelegate(d); }
            IntPtr genPtr = Reg(new FnGen((n, p) => { for (int i = 0; i < n; i++) Marshal.WriteInt32(p, i * 4, Interlocked.Increment(ref _nextId)); }));
            IntPtr noopV1 = Reg(new FnV1(_ => { })), noopV2 = Reg(new FnV2((_, _) => { })), noopV3 = Reg(new FnV3((_, _, _) => { })), noopV4 = Reg(new FnV4((_, _, _, _) => { }));
            IntPtr queryOk = Reg(new FnQuery((_, _, p) => { if (p != IntPtr.Zero) Marshal.WriteInt32(p, 1); }));
            IntPtr infoLog = Reg(new FnLog((_, _, l, _) => { if (l != IntPtr.Zero) Marshal.WriteInt32(l, 0); }));
            IntPtr createId = Reg(new FnU1(_ => (uint)Interlocked.Increment(ref _nextId)));

            _map["glGetString"] = Reg(new FnPtr(() => IntPtr.Zero));
            _map["glGetError"] = Reg(new FnU0(() => 0U));
            _map["glViewport"] = _map["glScissor"] = noopV4;
            _map["glClearColor"] = Reg(new FnV4F((_, _, _, _) => { }));
            _map["glClear"] = _map["glEnable"] = _map["glDisable"] = _map["glBlendEquation"] = _map["glActiveTexture"] = _map["glBindVertexArray"] = _map["glCompileShader"] = _map["glDeleteShader"] = _map["glLinkProgram"] = _map["glUseProgram"] = _map["glDeleteProgram"] = _map["glEnableVertexAttribArray"] = noopV1;
            _map["glBlendFunc"] = _map["glBindTexture"] = _map["glBindFramebuffer"] = _map["glBindBuffer"] = _map["glAttachShader"] = _map["glPixelStorei"] = _map["glUniform1i"] = noopV2;
            _map["glTexParameteri"] = _map["glDrawArrays"] = noopV3;
            _map["glGenTextures"] = _map["glGenFramebuffers"] = _map["glGenVertexArrays"] = _map["glGenBuffers"] = genPtr;
            _map["glDeleteTextures"] = _map["glDeleteFramebuffers"] = _map["glDeleteVertexArrays"] = _map["glDeleteBuffers"] = noopV2;
            _map["glTexImage2D"] = Reg(new FnTexImg((_, _, _, _, _, _, _, _, _) => { }));
            _map["glTexSubImage2D"] = Reg(new FnTexSub((_, _, _, _, _, _, _, _, _) => { }));
            _map["glFramebufferTexture2D"] = Reg(new FnFboTex((_, _, _, _, _) => { }));
            _map["glCheckFramebufferStatus"] = Reg(new FnU1(_ => 0x8CD5U));
            _map["glBlitFramebuffer"] = Reg(new FnBlit((_, _, _, _, _, _, _, _, _, _) => { }));
            _map["glReadPixels"] = Reg(new FnReadPx((_, _, _, _, _, _, _) => { }));
            _map["glBufferData"] = Reg(new FnBufData((_, _, _, _) => { }));
            _map["glBufferSubData"] = Reg(new FnBufSub((_, _, _, _) => { }));
            _map["glVertexAttribPointer"] = Reg(new FnAttr((_, _, _, _, _, _) => { }));
            _map["glVertexAttribIPointer"] = Reg(new FnAttrI((_, _, _, _, _) => { }));
            _map["glCreateShader"] = createId;
            _map["glCreateProgram"] = Reg(new FnU0(() => (uint)Interlocked.Increment(ref _nextId)));
            _map["glShaderSource"] = Reg(new FnShaderSrc((_, _, _, _) => { }));
            _map["glGetShaderiv"] = _map["glGetProgramiv"] = queryOk;
            _map["glGetShaderInfoLog"] = _map["glGetProgramInfoLog"] = infoLog;
            _map["glGetUniformLocation"] = Reg(new FnI2P((_, _) => 1));
            _map["glUniform2f"] = Reg(new FnV2F((_, _, _) => { }));
            _map["glUniform4f"] = Reg(new FnV5F((_, _, _, _, _) => { }));
        }

        public IntPtr GetProc(string name) => _map.TryGetValue(name, out IntPtr p) ? p : IntPtr.Zero;
        public void Dispose() => _keepAlive.Clear();
    }
}
