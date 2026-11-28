// @module: reStation.Tests.MultiBindAndSchemaBInputTests
// @purpose: Validates T7 RS-10 float mouse/targetFps, RS-11 multi-slot bindings + wheel/gamepad capture, and RS-16 Schema B native hooks.

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

public sealed class MultiBindAndSchemaBInputTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "restation-t7-in-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        TestTempCleanup.TryDeleteDirectory(_tempRoot, nameof(MultiBindAndSchemaBInputTests));
    }

    [Fact]
    public void Rs10_FloatMouseDelta_PreservesSubPixelPrecision_AndHostSetTargetFpsUpdatesPacer()
    {
        Directory.CreateDirectory(_tempRoot);
        var state = new HostInputState { MouseDeltaX = 0.5f, MouseDeltaY = -0.25f };
        Assert.Equal(0.5f, state.MouseDeltaX);
        Assert.Equal(-0.25f, state.MouseDeltaY);

        using var cpu = new PsxCpu(new RecompSettings(), _tempRoot, new SoftwareCpuRenderBackend(), new ReStationLogger(_tempRoot, false));
        using var pacer = new HighResFramePacer(60.0);
        using var host = new ReStationHost(cpu, new ReStationModuleManifest { ModuleId = "fps-sync" }, _tempRoot, new RecompHostOptions { TargetFps = 60.0 }, pacer, new ReStationLogger(_tempRoot, false));

        host.SetTargetFps(120, unlock: false);
        Assert.Equal(120.0, host.Options.TargetFps);
        Assert.Equal(120.0, pacer.TargetFps);
        Assert.False(host.Options.UnlockFps);

        host.SetTargetFps(0, unlock: true);
        Assert.True(host.Options.UnlockFps);
        Assert.Equal(0.0, pacer.TargetFps);
    }

    [Fact]
    public void Rs11_MultiBindSlots_WheelAndGamepadCodes_FireNativeActionsAndDrawerCapture()
    {
        Directory.CreateDirectory(_tempRoot);
        Assert.Equal(1007, KeyBindingsConfig.MaxMouseCode);
        Assert.Equal("WheelUp", KeyBindingsConfig.FormatBindingLabel(KeyBindingsConfig.MouseWheelUp));
        Assert.Equal("WheelDn", KeyBindingsConfig.FormatBindingLabel(KeyBindingsConfig.MouseWheelDown));
        Assert.Equal("Pad A", KeyBindingsConfig.FormatBindingLabel(GamepadBindingCode.South));

        var actions = new List<GameActionDescriptor>
        {
            new("useTool", "Use Tool", [4 /*A*/, KeyBindingsConfig.MouseLeft, KeyBindingsConfig.MouseWheelUp], [GamepadBindingCode.West], 0x4000, 0x4000),
            new("interact", "Interact", 6 /*C*/, 0x2000, 0x2000)
        };
        var provider = new StubModernProfile("Test Modern Profile", actions);
        var hook = new SpyNativeHook();
        var settings = new RecompSettings
        {
            InputProfileMode = InputProfileMode.PerGameModern,
            ControllerDpadMode = NativeInputModeIds.ControllerDpadWalkStickRun,
            IsometricMoveMode = NativeInputModeIds.IsometricMoveScreenSpace
        };
        settings.SetModernActionSlot(actions[0], slotIndex: 1, KeyBindingsConfig.MouseRight);
        settings.SetModernControllerSlot(actions[0], GamepadBindingCode.East);
        Assert.Equal(KeyBindingsConfig.MouseRight, settings.GetModernActionBindings(actions[0])[1]);
        Assert.Equal(GamepadBindingCode.East, settings.GetModernControllerBindings(actions[0])[0]);

        using var cpu = new PsxCpu(settings, _tempRoot, new SoftwareCpuRenderBackend(), new ReStationLogger(_tempRoot, false));
        byte[] keys = new byte[KeyBindingsConfig.MaxScancode + 1];
        var st = new HostInputState
        {
            WheelUp = true,
            ControllerConnected = true,
            RawLeftStickX = 0.75f,
            RawLeftStickY = -0.5f,
            GamepadButtonsMask = 1u << (GamepadBindingCode.East - GamepadBindingCode.MinCode)
        };

        var prev = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var active = new List<string>();
        var pressed = new List<string>();
        ModernInputController.ApplyNativeGameActions(keys, 0u, in st, cpu, settings, provider, hook, prev, active, pressed, out _);

        Assert.Contains("useTool", hook.LastActive);
        Assert.Contains("useTool", hook.LastPressed);
        Assert.Equal(NativeInputModeIds.ControllerDpadWalkStickRun, hook.LastContext.ControllerDpadMode);
        Assert.Equal(NativeInputModeIds.IsometricMoveScreenSpace, hook.LastContext.IsometricMoveMode);
        Assert.True(hook.LastContext.ControllerConnected);
        Assert.Equal(0.75f, hook.LastContext.RawLeftStickX);

        // KeybindsDrawerTab 5-column scroll + gamepad capture + right-click clear.
        var drawer = new SlideOverSettingsDrawer { IsOpen = true, ActiveTab = 2 };
        string cfgPath = Path.Combine(_tempRoot, "settings.json");
        drawer.AttachInputProfiles(new GlobalControllerProfile(), Path.Combine(_tempRoot, "global.json"), provider);
        var canvas = new PixelOverlayCanvas(1280, 720);
        drawer.RenderOverlay(canvas, settings, null, "No disc", false);

        drawer.KeybindsTab.ListeningModernActionId = "interact";
        drawer.KeybindsTab.ListeningSlotIndex = 3;
        Assert.True(drawer.KeybindsTab.TryHandleGamepadCapture(GamepadBindingCode.RB, settings, cfgPath, provider));
        Assert.Equal(GamepadBindingCode.RB, settings.GetModernControllerBindings(actions[1])[0]);

        settings.ClearModernActionSlot(actions[0], 1);
        Assert.DoesNotContain(KeyBindingsConfig.MouseRight, settings.GetModernActionBindings(actions[0]));
        Assert.False(drawer.KeybindsTab.ScrollBy(1, actions.Count));
    }

    private sealed class StubModernProfile(string name, IReadOnlyList<GameActionDescriptor> actions) : IModuleInputProfileProvider
    {
        public string ProfileDisplayName => name;
        public IReadOnlyList<GameActionDescriptor> ModernActions => actions;
        public bool IsGameplayContextActive(IReStationCpu cpu) => true;
    }

    private sealed class SpyNativeHook : INativeGameActionHook
    {
        public List<string> LastActive { get; } = new();
        public List<string> LastPressed { get; } = new();
        public NativeGameActionContext LastContext { get; private set; }

        public void ApplyNativeActions(IReStationCpuEx cpu, ReadOnlySpan<string> activeActions, ReadOnlySpan<string> pressedActions, (float MoveX, float MoveY, float LookX, float LookY) axes, bool gameplayActive, out bool releaseMouseCapture)
        {
            releaseMouseCapture = false;
        }

        public void ApplyNativeActions(IReStationCpuEx cpu, ReadOnlySpan<string> activeActions, ReadOnlySpan<string> pressedActions, in NativeGameActionContext context, bool gameplayActive, out bool releaseMouseCapture)
        {
            LastActive.Clear();
            foreach (string a in activeActions) LastActive.Add(a);
            LastPressed.Clear();
            foreach (string p in pressedActions) LastPressed.Add(p);
            LastContext = context;
            releaseMouseCapture = false;
        }
    }
}
