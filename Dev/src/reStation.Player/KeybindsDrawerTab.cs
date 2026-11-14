// @module: reStation.Player.KeybindsDrawerTab
// @purpose: Interactive 3-tier rebind grid (Global 1:1, Game 1:1, Module Modern PC) inside F1 drawer supporting keyboard scancodes and mouse buttons.
// @depends-on: Foundation.Core, reStation.Contracts, reStation.Core, reStation.Core.Input
// @exports: KeybindsDrawerTab
// @owner: devv-player-ui (T6)

using Foundation.Core;
using reStation.Contracts;
using reStation.Core;
using reStation.Core.Input;

namespace reStation.Player;

/// <summary>Renders the Keybinds tab (Global 1:1, Game 1:1, Modern PC) and captures the next pressed key or mouse button.</summary>
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
    public string? ListeningModernActionId { get; set; }
    public string? ListeningLabel { get; set; }
    public bool IsListening => ListeningAction.HasValue || !string.IsNullOrWhiteSpace(ListeningModernActionId);

    public void ClearListening()
    {
        ListeningAction = null;
        ListeningModernActionId = null;
        ListeningLabel = null;
    }

    public void Render(
        PixelOverlayCanvas canvas,
        int x,
        int y,
        int w,
        RecompSettings settings,
        GlobalControllerProfile globalProfile,
        string globalProfilePath,
        IModuleInputProfileProvider? moduleProfile,
        List<(UiRect Rect, Action<RecompSettings> OnClick, DrawerAction Action)> hitBoxes)
    {
        RenderTierSelector(canvas, x, y, w, settings, hitBoxes);

        string tierTitle = settings.InputProfileMode switch
        {
            InputProfileMode.GlobalController => "Global 1:1 PS1",
            InputProfileMode.PerGameController => "Game 1:1 PS1",
            _ => moduleProfile is not null ? moduleProfile.ProfileDisplayName : "Game 1:1 Fallback"
        };

        int maxChars = Math.Max(12, (w - 20) / (UiPaint.GlyphAdvance * UiPaint.TextScale));
        string prompt = IsListening
            ? $"Bind '{ListeningLabel}': Key/Mouse (Esc)"
            : $"{tierTitle} | Click row + Key/Mouse";
        RgbaColor pFill = IsListening ? new RgbaColor(0x28, 0x22, 0x14) : new RgbaColor(0x12, 0x16, 0x20);
        RgbaColor pBorder = IsListening ? new RgbaColor(0xE2, 0xAE, 0x4A) : UiPaint.Border;
        UiPaint.BorderRect(canvas, new UiRect(x, y + 38, w, 30), pFill, pBorder);
        UiPaint.Text(canvas, x + 10, y + 46, FitText(prompt, maxChars), IsListening ? UiPaint.Warn : UiPaint.Muted);

        const int cols = 2, colGap = 12, rowH = 40, rowGap = 5;
        int colW = (w - colGap) / cols;
        int gridY = y + 74;
        int rowCount = RenderBindingGrid(canvas, x, gridY, cols, colW, colGap, rowH, rowGap, settings, globalProfile, moduleProfile, hitBoxes);

        var resetBtn = new UiRect(x, gridY + rowCount * (rowH + rowGap) + 6, w, 38);
        string resetLabel = settings.InputProfileMode switch
        {
            InputProfileMode.GlobalController => "Reset Global 1:1 Defaults",
            InputProfileMode.PerGameModern when moduleProfile is not null => "Reset Modern PC Defaults",
            _ => "Reset Game 1:1 Defaults"
        };
        UiPaint.Pill(canvas, resetBtn, resetLabel, active: false);
        hitBoxes.Add((resetBtn, st =>
        {
            if (st.InputProfileMode == InputProfileMode.GlobalController)
            {
                globalProfile.KeyBindings.ResetDefaults(ControlMode.ModernWasdMouse);
                if (!string.IsNullOrWhiteSpace(globalProfilePath)) globalProfile.Save(globalProfilePath);
            }
            else if (st.InputProfileMode == InputProfileMode.PerGameModern && moduleProfile is not null)
            {
                st.ResetModernActionBindings();
            }
            else
            {
                st.KeyBindings ??= new KeyBindingsConfig();
                st.KeyBindings.ResetDefaults(st.ControlMode);
            }
            ClearListening();
        }, DrawerAction.MarkDirty));
    }

    private void RenderTierSelector(
        PixelOverlayCanvas canvas,
        int x,
        int y,
        int w,
        RecompSettings settings,
        List<(UiRect Rect, Action<RecompSettings> OnClick, DrawerAction Action)> hitBoxes)
    {
        int pw = (w - 12) / 3;
        var tiers = new (InputProfileMode Mode, string Label)[]
        {
            (InputProfileMode.GlobalController, "Global 1:1"),
            (InputProfileMode.PerGameController, "Game 1:1"),
            (InputProfileMode.PerGameModern, "Modern PC")
        };
        for (int i = 0; i < tiers.Length; i++)
        {
            var tier = tiers[i];
            var rect = new UiRect(x + i * (pw + 6), y, pw, 32);
            UiPaint.Pill(canvas, rect, tier.Label, settings.InputProfileMode == tier.Mode);
            hitBoxes.Add((rect, st =>
            {
                st.InputProfileMode = tier.Mode;
                ClearListening();
            }, DrawerAction.MarkDirty));
        }
    }

    private int RenderBindingGrid(
        PixelOverlayCanvas canvas,
        int x,
        int gridY,
        int cols,
        int colW,
        int colGap,
        int rowH,
        int rowGap,
        RecompSettings settings,
        GlobalControllerProfile globalProfile,
        IModuleInputProfileProvider? moduleProfile,
        List<(UiRect Rect, Action<RecompSettings> OnClick, DrawerAction Action)> hitBoxes)
    {
        if (settings.InputProfileMode == InputProfileMode.PerGameModern && moduleProfile is not null)
        {
            int count = Math.Min(16, moduleProfile.ModernActions.Count);
            for (int i = 0; i < count; i++)
            {
                GameActionDescriptor desc = moduleProfile.ModernActions[i];
                int cx = x + (i % cols) * (colW + colGap);
                int cy = gridY + (i / cols) * (rowH + rowGap);
                var cell = new UiRect(cx, cy, colW, rowH);
                bool active = string.Equals(ListeningModernActionId, desc.ActionId, StringComparison.OrdinalIgnoreCase);

                UiPaint.BorderRect(canvas, cell, active ? new RgbaColor(0x24, 0x20, 0x14) : UiPaint.Panel, active ? UiPaint.Warn : UiPaint.Border);
                UiPaint.Text(canvas, cx + 10, cy + 13, FitText(desc.Label, 13), UiPaint.TextPrimary);

                string keyText = active ? "..." : FormatScancode(settings.GetModernActionBinding(desc));
                var badge = new UiRect(cx + colW - 104, cy + 5, 96, 30);
                UiPaint.Pill(canvas, badge, keyText, active);
                hitBoxes.Add((cell, _ =>
                {
                    ListeningAction = null;
                    ListeningModernActionId = desc.ActionId;
                    ListeningLabel = desc.Label;
                }, DrawerAction.MarkDirty));
            }
            return Math.Max(1, (count + cols - 1) / cols);
        }

        KeyBindingsConfig kb = settings.InputProfileMode == InputProfileMode.GlobalController
            ? globalProfile.KeyBindings
            : (settings.KeyBindings ?? new KeyBindingsConfig());
        for (int i = 0; i < Actions.Length; i++)
        {
            BindableAction action = Actions[i];
            int cx = x + (i % cols) * (colW + colGap);
            int cy = gridY + (i / cols) * (rowH + rowGap);
            var cell = new UiRect(cx, cy, colW, rowH);
            bool active = ListeningAction == action;
            string label = KeyBindingsConfig.GetActionLabel(action);

            UiPaint.BorderRect(canvas, cell, active ? new RgbaColor(0x24, 0x20, 0x14) : UiPaint.Panel, active ? UiPaint.Warn : UiPaint.Border);
            UiPaint.Text(canvas, cx + 10, cy + 13, FitText(label, 13), UiPaint.TextPrimary);

            string keyText = active ? "..." : FormatScancode(kb.GetScancode(action));
            var badge = new UiRect(cx + colW - 104, cy + 5, 96, 30);
            UiPaint.Pill(canvas, badge, keyText, active);
            hitBoxes.Add((cell, _ =>
            {
                ListeningModernActionId = null;
                ListeningAction = action;
                ListeningLabel = label;
            }, DrawerAction.MarkDirty));
        }
        return (Actions.Length + cols - 1) / cols;
    }

    public bool TryHandleKeyCapture(
        int scancode,
        RecompSettings settings,
        string configPath,
        GlobalControllerProfile globalProfile,
        string globalProfilePath,
        IModuleInputProfileProvider? moduleProfile)
    {
        if (!IsListening) return false;
        if (scancode == Sdl3Native.ScEsc) { ClearListening(); return true; }
        if (scancode == Sdl3Native.ScF1) return false;
        return TryCommitCapturedBinding(scancode, settings, configPath, globalProfile, globalProfilePath, moduleProfile);
    }

    public bool TryHandleMouseCapture(
        int bindingCode,
        RecompSettings settings,
        string configPath,
        GlobalControllerProfile globalProfile,
        string globalProfilePath,
        IModuleInputProfileProvider? moduleProfile)
    {
        if (!IsListening) return false;
        return TryCommitCapturedBinding(bindingCode, settings, configPath, globalProfile, globalProfilePath, moduleProfile);
    }

    private bool TryCommitCapturedBinding(
        int bindingCode,
        RecompSettings settings,
        string configPath,
        GlobalControllerProfile globalProfile,
        string globalProfilePath,
        IModuleInputProfileProvider? moduleProfile)
    {
        int normalized = KeyBindingsConfig.NormalizeBindingCode(bindingCode, 0);
        if (normalized == 0) return true;

        if (!string.IsNullOrWhiteSpace(ListeningModernActionId) && moduleProfile is not null)
        {
            settings.SetModernActionBinding(ListeningModernActionId, normalized);
            settings.Save(configPath);
        }
        else if (ListeningAction.HasValue && settings.InputProfileMode == InputProfileMode.GlobalController)
        {
            globalProfile.KeyBindings.SetScancode(ListeningAction.Value, normalized);
            if (!string.IsNullOrWhiteSpace(globalProfilePath)) globalProfile.Save(globalProfilePath);
        }
        else if (ListeningAction.HasValue)
        {
            settings.KeyBindings ??= new KeyBindingsConfig();
            settings.KeyBindings.SetScancode(ListeningAction.Value, normalized);
            settings.Save(configPath);
        }
        ClearListening();
        return true;
    }

    /// <summary>Human-readable label for any SDL3 USB HID scancode or mouse binding code.</summary>
    public static string FormatScancode(int scancode) => KeyBindingsConfig.FormatBindingLabel(scancode);

    private static string FitText(string text, int maxChars) =>
        text.Length <= maxChars ? text : text[..Math.Max(1, maxChars)];
}
