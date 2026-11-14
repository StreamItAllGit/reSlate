// @module: reStation.Core.Input.KeyBindingsConfig
// @purpose: Standard 18 keyBindings model mapping PS1 pad buttons + host quick actions to SDL keyboard scancodes (4..511) or mouse buttons (1001..1005).
// @depends-on: System.Text.Json.Serialization, reStation.Core
// @exports: InputProfileMode, BindableAction, KeyBindingsConfig

using System.Text.Json.Serialization;

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
    public const int MinMouseCode = MouseLeft;
    public const int MaxMouseCode = MouseX2;

    [JsonPropertyName("bindings")]
    public Dictionary<BindableAction, int> Bindings { get; set; } = CreateDefaults(ControlMode.ModernWasdMouse);

    public static bool IsMouseCode(int code) => code is >= MinMouseCode and <= MaxMouseCode;

    public static bool IsValidBindingCode(int code) => code == 0 || (code is > 0 and <= MaxScancode) || IsMouseCode(code);

    public static int NormalizeBindingCode(int code, int fallback = 0) =>
        IsValidBindingCode(code) ? code : (IsValidBindingCode(fallback) ? fallback : 0);

    public int GetScancode(BindableAction action) => Bindings.TryGetValue(action, out int sc) ? sc : 0;

    public void SetScancode(BindableAction action, int scancode) => Bindings[action] = NormalizeBindingCode(scancode, 0);

    public void ResetDefaults(ControlMode mode) => Bindings = CreateDefaults(mode);

    /// <summary>Fills missing actions from <paramref name="mode"/> defaults and normalizes keyboard/mouse codes.</summary>
    public void Clamp(ControlMode mode)
    {
        Bindings ??= new Dictionary<BindableAction, int>();
        Dictionary<BindableAction, int> defaults = CreateDefaults(mode);
        foreach (BindableAction action in Enum.GetValues<BindableAction>())
        {
            Bindings[action] = Bindings.TryGetValue(action, out int sc)
                ? NormalizeBindingCode(sc, defaults[action])
                : defaults[action];
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

    /// <summary>Formats any keyboard scancode or mouse button code into a compact keycap label.</summary>
    public static string FormatBindingLabel(int code) => code switch
    {
        <= 0 => "---",
        MouseLeft => "LClick",
        MouseRight => "RClick",
        MouseMiddle => "MClick",
        MouseX1 => "Mouse4",
        MouseX2 => "Mouse5",
        >= 4 and <= 29 => ((char)('A' + code - 4)).ToString(),
        >= 30 and <= 38 => ((char)('1' + code - 30)).ToString(),
        39 => "0",
        40 => "Enter",
        41 => "Esc",
        42 => "Bksp",
        43 => "Tab",
        44 => "Space",
        >= 58 and <= 69 => $"F{code - 57}",
        79 => "Right",
        80 => "Left",
        81 => "Down",
        82 => "Up",
        224 => "LCtrl",
        225 => "LShift",
        226 => "LAlt",
        228 => "RCtrl",
        229 => "RShift",
        _ => $"Key#{code}"
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
}
