// @module: reStation.Core.Input.ModernInputController
// @purpose: Maps host keyboard/mouse/gamepad state onto PS1 digital+analog pad state per ControlMode and pushes it into PsxCpu.
// @depends-on: reStation.Core, reStation.Core.Cpu
// @exports: PsxPadButton, HostInputState, ModernInputController
// @owner: devv-audio-mdec (T3) — public surface T1-locked (Player HostInputMapper consumes it).

using reStation.Core.Cpu;

namespace reStation.Core.Input;

/// <summary>PS1 digital pad bits (active-high here; the pad protocol inverts them).</summary>
[Flags]
public enum PsxPadButton : ushort
{
    None = 0,
    Select = 0x0001, L3 = 0x0002, R3 = 0x0004, Start = 0x0008,
    Up = 0x0010, Right = 0x0020, Down = 0x0040, Left = 0x0080,
    L2 = 0x0100, R2 = 0x0200, L1 = 0x0400, R1 = 0x0800,
    Triangle = 0x1000, Circle = 0x2000, Cross = 0x4000, Square = 0x8000
}

/// <summary>Per-frame aggregated host input.</summary>
public struct HostInputState
{
    public PsxPadButton Buttons;
    /// <summary>Analog sticks in -1..1.</summary>
    public float LeftStickX, LeftStickY, RightStickX, RightStickY;
    /// <summary>Relative mouse motion in host pixels since last frame.</summary>
    public float MouseDeltaX, MouseDeltaY;
    public bool QuickSave, QuickLoad;
}

public static class ModernInputController
{
    private static readonly (BindableAction Action, PsxPadButton Button)[] ButtonMap =
    {
        (BindableAction.DPadUp, PsxPadButton.Up), (BindableAction.DPadDown, PsxPadButton.Down),
        (BindableAction.DPadLeft, PsxPadButton.Left), (BindableAction.DPadRight, PsxPadButton.Right),
        (BindableAction.Cross, PsxPadButton.Cross), (BindableAction.Circle, PsxPadButton.Circle),
        (BindableAction.Square, PsxPadButton.Square), (BindableAction.Triangle, PsxPadButton.Triangle),
        (BindableAction.L1, PsxPadButton.L1), (BindableAction.R1, PsxPadButton.R1),
        (BindableAction.L2, PsxPadButton.L2), (BindableAction.R2, PsxPadButton.R2),
        (BindableAction.Start, PsxPadButton.Start), (BindableAction.Select, PsxPadButton.Select)
    };

    /// <summary>ORs bound keys from an SDL keyboard-state array (indexed by scancode) into <paramref name="state"/>.</summary>
    public static void ApplyKeyBindings(ReadOnlySpan<byte> keyboardState, RecompSettings settings, ref HostInputState state)
    {
        ArgumentNullException.ThrowIfNull(settings);
        KeyBindingsConfig kb = settings.KeyBindings;
        foreach ((BindableAction action, PsxPadButton button) in ButtonMap)
        {
            if (IsDown(keyboardState, kb.GetScancode(action))) state.Buttons |= button;
        }
        state.QuickSave |= IsDown(keyboardState, kb.GetScancode(BindableAction.QuickSave));
        state.QuickLoad |= IsDown(keyboardState, kb.GetScancode(BindableAction.QuickLoad));
    }

    /// <summary>Pushes pad state into port 0; in ModernWasdMouse mode mouse motion drives the right stick.</summary>
    public static void ApplyToCpu(PsxCpu cpu, in HostInputState input, RecompSettings settings)
    {
        ArgumentNullException.ThrowIfNull(cpu);
        ArgumentNullException.ThrowIfNull(settings);
        float rx = input.RightStickX, ry = input.RightStickY;
        if (settings.ControlMode == ControlMode.ModernWasdMouse)
        {
            float k = (float)settings.MouseSensitivity / 32f;
            rx = Math.Clamp(rx + input.MouseDeltaX * k, -1f, 1f);
            ry = Math.Clamp(ry + input.MouseDeltaY * k, -1f, 1f);
        }
        cpu.SetControllerState(0, (ushort)input.Buttons, ToAxis(input.LeftStickX), ToAxis(input.LeftStickY), ToAxis(rx), ToAxis(ry));
    }

    /// <summary>Converts -1..1 to the PS1 analog byte range (0x00..0xFF, centre 0x80).</summary>
    public static byte ToAxis(float value) => (byte)Math.Clamp((int)MathF.Round((Math.Clamp(value, -1f, 1f) + 1f) * 127.5f), 0, 255);

    private static bool IsDown(ReadOnlySpan<byte> keys, int scancode) => scancode > 0 && scancode < keys.Length && keys[scancode] != 0;
}
