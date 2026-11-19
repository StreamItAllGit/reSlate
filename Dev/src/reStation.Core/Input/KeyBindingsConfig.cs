// @module: reStation.Core.Input.KeyBindingsConfig
// @purpose: Standard 18 keyBindings model mapping PS1 pad buttons + host quick actions to SDL keyboard scancodes (4..511), mouse buttons/wheel (1001..1007), or gamepad labels (2001..2032).
// @depends-on: System.Text.Json.Serialization, reStation.Contracts, reStation.Core
// @exports: InputProfileMode, BindableAction, KeyBindingsConfig

using System.Text.Json.Serialization;
using reStation.Contracts;

namespace reStation.Core.Input;

/// <summary>Selects which binding tier drives host-to-PS1 controller mapping (Standard 18 inputProfileMode).</summary>
public enum InputProfileMode
{
    GlobalController,
    PerGameController,
    PerGameModern
}

/// <summary>Rebindable actions (PS1 digital pad buttons plus host quick-save/load).</summary>
public enum BindableAction
{
    DPadUp, DPadDown, DPadLeft, DPadRight,
    Cross, Circle, Square, Triangle,
    L1, R1, L2, R2,
    Start, Select,
    QuickSave, QuickLoad
}

/// <summary>Action -> SDL keyboard scancode or mouse button code map persisted in <c>settings.json</c> / <c>settings.controls.json</c>.</summary>
public sealed class KeyBindingsConfig
{
    public const int MaxScancode = 511;
    public const int MouseLeft = 1001;
    public const int MouseRight = 1002;
    public const int MouseMiddle = 1003;
    public const int MouseX1 = 1004;
    public const int MouseX2 = 1005;
    public const int MouseWheelUp = 1006;
    public const int MouseWheelDown = 1007;
    public const int MinMouseCode = MouseLeft;
    public const int MaxMouseCode = MouseWheelDown;

    [JsonPropertyName("bindings")]
    public Dictionary<BindableAction, int> Bindings { get; set; } = CreateDefaults(ControlMode.ModernWasdMouse);

    [JsonPropertyName("controllerBindings")]
    public Dictionary<BindableAction, int> ControllerBindings { get; set; } = CreateControllerDefaults();

    public static bool IsMouseCode(int code) => code is >= MinMouseCode and <= MaxMouseCode;

    public static bool IsWheelCode(int code) => code is MouseWheelUp or MouseWheelDown;

    public static bool IsGamepadCode(int code) => GamepadBindingCode.IsGamepadCode(code);

    public static bool IsValidBindingCode(int code) => code == 0 || (code is > 0 and <= MaxScancode) || IsMouseCode(code);

    public static bool IsValidControllerCode(int code) => code == 0 || IsGamepadCode(code);

    public static int NormalizeBindingCode(int code, int fallback = 0) =>
        IsValidBindingCode(code) ? code : (IsValidBindingCode(fallback) ? fallback : 0);

    public static int NormalizeControllerCode(int code, int fallback = 0) =>
        IsValidControllerCode(code) ? code : (IsValidControllerCode(fallback) ? fallback : 0);

    public int GetScancode(BindableAction action) => Bindings.TryGetValue(action, out int sc) ? sc : 0;

    public void SetScancode(BindableAction action, int scancode) => Bindings[action] = NormalizeBindingCode(scancode, 0);

    public int GetControllerCode(BindableAction action) => ControllerBindings is not null && ControllerBindings.TryGetValue(action, out int code) ? code : 0;

    public void SetControllerCode(BindableAction action, int controllerCode)
    {
        ControllerBindings ??= new Dictionary<BindableAction, int>();
        ControllerBindings[action] = NormalizeControllerCode(controllerCode, 0);
    }

    public void ResetDefaults(ControlMode mode)
    {
        Bindings = CreateDefaults(mode);
        ControllerBindings = CreateControllerDefaults();
    }

    /// <summary>Fills missing actions from <paramref name="mode"/> defaults and normalizes keyboard/mouse and controller codes.</summary>
    public void Clamp(ControlMode mode)
    {
        Bindings ??= new Dictionary<BindableAction, int>();
        ControllerBindings ??= new Dictionary<BindableAction, int>();
        Dictionary<BindableAction, int> defaults = CreateDefaults(mode);
        Dictionary<BindableAction, int> ctrlDefaults = CreateControllerDefaults();
        foreach (BindableAction action in Enum.GetValues<BindableAction>())
        {
            Bindings[action] = Bindings.TryGetValue(action, out int sc)
                ? NormalizeBindingCode(sc, defaults[action])
                : defaults[action];
            ControllerBindings[action] = ControllerBindings.TryGetValue(action, out int cc)
                ? NormalizeControllerCode(cc, ctrlDefaults[action])
                : ctrlDefaults[action];
        }
    }

    public static string GetActionLabel(BindableAction action) => action switch
    {
        BindableAction.DPadUp => "D-Pad Up",
        BindableAction.DPadDown => "D-Pad Down",
        BindableAction.DPadLeft => "D-Pad Left",
        BindableAction.DPadRight => "D-Pad Right",
        BindableAction.QuickSave => "Quick Save",
        BindableAction.QuickLoad => "Quick Load",
        _ => action.ToString()
    };

