// @module: reStation.Tests.Wave3PlaytestAndCartridgeTests
// @purpose: Regression coverage for v0.1.2 playtest fixes (GTE negative IR1/IR2, PGXP clear, LastDrawnOrigin + FillRect skip guard, T:01 sub-tick marshal, 4 Hz HUD throttle, ZIP .recomp).
// @depends-on: xunit, Foundation.Core, Foundation.Retro, reEngine.Core, reStation.Contracts, reStation.Core, reStation.Player

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Foundation.Core;
using Foundation.Retro;
using reEngine.Core.Manifests;
using reStation.Contracts;
using reStation.Core;
using reStation.Core.Cpu;
using reStation.Core.DiscIngestion;
using reStation.Core.Gpu;
using reStation.Core.Gte;
using reStation.Player;
using Xunit;

namespace reStation.Tests;

public sealed class Wave3PlaytestAndCartridgeTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"restation-w3-{Guid.NewGuid():N}");

    public Wave3PlaytestAndCartridgeTests() => Directory.CreateDirectory(_tempRoot);

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true);
    }

    [Fact]
    public void Gte_RtpsWithLmTrue_PreservesNegativeIr1AndIr2WhileClampingIr3()
    {
        var gte = new PsxGte(new RecompSettings(), new PgxpTracker());
        gte.WriteControl(0, 0x1000U); // R11 = 1.0
        gte.WriteControl(2, 0x1000U); // R22 = 1.0
        gte.WriteControl(4, 0x1000U); // R33 = 1.0
        gte.WriteControl(5, unchecked((uint)-100)); // TRX = -100
        gte.WriteControl(6, unchecked((uint)-80));  // TRY = -80
        gte.WriteControl(7, unchecked((uint)-50));  // TRZ = -50
        gte.WriteControl(24, 160U << 16); // OFX = 160.0
        gte.WriteControl(25, 120U << 16); // OFY = 120.0
        gte.WriteControl(26, 200U);       // H = 200
        gte.WriteData(0, 0U);             // VX0=0, VY0=0
        gte.WriteData(1, 0U);             // VZ0=0

        uint rtpsWithLmAndSf = (1U << 19) | (1U << 10) | 0x01U;
        gte.Execute(rtpsWithLmAndSf);

        Assert.Equal((short)-100, (short)gte.ReadData(9));  // IR1 preserves negative coordinate
        Assert.Equal((short)-80, (short)gte.ReadData(10));  // IR2 preserves negative coordinate
        Assert.Equal((short)0, (short)gte.ReadData(11));    // IR3 respects lm=true
    }

    [Fact]
    public void PsxGpu_PresentFrame_ClearsPgxpTracksLastDrawnOriginAndSkipsFillWhenSuppressed()
    {
        var pgxp = new PgxpTracker();
        var spy = new SpyBackend();
        var gpu = new PsxGpu(new RecompSettings(), pgxp, spy);
        pgxp.Record(10, 20, 10.25f, 20.5f, 100f);
        Assert.Equal(1, pgxp.Count);

        gpu.WriteGp1(0x05000000U); // DisplayX=0, DisplayY=0
        // GP0(02h) FillRect x=0, y=240, w=320, h=240
        gpu.WriteGp0(0x02102030U);
        gpu.WriteGp0((240U << 16) | 0U);
        gpu.WriteGp0((240U << 16) | 320U);

        Assert.True(gpu.HasLastDrawnOrigin);
        Assert.Equal(0, gpu.LastDrawnX);
        Assert.Equal(240, gpu.LastDrawnY);
        Assert.True(gpu.FrameDrawOps > 0);

        gpu.PresentFrame(preferLastDrawnOrigin: true);
        Assert.Equal(0, pgxp.Count);
        Assert.Equal(0, gpu.FrameDrawOps);
        Assert.Equal((0, 240), (spy.LastDispX, spy.LastDispY));

        gpu.SkipPolygonRasterization = true;
        gpu.WriteGp0(0x02102030U);
        gpu.WriteGp0((0U << 16) | 0U);
        gpu.WriteGp0((240U << 16) | 320U);
        Assert.Equal(1, spy.FillCount); // dummy post-sub-tick FillRect was skipped!
        Assert.Equal(0, gpu.FrameDrawOps);
    }

    [Fact]
    public void ReStationHost_SubTickPresentAndLook_MarshalOntoHostThreadCleanly()
    {
        string dataDir = Path.Combine(_tempRoot, "data");
        string logDir = Path.Combine(_tempRoot, "logs");
        string gameData = DiscManifest.GetGameDataDir(dataDir);
        Directory.CreateDirectory(gameData);
        Directory.CreateDirectory(logDir);

        byte[] exe = new byte[0x800 + 8];
        Encoding.ASCII.GetBytes("PS-X EXE").CopyTo(exe, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(exe.AsSpan(0x10, 4), 0x80010000U);
        BinaryPrimitives.WriteUInt32LittleEndian(exe.AsSpan(0x18, 4), 0x80010000U);
        BinaryPrimitives.WriteUInt32LittleEndian(exe.AsSpan(0x1C, 4), 8U);
        BinaryPrimitives.WriteUInt32LittleEndian(exe.AsSpan(0x804, 4), (31U << 21) | 8U);
        File.WriteAllBytes(Path.Combine(gameData, "BOOT.EXE"), exe);

        int hostThreadId = Environment.CurrentManagedThreadId;
        int lookThreadId = -1, presentThreadId = -1, emuThreadId = -1;
        using var spyBackend = new SpyBackend();
        var cpu = new PsxCpu(new RecompSettings(), dataDir, spyBackend, new ReStationLogger(logDir, enabled: false));
        using var pacer = new HighResFramePacer(60.0);
        using var host = new ReStationHost(cpu, new ReStationModuleManifest { ModuleId = "subtick-marshal" }, dataDir, new RecompHostOptions(), pacer, new ReStationLogger(logDir, enabled: false));

        host.AttachRuntimeContext(new HostRuntimeContext(
            IsOffscreen: false,
            PollSubTickLook: () => { lookThreadId = Environment.CurrentManagedThreadId; return (1f, -1f, 0f, 0f); },
            PresentSubTickFrame: () => presentThreadId = Environment.CurrentManagedThreadId));

        host.RegisterModule(new SubTickMarshalModule(() => emuThreadId = Environment.CurrentManagedThreadId, cpu, host));
        Assert.True(host.LoadExecutable("BOOT.EXE", 0));

        host.RunFrame();
        Assert.NotEqual(-1, emuThreadId);
        Assert.NotEqual(hostThreadId, emuThreadId);
        Assert.Equal(hostThreadId, lookThreadId);
        Assert.Equal(hostThreadId, presentThreadId);
        Assert.Equal(1, host.SubTickPresentsInStep);
    }

    [Fact]
    public void OverlayPresenterAndZipCartridge_ThrottleHudAndLoadFromMemory()
    {
        var presenter = new UiOverlayPresenter();
        var drawer = new SlideOverSettingsDrawer { IsOpen = false };
        var settings = new RecompSettings { IsFpsOverlayEnabled = true };
        var manifest = new ReStationModuleManifest { ModuleId = "kf", Title = "KF", DisplayName = "KF", EngineId = ReStationEngineInfo.EngineId, EngineVersion = ReStationEngineInfo.Version, CompatibleEngineTrack = ReStationEngineInfo.CompatibleEngineTrack };

        Assert.True(presenter.EnsureRendered(drawer, settings, manifest, "SLUS-00158", false, PsxTelemetrySnapshot.Empty with { FrameNumber = 1 }));
        Assert.False(presenter.EnsureRendered(drawer, settings, manifest, "SLUS-00158", false, PsxTelemetrySnapshot.Empty with { FrameNumber = 2 }));
        drawer.IsOpen = true;
        Assert.True(presenter.EnsureRendered(drawer, settings, manifest, "SLUS-00158", false, PsxTelemetrySnapshot.Empty with { FrameNumber = 2 }));

        string appDir = Path.Combine(_tempRoot, "install", "app");
        string romDir = Path.Combine(_tempRoot, "install", "data", "rom");
        Directory.CreateDirectory(appDir);
        Directory.CreateDirectory(romDir);
        string pkgPath = Path.Combine(appDir, "Dummy.recomp");
        string manifestJson = $$"""
        {
          "schemaVersion": 1,
          "moduleId": "dummy-zip",
          "title": "Dummy ZIP",
          "platformId": "ps1",
          "version": "0.1.0",
          "engineId": "reStation",
          "engineVersion": "{{ReStationEngineInfo.Version}}",
          "compatibleEngineTrack": "0.1.x",
          "entryAssembly": "reStation.Tests.dll",
          "defaultBootExecutable": "SLUS_001.58",
          "moduleTypeName": "{{typeof(CartridgeTestModule).FullName}}"
        }
        """;
        using (ZipArchive zip = ZipFile.Open(pkgPath, ZipArchiveMode.Create))
        {
            ZipArchiveEntry mEntry = zip.CreateEntry("module.recomp.json");
            using (var w = new StreamWriter(mEntry.Open(), new UTF8Encoding(false))) w.Write(manifestJson);
            zip.CreateEntryFromFile(typeof(CartridgeTestModule).Assembly.Location, "reStation.Tests.dll");
        }

        Assert.Equal(Path.GetFullPath(Path.Combine(_tempRoot, "install")), RecompPackageReader.ResolveModuleRootDirectory(pkgPath));
        ReStationModuleManifest loaded = ReStationModuleManifest.LoadFromFile(pkgPath);
        Assert.Equal(Path.GetFullPath(pkgPath), loaded.ResolvedPackagePath);
        IRecompGameModule mod = ModuleAssemblyLoader.LoadGameModule(loaded, Path.Combine(_tempRoot, "install"), out ModuleAssemblyLoader alc);
        Assert.Equal("CartridgeTestModule", mod.GetType().Name);
        Assert.Equal(Path.GetFullPath(pkgPath), alc.PackageArchivePath);
    }

    private sealed class SubTickMarshalModule(Action captureEmuThread, PsxCpu cpu, ReStationHost host) : IRecompModule
    {
        public int ExeId => 0;
        public string ExeName => "BOOT.EXE";
        public uint TextStart => 0x80010000U;
        public uint TextEnd => 0x80020000U;

        public bool TryExecuteFunction(IReStationCpu cpuArg, uint pc)
        {
            if (pc != 0x80010000U) return false;
            captureEmuThread();
            _ = host.PollSubTickLook();
            host.PresentSubTickFrame();
            _ = cpu.TryAdvanceToNextVBlank();
            cpu.RequestExit();
            return true;
        }
    }

    private sealed class SpyBackend : IPsxRenderBackend
    {
        public string BackendId => "spy";
        public int ScaleFactor { get; set; } = 1;
        public int SurfaceWidth => 320;
        public int SurfaceHeight => 240;
        public long PrimitivesSubmitted => 0;
        public int FillCount { get; private set; }
        public int LastDispX { get; private set; }
        public int LastDispY { get; private set; }

        public void Initialize(int initialWidth, int initialHeight, int scaleFactor) { }
        public void Resize(int width, int height, int scaleFactor) { }
        public void BeginFrame(int width, int height) { }
        public void PresentRgba(int width, int height, ReadOnlySpan<byte> rgba) { }
        public void UploadVramRect(int x, int y, int w, int h, ReadOnlySpan<ushort> pixels15) { }
        public void CopyVramRect(int srcX, int srcY, int dstX, int dstY, int w, int h) { }
        public void FillDisplayRect(int x, int y, int w, int h, uint bgr24) => FillCount++;
        public void SubmitPrimitive(uint op, ReadOnlySpan<uint> words, ushort tp, int ox, int oy, int cl, int ct, int cr, int cb) { }
        public void EndFrameAndPresent(int dispX, int dispY, int dispW, int dispH, bool is24Bit, bool widescreen16By9)
        {
            LastDispX = dispX;
            LastDispY = dispY;
        }
        public byte[] CaptureRgba(out int width, out int height) { width = 1; height = 1; return new byte[4]; }
        public void Dispose() { }
    }
}

public sealed class CartridgeTestModule : IRecompGameModule
{
    public IRecompModuleManifest Manifest { get; } = new ReStationModuleManifest();
    public IReadOnlyList<RecompPatchDescriptor> TimingPatches => Array.Empty<RecompPatchDescriptor>();
    public Task InitializeAsync(IRecompHost host, CancellationToken ct = default) => Task.CompletedTask;
    public Task StepFrameAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task ApplyTimingPatchAsync(string patchId, CancellationToken ct = default) => Task.CompletedTask;
    public Task LoadSaveStateAsync(string saveStatePath, CancellationToken ct = default) => Task.CompletedTask;
}
