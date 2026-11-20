// @module: reStation.Player.SlideOverSettingsDrawer
// @purpose: Standard 09 F1 slide-over right settings drawer shell (Video, Controls, Keybinds, System, Disc) on PixelOverlayCanvas.
// @depends-on: Foundation.Core, reStation.Contracts, reStation.Core, reStation.Core.Input
// @exports: DrawerAction, SlideOverSettingsDrawer

using Foundation.Core;
using reStation.Contracts;
using reStation.Core;
using reStation.Core.Input;

namespace reStation.Player;

internal enum DrawerAction
{
    None,
    MarkDirty,
    CloseDrawer,
    ToggleBackend,
    ToggleHdPacks,
    ToggleAspectMode,
    ToggleOverscanCrop,
    ToggleFullscreen,
    ToggleFpsOverlay,
    ToggleRuntimeLogging,
    ToggleVsync,
    ToggleFastForward,
    CutsceneSkip,
    QuickSave,
    QuickLoad,
    ChangeDisc,
    ChangeModule
}

internal readonly record struct DrawerHitBox(
    UiRect Rect,
    Action<RecompSettings> OnClick,
    DrawerAction Action,
    Action<RecompSettings>? OnRightClick = null,
    string ControlId = "",
    string ControlType = "Button",
    string Label = "");

/// <summary>Right-docked 5-tab settings drawer toggled by F1 or the top-right Settings pill.</summary>
internal sealed partial class SlideOverSettingsDrawer
{
    private static readonly string[] TabLabels = { "Video", "Controls", "Keybinds", "System", "Disc" };
    private static readonly string[] TabIds = { "Tab_Video", "Tab_Controls", "Tab_Keybinds", "Tab_System", "Tab_DiscMedia" };
    private readonly List<DrawerHitBox> _hitBoxes = new();
    private GlobalControllerProfile _globalProfile = new();
    private string _globalProfilePath = string.Empty;
    private IModuleInputProfileProvider? _moduleProfile;

    public bool IsOpen { get; set; }
    public int ActiveTab { get; set; }
    public string? VideoWarningBanner { get; set; }
    public string? PendingRequestedBackendId { get; set; }
    public string? ActiveBackendOverride { get; set; }
    public UiRect CurrentDrawerRect { get; private set; }
    public IReadOnlyList<DrawerHitBox> HitBoxes => _hitBoxes;
    public KeybindsDrawerTab KeybindsTab { get; } = new();
    public GlobalControllerProfile GlobalProfile => _globalProfile;
    public IModuleInputProfileProvider? ModuleProfile => _moduleProfile;

    public void AttachInputProfiles(GlobalControllerProfile globalProfile, string globalProfilePath, IModuleInputProfileProvider? moduleProfile)
    {
        _globalProfile = globalProfile ?? new GlobalControllerProfile();
        _globalProfilePath = globalProfilePath ?? string.Empty;
        _moduleProfile = moduleProfile;
    }

