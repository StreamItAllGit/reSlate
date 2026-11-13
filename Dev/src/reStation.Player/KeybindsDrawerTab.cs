// @module: reStation.Player.KeybindsDrawerTab
// @purpose: Interactive 16-action PS1 + QuickSave/Load key rebinding grid inside the F1 drawer with SDL scancode formatting.
// @depends-on: Foundation.Core, reStation.Core, reStation.Core.Input
// @exports: KeybindsDrawerTab
// @owner: devv-player-ui (T6)

using Foundation.Core;
using reStation.Core;
using reStation.Core.Input;

namespace reStation.Player;

/// <summary>Renders the Keybinds tab (2x8 grid) and captures the next pressed SDL scancode.</summary>
internal sealed class KeybindsDrawerTab
{
    private static readonly BindableAction[] Actions =
    {
        BindableAction.DPadUp, BindableAction.DPadDown,
        BindableAction.DPadLeft, BindableAction.DPadRight,
        BindableAction.Cross, BindableAction.Circle,
        BindableAction.Square, BindableAction.Triangle,
        BindableAction.L1, BindableAction.R1,
        BindableAction.L2, BindableAction.R2,
        BindableAction.Start, BindableAction.Select,
        BindableAction.QuickSave, BindableAction.QuickLoad
    };

    public BindableAction? ListeningAction { get; set; }

    public void Render(
        PixelOverlayCanvas canvas,
        int x,
        int y,
        int w,
        RecompSettings settings,
        List<(UiRect Rect, Action<RecompSettings> OnClick, DrawerAction Action)> hitBoxes)
    {
        bool listening = ListeningAction.HasValue;
        string prompt = listening
            ? $"Press key for {KeyBindingsConfig.GetActionLabel(ListeningAction!.Value)}... (Esc cancels)"
            : "Click an action to rebind (Esc cancels)";
        RgbaColor pFill = listening ? new RgbaColor(0x28, 0x22, 0x14) : new RgbaColor(0x12, 0x16, 0x20);
        RgbaColor pBorder = listening ? new RgbaColor(0xE2, 0xAE, 0x4A) : UiPaint.Border;
        UiPaint.BorderRect(canvas, new UiRect(x, y, w, 32), pFill, pBorder);
        UiPaint.Text(canvas, x + 12, y + 9, prompt, listening ? UiPaint.Warn : UiPaint.Muted);

        KeyBindingsConfig kb = settings.KeyBindings ?? new KeyBindingsConfig();
        const int cols = 2, colGap = 12, rowH = 44, rowGap = 6;
        int colW = (w - colGap) / cols;
        int gridY = y + 40;

        for (int i = 0; i < Actions.Length; i++)
        {
            BindableAction action = Actions[i];
            int cx = x + (i % cols) * (colW + colGap);
            int cy = gridY + (i / cols) * (rowH + rowGap);
            var cell = new UiRect(cx, cy, colW, rowH);
            bool active = ListeningAction == action;

            UiPaint.BorderRect(canvas, cell, active ? new RgbaColor(0x24, 0x20, 0x14) : UiPaint.Panel, active ? UiPaint.Warn : UiPaint.Border);
            UiPaint.Text(canvas, cx + 10, cy + 15, KeyBindingsConfig.GetActionLabel(action), UiPaint.TextPrimary);

            string keyText = active ? "..." : FormatScancode(kb.GetScancode(action));
            var badge = new UiRect(cx + colW - 96, cy + 7, 86, 30);
            UiPaint.Pill(canvas, badge, keyText, active);
            hitBoxes.Add((cell, _ => ListeningAction = action, DrawerAction.MarkDirty));
        }

        var resetBtn = new UiRect(x, gridY + 8 * (rowH + rowGap) + 4, w, 40);
        UiPaint.Pill(canvas, resetBtn, "Reset Default Keybinds", active: false);
        hitBoxes.Add((resetBtn, st =>
        {
            st.KeyBindings ??= new KeyBindingsConfig();
            st.KeyBindings.ResetDefaults(st.ControlMode);
            ListeningAction = null;
        }, DrawerAction.MarkDirty));
    }

    public bool TryHandleKeyCapture(int scancode, RecompSettings settings, string configPath)
    {
        if (!ListeningAction.HasValue) return false;
        if (scancode == Sdl3Native.ScEsc) { ListeningAction = null; return true; }
        if (scancode == Sdl3Native.ScF1) return false;
        if (scancode is >= 4 and <= KeyBindingsConfig.MaxScancode)
        {
            settings.KeyBindings ??= new KeyBindingsConfig();
            settings.KeyBindings.SetScancode(ListeningAction.Value, scancode);
            settings.Save(configPath);
            ListeningAction = null;
        }
        return true;
    }

    /// <summary>Human-readable label for an SDL3 USB HID scancode.</summary>
    public static string FormatScancode(int scancode) => scancode switch
    {
        0 => "None",
        >= 4 and <= 29 => ((char)('A' + (scancode - 4))).ToString(),
        >= 30 and <= 38 => ((char)('1' + (scancode - 30))).ToString(),
        39 => "0",
        40 => "Enter",
        41 => "Esc",
        42 => "Bksp",
        43 => "Tab",
        44 => "Space",
        >= 58 and <= 69 => $"F{scancode - 57}",
        79 => "Right",
        80 => "Left",
        81 => "Down",
        82 => "Up",
        224 => "LCtrl",
        225 => "LShift",
        226 => "LAlt",
        228 => "RCtrl",
        229 => "RShift",
        230 => "RAlt",
        _ => $"SC{scancode}"
    };
}
