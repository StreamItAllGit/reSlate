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
        out bool releaseMouseCapture)
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
        ModernInputController.ApplyProfileBindings(mergedKeys, mergedMouseMask, cpu, settings, globalBindings, profileProvider, ref st);

        if (controllerConnected && !(settings.InputProfileMode == InputProfileMode.PerGameModern && profileProvider is not null))
        {
            ApplyDirectControllerFallback(gamepad, ref st);
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

    private static void ApplyDirectControllerFallback(IntPtr gamepad, ref HostInputState st)
    {
        if (Sdl3Native.SDL_GetGamepadButton(gamepad, 0) != 0) st.Buttons |= PsxPadButton.Cross;
        if (Sdl3Native.SDL_GetGamepadButton(gamepad, 1) != 0) st.Buttons |= PsxPadButton.Circle;
        if (Sdl3Native.SDL_GetGamepadButton(gamepad, 2) != 0) st.Buttons |= PsxPadButton.Square;
        if (Sdl3Native.SDL_GetGamepadButton(gamepad, 3) != 0) st.Buttons |= PsxPadButton.Triangle;
        if (Sdl3Native.SDL_GetGamepadButton(gamepad, 4) != 0) st.Buttons |= PsxPadButton.Select;
        if (Sdl3Native.SDL_GetGamepadButton(gamepad, 6) != 0) st.Buttons |= PsxPadButton.Start;
        if (Sdl3Native.SDL_GetGamepadButton(gamepad, 7) != 0) st.Buttons |= PsxPadButton.L3;
        if (Sdl3Native.SDL_GetGamepadButton(gamepad, 8) != 0) st.Buttons |= PsxPadButton.R3;
        if (Sdl3Native.SDL_GetGamepadButton(gamepad, 9) != 0) st.Buttons |= PsxPadButton.L1;
        if (Sdl3Native.SDL_GetGamepadButton(gamepad, 10) != 0) st.Buttons |= PsxPadButton.R1;
        if (Sdl3Native.SDL_GetGamepadButton(gamepad, 11) != 0) st.Buttons |= PsxPadButton.Up;
        if (Sdl3Native.SDL_GetGamepadButton(gamepad, 12) != 0) st.Buttons |= PsxPadButton.Down;
        if (Sdl3Native.SDL_GetGamepadButton(gamepad, 13) != 0) st.Buttons |= PsxPadButton.Left;
        if (Sdl3Native.SDL_GetGamepadButton(gamepad, 14) != 0) st.Buttons |= PsxPadButton.Right;
        if (ReadRawAxis(gamepad, 4) >= 0.4f) st.Buttons |= PsxPadButton.L2;
        if (ReadRawAxis(gamepad, 5) >= 0.4f) st.Buttons |= PsxPadButton.R2;
    }

    private static float ReadRawAxis(IntPtr gamepad, int axis) =>
        gamepad == IntPtr.Zero ? 0f : Math.Clamp(Sdl3Native.SDL_GetGamepadAxis(gamepad, axis) / 32768f, -1f, 1f);

    private static float ApplyDeadzone(float raw) => MathF.Abs(raw) < StickDeadzone ? 0f : raw;

    private static float ReadAxis(IntPtr gamepad, int axis) => ApplyDeadzone(ReadRawAxis(gamepad, axis));
}
