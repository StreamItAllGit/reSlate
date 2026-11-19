// @module: reStation.Tests.FrameCadenceAndInputEdgeTests
// @purpose: Regression coverage for T5 (HM-14/HM-15): 60 Hz GPU VBlank sequence lock, 735-sample SPU pacing, 30 Hz swap-only decimation, Enhanced sub-tick pacing, repeat filtering, and mouse-look edge isolation.
// @depends-on: System.Buffers.Binary, Foundation.Core, Foundation.Retro, reStation.Contracts, reStation.Core, reStation.Core.Cpu, reStation.Core.DiscIngestion, reStation.Core.Gpu, reStation.Core.Gte, reStation.Core.Input, reStation.Player, Xunit
// @exports: FrameCadenceAndInputEdgeTests
// @owner: devv-player-host (T5/T6)

using System.Buffers.Binary;
using Foundation.Core;
using Foundation.Retro;
using reStation.Contracts;
using reStation.Core;
using reStation.Core.Aot;
using reStation.Core.Audio;
using reStation.Core.Cpu;
using reStation.Core.DiscIngestion;
using reStation.Core.Gpu;
using reStation.Core.Gte;
using reStation.Core.Input;
using reStation.Player;
using Xunit;

namespace reStation.Tests;

public sealed class FrameCadenceAndInputEdgeTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"reslate-cadence-edge-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (!Directory.Exists(_tempRoot)) return;
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void PsxGpu_TickTo_PhaseLocks60HzVBlankAndTracksSequenceAcrossOvershootAndSnapshot()
    {
        var gpu = new PsxGpu(new RecompSettings(), new PgxpTracker(), new SoftwareCpuRenderBackend());
        Assert.Equal(0L, gpu.VBlankSequence);
        Assert.Equal(PsxGpu.CyclesPerVBlank, gpu.NextVBlankCycle);

        // Overshoot past edge 1: NextVBlankCycle must remain phase-locked to 2 * CyclesPerVBlank.
        gpu.TickTo(PsxGpu.CyclesPerVBlank + 12_345L, out bool edge1);
        Assert.True(edge1);
        Assert.Equal(1L, gpu.VBlankSequence);
        Assert.Equal(2L * PsxGpu.CyclesPerVBlank, gpu.NextVBlankCycle);

        // Sub-threshold advance must not emit a spurious edge.
        gpu.TickTo(2L * PsxGpu.CyclesPerVBlank - 1L, out bool edgeMid);
        Assert.False(edgeMid);
        Assert.Equal(1L, gpu.VBlankSequence);

        // Multi-edge jump must account for all elapsed 60 Hz VBlanks deterministically.
        gpu.TickTo(4L * PsxGpu.CyclesPerVBlank + 555L, out bool edgeMulti);
        Assert.True(edgeMulti);
        Assert.Equal(4L, gpu.VBlankSequence);
        Assert.Equal(5L * PsxGpu.CyclesPerVBlank, gpu.NextVBlankCycle);

        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
            gpu.WriteSnapshotState(w);
        ms.Position = 0;

        var restored = new PsxGpu(new RecompSettings(), new PgxpTracker(), new SoftwareCpuRenderBackend());
        using (var r = new BinaryReader(ms, System.Text.Encoding.UTF8, leaveOpen: true))
            restored.ReadSnapshotState(r);

        Assert.Equal(4L, restored.VBlankSequence);
        Assert.Equal(5L * PsxGpu.CyclesPerVBlank, restored.NextVBlankCycle);
    }

    [Theory]
    [InlineData(FrameRateMode.Original, 30)]
    [InlineData(FrameRateMode.Original, 60)]
    [InlineData(FrameRateMode.Enhanced, 120)]
    [InlineData(FrameRateMode.Enhanced, 0)]
    public void ReStationHost_RunFrame_LocksHardwareVBlankAnd735SpuSamplesAt60Hz(FrameRateMode mode, int targetFps)
    {
        Directory.CreateDirectory(_tempRoot);
        string dataDir = Path.Combine(_tempRoot, $"run-{mode}-{targetFps}");
        string logDir = Path.Combine(dataDir, "logs");
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(logDir);

        var settings = new RecompSettings { FrameRateMode = mode, TargetFps = targetFps, BackendId = PsxRenderBackendIds.Software };
        using var logger = new ReStationLogger(logDir, false);
        using var backend = new SoftwareCpuRenderBackend();
        using var cpu = new PsxCpu(settings, dataDir, backend, logger);
        using var pacer = new HighResFramePacer(0.0);
        var hostOptions = new RecompHostOptions { TargetFps = targetFps, UnlockFps = mode == FrameRateMode.Enhanced && targetFps <= 0 };
        using var host = new ReStationHost(cpu, CreateManifest("cadence-test"), dataDir, hostOptions, pacer, logger);
        host.AttachRuntimeContext(new HostRuntimeContext(IsOffscreen: true, IsEnhancedFrameRateEnabled: () => mode == FrameRateMode.Enhanced));

        short[] spuDrain = new short[PsxSpu.SampleRate];
        int totalStereoSamples = 0;
        const int Frames = 6;
        for (int i = 0; i < Frames; i++)
        {
            host.RunFrame();
            totalStereoSamples += cpu.Spu.Output.Read(spuDrain);
        }

        Assert.Equal(Frames, host.FramesRun);
        Assert.Equal(Frames, cpu.VBlankSequence);
        Assert.Equal(Frames, cpu.Gpu.VBlankSequence);
        Assert.Equal(Frames * ReStationHost.SamplesPerFrame * 2, totalStereoSamples);
        Assert.Equal(0L, cpu.Spu.XaDrops);
    }

    [Fact]
    public void ShouldDecimateDisplayPresentation_DecimatesEverySecondVBlankAt30HzOnly()
    {
        var original30 = new RecompSettings { FrameRateMode = FrameRateMode.Original, TargetFps = 30 };
        var enhanced30 = new RecompSettings { FrameRateMode = FrameRateMode.Enhanced, TargetFps = 30 };
        var original60 = new RecompSettings { FrameRateMode = FrameRateMode.Original, TargetFps = 60 };
        var enhanced120 = new RecompSettings { FrameRateMode = FrameRateMode.Enhanced, TargetFps = 120 };

        int presented30 = 0, decimated30 = 0, decimatedEnh30 = 0, decimated60 = 0, decimated120 = 0;
        for (long vblank = 1; vblank <= 60; vblank++)
        {
            if (Program.ShouldDecimateDisplayPresentation(original30, 30, vblank, hasPendingShot: false)) decimated30++;
            else presented30++;
            if (Program.ShouldDecimateDisplayPresentation(enhanced30, 30, vblank, hasPendingShot: false)) decimatedEnh30++;
            if (Program.ShouldDecimateDisplayPresentation(original60, 60, vblank, hasPendingShot: false)) decimated60++;
            if (Program.ShouldDecimateDisplayPresentation(enhanced120, 120, vblank, hasPendingShot: false)) decimated120++;
        }

        Assert.Equal(30, presented30);
        Assert.Equal(30, decimated30);
        Assert.Equal(30, decimatedEnh30);
        Assert.Equal(0, decimated60);
        Assert.Equal(0, decimated120);
        Assert.False(Program.ShouldDecimateDisplayPresentation(original30, 30, nextHostFrameNumber: 2, hasPendingShot: true));
    }

    [Fact]
    public void ReStationHost_ForwardSubTickHook_RespectsEnhancedGateAndNamesThreadReSlateCoreLoop()
    {
        Directory.CreateDirectory(_tempRoot);
        string dataDir = Path.Combine(_tempRoot, "subtick-host");
        string logDir = Path.Combine(dataDir, "logs");
        string gameData = DiscManifest.GetGameDataDir(dataDir);
        Directory.CreateDirectory(gameData);
        Directory.CreateDirectory(logDir);
        WriteTinyPsxExe(Path.Combine(gameData, "TINY.EXE"), 0x80010000U);

        var settings = new RecompSettings { FrameRateMode = FrameRateMode.Original, TargetFps = 60, BackendId = PsxRenderBackendIds.Software };
        using var logger = new ReStationLogger(logDir, false);
        using var backend = new SoftwareCpuRenderBackend();
        using var cpu = new PsxCpu(settings, dataDir, backend, logger);

        var mod = new RecordingCoreModule(entryPc: 0x80010000U, subTickPc: 0x80010040U);
        var patch = new RecordingSubTickPatch(0x80010040U);
        using var pacer = new HighResFramePacer(0.0);
        var hostOptions = new RecompHostOptions { TargetFps = 60, UnlockFps = false };
        using var host = new ReStationHost(cpu, CreateManifest("subtick-test"), dataDir, hostOptions, pacer, logger);
        host.RegisterModule(mod);
        host.SetFramePatch(patch);

        int subTickPresents = 0;
        host.AttachRuntimeContext(new HostRuntimeContext(
            IsOffscreen: false,
            PresentSubTickFrame: () => subTickPresents++,
            IsEnhancedFrameRateEnabled: () => settings.FrameRateMode == FrameRateMode.Enhanced));

        Assert.True(host.LoadExecutable("TINY.EXE", exeId: 0));

        // Original 60 Hz: core thread must boot named reSlate.CoreLoop and skip Enhanced sub-ticks.
        host.RunFrame();
        host.RunFrame();
        Assert.Equal("reSlate.CoreLoop", mod.ObservedThreadName);
        Assert.Equal(0, patch.SubTickBeginCount);
        Assert.Equal(0, mod.SubTickExecutions);
        Assert.Equal(0, subTickPresents);

        // Switch to Enhanced 120 Hz: ForwardSubTickHook runs 2 sub-ticks/VBlank and presents intermediate sub-tick.
        settings.FrameRateMode = FrameRateMode.Enhanced;
        settings.TargetFps = 120;
        host.SetTargetFps(120, unlock: false);
        host.RunFrame();

        Assert.Equal(2, patch.SubTickBeginCount);
        Assert.Equal(2, patch.SubTickEndCount);
        Assert.Equal(2, mod.SubTickExecutions);
        Assert.Equal(1, subTickPresents);
    }

    [Fact]
    public void EventPump_FiltersOsKeyRepeat_AndIsolatesMouseLookFromSinglePressEdges()
    {
        Directory.CreateDirectory(_tempRoot);
        string dataDir = Path.Combine(_tempRoot, "edge-data");
        string logDir = Path.Combine(dataDir, "logs");
        string cfgPath = Path.Combine(dataDir, "settings.json");
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(logDir);

        var env = new AppEnvironment("reslate", ["--data-dir", dataDir, "--config", cfgPath, "--log-dir", logDir, "--tray-only"]);
        var settings = new RecompSettings
        {
            ControlMode = ControlMode.ModernWasdMouse,
            InputProfileMode = InputProfileMode.GlobalController,
            BackendId = PsxRenderBackendIds.Software
        };
        var globalBindings = new KeyBindingsConfig
        {
            Bindings = KeyBindingsConfig.CreateDefaults(ControlMode.ModernWasdMouse)
        };
        globalBindings.Bindings[BindableAction.Cross] = Sdl3Native.ScRet;

        using var logger = new ReStationLogger(logDir, false);
        using var video = HostVideoSurface.CreateOffscreen(settings, logger, "unit-test");
        using var cpu = new PsxCpu(settings, dataDir, video.Backend, logger);
        using var pacer = new HighResFramePacer(0.0);
        using var host = new ReStationHost(cpu, CreateManifest("input-edge"), dataDir, new RecompHostOptions(), pacer, logger);
        var drawer = new SlideOverSettingsDrawer();
        var overlay = new UiOverlayPresenter();
        var st = new Program.LoopState { Gamepad = IntPtr.Zero, MouseCaptured = true, IsOffscreen = false };

        byte[] initialF1 = BuildSdlKeyDownEvent(Sdl3Native.ScF1, repeat: 0);
        byte[] repeatF1 = BuildSdlKeyDownEvent(Sdl3Native.ScF1, repeat: 1);
        Assert.Equal((byte)0, Sdl3Native.EventKeyRepeat(initialF1));
        Assert.Equal((byte)1, Sdl3Native.EventKeyRepeat(repeatF1));

        // OS key-repeat F1 must NOT double-toggle the drawer; single non-repeat toggles once.
        Program.ProcessSdlEventSpan(initialF1, video, host, cpu, drawer, overlay, settings, env, pacer, logger, st);
        Assert.True(drawer.IsOpen);
        Program.ProcessSdlEventSpan(repeatF1, video, host, cpu, drawer, overlay, settings, env, pacer, logger, st);
        Assert.True(drawer.IsOpen);
        Program.ProcessSdlEventSpan(initialF1, video, host, cpu, drawer, overlay, settings, env, pacer, logger, st);
        Assert.False(drawer.IsOpen);

        // Single Enter press followed by OS key-repeat + sub-tick mouse polling = strictly 1 rising pad edge.
        byte[] initialRet = BuildSdlKeyDownEvent(Sdl3Native.ScRet, repeat: 0);
        byte[] repeatRet = BuildSdlKeyDownEvent(Sdl3Native.ScRet, repeat: 1);

        int crossEdges = 0;
        PsxPadButton prevButtons = PsxPadButton.None;
        for (int frame = 0; frame < 4; frame++)
        {
            Program.ProcessSdlEventSpan(frame == 0 ? initialRet : repeatRet, video, host, cpu, drawer, overlay, settings, env, pacer, logger, st);
            _ = Program.PollLiveSubTickLook(video, drawer, st, isOffscreen: false);
            if (frame > 0) Assert.Equal((byte)0, st.LatchedKeys[Sdl3Native.ScRet]);

            HostInputState input = HostInputMapper.BuildLiveInput(
                cpu, drawer.IsOpen, sdlAvailable: false, settings, globalBindings, profileProvider: null,
                IntPtr.Zero, ref st.MouseDx, ref st.MouseDy, st.WheelUp, st.WheelDown,
                st.LatchedKeys, st.LatchedMouseMask, nativeHook: null,
                st.PrevNativeActions, st.ActiveNativeBuffer, st.PressedNativeBuffer, out _);
            Array.Clear(st.LatchedKeys);
            st.LatchedMouseMask = 0u;

            PsxPadButton rising = input.Buttons & ~prevButtons;
            if ((rising & PsxPadButton.Cross) != 0) crossEdges++;
            prevButtons = input.Buttons;
        }

        Assert.Equal(1, crossEdges);
        Assert.StartsWith("SlateOne Disc Images", Sdl3Native.DiscImageFilter, StringComparison.Ordinal);
        Assert.StartsWith("reSlate Modules", Sdl3Native.ModuleFilter, StringComparison.Ordinal);
    }

    private static byte[] BuildSdlKeyDownEvent(int scancode, byte repeat)
    {
        byte[] ev = new byte[128];
        BinaryPrimitives.WriteUInt32LittleEndian(ev.AsSpan(0, 4), Sdl3Native.EventKeyDown);
        BinaryPrimitives.WriteInt32LittleEndian(ev.AsSpan(24, 4), scancode);
        ev[36] = 1;      // SDL_KeyboardEvent.down
        ev[37] = repeat; // SDL_KeyboardEvent.repeat
        return ev;
    }

    private static void WriteTinyPsxExe(string path, uint entryPc)
    {
        byte[] exe = new byte[0x800 + 16];
        "PS-X EXE"u8.CopyTo(exe.AsSpan(0, 8));
        BinaryPrimitives.WriteUInt32LittleEndian(exe.AsSpan(0x10, 4), entryPc);
        BinaryPrimitives.WriteUInt32LittleEndian(exe.AsSpan(0x18, 4), entryPc);
        BinaryPrimitives.WriteUInt32LittleEndian(exe.AsSpan(0x1C, 4), 16U);
        BinaryPrimitives.WriteUInt32LittleEndian(exe.AsSpan(0x30, 4), 0x801FFF00U);
        File.WriteAllBytes(path, exe);
    }

    private static ReStationModuleManifest CreateManifest(string id) => new()
    {
        ModuleId = id,
        Title = id,
        DisplayName = id,
        SerialNumber = "SLUS-00000"
    };

    private sealed class RecordingCoreModule(uint entryPc, uint subTickPc) : IRecompModule, IRecompCompiledEntryLookup
    {
        public int ExeId => 0;
        public string ExeName => "TINY.EXE";
        public uint TextStart => entryPc;
        public uint TextEnd => entryPc + 0x200U;
        public string? ObservedThreadName { get; private set; }
        public int SubTickExecutions { get; private set; }

        public bool ContainsCompiledEntry(uint pc) => pc == entryPc || pc == subTickPc;

        public bool TryExecuteFunction(IReStationCpu cpu, uint pc)
        {
            if (pc == subTickPc)
            {
                SubTickExecutions++;
                return true;
            }
            if (pc != entryPc) return false;

            ObservedThreadName = Thread.CurrentThread.Name;
            if (cpu is PsxCpu psx)
            {
                while (!psx.ExitRequested && !psx.ShouldUnwind)
                {
                    psx.TotalCycles += PsxCpu.CyclesPerFrame;
                    psx.PollEvents();
                }
            }
            return true;
        }
    }

    private sealed class RecordingSubTickPatch(uint subTickEntryPc) : ISubTickFrameHook
    {
        public uint SubTickEntryPc => subTickEntryPc;
        public uint MaxSubTickCycles => 65_536U;
        public int SubTickBeginCount { get; private set; }
        public int SubTickEndCount { get; private set; }

        public bool TryInterceptCall(IReStationCpu cpu, uint canonPc) => false;
        public void Reset() { }
        public bool ShouldRunSubTick(uint subFrameIndex, uint totalSubFrames) => true;
        public void OnSubTickBegin(IReStationCpuEx cpu, uint subFrameIndex, uint totalSubFrames) => SubTickBeginCount++;
        public void OnSubTickEnd(IReStationCpuEx cpu, uint subFrameIndex, bool completedCleanly) => SubTickEndCount++;
    }
}