    /// <summary>Formats any keyboard scancode, mouse button/wheel code, or gamepad binding code into a compact keycap label.</summary>
    public static string FormatBindingLabel(int code)
    {
        if (IsGamepadCode(code)) return FormatCompactGamepadLabel(code);
        return code switch
        {
            <= 0 => "---",
            MouseLeft => "LClick",
            MouseRight => "RClick",
            MouseMiddle => "MClick",
            MouseX1 => "Mouse4",
            MouseX2 => "Mouse5",
            MouseWheelUp => "WheelUp",
            MouseWheelDown => "WheelDn",
            >= 4 and <= 29 => ((char)('A' + code - 4)).ToString(),
            >= 30 and <= 38 => ((char)('1' + code - 30)).ToString(),
            39 => "0",
            40 => "Enter",
            41 => "Esc",
            42 => "Bksp",
            43 => "Tab",
            44 => "Space",
            >= 58 and <= 69 => $"F{code - 57}",
            73 => "Ins",
            74 => "Home",
            75 => "PgUp",
            76 => "Del",
            77 => "End",
            78 => "PgDn",
            79 => "Right",
            80 => "Left",
            81 => "Down",
            82 => "Up",
            84 => "Num/",
            85 => "Num*",
            86 => "Num-",
            87 => "Num+",
            88 => "NumEnt",
            89 => "Num1",
            90 => "Num2",
            91 => "Num3",
            92 => "Num4",
            93 => "Num5",
            94 => "Num6",
            95 => "Num7",
            96 => "Num8",
            97 => "Num9",
            98 => "Num0",
            99 => "Num.",
            224 => "LCtrl",
            225 => "LShift",
            226 => "LAlt",
            228 => "RCtrl",
            229 => "RShift",
            230 => "RAlt",
            _ => $"Key#{code}"
        };
    }

    private static string FormatCompactGamepadLabel(int code) => code switch
    {
        GamepadBindingCode.South => "Pad A",
        GamepadBindingCode.East => "Pad B",
        GamepadBindingCode.West => "Pad X",
        GamepadBindingCode.North => "Pad Y",
        GamepadBindingCode.Back => "Back",
        GamepadBindingCode.Guide => "Guide",
        GamepadBindingCode.Start => "Start",
        GamepadBindingCode.L3 => "L3",
        GamepadBindingCode.R3 => "R3",
        GamepadBindingCode.LB => "LB",
        GamepadBindingCode.RB => "RB",
        GamepadBindingCode.DPadUp => "D-Up",
        GamepadBindingCode.DPadDown => "D-Dn",
        GamepadBindingCode.DPadLeft => "D-Lt",
        GamepadBindingCode.DPadRight => "D-Rt",
        GamepadBindingCode.LT => "LT",
        GamepadBindingCode.RT => "RT",
        GamepadBindingCode.Misc1 => "Misc",
        GamepadBindingCode.Paddle1 => "PadP1",
        GamepadBindingCode.Touchpad => "Touch",
        GamepadBindingCode.Paddle2 => "PadP2",
        GamepadBindingCode.Paddle3 => "PadP3",
        GamepadBindingCode.Paddle4 => "PadP4",
        _ => $"Pad#{code - GamepadBindingCode.MinCode}"
    };

    /// <summary>SDL3 scancode defaults (W=26, A=4, S=22, D=7, arrows 79-82, Space=44, F5=62, F9=66).</summary>
    public static Dictionary<BindableAction, int> CreateDefaults(ControlMode mode)
    {
        bool modern = mode == ControlMode.ModernWasdMouse;
        return new Dictionary<BindableAction, int>
        {
            [BindableAction.DPadUp] = modern ? 26 : 82,
            [BindableAction.DPadDown] = modern ? 22 : 81,
            [BindableAction.DPadLeft] = modern ? 4 : 80,
            [BindableAction.DPadRight] = modern ? 7 : 79,
            [BindableAction.Cross] = 44,
            [BindableAction.Circle] = 225,
            [BindableAction.Square] = 9,
            [BindableAction.Triangle] = 21,
            [BindableAction.L1] = 20,
            [BindableAction.R1] = 8,
            [BindableAction.L2] = 29,
            [BindableAction.R2] = 6,
            [BindableAction.Start] = 40,
            [BindableAction.Select] = 43,
            [BindableAction.QuickSave] = 62,
            [BindableAction.QuickLoad] = 66
        };
    }

    /// <summary>Default 1:1 SDL3 controller button/trigger mapping (<c>2001..2032</c>) preserving QuickSave/QuickLoad unbound.</summary>
    public static Dictionary<BindableAction, int> CreateControllerDefaults() => new()
    {
        [BindableAction.DPadUp] = GamepadBindingCode.DPadUp,
        [BindableAction.DPadDown] = GamepadBindingCode.DPadDown,
        [BindableAction.DPadLeft] = GamepadBindingCode.DPadLeft,
        [BindableAction.DPadRight] = GamepadBindingCode.DPadRight,
        [BindableAction.Cross] = GamepadBindingCode.South,
        [BindableAction.Circle] = GamepadBindingCode.East,
        [BindableAction.Square] = GamepadBindingCode.West,
        [BindableAction.Triangle] = GamepadBindingCode.North,
        [BindableAction.L1] = GamepadBindingCode.LB,
        [BindableAction.R1] = GamepadBindingCode.RB,
        [BindableAction.L2] = GamepadBindingCode.LT,
        [BindableAction.R2] = GamepadBindingCode.RT,
        [BindableAction.Start] = GamepadBindingCode.Start,
        [BindableAction.Select] = GamepadBindingCode.Back,
        [BindableAction.QuickSave] = GamepadBindingCode.None,
        [BindableAction.QuickLoad] = GamepadBindingCode.None
    };
}
