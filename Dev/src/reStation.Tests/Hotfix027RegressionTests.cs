// @module: reStation.Tests.Hotfix027RegressionTests
// @purpose: Regression coverage for v0.2.7 PS1 kernel IntRP/GetC0Table/SIO0 IRQ7 ticks, input ordering, RSTX QuickSave provider, --jit-bringup wiring, and LockedCap default.
// @depends-on: Xunit, Foundation.Core, Foundation.Retro, reStation.Contracts, reStation.Core, reStation.Player
// @exports: Hotfix027RegressionTests

using System.Buffers.Binary;
using System.Text.Json;
using Foundation.Core;
using Foundation.Retro;
using reStation.Contracts;
using reStation.Core;
using reStation.Core.Bringup;
using reStation.Core.Cpu;
using reStation.Core.Gpu;
using reStation.Core.Hardware;
using reStation.Core.Input;
using reStation.Player;
using Xunit;

namespace reStation.Tests;

public sealed class Hotfix027RegressionTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"restation-v027-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    private sealed class IntRpTestModule(uint func1Pc, uint func2Pc, Action<IReStationCpu> onFunc2) : IRecompModule
    {
        public int ExeId => 0;
        public string ExeName => "SLUS_011.15";
        public uint TextStart => Math.Min(func1Pc, func2Pc);
        public uint TextEnd => Math.Max(func1Pc, func2Pc) + 16U;

        public bool TryExecuteFunction(IReStationCpu cpu, uint pc)
        {
            if (pc == func1Pc) { cpu.R[2] = 1U; return true; }
            if (pc == func2Pc) { onFunc2(cpu); return true; }
            return false;
        }
    }

    private sealed class NativePadOverlayHook : IModuleInputProfileProvider, INativeGameActionHook
    {
        public string ProfileDisplayName => "Native Pad Test";
        public IReadOnlyList<GameActionDescriptor> ModernActions { get; } =
            [new("menuConfirm", "Confirm", 40, (ushort)PsxPadButton.Start, (ushort)PsxPadButton.Start)];
        public bool IsGameplayContextActive(IReStationCpu cpu) => true;

        public void ApplyNativeActions(
            IReStationCpuEx cpu, ReadOnlySpan<string> activeActions, ReadOnlySpan<string> pressedActions,
            (float MoveX, float MoveY, float LookX, float LookY) axes, bool gameplayActive, out bool releaseMouseCapture)
        {
            releaseMouseCapture = false;
            ((PsxCpu)cpu).SetControllerState(0, (ushort)PsxPadButton.Start, 0x80, 0x80, 0x80, 0x80);
        }
    }

    private sealed class StubQuickSaveProvider : IRecompQuickSaveProvider
    {
        public byte[]? RestoredOvly { get; private set; }

        public void CaptureQuickSaveChunks(IDictionary<string, byte[]> chunks) =>
            chunks["OVLY"] = [0x48, 0x4D, 0x4F, 0x56, 0x06, 0x10];

        public void OnQuickSaveLoaded(IReadOnlyDictionary<string, byte[]> chunks) =>
            RestoredOvly = chunks.TryGetValue("OVLY", out byte[]? data) ? data : null;
    }

    [Fact]
    public void BiosHle_InitializesKernelTables_DispatchesIntRpChainsAndTicksJoypadOnIrqPoll()
    {
        Directory.CreateDirectory(_tempRoot);
        using var backend = new SoftwareCpuRenderBackend();
        using var cpu = new PsxCpu(new RecompSettings(), _tempRoot, backend);

        // ToT[0] (0x80000100) and B0:0x56 GetC0Table -> [0x80000518]
        Assert.Equal(0x80000200U, cpu.Bus.Read32(0x80000100U));
        cpu.R[9] = 0x56U;
        cpu.Bios.HandleVectorCall(0xB0U);
        Assert.Equal(0x80000500U, cpu.R[2]);
        Assert.Equal(0x80000540U, cpu.Bus.Read32(cpu.R[2] + 0x18U));

        const uint nodeAddr = 0x80020000U;
        const uint func1Pc = 0x80030000U;
        const uint func2Pc = 0x80030100U;
        cpu.Bus.Write32(nodeAddr + 4U, func2Pc);
        cpu.Bus.Write32(nodeAddr + 8U, func1Pc);

        cpu.RegisterModule(new IntRpTestModule(func1Pc, func2Pc, c =>
        {
            c.Write32(0x80040000U, 0xCAFEBABEU);
            // Self-dequeue during callback via C0:0x03 SysDeqIntRP(2, nodeAddr)
            c.R[9] = 0x03U; c.R[4] = 2U; c.R[5] = nodeAddr;
            ((PsxCpu)c).Bios.HandleVectorCall(0xC0U);
        }));

        // C0:0x02 SysEnqIntRP(2, nodeAddr)
        cpu.R[9] = 0x02U; cpu.R[4] = 2U; cpu.R[5] = nodeAddr;
        cpu.Bios.HandleVectorCall(0xC0U);
        Assert.Equal(nodeAddr, cpu.Bus.Read32(cpu.Bus.Read32(0x80000100U) + 16U));

        // Even without HookEntryInt registered (_hookEntryIntBufAddr == 0), TryRunInterruptHook dispatches IntRP chains.
        cpu.TimersAndDma.InterruptStatus = 1U;
        cpu.Bios.TryRunInterruptHook();
        Assert.Equal(0xCAFEBABEU, cpu.Bus.Read32(0x80040000U));
        Assert.Equal(0U, cpu.Bus.Read32(cpu.Bus.Read32(0x80000100U) + 16U));

        // ReadIrqReg(0x1F801070) ticks Joypad to raise IRQ7 when AckDelayCycles has elapsed.
        cpu.Joypad.Write16(0x1F80104AU, 0x1002); // TXEN | JOYn + RX_INTEN (bit 12)
        cpu.Joypad.Write16(0x1F801040U, 0x01);
        cpu.TotalCycles += PsxJoypadPort.AckDelayCycles;
        uint istat = cpu.TimersAndDma.ReadIrqReg(0x1F801070U);
        Assert.NotEqual(0U, istat & (1U << PsxIrq.Controller));
    }

    [Fact]
    public void HostInputMapper_AppliesNativeGameActionsAfterApplyToCpu_AndClearsOnDrawerOpen()
    {
        Directory.CreateDirectory(_tempRoot);
        using var backend = new SoftwareCpuRenderBackend();
        using var cpu = new PsxCpu(new RecompSettings(), _tempRoot, backend);
        var settings = new RecompSettings { InputProfileMode = InputProfileMode.PerGameModern };
        var hook = new NativePadOverlayHook();
        var prev = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var active = new List<string>();
        var pressed = new List<string>();
        float dx = 0f, dy = 0f;

        HostInputMapper.BuildLiveInput(
            cpu, drawerOpen: false, sdlAvailable: false, settings, globalBindings: null, hook,
            IntPtr.Zero, ref dx, ref dy, false, false, hook, prev, active, pressed, out _);
        Assert.Equal((ushort)PsxPadButton.Start, cpu.GetControllerButtons(0));

        HostInputMapper.BuildLiveInput(
            cpu, drawerOpen: true, sdlAvailable: false, settings, globalBindings: null, hook,
            IntPtr.Zero, ref dx, ref dy, false, false, hook, prev, active, pressed, out _);
        Assert.Equal((ushort)0, cpu.GetControllerButtons(0));
    }

    [Fact]
    public void ReStationHost_QuickSaveAndLoad_RoundTripsProviderRstxChunks()
    {
        Directory.CreateDirectory(_tempRoot);
        string logDir = Path.Combine(_tempRoot, "logs");
        using var backend = new SoftwareCpuRenderBackend();
        using var cpu = new PsxCpu(new RecompSettings(), _tempRoot, backend);
        using var pacer = new HighResFramePacer(60.0);
        using var logger = new ReStationLogger(logDir, enabled: false);
        var manifest = new ReStationModuleManifest { ModuleId = "hm-test", Title = "HM", DisplayName = "HM" };
        var provider = new StubQuickSaveProvider();
        using var host = new ReStationHost(cpu, manifest, _tempRoot, new RecompHostOptions(), pacer, logger)
        {
            QuickSaveProvider = provider
        };

        cpu.Bus.Write32(0x8005E9C8U, 0x12345678U);
        host.SaveQuickSlot(0);
        cpu.Bus.Write32(0x8005E9C8U, 0U);

        Assert.True(host.LoadQuickSlot(0));
        Assert.Equal(0x12345678U, cpu.Bus.Read32(0x8005E9C8U));
        Assert.Equal([0x48, 0x4D, 0x4F, 0x56, 0x06, 0x10], provider.RestoredOvly);
    }

    [Fact]
    public void PsxCpu_BringupRecorder_TracksBlocksResetsOnVBlankTripsWatchdogAndWritesProfile()
    {
        Directory.CreateDirectory(_tempRoot);
        string profilePath = Path.Combine(_tempRoot, "cache", "bringup.profile.json");
        using var backend = new SoftwareCpuRenderBackend();
        using var cpu = new PsxCpu(new RecompSettings(), _tempRoot, backend);
        var recorder = new JitBringupRecorder(
            "bringup-mod", ReStationExecutionTierModes.JitBringup, true, profilePath, spinWatchdogCycleThreshold: 10_000L);
        cpu.BringupRecorder = recorder;

        // Place valid MIPS leaf: jr $ra; nop at 0x80010000
        cpu.Bus.Write32(0x80010000U, 0x03E00008U);
        cpu.Bus.Write32(0x80010004U, 0U);
        cpu.RunSlice(0x80010000U);
        Assert.True(recorder.DiscoveredFunctionCount >= 1);

        cpu.TotalCycles = 5_000L;
        cpu.PollEvents();
        Assert.False(cpu.ExitRequested);
        Assert.Equal(5_000L, recorder.NonVBlankSpinCycles);

        cpu.TotalCycles = 20_000L;
        cpu.PollEvents();
        Assert.True(recorder.IsSpinWatchdogTripped);
        Assert.True(cpu.ExitRequested);

        string written = recorder.WriteProfileAtomic(compiledDispatchCount: cpu.CompiledDispatchCount, interpreterFallbackCount: cpu.InterpreterFallbackCount);
        Assert.True(File.Exists(written));
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(written));
        Assert.True(doc.RootElement.GetProperty("isSpinWatchdogTripped").GetBoolean());
    }

    [Fact]
    public void Program_ApplyFirstLaunchTimingDefaults_SelectsOriginalForLockedCapManifestWhenUnconfigured()
    {
        var manifest = new ReStationModuleManifest
        {
            ModuleId = "locked-cap-mod", Title = "Locked", DisplayName = "Locked",
            DefaultFramerateMode = RecompTimingMode.LockedCap
        };
        CliOptions noOverride = CliOptions.Parse([]);
        CliOptions forcedEnh = CliOptions.Parse(["--fps-mode", "enhanced"]);

        var firstLaunch = new RecompSettings { FrameRateMode = FrameRateMode.Enhanced };
        Program.ApplyFirstLaunchTimingDefaults(firstLaunch, noOverride, manifest, hadConfigFile: false);
        Assert.Equal(FrameRateMode.Original, firstLaunch.FrameRateMode);

        var existingCfg = new RecompSettings { FrameRateMode = FrameRateMode.Enhanced };
        Program.ApplyFirstLaunchTimingDefaults(existingCfg, noOverride, manifest, hadConfigFile: true);
        Assert.Equal(FrameRateMode.Enhanced, existingCfg.FrameRateMode);

        var cliOverride = new RecompSettings { FrameRateMode = FrameRateMode.Enhanced };
        Program.ApplyFirstLaunchTimingDefaults(cliOverride, forcedEnh, manifest, hadConfigFile: false);
        Assert.Equal(FrameRateMode.Enhanced, cliOverride.FrameRateMode);
    }
}
