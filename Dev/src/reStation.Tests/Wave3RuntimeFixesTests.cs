// @module: reStation.Tests.Wave3RuntimeFixesTests
// @purpose: Validates T7 runtime fixes RS-07..RS-09, RS-12..RS-15 (mid-stack resume, HasCompiledEntry, GL hot-swap/aspect, compact HUD, joypad /ACK & Analog 0x73, polled timers).

using Foundation.Core;
using Foundation.Retro;
using reStation.Contracts;
using reStation.Core;
using reStation.Core.Aot;
using reStation.Core.Cpu;
using reStation.Core.Gpu;
using reStation.Core.Gte;
using reStation.Core.Hardware;
using reStation.Core.Input;
using reStation.Player;
using Xunit;

namespace reStation.Tests;

public sealed class Wave3RuntimeFixesTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "restation-t7-rt-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        TestTempCleanup.TryDeleteDirectory(_tempRoot, nameof(Wave3RuntimeFixesTests));
    }

    [Fact]
    public void Rs07_MidStackDispatch_UsesInterpreterResumeAndHasCompiledEntryLookupAndQuickQueues()
    {
        Directory.CreateDirectory(_tempRoot);
        var mod = new SelectiveCompiledLookupModule(0x80020000U, 0x80020100U, exactEntry: 0x80020040U);
        using var cpu = new PsxCpu(new RecompSettings(), _tempRoot, new SoftwareCpuRenderBackend(), new ReStationLogger(_tempRoot, false));
        cpu.RegisterModule(mod);

        Assert.True(cpu.HasCompiledEntry(0x80020040U));
        Assert.False(cpu.HasCompiledEntry(0x80020080U)); // Inside [TextStart, TextEnd) but NOT lifted entrypoint!

        // Mid-stack resume: R[31] != ReturnSentinelPc -> must NOT restart cold compiled entrypoint 0x80020040; resumes interpreter at snapshot Pc.
        cpu.Bus.Write32(0x80030000U, 0x24020063U); // addiu v0, zero, 99
        cpu.Bus.Write32(0x80030004U, 0x03E00008U); // jr ra
        cpu.Bus.Write32(0x80030008U, 0x00000000U); // nop
        cpu.R[29] = 0x80040000U; // $sp holding saved ReturnSentinelPc
        cpu.Bus.Write32(0x80040000U, IReStationCpu.ReturnSentinelPc);
        cpu.Bus.Write32(0x80030020U, 0x8FBF0000U); // lw ra, 0($sp)
        cpu.Bus.Write32(0x80030024U, 0x00000000U); // nop (load delay)
        cpu.Bus.Write32(0x80030028U, 0x03E00008U); // jr ra -> ReturnSentinel
        cpu.Bus.Write32(0x8003002CU, 0x00000000U); // nop
        cpu.R[31] = 0x80030020U; // mid-stack return address != ReturnSentinelPc

        cpu.DispatchFunction(0x80030000U);
        Assert.Equal(99U, cpu.R[2]);
        Assert.Equal(0, mod.CompiledEntryHitCount);

        cpu.R[31] = IReStationCpu.ReturnSentinelPc;
        cpu.DispatchFunction(0x80020040U);
        Assert.Equal(1, mod.CompiledEntryHitCount);

        using var pacer = new HighResFramePacer(60.0);
        using var host = new ReStationHost(cpu, new ReStationModuleManifest { ModuleId = "qs-queue" }, _tempRoot, new RecompHostOptions(), pacer, new ReStationLogger(_tempRoot, false));
        Assert.True(host.RequestQuickSave(0));
        string slotPath = reStation.Core.Saves.QuickSaveManager.GetSlotPath(_tempRoot, "qs-queue", 0);
        Assert.True(File.Exists(slotPath));
    }

    [Fact]
    public void Rs08AndRs15_GpuBackendHotSwap_UploadsVramAndEnforces4By3AspectUnlessSupportedAndEnabled()
    {
        Directory.CreateDirectory(_tempRoot);
        var settings = new RecompSettings { IsWidescreen16By9 = false };
        var spyA = new RecordingBackend("software");
        var spyB = new RecordingBackend("opengl");
        var pgxp = new PgxpTracker();
        var gpu = new PsxGpu(settings, pgxp, spyA) { WidescreenSupportChecker = () => true };

        Assert.False(gpu.EffectiveWidescreen);
        gpu.Vram[10] = 0x7C00;
        gpu.SetBackend(spyB);

        Assert.Same(spyB, gpu.Backend);
        Assert.Same(pgxp, spyB.AttachedPgxp);
        Assert.Null(spyA.AttachedPgxp);
        Assert.True(spyB.UploadedFullVram);

        settings.IsWidescreen16By9 = true;
        Assert.True(gpu.EffectiveWidescreen);
        gpu.WidescreenSupportChecker = () => false;
        Assert.False(gpu.EffectiveWidescreen);

        // HostVideoSurface offscreen backend swap: rejects opengl cleanly while keeping software operational.
        using var logger = new ReStationLogger(_tempRoot, false);
        using var surface = HostVideoSurface.CreateOffscreen(settings, logger, "unit-test");
        Assert.False(surface.TrySwitchBackend(PsxRenderBackendIds.OpenGl, settings, logger, out string? glErr));
        Assert.Contains("unavailable", glErr ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(PsxRenderBackendIds.Software, surface.Backend.BackendId);
    }

    [Fact]
    public void Rs09AndRs14_UiOverlayPresenter_ActivatesCompactHudStripAndThrottlesOpenDrawerHud()
    {
        var settings = new RecompSettings { IsFpsOverlayEnabled = true, IsWidescreen16By9 = false };
        var drawer = new SlideOverSettingsDrawer { IsOpen = false };
        var presenter = new UiOverlayPresenter();
        var telemetryA = PsxTelemetrySnapshot.Empty with { FrameNumber = 1, CurrentFps = 60.0 };

        bool firstRendered = presenter.EnsureRendered(drawer, settings, null, "SLUS", false, telemetryA);
        Assert.True(firstRendered);
        Assert.True(presenter.IsCompactHudStripActive);
        Assert.Equal(UiOverlayPresenter.HudStripWidth, presenter.ActiveOverlayWidth);
        Assert.Equal(UiOverlayPresenter.HudStripHeight, presenter.ActiveOverlayHeight);

        SdlFRect dst43 = UiOverlayPresenter.ComputeGameDstRect(1280, 720, settings, null, new StubWidescreenProvider(false));
        Assert.True(dst43.W < 1280f && dst43.X > 0f);

        drawer.IsOpen = true;
        var telemetryB = PsxTelemetrySnapshot.Empty with { FrameNumber = 2, CurrentFps = 59.9 };
        Assert.True(presenter.EnsureRendered(drawer, settings, null, "SLUS", false, telemetryB));
        Assert.False(presenter.IsCompactHudStripActive);
        Assert.Equal(UiOverlayPresenter.OverlayWidth, presenter.ActiveOverlayWidth);

        // Within 250 ms throttle window, HUD text-only updates with drawer open must NOT dirty/rerender every frame.
        var telemetryC = PsxTelemetrySnapshot.Empty with { FrameNumber = 3, CurrentFps = 58.0 };
        Assert.False(presenter.EnsureRendered(drawer, settings, null, "SLUS", false, telemetryC));
    }

    [Fact]
    public void Rs12AndRs13_JoypadAckPulseAnalog0x73_AndSyncedTimersAdvanceOnPolledReads()
    {
        Directory.CreateDirectory(_tempRoot);
        using var cpu = new PsxCpu(new RecompSettings(), _tempRoot, new SoftwareCpuRenderBackend(), new ReStationLogger(_tempRoot, false));

        // RS-13: RCnt1 / RCnt2 must advance via TickTo(_cpu.TotalCycles) directly inside ReadTimerReg/WriteTimerReg.
        cpu.TimersAndDma.WriteTimerReg(0x1F801124U, 0x0000U); // timer2 sysclk mode
        cpu.TotalCycles = 1200;
        uint rc2 = cpu.TimersAndDma.ReadTimerReg(0x1F801120U);
        Assert.Equal(1200U, rc2);

        // RS-12: Finite 96-cycle /ACK pulse + separate JOY_STAT bit 9 IRQ latch + Analog 0x73 packet.
        Assert.Equal(96L, PsxJoypadPort.AckPulseCycles);
        cpu.Joypad.AnalogMode = true;
        cpu.SetControllerState(0, 0x0008, 0x11, 0x22, 0x33, 0x44);
        cpu.Joypad.Write16(0x1F80104AU, 0x1003); // JOY_CTRL TXEN + /JOY1 + ACK_IRQEN

        // Byte 0: select controller (0x01)
        cpu.Joypad.Write16(0x1F801040U, 0x01);
        cpu.TotalCycles += PsxJoypadPort.AckDelayCycles;
        ushort statDuringAck = cpu.Joypad.Read16(0x1F801044U);
        Assert.True((statDuringAck & 0x0080) != 0, "/ACK bit 7 must pulse high at AckDelayCycles.");
        Assert.True((statDuringAck & 0x0200) != 0, "IRQ bit 9 must latch on /ACK edge.");

        cpu.TotalCycles += PsxJoypadPort.AckPulseCycles;
        ushort statAfterPulse = cpu.Joypad.Read16(0x1F801044U);
        Assert.Equal(0, statAfterPulse & 0x0080); // finite pulse ended cleanly
        Assert.True((statAfterPulse & 0x0200) != 0, "IRQ bit 9 stays latched until explicit ACK write 0x0010.");
        cpu.Joypad.Write16(0x1F80104AU, 0x1013); // write ACK 0x10 + maintain TX/RX + /JOY1
        Assert.Equal(0, cpu.Joypad.Read16(0x1F801044U) & 0x0200);
        _ = cpu.Joypad.Read16(0x1F801040U);

        // Byte 1: command 0x42 -> 0x73 Analog ID
        cpu.Joypad.Write16(0x1F801040U, 0x42);
        cpu.TotalCycles += PsxJoypadPort.AckDelayCycles + PsxJoypadPort.AckPulseCycles;
        Assert.Equal(0x73, cpu.Joypad.Read16(0x1F801040U));
    }

    private sealed class SelectiveCompiledLookupModule(uint start, uint end, uint exactEntry) : IRecompModule, IRecompCompiledEntryLookup
    {
        public int ExeId => 0;
        public string ExeName => "BOOT.EXE";
        public uint TextStart => start;
        public uint TextEnd => end;
        public int CompiledEntryHitCount { get; private set; }
        public bool ContainsCompiledEntry(uint pc) => pc == exactEntry;
        public bool TryExecuteFunction(IReStationCpu cpu, uint pc)
        {
            if (pc != exactEntry) return false;
            CompiledEntryHitCount++;
            return true;
        }
    }

    private sealed class StubWidescreenProvider(bool supported) : IRecompWidescreenProvider
    {
        public bool IsActiveTrueWidescreenSupported(IReStationCpu cpu) => supported;
    }

    private sealed class RecordingBackend(string backendId) : IPsxRenderBackend, IPsxGpuBackendHooks
    {
        public string BackendId => backendId;
        public int ScaleFactor { get; set; } = 1;
        public PgxpTracker? AttachedPgxp { get; private set; }
        public bool UploadedFullVram { get; private set; }
        public void Initialize(int initialWidth, int initialHeight, int scaleFactor) { }
        public void Resize(int width, int height, int scaleFactor) { }
        public void BeginFrame(int width, int height) { }
        public void AttachPgxp(PgxpTracker? tracker) => AttachedPgxp = tracker;
        public void SetTextureWindow(int maskX, int maskY, int offsetX, int offsetY) { }
        public void UploadVramRect(int x, int y, int w, int h, ReadOnlySpan<ushort> pixels15) => UploadedFullVram |= x == 0 && y == 0 && w == PsxGpu.VramWidth && h == PsxGpu.VramHeight;
        public bool TryReadVramRect(int x, int y, int w, int h, Span<ushort> destination) => false;
        public void CopyVramRect(int srcX, int srcY, int dstX, int dstY, int w, int h) { }
        public void FillDisplayRect(int x, int y, int w, int h, uint bgr24) { }
        public void SubmitPrimitive(uint gp0Opcode, ReadOnlySpan<uint> words, ushort texPage, int drawOffsetX, int drawOffsetY, int clipLeft, int clipTop, int clipRight, int clipBottom) { }
        public void EndFrameAndPresent(int dispX, int dispY, int dispW, int dispH, bool is24Bit, bool widescreen16By9) { }
        public void PresentRgba(int width, int height, ReadOnlySpan<byte> rgbaPixels) { }
        public void RenderUiOverlay(ReadOnlySpan<byte> rgbaPixels, int width, int height, bool uploadPixels) { }
        public byte[] CaptureRgba(out int width, out int height) { width = 1; height = 1; return new byte[4]; }
        public void Dispose() { }
    }
}
