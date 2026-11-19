// @module: reStation.Tests.Hotfix030Hf2RegressionTests
// @purpose: Verifies v0.3.0-hf.2 VBlank clamp fix, Original 30/Enhanced >60 presentation FPS + SPU 60 Hz lock, 3-tier controller rebinding + triggers + clearability, ghost-slot cleanup, and Context masks.
// @depends-on: Xunit, Foundation.Core, reStation.Contracts, reStation.Core, reStation.Core.Cpu, reStation.Core.Gpu, reStation.Core.Input, reStation.Player
// @exports: Hotfix030Hf2RegressionTests

using System.Text;
using Foundation.Core;
using Foundation.Retro;
using reStation.Contracts;
using reStation.Core;
using reStation.Core.Cpu;
using reStation.Core.Gpu;
using reStation.Core.Input;
using reStation.Player;
using Xunit;

namespace reStation.Tests;

public sealed class Hotfix030Hf2RegressionTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "reslate-hf2-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(63)]
    public void TryAdvanceToNextVBlank_WhenIntermediateWakeLeavesUnder64Cycles_DoesNotThrow(int remainingCycles)
    {
        Directory.CreateDirectory(_tempRoot);
        var settings = new RecompSettings { FrameRateMode = FrameRateMode.Original, TargetFps = 60 };
        using var cpu = new PsxCpu(settings, _tempRoot, new SoftwareCpuRenderBackend(), new ReStationLogger(_tempRoot, false));
        cpu.TimersAndDma.InterruptMask = 1;

        uint targetTicks = checked((uint)Math.Max(128L, (cpu.Gpu.NextVBlankCycle - cpu.TotalCycles) - remainingCycles));
        cpu.Write32(0x1F801100U, 0U);
        cpu.Write32(0x1F801108U, targetTicks);
        cpu.Write32(0x1F801104U, 0x10U);

        bool advanced = cpu.TryAdvanceToNextVBlank();
        Assert.True(advanced);
        Assert.True(cpu.TotalCycles >= targetTicks);
    }

    [Fact]
    public void ResolveHostTargetFps_AndPresentationTracker_Honor30AndOver60WhileLocking60HzSpu()
    {
        Directory.CreateDirectory(_tempRoot);
        var orig30 = new RecompSettings { FrameRateMode = FrameRateMode.Original, TargetFps = 30 };
        var orig60 = new RecompSettings { FrameRateMode = FrameRateMode.Original, TargetFps = 60 };
        var enhMax = new RecompSettings { FrameRateMode = FrameRateMode.Enhanced, TargetFps = 0 };
        Assert.Equal(30, Program.ResolveHostTargetFps(orig30));
        Assert.Equal(60, Program.ResolveHostTargetFps(orig60));
        Assert.Equal(0, Program.ResolveHostTargetFps(enhMax));
        Assert.True(Program.ShouldDecimateDisplayPresentation(orig30, Program.ResolveHostTargetFps(orig30), 2, hasPendingShot: false));
        Assert.False(Program.ShouldDecimateDisplayPresentation(orig30, Program.ResolveHostTargetFps(orig30), 3, hasPendingShot: false));

        var tracker = new PresentationFpsTracker();
        using var cpu = new PsxCpu(orig30, _tempRoot, new SoftwareCpuRenderBackend(), new ReStationLogger(_tempRoot, false));
        using var pacer = new HighResFramePacer(60.0);
        using var host = new ReStationHost(cpu, new ReStationModuleManifest { ModuleId = "pres-test" }, _tempRoot,
            new RecompHostOptions { TargetFps = Program.ResolveHostTargetFps(orig30) }, pacer, new ReStationLogger(_tempRoot, false))
        {
            PresentationTracker = tracker
        };

        for (int i = 0; i < 6; i++)
        {
            host.RunFrame();
            if ((i & 1) == 0) tracker.RecordPresentedFrame();
            Thread.Sleep(16);
        }
        PsxTelemetrySnapshot snap = host.GetTelemetry();
        Assert.Equal(6, cpu.Spu.Output.BufferedSamples / (ReStationHost.SamplesPerFrame * 2));
        Assert.InRange(snap.CurrentFps, 10.0, 55.0);
    }

    [Fact]
    public void ClassicControllerBindings_SupportGlobalAndPerGameRebindTriggersAndClearToZero()
    {
        Directory.CreateDirectory(_tempRoot);
        string cfgPath = Path.Combine(_tempRoot, "settings.json");
        string globalPath = Path.Combine(_tempRoot, "settings.controls.json");
        var globalProfile = new GlobalControllerProfile();
        var settings = new RecompSettings { InputProfileMode = InputProfileMode.GlobalController };
        var drawer = new SlideOverSettingsDrawer { IsOpen = true, ActiveTab = 2 };
        drawer.AttachInputProfiles(globalProfile, globalPath, null);

        var canvas = new PixelOverlayCanvas(UiOverlayPresenter.OverlayWidth, UiOverlayPresenter.OverlayHeight);
        drawer.RenderOverlay(canvas, settings, null, "No disc", false);

        // Verify Global 1:1 exposes both Kbm and Pad pills plus "VSync (Caps to Monitor Hz)" on Video tab.
        Assert.Contains(drawer.HitBoxes, h => h.ControlId == "Keybinds_Classic_Cross_Pad");
        Assert.Contains(drawer.HitBoxes, h => h.ControlId == "Keybinds_Classic_Cross_Kbm");
        drawer.KeybindsTab.ListeningAction = BindableAction.Cross;
        drawer.KeybindsTab.ListeningSlotIndex = 1;
        Assert.True(drawer.TryHandleGamepadCapture(GamepadBindingCode.East, settings, cfgPath));
        Assert.Equal(GamepadBindingCode.East, globalProfile.KeyBindings.GetControllerCode(BindableAction.Cross));

        drawer.KeybindsTab.ListeningAction = BindableAction.L2;
        drawer.KeybindsTab.ListeningSlotIndex = 1;
        Assert.True(drawer.TryHandleGamepadCapture(GamepadBindingCode.LT, settings, cfgPath));
        Assert.Equal(GamepadBindingCode.LT, globalProfile.KeyBindings.GetControllerCode(BindableAction.L2));

        // Clear via Del key -> must stay 0 across Clamp() and Reload.
        drawer.KeybindsTab.ListeningAction = BindableAction.Circle;
        drawer.KeybindsTab.ListeningSlotIndex = 1;
        Assert.True(drawer.TryHandleKeyCapture(Sdl3Native.ScDelete, settings, cfgPath));
        GlobalControllerProfile reloadedGlobal = GlobalControllerProfile.LoadOrDefault(globalPath);
        Assert.Equal(0, reloadedGlobal.KeyBindings.GetControllerCode(BindableAction.Circle));
        Assert.Equal(GamepadBindingCode.East, reloadedGlobal.KeyBindings.GetControllerCode(BindableAction.Cross));

        // Switch to Game 1:1 and verify controller rebind drives PsxPadButton without fallback override.
        settings.InputProfileMode = InputProfileMode.PerGameController;
        drawer.KeybindsTab.ListeningAction = BindableAction.Cross;
        drawer.KeybindsTab.ListeningSlotIndex = 1;
        Assert.True(drawer.TryHandleGamepadCapture(GamepadBindingCode.West, settings, cfgPath));
        Assert.Equal(GamepadBindingCode.West, settings.KeyBindings.GetControllerCode(BindableAction.Cross));

        byte[] keys = new byte[KeyBindingsConfig.MaxScancode + 1];
        var southOnly = new HostInputState
        {
            ControllerConnected = true,
            GamepadButtonsMask = 1u << (GamepadBindingCode.South - GamepadBindingCode.MinCode)
        };
        ModernInputController.ApplyProfileBindings(keys, 0u, null, settings, globalProfile.KeyBindings, null, ref southOnly);
        Assert.Equal(PsxPadButton.None, southOnly.Buttons & PsxPadButton.Cross);

        var westOnly = new HostInputState
        {
            ControllerConnected = true,
            GamepadButtonsMask = 1u << (GamepadBindingCode.West - GamepadBindingCode.MinCode)
        };
        ModernInputController.ApplyProfileBindings(keys, 0u, null, settings, globalProfile.KeyBindings, null, ref westOnly);
        Assert.NotEqual(PsxPadButton.None, westOnly.Buttons & PsxPadButton.Cross);

        // Video toggle label check.
        drawer.ActiveTab = 0;
        drawer.RenderOverlay(canvas, settings, null, "No disc", false);
        Assert.Contains(drawer.HitBoxes, h => h.ControlId == "Video_ToggleVsync" && h.Label == "VSync (Caps to Monitor Hz)");
    }

    [Fact]
    public void SetModernControllerSlot_ClearsTrailingDefaults_AndExportsContextMasks()
    {
        var actions = new List<GameActionDescriptor>
        {
            new("moveUp", "Move Up", [26], [GamepadBindingCode.DPadUp], 0x0010, 0x0010),
            new("moveLeft", "Move Left", [4], [GamepadBindingCode.DPadLeft], 0x0080, 0x0080),
            new("map", "Open Map", [16], [GamepadBindingCode.DPadLeft, GamepadBindingCode.Back], 0x0001, 0x0001)
        };
        var provider = new StubProfile(actions);
        var hook = new CaptureHook();
        var settings = new RecompSettings { InputProfileMode = InputProfileMode.PerGameModern };
        settings.SetModernControllerSlot(actions[2], GamepadBindingCode.Back);
        IReadOnlyList<int> mapPad = settings.GetModernControllerBindings(actions[2]);
        Assert.Single(mapPad);
        Assert.Equal(GamepadBindingCode.Back, mapPad[0]);

        // Rebind map onto DPadUp -> DPadUp becomes claimed by non-move action cleanly.
        settings.SetModernControllerSlot(actions[2], GamepadBindingCode.DPadUp);
        Directory.CreateDirectory(_tempRoot);
        using var cpu = new PsxCpu(settings, _tempRoot, new SoftwareCpuRenderBackend(), new ReStationLogger(_tempRoot, false));
        byte[] keys = new byte[KeyBindingsConfig.MaxScancode + 1];
        keys[26] = 1; // W (moveUp)
        var st = new HostInputState
        {
            ControllerConnected = true,
            GamepadButtonsMask = 1u << (GamepadBindingCode.DPadLeft - GamepadBindingCode.MinCode)
        };
        ModernInputController.ApplyNativeGameActions(keys, 0u, in st, cpu, settings, provider, hook,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase), [], [], out _);

        Assert.Equal(1, hook.LastContext.KbmMoveMask);
        Assert.Equal(1, hook.LastContext.ControllerDpadClaimedByNonMoveMask);
        Assert.Equal(st.GamepadButtonsMask, hook.LastContext.GamepadButtonsMask);

        // Verify 144 FPS accumulator averages 2.4x (7 extra swaps across 5 VBlanks -> 12 total swaps) and L3/R3 rebound guard.
        double swapBudget = 0.0;
        int total144Swaps = 0;
        for (int v = 0; v < 5; v++)
            total144Swaps += 1 + Program.ComputePlannedExtraSwaps(uncapped: false, cappedTarget: 144, ref swapBudget);
        Assert.Equal(12, total144Swaps);

        var kbL3 = new KeyBindingsConfig();
        Assert.False(HostInputMapper.IsControllerCodeBound(kbL3, GamepadBindingCode.L3));
        kbL3.SetControllerCode(BindableAction.Cross, GamepadBindingCode.L3);
        Assert.True(HostInputMapper.IsControllerCodeBound(kbL3, GamepadBindingCode.L3));
    }

    [Fact]
    public void RequestQuickLoad_WhenSlotMissing_ReturnsFalseWithoutUnwindingCpu()
    {
        Directory.CreateDirectory(_tempRoot);
        var settings = new RecompSettings();
        var logger = new ReStationLogger(_tempRoot, false);
        using var cpu = new PsxCpu(settings, _tempRoot, new SoftwareCpuRenderBackend(), logger);
        using var pacer = new HighResFramePacer(60.0);
        using var host = new ReStationHost(cpu, new ReStationModuleManifest { ModuleId = "ql-missing" }, _tempRoot, new RecompHostOptions(), pacer, logger);

        bool requested = host.RequestQuickLoad(0);
        Assert.False(requested);
        Assert.False(cpu.ShouldUnwind);
        Assert.False(host.HasFinished);
        Assert.Equal(-1, host.PendingQuickLoadSlot);
    }

    [Fact]
    public void BiosMemCard_AsyncOpenReadWriteAndFirstFile_DeliverIoEndAndReturnZeroForLibmcrd()
    {
        Directory.CreateDirectory(_tempRoot);
        using var cpu = new PsxCpu(new RecompSettings(), _tempRoot, new SoftwareCpuRenderBackend(), new ReStationLogger(_tempRoot, false));
        cpu.Bios.SetCardIdentity("libmcrd-async-test");
        byte[] payload = new byte[8192];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)((i * 7 + 3) & 0xFF);
        Assert.True(cpu.Bios.MemCard.WriteFile("bu00:BASLUS-01115-FARM0", payload));

        cpu.R[4] = 0xF4000001U; cpu.R[5] = 0x0004U; cpu.R[6] = 0x2000U; cpu.R[7] = 0;
        cpu.R[9] = 0x08; cpu.Bios.HandleVectorCall(0xB0U);
        uint evSwIoe = cpu.R[2];
        cpu.R[4] = evSwIoe; cpu.R[9] = 0x0C; cpu.Bios.HandleVectorCall(0xB0U);

        cpu.R[4] = 0xF0000011U; cpu.R[5] = 0x0004U; cpu.R[6] = 0x2000U; cpu.R[7] = 0;
        cpu.R[9] = 0x08; cpu.Bios.HandleVectorCall(0xB0U);
        uint evHwIoe = cpu.R[2];
        cpu.R[4] = evHwIoe; cpu.R[9] = 0x0C; cpu.Bios.HandleVectorCall(0xB0U);

        const uint nameAddr = 0x80028000U, dstAddr = 0x80029000U, dirAddr = 0x8002A000U;
        byte[] fBytes = Encoding.ASCII.GetBytes("bu00:BASLUS-01115-FARM0\0");
        for (uint i = 0; i < fBytes.Length; i++) cpu.Bus.Write8(nameAddr + i, fBytes[i]);

        // Async read open (0x8001): read() returns 0, notifies RAM, and delivers SwCARD/HwCARD EvSpIOE.
        var ramNotifies = new List<(uint Start, uint Len)>();
        cpu.Bus.OnRamWriteRange += (s, l) => ramNotifies.Add((s, l));
        cpu.R[4] = nameAddr; cpu.R[5] = 0x8001U; cpu.R[9] = 0x32; cpu.Bios.HandleVectorCall(0xB0U);
        uint fd = cpu.R[2];
        Assert.NotEqual(0xFFFFFFFFU, fd);
        cpu.R[4] = fd; cpu.R[5] = 0x200U; cpu.R[6] = 0U; cpu.R[9] = 0x33; cpu.Bios.HandleVectorCall(0xB0U);
        Assert.Equal(0x200U, cpu.R[2]);
        cpu.R[4] = fd; cpu.R[5] = dstAddr; cpu.R[6] = 0x80U; cpu.R[9] = 0x34; cpu.Bios.HandleVectorCall(0xB0U);
        Assert.Equal(0U, cpu.R[2]);
        Assert.Contains((dstAddr & 0x1FFFFFU, 0x80U), ramNotifies);
        for (uint i = 0; i < 0x80U; i++) Assert.Equal(payload[0x200 + i], cpu.Bus.Read8(dstAddr + i));
        cpu.R[4] = evSwIoe; cpu.R[9] = 0x0B; cpu.Bios.HandleVectorCall(0xB0U);
        Assert.Equal(1U, cpu.R[2]);
        cpu.R[4] = fd; cpu.R[9] = 0x36; cpu.Bios.HandleVectorCall(0xB0U);

        // Async write open (0x8001 without 0x0202): write() returns 0, delivers EvSpIOE, and flushes on close().
        cpu.Bus.Write32(dstAddr, 0xCAFEBABEU);
        cpu.R[4] = nameAddr; cpu.R[5] = 0x8001U; cpu.R[9] = 0x32; cpu.Bios.HandleVectorCall(0xB0U);
        fd = cpu.R[2];
        cpu.R[4] = fd; cpu.R[5] = 0x200U; cpu.R[6] = 0U; cpu.R[9] = 0x33; cpu.Bios.HandleVectorCall(0xB0U);
        cpu.R[4] = fd; cpu.R[5] = dstAddr; cpu.R[6] = 0x80U; cpu.R[9] = 0x35; cpu.Bios.HandleVectorCall(0xB0U);
        Assert.Equal(0U, cpu.R[2]);
        cpu.R[4] = evSwIoe; cpu.R[9] = 0x0B; cpu.Bios.HandleVectorCall(0xB0U);
        Assert.Equal(1U, cpu.R[2]);
        cpu.R[4] = fd; cpu.R[9] = 0x36; cpu.Bios.HandleVectorCall(0xB0U);
        byte[]? updated = cpu.Bios.MemCard.ReadFile("bu00:BASLUS-01115-FARM0");
        Assert.NotNull(updated);
        Assert.Equal((byte)0xBE, updated![0x200]);

        // Synchronous read open (0x0001) still returns byte count; FirstFile miss delivers HwCARD EvSpIOE.
        cpu.R[4] = nameAddr; cpu.R[5] = 0x0001U; cpu.R[9] = 0x32; cpu.Bios.HandleVectorCall(0xB0U);
        fd = cpu.R[2];
        cpu.R[4] = fd; cpu.R[5] = dstAddr + 0x100U; cpu.R[6] = 0x80U; cpu.R[9] = 0x34; cpu.Bios.HandleVectorCall(0xB0U);
        Assert.Equal(0x80U, cpu.R[2]);
        cpu.R[4] = fd; cpu.R[9] = 0x36; cpu.Bios.HandleVectorCall(0xB0U);

        cpu.R[4] = evHwIoe; cpu.R[9] = 0x0B; cpu.Bios.HandleVectorCall(0xB0U);
        Assert.Equal(0x80000600U, cpu.Bus.Read32(0x80000150U));
        Assert.Equal(0xA0U, cpu.Bus.Read32(0x80000154U));
        uint buNamePtr = cpu.Bus.Read32(0x80000600U);
        Assert.Equal((byte)'b', cpu.Bus.Read8(buNamePtr));
        Assert.Equal((byte)'u', cpu.Bus.Read8(buNamePtr + 1U));
        Assert.Equal((byte)0, cpu.Bus.Read8(buNamePtr + 2U));
        Assert.Equal(0x800006F4U, cpu.Bus.Read32(0x80000634U));
        cpu.Bus.Write32(0x80000634U, 0x80042A70U);

        byte[] missBytes = Encoding.ASCII.GetBytes("bu00:MISSING*\0");
        for (uint i = 0; i < missBytes.Length; i++) cpu.Bus.Write8(nameAddr + i, missBytes[i]);
        cpu.R[4] = nameAddr; cpu.R[5] = dirAddr; cpu.R[9] = 0x42; cpu.Bios.HandleVectorCall(0xB0U);
        Assert.Equal(0U, cpu.R[2]);
        Assert.Equal(0x800006F4U, cpu.Bus.Read32(0x80000634U));
        cpu.R[4] = evHwIoe; cpu.R[9] = 0x0B; cpu.Bios.HandleVectorCall(0xB0U);
        Assert.Equal(1U, cpu.R[2]);
    }

    private sealed class StubProfile(IReadOnlyList<GameActionDescriptor> actions) : IModuleInputProfileProvider
    {
        public string ProfileDisplayName => "Stub";
        public IReadOnlyList<GameActionDescriptor> ModernActions => actions;
        public bool IsGameplayContextActive(IReStationCpu cpu) => true;
    }

    private sealed class CaptureHook : INativeGameActionHook
    {
        public NativeGameActionContext LastContext { get; private set; }
        public void ApplyNativeActions(IReStationCpuEx cpu, ReadOnlySpan<string> activeActions, ReadOnlySpan<string> pressedActions, (float MoveX, float MoveY, float LookX, float LookY) axes, bool gameplayActive, out bool releaseMouseCapture)
            => releaseMouseCapture = false;
        public void ApplyNativeActions(IReStationCpuEx cpu, ReadOnlySpan<string> activeActions, ReadOnlySpan<string> pressedActions, in NativeGameActionContext context, bool gameplayActive, out bool releaseMouseCapture)
        {
            LastContext = context;
            releaseMouseCapture = false;
        }
    }
}
