// @module: reStation.Tests.Wave2CliAndFixtureTests
// @purpose: Wave 2 Slice 2 verification for CLI --preload-media/--wait, priority/containment, BLINDSPOT-01/02, corrupt RST1, IReStationHostEx, VisibleTestHarness, and >12 overlays.
// @depends-on: Xunit, System.Buffers.Binary, System.Diagnostics, System.Text, System.Text.Json, Foundation.Core, Foundation.Retro, reStation.Compiler, reStation.Contracts, reStation.Core, reStation.Player
// @exports: Wave2CliAndFixtureTests

using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Foundation.Core;
using Foundation.Retro;
using reStation.Compiler;
using reStation.Contracts;
using reStation.Core;
using reStation.Core.Cpu;
using reStation.Core.DiscIngestion;
using reStation.Core.Gpu;
using reStation.Core.Saves;
using reStation.Player;
using Xunit;
using PlayerProgram = reStation.Player.Program;

namespace reStation.Tests;

public sealed class Wave2CliAndFixtureTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"restation-w2-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true);
    }

    [Fact]
    public void CliOptions_Parse_PreloadMediaWaitAndGlAlias_ValidatesStrictly()
    {
        Assert.Contains("--preload-media", CliOptions.HelpText, StringComparison.Ordinal);
        Assert.Contains("--wait", CliOptions.HelpText, StringComparison.Ordinal);

        CliOptions opts = CliOptions.Parse(new[] { "--preload-media", "data\\rom\\kf.cue", "--wait", "--backend", "gl" });
        Assert.Equal("data\\rom\\kf.cue", opts.PreloadMediaPath);
        Assert.True(opts.ShouldWait);
        Assert.Equal(PsxRenderBackendIds.OpenGl, opts.BackendId);

        Assert.Throws<ArgumentException>(() => CliOptions.Parse(new[] { "--preload-media" }));
        Assert.Throws<ArgumentException>(() => CliOptions.Parse(new[] { "--preload-media", "--wait" }));
        Assert.Throws<ArgumentException>(() => CliOptions.Parse(new[] { "--preload-media", "   " }));
    }

    [Fact]
    public void Program_ResolveMediaCandidateAndVerify_EnforcesPriorityAndPropagatesTelemetry()
    {
        string modDir = Path.Combine(_tempRoot, "mod");
        string romDir = Path.Combine(modDir, "data", "rom");
        string sandboxDir = Path.Combine(_tempRoot, "Sandbox");
        string dataDir = Path.Combine(sandboxDir, "data");
        Directory.CreateDirectory(romDir);
        Directory.CreateDirectory(dataDir);

        string disc = Path.Combine(dataDir, "explicit.bin");
        string preload = Path.Combine(dataDir, "preload.bin");
        string romFallback = Path.Combine(romDir, "fallback.bin");
        File.WriteAllBytes(disc, new byte[2048]);
        File.WriteAllBytes(preload, new byte[2048]);
        File.WriteAllBytes(romFallback, new byte[2048]);
        var manifest = new ReStationModuleManifest { ModuleId = "kf", SerialNumber = "SLUS-00158", PreloadRomDir = "data\\rom" };

        CliOptions both = CliOptions.Parse(new[] { "--disc", disc, "--preload-media", preload });
        Assert.Equal(disc, PlayerProgram.ResolveMediaCandidate(both, manifest, modDir, null));
        CliOptions preloadOnly = CliOptions.Parse(new[] { "--preload-media", preload });
        Assert.Equal(preload, PlayerProgram.ResolveMediaCandidate(preloadOnly, manifest, modDir, null));
        CliOptions neither = CliOptions.Parse(Array.Empty<string>());
        Assert.Equal(romFallback, PlayerProgram.ResolveMediaCandidate(neither, manifest, modDir, null));

        string logDir = Path.Combine(sandboxDir, "logs");
        int verifyExit = PlayerProgram.RunMain(new[] { "--verify", "--preload-media", preload, "--scale", "4", "--backend", "software", "--test-mode", "--data-dir", dataDir, "--log-dir", logDir, "--enable-logs" });
        Assert.Equal(0, verifyExit);
        string logText = File.ReadAllText(Path.Combine(logDir, "runtime.log"));
        Assert.Contains("[HEARTBEAT]", logText, StringComparison.Ordinal);
        Assert.Contains("resolutionScale=4 backend=software", logText, StringComparison.Ordinal);
    }

    [Fact]
    public void Program_RunMain_ContainmentViolationReturnsExit3_MissingMediaReturnsExit4()
    {
        string sandboxDir = Path.Combine(_tempRoot, "Sandbox");
        string dataDir = Path.Combine(sandboxDir, "data");
        string logDir = Path.Combine(sandboxDir, "logs");
        Directory.CreateDirectory(dataDir);
        string outside = Path.Combine(_tempRoot, "outside.cue");
        File.WriteAllText(outside, "FILE \"outside.bin\" BINARY\n");

        int exitEscaping = PlayerProgram.RunMain(new[] { "--preload-media", outside, "--test-mode", "--data-dir", dataDir, "--log-dir", logDir, "--headless", "--wait", "--enable-logs" });
        Assert.Equal(3, exitEscaping);
        Assert.Contains("[ERROR] [SANDBOX]", File.ReadAllText(Path.Combine(logDir, "runtime.log")), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(logDir, "*crash*"));

        string missingInside = Path.Combine(dataDir, "missing.cue");
        int exitMissing = PlayerProgram.RunMain(new[] { "--preload-media", missingInside, "--test-mode", "--data-dir", dataDir, "--log-dir", logDir, "--headless", "--wait" });
        Assert.Equal(4, exitMissing);
    }

    [Fact]
    public void HostVideoSurface_HeadlessOverGl_LogsBlindspot02FallbackAndForcesSoftware()
    {
        string logDir = Path.Combine(_tempRoot, "logs-gl");
        using var logger = new ReStationLogger(logDir, enabled: true);
        using HostVideoSurface surface = HostVideoSurface.Create("Test", new RecompSettings { BackendId = "opengl" }, headless: true, foreground: false, logger);
        Assert.Equal(PsxRenderBackendIds.Software, surface.Backend.BackendId);
        Assert.Contains(logger.GetRecentLines(), static l => l.Contains("[WARN] [GL_FALLBACK] reason=headless forced=software", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FixtureRunnerHost_Blindspot01AndCorruptRst1_IsolatesFailuresWithoutCrashBundle()
    {
        string dataDir = Path.Combine(_tempRoot, "fix-data");
        string outDir = Path.Combine(_tempRoot, "fix-out");
        string logDir = Path.Combine(_tempRoot, "fix-logs");
        Directory.CreateDirectory(dataDir);

        string specOk = Path.Combine(_tempRoot, "ok.json");
        File.WriteAllText(specOk, JsonSerializer.Serialize(new[] { new SaveStateFixtureSpec("f1", "Cold", "", 3) }));
        using var logger = new ReStationLogger(logDir, enabled: true);
        int okExit = await FixtureRunnerHost.RunWithEngineAsync(specOk, outDir, null, null, dataDir, new RecompSettings { BackendId = "opengl", ResolutionScale = 2 }, logger, headless: true);
        Assert.Equal(0, okExit);
        IReadOnlyList<string> lines = logger.GetRecentLines();
        Assert.Single(lines, static l => l.Contains("[WARN] [GL_FALLBACK] reason=headless forced=software", StringComparison.Ordinal));
        Assert.Contains(lines, static l => l.Contains("[HEARTBEAT]", StringComparison.Ordinal) && l.Contains("1/3 (", StringComparison.Ordinal) && l.Contains("resolutionScale=2 backend=software", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, static l => l.Contains("/0 (", StringComparison.Ordinal));

        string corruptRst = Path.Combine(dataDir, "corrupt.rst");
        File.WriteAllBytes(corruptRst, new byte[] { 0x52, 0x53, 0x54, 0x31, 0x00, 0x00 });
        string specBad = Path.Combine(_tempRoot, "bad.json");
        File.WriteAllText(specBad, JsonSerializer.Serialize(new[] { new SaveStateFixtureSpec("bad-rst", "Corrupt", corruptRst, 1) }));
        int failExit = await FixtureRunnerHost.RunWithEngineAsync(specBad, outDir, null, null, dataDir, new RecompSettings { BackendId = "software" }, logger, headless: true);
        Assert.Equal(1, failExit);
        Assert.Contains(logger.GetRecentLines(), static l => l.Contains("[ERROR] [FIXTURE]", StringComparison.Ordinal));
        Assert.Empty(Directory.GetFiles(logDir, "*crash*"));
    }

    [Fact]
    public void ReStationHost_ImplementsIReStationHostEx_SubTickHookAndQuickSaveAndNotifyRamWrite()
    {
        string dataDir = Path.Combine(_tempRoot, "host-data");
        string gameData = DiscManifest.GetGameDataDir(dataDir);
        Directory.CreateDirectory(gameData);
        WriteMinimalPsxExe(Path.Combine(gameData, "BOOT.EXE"));

        using var backend = new SoftwareCpuRenderBackend();
        backend.Initialize(320, 240, 1);
        using var logger = new ReStationLogger(Path.Combine(_tempRoot, "host-logs"), enabled: true);
        var cpu = new PsxCpu(new RecompSettings(), dataDir, backend, logger);
        using var pacer = new HighResFramePacer(0);
        var manifest = new ReStationModuleManifest { ModuleId = "kf-host", DisplayName = "KF", Title = "KF" };
        using var host = new ReStationHost(cpu, manifest, dataDir, new RecompHostOptions { TargetFps = 120 }, pacer, logger);

        int subPresents = 0;
        host.AttachRuntimeContext(new HostRuntimeContext(
            IsHeadless: false, HasPendingCapture: () => true, IsModernMouseLookActive: () => true,
            MouseSensitivity: () => 1.75f, PollSubTickLook: () => (2f, -3f, 0.5f, 0f), PresentSubTickFrame: () => subPresents++));

        uint notifiedAddr = 0, notifiedLen = 0;
        cpu.Bus.OnRamWriteRange += (a, len) => { notifiedAddr = a; notifiedLen = len; };
        Assert.True(host.LoadExecutable("BOOT.EXE", 0));
        Assert.Equal(0x10000U, notifiedAddr);
        Assert.Equal(12U, notifiedLen);

        var mod = new StubModule(0x80010000U, 0x80011000U, 0x80010040U);
        var hook = new StubSubTickHook(0x80010040U);
        host.RegisterModule(mod);
        host.SetFramePatch(hook);
        IReStationHostEx hostEx = host;
        Assert.False(hostEx.IsHeadless);
        Assert.True(hostEx.HasPendingCapture);
        Assert.Equal(120, hostEx.TargetFps);
        Assert.True(hostEx.IsModernMouseLookActive);
        Assert.Equal(1.75f, hostEx.MouseSensitivity);
        Assert.Equal((2f, -3f, 0.5f, 0f), hostEx.PollSubTickLook());
        hostEx.WaitUntilTimestamp(Stopwatch.GetTimestamp() + Stopwatch.Frequency / 200);

        host.RunFrame();
        Assert.Equal(2, hook.BeginCount);
        Assert.Equal(2, hook.EndCount);
        Assert.Equal(2, mod.SubTickCalls);
        Assert.Equal(1, subPresents);

        host.SaveQuickSlot(0);
        string slot0 = QuickSaveManager.GetSlotPath(dataDir, manifest.ModuleId, 0);
        cpu.R[9] = 0x2F; cpu.Bios.HandleVectorCall(0xA0); uint expectedNextRng = cpu.R[2];
        cpu.R[9] = 0x2F; cpu.Bios.HandleVectorCall(0xA0); Assert.NotEqual(expectedNextRng, cpu.R[2]);
        using (var probeCpu = new PsxCpu(new RecompSettings(), dataDir, backend, logger))
        {
            QuickSaveLoadResult saved = QuickSaveManager.LoadFromFile(probeCpu, slot0);
            Assert.Equal("RST1+RSTX", saved.Format);
            Assert.True(saved.RstxChunks is not null && saved.RstxChunks.TryGetValue(QuickSaveTrailer.ChunkBios, out byte[]? biosChunk) && biosChunk.Length > 0);
        }
        Assert.True(host.RequestQuickLoad(0));
        cpu.R[9] = 0x2F; cpu.Bios.HandleVectorCall(0xA0);
        Assert.Equal(expectedNextRng, cpu.R[2]);
        Assert.Contains(logger.GetRecentLines(), static l => l.Contains("[SAVE] Saved", StringComparison.Ordinal));
        Assert.Contains(logger.GetRecentLines(), static l => l.Contains("[SAVE] Loaded", StringComparison.Ordinal));
    }

    [Fact]
    public void Program_RunMain_VersionFlag_ExitsZeroAndPrintsVersion()
    {
        TextWriter origOut = Console.Out;
        using var sw = new StringWriter();
        int code;
        try
        {
            Console.SetOut(sw);
            code = PlayerProgram.RunMain(new[] { "--version" });
        }
        finally
        {
            Console.SetOut(origOut);
        }
        Assert.Equal(0, code);
        Assert.Contains($"reStation v{ReStationEngineInfo.Version}", sw.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void VisibleTestHarness_AndCompilerMoreThan12OverlaysAndEmitter011()
    {
        PsxTelemetrySnapshot snap = VisibleTestHarness.CreateTelemetrySnapshot(4, 1.25, 0.85, 13.9, 60.0, 16.6, 4, PsxRenderBackendIds.OpenGl);
        string metrics = snap.ToHeartbeatMetrics();
        Assert.Contains("cpuMs=1.25 gpuMs=0.85 waitMs=13.90", metrics, StringComparison.Ordinal);
        Assert.Contains("resolutionScale=4 backend=opengl", metrics, StringComparison.Ordinal);

        string gd = Path.Combine(_tempRoot, "exes");
        string outDir = Path.Combine(_tempRoot, "emitted");
        Directory.CreateDirectory(gd);
        Directory.CreateDirectory(outDir);
        WriteMinimalPsxExe(Path.Combine(gd, "SLUS_000.01"));
        for (int i = 1; i <= 14; i++) WriteMinimalPsxExe(Path.Combine(gd, $"OVL{i:00}.EXE"), 0x80020000U);
        File.WriteAllText(Path.Combine(gd, "SYSTEM.CNF"), "BOOT = cdrom:\\SLUS_000.01;1\n");

        var opts = new RecompCompilerOptions(null, gd, string.Empty, outDir, "kf", "SLUS-00001", "KF") { Version = "" };
        List<DiscoveredExe> exes = DiscExeLoader.Discover(opts);
        Assert.Equal(15, exes.Count);
        Assert.Equal("SLUS_000.01", exes[0].ExeName);

        var specs = new[] { new GeneratedExeSpec(0, "SLUS_000.01", "SLUS_000.01", "Slus00001Exe", "Slus00001Module", 0x80010000U, 0x8001000CU, new List<GeneratedFnSpec>()) };
        ModuleManifestEmitter.EmitAll(outDir, opts, "Kf.Recomp", "Kf", specs, out string manifestPath, out _);
        Assert.Equal("0.1.1", ReStationModuleManifest.LoadFromFile(manifestPath).Version);
        string emittedSource = File.ReadAllText(Path.Combine(outDir, "KfGameModule.g.cs"));
        Assert.Contains("QuickSaveManager.LoadFromFile(", emittedSource, StringComparison.Ordinal);
        Assert.Contains("Version = \"0.1.1\"", emittedSource, StringComparison.Ordinal);
    }

    private static void WriteMinimalPsxExe(string path, uint textAddr = 0x80010000U)
    {
        byte[] exe = new byte[0x800 + 12];
        Encoding.ASCII.GetBytes("PS-X EXE").CopyTo(exe, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(exe.AsSpan(0x10, 4), textAddr);
        BinaryPrimitives.WriteUInt32LittleEndian(exe.AsSpan(0x18, 4), textAddr);
        BinaryPrimitives.WriteUInt32LittleEndian(exe.AsSpan(0x1C, 4), 12U);
        BinaryPrimitives.WriteUInt32LittleEndian(exe.AsSpan(0x804, 4), (31U << 21) | 8U);
        File.WriteAllBytes(path, exe);
    }

    private sealed class StubModule(uint start, uint end, uint subTickPc) : IRecompModule
    {
        public int ExeId => 0;
        public string ExeName => "BOOT.EXE";
        public uint TextStart => start;
        public uint TextEnd => end;
        public int SubTickCalls { get; private set; }
        public bool TryExecuteFunction(IReStationCpu cpu, uint pc)
        {
            if (pc == subTickPc) SubTickCalls++;
            return true;
        }
    }

    private sealed class StubSubTickHook(uint entryPc) : ISubTickFrameHook
    {
        public uint SubTickEntryPc => entryPc;
        public uint MaxSubTickCycles => 5000U;
        public int BeginCount { get; private set; }
        public int EndCount { get; private set; }
        public bool TryInterceptCall(IReStationCpu cpu, uint canonPc) => false;
        public void Reset() { }
        public bool ShouldRunSubTick(uint subFrameIndex, uint totalSubFrames) => true;
        public void OnSubTickBegin(IReStationCpuEx cpu, uint subFrameIndex, uint totalSubFrames) => BeginCount++;
        public void OnSubTickEnd(IReStationCpuEx cpu, uint subFrameIndex, bool completedCleanly) => EndCount++;
    }
}
