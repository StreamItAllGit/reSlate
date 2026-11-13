// @module: reStation.Player.HostInputMapper
// @purpose: Aggregates SDL3 keyboard, mouse delta/buttons, and gamepad axes/buttons into reStation HostInputState.
// @depends-on: reStation.Contracts, reStation.Core, reStation.Core.Input
// @exports: HostInputMapper
// @owner: devv-player-ui (T6)

using reStation.Contracts;
using reStation.Core;
using reStation.Core.Input;

namespace reStation.Player;

/// <summary>Reads SDL3 input devices and maps them into <see cref="HostInputState"/>.</summary>
internal static unsafe class HostInputMapper
{
    private const float StickDeadzone = 0.15f;

    public static HostInputState BuildLiveInput(
        IReStationCpu cpu,
        bool drawerOpen,
        RecompSettings settings,
        IntPtr gamepad,
        ref int dx,
        ref int dy,
        bool mouseLeft,
        bool mouseRight)
    {
        _ = cpu;
        if (drawerOpen) { dx = 0; dy = 0; return default; }

        byte* ks = Sdl3Native.SDL_GetKeyboardState(out int n);
        ReadOnlySpan<byte> keys = ks != null ? new ReadOnlySpan<byte>(ks, n) : ReadOnlySpan<byte>.Empty;

        var st = new HostInputState
        {
            MouseDeltaX = dx,
            MouseDeltaY = dy,
            LeftStickX = ReadAxis(gamepad, 0),
            LeftStickY = ReadAxis(gamepad, 1),
            RightStickX = ReadAxis(gamepad, 2),
            RightStickY = ReadAxis(gamepad, 3)
        };
        ModernInputController.ApplyKeyBindings(keys, settings, ref st);

        if (gamepad != IntPtr.Zero)
        {
            if (Sdl3Native.SDL_GetGamepadButton(gamepad, 0) != 0) st.Buttons |= PsxPadButton.Cross;
            if (Sdl3Native.SDL_GetGamepadButton(gamepad, 1) != 0) st.Buttons |= PsxPadButton.Circle;
            if (Sdl3Native.SDL_GetGamepadButton(gamepad, 2) != 0) st.Buttons |= PsxPadButton.Square;
            if (Sdl3Native.SDL_GetGamepadButton(gamepad, 3) != 0) st.Buttons |= PsxPadButton.Triangle;
            if (Sdl3Native.SDL_GetGamepadButton(gamepad, 6) != 0) st.Buttons |= PsxPadButton.Start;
            if (Sdl3Native.SDL_GetGamepadButton(gamepad, 4) != 0) st.Buttons |= PsxPadButton.Select;
            if (Sdl3Native.SDL_GetGamepadButton(gamepad, 9) != 0) st.Buttons |= PsxPadButton.L1;
            if (Sdl3Native.SDL_GetGamepadButton(gamepad, 10) != 0) st.Buttons |= PsxPadButton.R1;
        }

        if (mouseLeft) st.Buttons |= PsxPadButton.R1;
        if (mouseRight) st.Buttons |= PsxPadButton.L1;
        dx = 0;
        dy = 0;
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

    private static float ReadAxis(IntPtr gamepad, int axis)
    {
        if (gamepad == IntPtr.Zero) return 0f;
        float v = Sdl3Native.SDL_GetGamepadAxis(gamepad, axis) / 32768f;
        return MathF.Abs(v) < StickDeadzone ? 0f : Math.Clamp(v, -1f, 1f);
    }
}
