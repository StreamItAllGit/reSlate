// @module: reStation.Player.HostInputMapper
// @purpose: Aggregates SDL3 keyboard, mouse delta/buttons, and gamepad axes/buttons into reSlate HostInputState.
// @depends-on: reStation.Contracts, reStation.Core, reStation.Core.Cpu, reStation.Core.Input
// @exports: HostInputMapper
// @owner: devv-player-ui (T6)

using reStation.Contracts;
using reStation.Core;
using reStation.Core.Cpu;
using reStation.Core.Input;

namespace reStation.Player;

/// <summary>Reads SDL3 input devices and maps them into <see cref="HostInputState"/>.</summary>
internal static unsafe class HostInputMapper
{
    private const float StickDeadzone = 0.15f;

    public static HostInputState BuildLiveInput(
        IReStationCpuEx cpu,
        bool drawerOpen,
        bool sdlAvailable,
        RecompSettings settings,
        KeyBindingsConfig? globalBindings,
        IModuleInputProfileProvider? profileProvider,
        IntPtr gamepad,
        ref float dx,
        ref float dy,
        bool wheelUp,
        bool wheelDown,
        INativeGameActionHook? nativeHook,
        ISet<string> prevNativeActions,
        List<string> activeNativeBuffer,
        List<string> pressedNativeBuffer,
        out bool releaseMouseCapture) =>
        BuildLiveInput(
            cpu, drawerOpen, sdlAvailable, settings, globalBindings, profileProvider, gamepad,
            ref dx, ref dy, wheelUp, wheelDown, ReadOnlySpan<byte>.Empty, 0u,
            nativeHook, prevNativeActions, activeNativeBuffer, pressedNativeBuffer, out releaseMouseCapture);

    public static HostInputState BuildLiveInput(
        IReStationCpuEx cpu,
        bool drawerOpen,
        bool sdlAvailable,
        RecompSettings settings,
        KeyBindingsConfig? globalBindings,
        IModuleInputProfileProvider? profileProvider,
        IntPtr gamepad,
        ref float dx,
        ref float dy,
        bool wheelUp,
        bool wheelDown,
        ReadOnlySpan<byte> latchedKeys,
        uint latchedMouseMask,
        INativeGameActionHook? nativeHook,
        ISet<string> prevNativeActions,
        List<string> activeNativeBuffer,
        List<string> pressedNativeBuffer,
        out bool releaseMouseCapture
#if DEBUG || RESTATION_TEST_AUTOMATION
        , DebugAutomationDiagnostics? diagnostics = null
#endif
        )
    {
        if (drawerOpen)
        {
            dx = 0f;
            dy = 0f;
            releaseMouseCapture = false;
            prevNativeActions.Clear();
            if (cpu is PsxCpu drawerCpu)
                ModernInputController.ApplyToCpu(drawerCpu, default, settings);
            return default;
        }

        ReadOnlySpan<byte> liveKeys = ReadOnlySpan<byte>.Empty;
        uint liveMouseMask = 0u;
        if (sdlAvailable)
        {
            byte* ks = Sdl3Native.SDL_GetKeyboardState(out int n);
            if (ks != null && n > 0) liveKeys = new ReadOnlySpan<byte>(ks, n);
            liveMouseMask = Sdl3Native.GetMouseButtonsMask();
        }

        Span<byte> mergedKeys = stackalloc byte[KeyBindingsConfig.MaxScancode + 1];
        int liveCopy = Math.Min(liveKeys.Length, mergedKeys.Length);
        liveKeys[..liveCopy].CopyTo(mergedKeys);
        int latchedCopy = Math.Min(latchedKeys.Length, mergedKeys.Length);
        for (int i = 0; i < latchedCopy; i++)
            mergedKeys[i] |= latchedKeys[i];
        uint mergedMouseMask = liveMouseMask | latchedMouseMask;
        bool controllerConnected = sdlAvailable && gamepad != IntPtr.Zero;
        float rawLx = controllerConnected ? ReadRawAxis(gamepad, 0) : 0f;
        float rawLy = controllerConnected ? ReadRawAxis(gamepad, 1) : 0f;

        var st = new HostInputState
        {
            MouseDeltaX = dx,
            MouseDeltaY = dy,
            WheelUp = wheelUp,
            WheelDown = wheelDown,
            ControllerConnected = controllerConnected,
            RawLeftStickX = rawLx,
            RawLeftStickY = rawLy,
            LeftStickX = ApplyDeadzone(rawLx),
            LeftStickY = ApplyDeadzone(rawLy),
            RightStickX = controllerConnected ? ReadAxis(gamepad, 2) : 0f,
            RightStickY = controllerConnected ? ReadAxis(gamepad, 3) : 0f,
            GamepadButtonsMask = controllerConnected ? ReadGamepadButtonsMask(gamepad) : 0u
        };
#if DEBUG || RESTATION_TEST_AUTOMATION
        diagnostics?.ApplyVirtualOverrides(mergedKeys, ref st);
#endif
        ModernInputController.ApplyProfileBindings(mergedKeys, mergedMouseMask, cpu, settings, globalBindings, profileProvider, ref st);

        if (st.ControllerConnected && !(settings.InputProfileMode == InputProfileMode.PerGameModern && profileProvider is not null) && gamepad != IntPtr.Zero)
        {
            KeyBindingsConfig? activeKb = settings.InputProfileMode == InputProfileMode.GlobalController
                ? (globalBindings ?? settings.KeyBindings)
                : settings.KeyBindings;
            ApplyStickClickPassthrough(gamepad, activeKb, ref st);
        }

        if (cpu is PsxCpu livePsxCpu)
            ModernInputController.ApplyToCpu(livePsxCpu, in st, settings);

        ModernInputController.ApplyNativeGameActions(
            mergedKeys, mergedMouseMask, in st, cpu, settings, profileProvider,
            nativeHook, prevNativeActions, activeNativeBuffer, pressedNativeBuffer, out releaseMouseCapture);

        dx = 0f;
        dy = 0f;
        return st;
    }

