// @module: reStation.Core.Input.KeyBindingsConfig
// @purpose: Standard 18 keyBindings model mapping PS1 pad buttons + host quick actions to SDL scancodes with per-ControlMode defaults.
// @depends-on: System.Text.Json.Serialization, reStation.Core
// @exports: BindableAction, KeyBindingsConfig
// @owner: devv-audio-mdec (T3) — BindableAction values and public members T1-locked (Player KeybindsDrawerTab enumerates them).

using System.Text.Json.Serialization;

namespace reStation.Core.Input;

/// <summary>Rebindable actions (PS1 digital pad buttons plus host quick-save/load).</summary>
public enum BindableAction
{
    DPadUp, DPadDown, DPadLeft, DPadRight,
    Cross, Circle, Square, Triangle,
    L1, R1, L2, R2,
    Start, Select,
    QuickSave, QuickLoad
}

/// <summary>Action -> SDL scancode map persisted inside settings.json <c>keyBindings</c>.</summary>
public sealed class KeyBindingsConfig
{
    public const int MaxScancode = 511;

    [JsonPropertyName("bindings")]
    public Dictionary<BindableAction, int> Bindings { get; set; } = CreateDefaults(ControlMode.ModernWasdMouse);

    public int GetScancode(BindableAction action) => Bindings.TryGetValue(action, out int sc) ? sc : 0;

    public void SetScancode(BindableAction action, int scancode) => Bindings[action] = Math.Clamp(scancode, 0, MaxScancode);

    public void ResetDefaults(ControlMode mode) => Bindings = CreateDefaults(mode);

    /// <summary>Fills missing actions from <paramref name="mode"/> defaults and clamps scancodes.</summary>
    public void Clamp(ControlMode mode)
    {
        Bindings ??= new Dictionary<BindableAction, int>();
        Dictionary<BindableAction, int> defaults = CreateDefaults(mode);
        foreach (BindableAction action in Enum.GetValues<BindableAction>())
        {
            Bindings[action] = Bindings.TryGetValue(action, out int sc) ? Math.Clamp(sc, 0, MaxScancode) : defaults[action];
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
