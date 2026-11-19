// @module: reStation.Player.KeybindsDrawerTab
// @purpose: Interactive 3-tier rebind UI (Global 1:1, Game 1:1, 5-column scrollable Module Modern PC) inside F1 drawer supporting keyboard, mouse/wheel, and gamepad.
// @depends-on: Foundation.Core, reStation.Contracts, reStation.Core, reStation.Core.Input
// @exports: KeybindsDrawerTab
// @owner: devv-player-ui (T6)

using Foundation.Core;
using reStation.Contracts;
using reStation.Core;
using reStation.Core.Input;

namespace reStation.Player;

/// <summary>Renders the Keybinds tab (Global 1:1, Game 1:1, 5-col scrollable Modern PC) and handles disambiguated input capture.</summary>
internal sealed partial class KeybindsDrawerTab
{
    public const int VisibleModernRows = 11;

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
    public int ListeningSlotIndex { get; set; }
    public string? ListeningLabel { get; set; }
    public int ScrollRowOffset { get; set; }
    public bool IsListening => ListeningAction.HasValue || !string.IsNullOrWhiteSpace(ListeningModernActionId);

    public void ClearListening()
    {
        ListeningAction = null;
        ListeningModernActionId = null;
        ListeningSlotIndex = 0;
        ListeningLabel = null;
    }