    public static (float StickX, float StickY) ReadRightStick(IntPtr gamepad) =>
        (ReadAxis(gamepad, 2), ReadAxis(gamepad, 3));

    public static IntPtr OpenFirstGamepad()
    {
        uint* ids = Sdl3Native.SDL_GetGamepads(out int count);
        IntPtr gp = count > 0 && ids != null ? Sdl3Native.SDL_OpenGamepad(ids[0]) : IntPtr.Zero;
        if (ids != null) Sdl3Native.SDL_free(ids);
        return gp;
    }

    private static uint ReadGamepadButtonsMask(IntPtr gamepad)
    {
        uint mask = 0u;
        IReadOnlyList<int> codes = GamepadBindingCode.StandardCodes;
        for (int i = 0; i < codes.Count; i++)
        {
            int code = codes[i];
            bool down = code == GamepadBindingCode.LT
                ? ReadRawAxis(gamepad, 4) >= 0.4f
                : code == GamepadBindingCode.RT
                    ? ReadRawAxis(gamepad, 5) >= 0.4f
                    : GamepadBindingCode.TryToSdlButtonIndex(code, out int btnIdx) && Sdl3Native.SDL_GetGamepadButton(gamepad, btnIdx) != 0;
            if (down) mask |= 1u << (code - GamepadBindingCode.MinCode);
        }
        return mask;
    }

    internal static void ApplyStickClickPassthrough(IntPtr gamepad, KeyBindingsConfig? activeKb, ref HostInputState st)
    {
        if (!IsControllerCodeBound(activeKb, GamepadBindingCode.L3) && Sdl3Native.SDL_GetGamepadButton(gamepad, 7) != 0)
            st.Buttons |= PsxPadButton.L3;
        if (!IsControllerCodeBound(activeKb, GamepadBindingCode.R3) && Sdl3Native.SDL_GetGamepadButton(gamepad, 8) != 0)
            st.Buttons |= PsxPadButton.R3;
    }

    internal static bool IsControllerCodeBound(KeyBindingsConfig? kb, int code)
    {
        if (kb?.ControllerBindings is null) return false;
        foreach (int bound in kb.ControllerBindings.Values)
            if (bound == code) return true;
        return false;
    }

    private static float ReadRawAxis(IntPtr gamepad, int axis) =>
        gamepad == IntPtr.Zero ? 0f : Math.Clamp(Sdl3Native.SDL_GetGamepadAxis(gamepad, axis) / 32768f, -1f, 1f);

    private static float ApplyDeadzone(float raw) => MathF.Abs(raw) < StickDeadzone ? 0f : raw;

    private static float ReadAxis(IntPtr gamepad, int axis) => ApplyDeadzone(ReadRawAxis(gamepad, axis));
}
