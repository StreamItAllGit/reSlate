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
        bool sdlAvailable,
        RecompSettings settings,
        KeyBindingsConfig? globalBindings,
        IModuleInputProfileProvider? profileProvider,
        IntPtr gamepad,
        ref int dx,
        ref int dy,
        ReadOnlySpan<byte> scriptKeys = default,
        uint scriptMouseMask = 0u)
    {
        if (drawerOpen) { dx = 0; dy = 0; return default; }

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
        int scriptCopy = Math.Min(scriptKeys.Length, mergedKeys.Length);
        for (int i = 0; i < scriptCopy; i++)
        {
            if (scriptKeys[i] != 0) mergedKeys[i] = 1;
        }
        uint mergedMouseMask = liveMouseMask | scriptMouseMask;

        var st = new HostInputState
        {
            MouseDeltaX = dx,
            MouseDeltaY = dy,
            LeftStickX = sdlAvailable ? ReadAxis(gamepad, 0) : 0f,
            LeftStickY = sdlAvailable ? ReadAxis(gamepad, 1) : 0f,
            RightStickX = sdlAvailable ? ReadAxis(gamepad, 2) : 0f,
            RightStickY = sdlAvailable ? ReadAxis(gamepad, 3) : 0f
        };
        ModernInputController.ApplyProfileBindings(mergedKeys, mergedMouseMask, cpu, settings, globalBindings, profileProvider, ref st);

        if (sdlAvailable && gamepad != IntPtr.Zero)
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