    public bool ScrollBy(int deltaRows, int totalActions)
    {
        int maxOffset = Math.Max(0, totalActions - VisibleModernRows);
        int next = Math.Clamp(ScrollRowOffset + deltaRows, 0, maxOffset);
        if (next == ScrollRowOffset) return false;
        ScrollRowOffset = next;
        return true;
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
        List<DrawerHitBox> hitBoxes)
    {
        RenderTierSelector(canvas, x, y, w, settings, hitBoxes);

        string tierTitle = settings.InputProfileMode switch
        {
            InputProfileMode.GlobalController => "Global 1:1 PS1",
            InputProfileMode.PerGameController => "Game 1:1 PS1",
            _ => moduleProfile is not null ? moduleProfile.ProfileDisplayName : "Game 1:1 Fallback"
        };

        int maxChars = Math.Max(12, (w - 20) / (UiPaint.GlyphAdvance * UiPaint.TextScale));
        bool isPadSlot = (!string.IsNullOrWhiteSpace(ListeningModernActionId) && ListeningSlotIndex == 3)
            || (ListeningAction.HasValue && ListeningSlotIndex == 1);
        string slotName = ListeningSlotIndex switch { 0 => "1st", 1 => "2nd", 2 => "3rd", _ => "Controller" };
        string prompt = IsListening
            ? (isPadSlot
                ? $"Bind '{FitText(ListeningLabel ?? string.Empty, 18)}' [Ctrl]: Pad Btn (Esc/Del)"
                : $"Bind '{FitText(ListeningLabel ?? string.Empty, 18)}' [{slotName}]: Input (Esc cancel)")
            : settings.InputProfileMode == InputProfileMode.PerGameModern && moduleProfile is not null
                ? $"{FitText(tierTitle, 24)} | LClick:Bind RClick:Clr Wheel"
                : $"{FitText(tierTitle, 20)} | LClick:Bind RClick:Clr (Esc)";
        RgbaColor pFill = IsListening ? new RgbaColor(0x28, 0x22, 0x14) : new RgbaColor(0x12, 0x16, 0x20);
        RgbaColor pBorder = IsListening ? new RgbaColor(0xE2, 0xAE, 0x4A) : UiPaint.Border;
        UiPaint.BorderRect(canvas, new UiRect(x, y + 38, w, 30), pFill, pBorder);
        UiPaint.Text(canvas, x + 10, y + 46, FitText(prompt, maxChars), IsListening ? UiPaint.Warn : UiPaint.Muted);

        int gridY = y + 74;
        int resetY = settings.InputProfileMode == InputProfileMode.PerGameModern && moduleProfile is not null
            ? RenderModernTable(canvas, x, gridY, w, settings, moduleProfile, hitBoxes)
            : RenderClassicGrid(canvas, x, gridY, w, settings, globalProfile, globalProfilePath, hitBoxes);

        var resetBtn = new UiRect(x, resetY, w, 36);
        string resetLabel = settings.InputProfileMode switch
        {
            InputProfileMode.GlobalController => "Reset Global 1:1 Defaults",
            InputProfileMode.PerGameModern when moduleProfile is not null => "Reset Modern PC Defaults",
            _ => "Reset Game 1:1 Defaults"
        };
        UiPaint.Pill(canvas, resetBtn, resetLabel, active: false);
        hitBoxes.Add(new DrawerHitBox(resetBtn, st =>
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
        }, DrawerAction.MarkDirty, ControlId: "Keybinds_Reset", Label: resetLabel));
    }

    private void RenderTierSelector(
        PixelOverlayCanvas canvas,
        int x,
        int y,
        int w,
        RecompSettings settings,
        List<DrawerHitBox> hitBoxes)
    {
        int pw = (w - 12) / 3;
        var tiers = new (InputProfileMode Mode, string Label, string ControlId)[]
        {
            (InputProfileMode.GlobalController, "Global 1:1", "Keybinds_Tier_Global"),
            (InputProfileMode.PerGameController, "Game 1:1", "Keybinds_Tier_Game"),
            (InputProfileMode.PerGameModern, "Modern PC", "Keybinds_Tier_Modern")
        };
        for (int i = 0; i < tiers.Length; i++)
        {
            var tier = tiers[i];
            var rect = new UiRect(x + i * (pw + 6), y, pw, 32);
            UiPaint.Pill(canvas, rect, tier.Label, settings.InputProfileMode == tier.Mode);
            hitBoxes.Add(new DrawerHitBox(rect, st =>
            {
                st.InputProfileMode = tier.Mode;
                ClearListening();
            }, DrawerAction.MarkDirty, ControlId: tier.ControlId, Label: tier.Label));
        }
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
        if (scancode == Sdl3Native.ScDelete || scancode == Sdl3Native.ScBackspace)
        {
            if (!string.IsNullOrWhiteSpace(ListeningModernActionId) && ListeningSlotIndex is >= 1 and <= 3 && moduleProfile is not null)
            {
                GameActionDescriptor? desc = FindAction(moduleProfile, ListeningModernActionId);
                if (desc is not null)
                {
                    settings.ClearModernActionSlot(desc, ListeningSlotIndex);
                    settings.Save(configPath);
                }
                ClearListening();
                return true;
            }
            if (ListeningAction.HasValue)
            {
                CommitClassicBinding(ListeningAction.Value, ListeningSlotIndex == 1, 0, settings, configPath, globalProfile, globalProfilePath);
                ClearListening();
                return true;
            }
        }
        if ((!string.IsNullOrWhiteSpace(ListeningModernActionId) && ListeningSlotIndex == 3)
            || (ListeningAction.HasValue && ListeningSlotIndex == 1))
            return true;
        return TryCommitKbmBinding(scancode, settings, configPath, globalProfile, globalProfilePath, moduleProfile);
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
        if ((!string.IsNullOrWhiteSpace(ListeningModernActionId) && ListeningSlotIndex == 3)
            || (ListeningAction.HasValue && ListeningSlotIndex == 1))
            return true;
        return TryCommitKbmBinding(bindingCode, settings, configPath, globalProfile, globalProfilePath, moduleProfile);
    }

    public bool TryHandleGamepadCapture(
        int gamepadCode,
        RecompSettings settings,
        string configPath,
        IModuleInputProfileProvider? moduleProfile) =>
        TryHandleGamepadCapture(gamepadCode, settings, configPath, null, string.Empty, moduleProfile);

    public bool TryHandleGamepadCapture(
        int gamepadCode,
        RecompSettings settings,
        string configPath,
        GlobalControllerProfile? globalProfile,
        string globalProfilePath,
        IModuleInputProfileProvider? moduleProfile)
    {
        if (!IsListening) return false;
        int norm = KeyBindingsConfig.NormalizeControllerCode(gamepadCode, 0);
        if (norm == 0) return true;

        if (!string.IsNullOrWhiteSpace(ListeningModernActionId) && ListeningSlotIndex == 3 && moduleProfile is not null)
        {
            GameActionDescriptor? desc = FindAction(moduleProfile, ListeningModernActionId);
            if (desc is not null)
            {
                settings.SetModernControllerSlot(desc, norm);
                settings.Save(configPath);
            }
            ClearListening();
            return true;
        }

        if (ListeningAction.HasValue && ListeningSlotIndex == 1)
        {
            CommitClassicBinding(ListeningAction.Value, isController: true, norm, settings, configPath, globalProfile, globalProfilePath);
            ClearListening();
            return true;
        }
        return false;
    }

    private static void CommitClassicBinding(
        BindableAction action,
        bool isController,
        int code,
        RecompSettings settings,
        string configPath,
        GlobalControllerProfile? globalProfile,
        string globalProfilePath)
    {
        if (settings.InputProfileMode == InputProfileMode.GlobalController && globalProfile is not null)
        {
            if (isController) globalProfile.KeyBindings.SetControllerCode(action, code);
            else globalProfile.KeyBindings.SetScancode(action, code);
            if (!string.IsNullOrWhiteSpace(globalProfilePath)) globalProfile.Save(globalProfilePath);
            return;
        }
        settings.KeyBindings ??= new KeyBindingsConfig();
        if (isController) settings.KeyBindings.SetControllerCode(action, code);
        else settings.KeyBindings.SetScancode(action, code);
        settings.Save(configPath);
    }

    private bool TryCommitKbmBinding(
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
            GameActionDescriptor? desc = FindAction(moduleProfile, ListeningModernActionId);
            if (desc is not null) settings.SetModernActionSlot(desc, ListeningSlotIndex, normalized);
            else settings.SetModernActionBinding(ListeningModernActionId, normalized);
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

    private static GameActionDescriptor? FindAction(IModuleInputProfileProvider moduleProfile, string actionId)
    {
        foreach (GameActionDescriptor d in moduleProfile.ModernActions)
            if (string.Equals(d.ActionId, actionId, StringComparison.OrdinalIgnoreCase)) return d;
        return null;
    }

    /// <summary>Human-readable label for any SDL3 USB HID scancode, mouse/wheel code, or gamepad binding code.</summary>
    public static string FormatScancode(int scancode) => KeyBindingsConfig.FormatBindingLabel(scancode);

    private static string FitText(string text, int maxChars) =>
        text.Length <= maxChars ? text : text[..Math.Max(1, maxChars)];
}
