// @module: reStation.Core.Input.ModernInputController
// @purpose: Maps host keyboard/mouse/gamepad state onto PS1 digital+analog pad state per ControlMode and pushes it into PsxCpu.
// @depends-on: reStation.Core, reStation.Core.Cpu
// @exports: PsxPadButton, HostInputState, ModernInputController
// @owner: devv-audio-mdec (T3) — public surface T1-locked (Player HostInputMapper consumes it).

using reStation.Contracts;
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
    public static void ApplyKeyBindings(ReadOnlySpan<byte> keyboardState, RecompSettings settings, ref HostInputState state) =>
        ApplyKeyMap(keyboardState, 0u, settings?.KeyBindings ?? new KeyBindingsConfig(), ref state);

    /// <summary>
    /// Applies active 3-tier bindings (Global 1:1, Game 1:1, or Module-declared Modern PC context map) supporting keyboard scancodes and mouse button codes (<c>1001..1005</c>).
    /// </summary>
    public static void ApplyProfileBindings(
        ReadOnlySpan<byte> keyboardState,
        uint mouseButtonsMask,
        IReStationCpu? cpu,
        RecompSettings settings,
        KeyBindingsConfig? globalBindings,
        IModuleInputProfileProvider? profileProvider,
        ref HostInputState state)
    {
        ArgumentNullException.ThrowIfNull(settings);
        InputProfileMode mode = settings.InputProfileMode;
        if (mode == InputProfileMode.GlobalController)
        {
            ApplyKeyMap(keyboardState, mouseButtonsMask, globalBindings ?? settings.KeyBindings ?? new KeyBindingsConfig(), ref state);
            return;
        }
        if (mode == InputProfileMode.PerGameModern && profileProvider is not null)
        {
            ApplyModernModuleProfile(keyboardState, mouseButtonsMask, cpu, settings, profileProvider, ref state);
            return;
        }
        ApplyKeyMap(keyboardState, mouseButtonsMask, settings.KeyBindings ?? new KeyBindingsConfig(), ref state);
    }

    private static void ApplyKeyMap(ReadOnlySpan<byte> keyboardState, uint mouseButtonsMask, KeyBindingsConfig kb, ref HostInputState state)
    {
        foreach ((BindableAction action, PsxPadButton button) in ButtonMap)
        {
            if (IsCodeDown(keyboardState, mouseButtonsMask, kb.GetScancode(action))) state.Buttons |= button;
        }
        state.QuickSave |= IsCodeDown(keyboardState, mouseButtonsMask, kb.GetScancode(BindableAction.QuickSave));
        state.QuickLoad |= IsCodeDown(keyboardState, mouseButtonsMask, kb.GetScancode(BindableAction.QuickLoad));
    }

    private static void ApplyModernModuleProfile(
        ReadOnlySpan<byte> keyboardState,
        uint mouseButtonsMask,
        IReStationCpu? cpu,
        RecompSettings settings,
        IModuleInputProfileProvider profileProvider,
        ref HostInputState state)
    {
        bool gameplay = cpu is not null && profileProvider.IsGameplayContextActive(cpu);
        bool upBound = false, downBound = false, leftBound = false, rightBound = false;
        foreach (GameActionDescriptor action in profileProvider.ModernActions)
        {
            int code = settings.GetModernActionBinding(action);
            if (code == 82) upBound = true;
            else if (code == 81) downBound = true;
            else if (code == 80) leftBound = true;
            else if (code == 79) rightBound = true;

            if (!IsCodeDown(keyboardState, mouseButtonsMask, code)) continue;
            if (action.IsQuickSave) state.QuickSave = true;
            if (action.IsQuickLoad) state.QuickLoad = true;
            ushort mask = gameplay ? action.GameplayPadMask : action.MenuPadMask;
            state.Buttons |= (PsxPadButton)mask;
        }
        if (!upBound && IsDown(keyboardState, 82)) state.Buttons |= PsxPadButton.Up;
        if (!downBound && IsDown(keyboardState, 81)) state.Buttons |= PsxPadButton.Down;
        if (!leftBound && IsDown(keyboardState, 80)) state.Buttons |= PsxPadButton.Left;
        if (!rightBound && IsDown(keyboardState, 79)) state.Buttons |= PsxPadButton.Right;
    }

    private static bool IsCodeDown(ReadOnlySpan<byte> keys, uint mouseButtonsMask, int code) => code switch
    {
        <= 0 => false,
        KeyBindingsConfig.MouseLeft => (mouseButtonsMask & 1u) != 0,
        KeyBindingsConfig.MouseMiddle => (mouseButtonsMask & 2u) != 0,
        KeyBindingsConfig.MouseRight => (mouseButtonsMask & 4u) != 0,
        KeyBindingsConfig.MouseX1 => (mouseButtonsMask & 8u) != 0,
        KeyBindingsConfig.MouseX2 => (mouseButtonsMask & 16u) != 0,
        _ => IsDown(keys, code)
    };

    /// <summary>Pushes pad state into port 0; in ModernWasdMouse mode mouse motion drives the right stick.</summary>
    public static void ApplyToCpu(PsxCpu cpu, in HostInputState input, RecompSettings settings) =>
        ApplyToCpu(cpu, 0, in input, settings);

    /// <summary>Pushes pad state into <paramref name="port"/> (0 or 1); in ModernWasdMouse mode mouse motion drives the right stick.</summary>
    public static void ApplyToCpu(PsxCpu cpu, int port, in HostInputState input, RecompSettings settings)
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
        cpu.SetControllerState(port, (ushort)input.Buttons, ToAxis(input.LeftStickX), ToAxis(input.LeftStickY), ToAxis(rx), ToAxis(ry));
    }

    /// <summary>Parses a scripted frame-range token list (<c>start[-end]:up,cross;...</c>) for deterministic fixtures and tests.</summary>
    public static HostInputState ParseScriptForFrame(string? script, long frame)
    {
        var state = new HostInputState();
        if (string.IsNullOrWhiteSpace(script)) return state;
        foreach (string token in script.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = token.Split(':');
            if (parts.Length < 2) continue;
            string[] range = parts[0].Split('-');
            if (!long.TryParse(range[0], out long start)) continue;
            long end = range.Length > 1 && long.TryParse(range[1], out long parsedEnd) ? parsedEnd : start;
            if (frame < start || frame > end) continue;
            foreach (string key in parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                switch (key.ToLowerInvariant())
                {
                    case "w": case "up": state.Buttons |= PsxPadButton.Up; break;
                    case "s": case "down": state.Buttons |= PsxPadButton.Down; break;
                    case "a": case "left": state.Buttons |= PsxPadButton.Left; break;
                    case "d": case "right": state.Buttons |= PsxPadButton.Right; break;
                    case "cross": state.Buttons |= PsxPadButton.Cross; break;
                    case "circle": state.Buttons |= PsxPadButton.Circle; break;
                    case "square": state.Buttons |= PsxPadButton.Square; break;
                    case "triangle": state.Buttons |= PsxPadButton.Triangle; break;
                    case "l1": state.Buttons |= PsxPadButton.L1; break;
                    case "r1": state.Buttons |= PsxPadButton.R1; break;
                    case "l2": state.Buttons |= PsxPadButton.L2; break;
                    case "r2": state.Buttons |= PsxPadButton.R2; break;
                    case "start": state.Buttons |= PsxPadButton.Start; break;
                    case "select": state.Buttons |= PsxPadButton.Select; break;
                    case "lookright": state.MouseDeltaX += 6.0f; break;
                    case "lookleft": state.MouseDeltaX -= 6.0f; break;
                    case "lookup": state.MouseDeltaY -= 6.0f; break;
                    case "lookdown": state.MouseDeltaY += 6.0f; break;
                    case "f5": case "quicksave": state.QuickSave = true; break;
                    case "f9": case "quickload": state.QuickLoad = true; break;
                }
            }
        }
        return state;
    }

    /// <summary>Converts -1..1 to the PS1 analog byte range (0x00..0xFF, centre 0x80).</summary>
    public static byte ToAxis(float value) => (byte)Math.Clamp((int)MathF.Round((Math.Clamp(value, -1f, 1f) + 1f) * 127.5f), 0, 255);

    private static bool IsDown(ReadOnlySpan<byte> keys, int scancode) => scancode > 0 && scancode < keys.Length && keys[scancode] != 0;
}