    public void RenderOverlay(
        PixelOverlayCanvas canvas,
        RecompSettings settings,
        IReStationModuleManifest? manifest,
        string discInfo,
        bool loggingActive)
    {
        _hitBoxes.Clear();
        if (!IsOpen || ActiveTab != 2) KeybindsTab.ClearListening();

        int w = canvas.Width, h = canvas.Height;
        var topPill = new UiRect(Math.Max(4, w - 188), 12, Math.Min(174, w - 8), 34);
        string pillText = IsOpen ? "Close (F1)" : "Settings (F1)";
        UiPaint.Pill(canvas, topPill, pillText, IsOpen);
        _hitBoxes.Add(new DrawerHitBox(topPill, _ =>
        {
            IsOpen = !IsOpen;
            if (!IsOpen) KeybindsTab.ClearListening();
        }, DrawerAction.MarkDirty, ControlId: "Drawer_TogglePill", Label: pillText));
        if (!IsOpen) { CurrentDrawerRect = default; return; }

        canvas.FillRect(0, 0, w, h, UiPaint.Scrim);
        int dw = ActiveTab == 2 ? Math.Min(w, 800) : Math.Min(w, 600);
        int dx = w - dw;
        CurrentDrawerRect = new UiRect(dx, 0, dw, h);
        UiPaint.BorderRect(canvas, CurrentDrawerRect, new RgbaColor(0x14, 0x18, 0x22), UiPaint.Accent);
        UiPaint.Text(canvas, dx + 20, 18, "Settings", UiPaint.AccentText, 3);

        var closeBtn = new UiRect(w - 114, 14, 96, 32);
        UiPaint.Pill(canvas, closeBtn, "Close", active: false);
        _hitBoxes.Add(new DrawerHitBox(closeBtn, _ =>
        {
            IsOpen = false;
            KeybindsTab.ClearListening();
        }, DrawerAction.CloseDrawer, ControlId: "Drawer_CloseButton", Label: "Close"));

        int tabW = (dw - 36 - ((TabLabels.Length - 1) * 6)) / TabLabels.Length;
        for (int t = 0; t < TabLabels.Length; t++)
        {
            int idx = t;
            var tr = new UiRect(dx + 18 + (t * (tabW + 6)), 60, tabW, 34);
            UiPaint.Pill(canvas, tr, TabLabels[t], ActiveTab == t);
            _hitBoxes.Add(new DrawerHitBox(tr, _ =>
            {
                ActiveTab = idx;
                if (idx != 2) KeybindsTab.ClearListening();
            }, DrawerAction.MarkDirty, ControlId: TabIds[t], Label: TabLabels[t]));
        }

        int x = dx + 18, y = 108, rw = dw - 36;
        switch (ActiveTab)
        {
            case 0: RenderVideoTab(canvas, x, y, rw, settings); break;
            case 1: RenderControlsTab(canvas, x, y, rw, settings); break;
            case 2: KeybindsTab.Render(canvas, x, y, rw, settings, _globalProfile, _globalProfilePath, _moduleProfile, _hitBoxes); break;
            case 3: RenderSystemTab(canvas, x, y, rw, settings, loggingActive); break;
            default: RenderDiscTab(canvas, x, y, rw, manifest, discInfo); break;
        }
    }

    public bool TryHandleKeyCapture(int scancode, RecompSettings settings, string configPath) =>
        IsOpen && ActiveTab == 2 && KeybindsTab.TryHandleKeyCapture(scancode, settings, configPath, _globalProfile, _globalProfilePath, _moduleProfile);

    public bool TryHandleMouseCapture(int bindingCode, RecompSettings settings, string configPath) =>
        IsOpen && ActiveTab == 2 && KeybindsTab.TryHandleMouseCapture(bindingCode, settings, configPath, _globalProfile, _globalProfilePath, _moduleProfile);

    public bool TryHandleGamepadCapture(int gamepadCode, RecompSettings settings, string configPath) =>
        IsOpen && ActiveTab == 2 && KeybindsTab.TryHandleGamepadCapture(gamepadCode, settings, configPath, _globalProfile, _globalProfilePath, _moduleProfile);

    public bool TryScrollKeybinds(int deltaRows)
    {
        if (!IsOpen || ActiveTab != 2 || KeybindsTab.IsListening) return false;
        return KeybindsTab.ScrollBy(deltaRows, _moduleProfile?.ModernActions.Count ?? 0);
    }

    public DrawerAction HandleClick(int px, int py, RecompSettings settings, string configPath) =>
        HandleClick(px, py, settings, configPath, isRightClick: false);

    public DrawerAction HandleClick(int px, int py, RecompSettings settings, string configPath, bool isRightClick)
    {
        for (int i = _hitBoxes.Count - 1; i >= 0; i--)
        {
            DrawerHitBox hb = _hitBoxes[i];
            if (!hb.Rect.Contains(px, py)) continue;
            if (isRightClick)
            {
                if (hb.OnRightClick is null) continue;
                hb.OnRightClick(settings);
                settings.Save(configPath);
                return hb.Action;
            }
            hb.OnClick(settings);
            settings.Save(configPath);
            return hb.Action;
        }
        return DrawerAction.None;
    }

    private void AddToggle(PixelOverlayCanvas c, UiRect r, string label, bool on, Action<RecompSettings> onClick, DrawerAction action, string controlId = "")
    {
        UiPaint.ToggleRow(c, r, label, on);
        _hitBoxes.Add(new DrawerHitBox(r, onClick, action, ControlId: controlId, ControlType: "CheckBox", Label: label));
    }
}
