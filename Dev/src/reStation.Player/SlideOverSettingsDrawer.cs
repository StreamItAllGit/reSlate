// @module: reStation.Player.SlideOverSettingsDrawer
// @purpose: Standard 09 F1 slide-over right settings drawer (Video, Controls, Keybinds, System, Disc) rendered on PixelOverlayCanvas.
// @depends-on: Foundation.Core, reStation.Contracts, reStation.Core, reStation.Core.Input
// @exports: DrawerAction, SlideOverSettingsDrawer
// @owner: devv-player-ui (T6)

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
    ToggleFullscreen,
    ToggleFpsOverlay,
    ToggleRuntimeLogging,
    ToggleVsync,
    QuickSave,
    QuickLoad,
    ChangeDisc,
    ChangeModule
}

/// <summary>Right-docked 5-tab settings drawer toggled by F1 or the top-right Settings pill.</summary>
internal sealed class SlideOverSettingsDrawer
{
    private static readonly string[] TabLabels = { "Video", "Controls", "Keybinds", "System", "Disc" };
    private readonly List<(UiRect Rect, Action<RecompSettings> OnClick, DrawerAction Action)> _hitBoxes = new();
    private GlobalControllerProfile _globalProfile = new();
    private string _globalProfilePath = string.Empty;
    private IModuleInputProfileProvider? _moduleProfile;

    public bool IsOpen { get; set; }
    public int ActiveTab { get; set; }
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
        UiPaint.Pill(canvas, topPill, IsOpen ? "Close (F1)" : "Settings (F1)", IsOpen);
        _hitBoxes.Add((topPill, _ =>
        {
            IsOpen = !IsOpen;
            if (!IsOpen) KeybindsTab.ClearListening();
        }, DrawerAction.MarkDirty));
        if (!IsOpen) return;

        canvas.FillRect(0, 0, w, h, UiPaint.Scrim);
        int dw = Math.Min(w, 600);
        int dx = w - dw;
        UiPaint.BorderRect(canvas, new UiRect(dx, 0, dw, h), new RgbaColor(0x14, 0x18, 0x22), UiPaint.Accent);
        UiPaint.Text(canvas, dx + 20, 18, "Settings", UiPaint.AccentText, 3);

        var closeBtn = new UiRect(w - 114, 14, 96, 32);
        UiPaint.Pill(canvas, closeBtn, "Close", active: false);
        _hitBoxes.Add((closeBtn, _ =>
        {
            IsOpen = false;
            KeybindsTab.ClearListening();
        }, DrawerAction.CloseDrawer));

        int tabW = (dw - 36 - ((TabLabels.Length - 1) * 6)) / TabLabels.Length;
        for (int t = 0; t < TabLabels.Length; t++)
        {
            int idx = t;
            var tr = new UiRect(dx + 18 + (t * (tabW + 6)), 60, tabW, 34);
            UiPaint.Pill(canvas, tr, TabLabels[t], ActiveTab == t);
            _hitBoxes.Add((tr, _ =>
            {
                ActiveTab = idx;
                if (idx != 2) KeybindsTab.ClearListening();
            }, DrawerAction.MarkDirty));
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

    public DrawerAction HandleClick(int px, int py, RecompSettings settings, string configPath)
    {
        for (int i = _hitBoxes.Count - 1; i >= 0; i--)
        {
            var hb = _hitBoxes[i];
            if (!hb.Rect.Contains(px, py)) continue;
            hb.OnClick(settings);
            settings.Save(configPath);
            return hb.Action;
        }
        return DrawerAction.None;
    }

    private void RenderVideoTab(PixelOverlayCanvas c, int x, int y, int w, RecompSettings s)
    {
        UiPaint.Text(c, x, y, "Render Backend", UiPaint.Muted);
        int bw = (w - 8) / 2;
        var glPill = new UiRect(x, y + 20, bw, 32);
        var swPill = new UiRect(x + bw + 8, y + 20, bw, 32);
        UiPaint.Pill(c, glPill, "OpenGL 3.3", string.Equals(s.BackendId, PsxRenderBackendIds.OpenGl, StringComparison.Ordinal));
        UiPaint.Pill(c, swPill, "Software CPU", string.Equals(s.BackendId, PsxRenderBackendIds.Software, StringComparison.Ordinal));
        _hitBoxes.Add((glPill, st => st.BackendId = PsxRenderBackendIds.OpenGl, DrawerAction.ToggleBackend));
        _hitBoxes.Add((swPill, st => st.BackendId = PsxRenderBackendIds.Software, DrawerAction.ToggleBackend));

        UiPaint.Text(c, x, y + 62, "Render Scale", UiPaint.Muted);
        int pw = (w - 18) / 4;
        for (int i = 0; i < RecompSettings.AllowedResolutionScales.Count; i++)
        {
            int sc = RecompSettings.AllowedResolutionScales[i];
            var r = new UiRect(x + (i * (pw + 6)), y + 82, pw, 32);
            UiPaint.Pill(c, r, $"{sc}x", s.ResolutionScale == sc);
            _hitBoxes.Add((r, st => st.ResolutionScale = sc, DrawerAction.MarkDirty));
        }

        UiPaint.Text(c, x, y + 124, "Framerate Mode", UiPaint.Muted);
        var origPill = new UiRect(x, y + 144, bw, 32);
        var enhPill = new UiRect(x + bw + 8, y + 144, bw, 32);
        UiPaint.Pill(c, origPill, "Original (60/30)", s.FrameRateMode == FrameRateMode.Original);
        UiPaint.Pill(c, enhPill, "Enhanced FPS", s.FrameRateMode == FrameRateMode.Enhanced);
        _hitBoxes.Add((origPill, st => st.FrameRateMode = FrameRateMode.Original, DrawerAction.MarkDirty));
        _hitBoxes.Add((enhPill, st => st.FrameRateMode = FrameRateMode.Enhanced, DrawerAction.MarkDirty));

        UiPaint.Text(c, x, y + 186, "Target FPS", UiPaint.Muted);
        int[] targets = { 30, 60, 120, 144, 240, 0 };
        int tw = (w - 30) / 6;
        for (int i = 0; i < targets.Length; i++)
        {
            int tf = targets[i];
            var tr = new UiRect(x + (i * (tw + 6)), y + 206, tw, 32);
            bool selected = s.FrameRateMode == FrameRateMode.Enhanced && s.TargetFps == tf;
            UiPaint.Pill(c, tr, tf == 0 ? "Max" : tf.ToString(), selected);
            _hitBoxes.Add((tr, st => { st.FrameRateMode = FrameRateMode.Enhanced; st.TargetFps = tf; }, DrawerAction.MarkDirty));
        }

        AddToggle(c, new UiRect(x, y + 250, w, 38), "VSync", s.IsVsyncEnabled, st => st.IsVsyncEnabled = !st.IsVsyncEnabled, DrawerAction.ToggleVsync);
        AddToggle(c, new UiRect(x, y + 294, w, 38), "16:9 Widescreen", s.IsWidescreen16By9, st => st.IsWidescreen16By9 = !st.IsWidescreen16By9, DrawerAction.MarkDirty);
        AddToggle(c, new UiRect(x, y + 338, w, 38), "PGXP Precision", s.IsPgxpEnabled, st => st.IsPgxpEnabled = !st.IsPgxpEnabled, DrawerAction.MarkDirty);
        AddToggle(c, new UiRect(x, y + 382, w, 38), "Fullscreen", s.IsFullscreen, st => st.IsFullscreen = !st.IsFullscreen, DrawerAction.ToggleFullscreen);
        AddToggle(c, new UiRect(x, y + 426, w, 38), "FPS Overlay (F3)", s.IsFpsOverlayEnabled, st => st.IsFpsOverlayEnabled = !st.IsFpsOverlayEnabled, DrawerAction.ToggleFpsOverlay);
    }

    private void RenderControlsTab(PixelOverlayCanvas c, int x, int y, int w, RecompSettings s)
    {
        AddToggle(c, new UiRect(x, y, w, 46), "Modern WASD + Mouse", s.ControlMode == ControlMode.ModernWasdMouse,
            st => st.ControlMode = st.ControlMode == ControlMode.ModernWasdMouse ? ControlMode.ClassicTank : ControlMode.ModernWasdMouse,
            DrawerAction.MarkDirty);

        var minus = new UiRect(x, y + 62, 68, 38);
        var plus = new UiRect(x + w - 68, y + 62, 68, 38);
        UiPaint.Stepper(c, minus, plus, x + 92, $"Mouse Sens {s.MouseSensitivity:F1}");
        _hitBoxes.Add((minus, st => st.MouseSensitivity = Math.Max(0.2, Math.Round(st.MouseSensitivity - 0.2, 1)), DrawerAction.MarkDirty));
        _hitBoxes.Add((plus, st => st.MouseSensitivity = Math.Min(4.0, Math.Round(st.MouseSensitivity + 0.2, 1)), DrawerAction.MarkDirty));

        string tierLabel = s.InputProfileMode switch
        {
            InputProfileMode.GlobalController => "Global 1:1 PS1",
            InputProfileMode.PerGameController => "Game 1:1 PS1",
            _ => _moduleProfile is not null ? _moduleProfile.ProfileDisplayName : "Game 1:1 Fallback"
        };
        UiPaint.Text(c, x, y + 114, $"Active Tier: {tierLabel}", UiPaint.AccentText);

        KeyBindingsConfig kb = s.InputProfileMode == InputProfileMode.GlobalController ? _globalProfile.KeyBindings : (s.KeyBindings ?? new KeyBindingsConfig());
        string move = $"{KeybindsDrawerTab.FormatScancode(kb.GetScancode(BindableAction.DPadUp))}/{KeybindsDrawerTab.FormatScancode(kb.GetScancode(BindableAction.DPadLeft))}/{KeybindsDrawerTab.FormatScancode(kb.GetScancode(BindableAction.DPadDown))}/{KeybindsDrawerTab.FormatScancode(kb.GetScancode(BindableAction.DPadRight))} D-Pad";
        string acts = $"{KeybindsDrawerTab.FormatScancode(kb.GetScancode(BindableAction.Cross))} Cross  {KeybindsDrawerTab.FormatScancode(kb.GetScancode(BindableAction.Circle))} Circle  {KeybindsDrawerTab.FormatScancode(kb.GetScancode(BindableAction.Square))} Square  {KeybindsDrawerTab.FormatScancode(kb.GetScancode(BindableAction.Triangle))} Tri";
        string sys = $"{KeybindsDrawerTab.FormatScancode(kb.GetScancode(BindableAction.Start))} Start  {KeybindsDrawerTab.FormatScancode(kb.GetScancode(BindableAction.Select))} Select  {KeybindsDrawerTab.FormatScancode(kb.GetScancode(BindableAction.QuickSave))} Save  {KeybindsDrawerTab.FormatScancode(kb.GetScancode(BindableAction.QuickLoad))} Load";
        UiPaint.Text(c, x, y + 144, move, UiPaint.Muted);
        UiPaint.Text(c, x, y + 172, acts, UiPaint.Muted);
        UiPaint.Text(c, x, y + 200, sys, UiPaint.Muted);

        var customBtn = new UiRect(x, y + 242, w, 44);
        UiPaint.Pill(c, customBtn, "Customize Keybinds (3 Tiers)...", active: false);
        _hitBoxes.Add((customBtn, _ => ActiveTab = 2, DrawerAction.MarkDirty));
    }

    private void RenderSystemTab(PixelOverlayCanvas c, int x, int y, int w, RecompSettings s, bool loggingActive)
    {
        AddToggle(c, new UiRect(x, y, w, 44), "Use Global Format Rules", s.UseGlobalFormatRules,
            st => st.UseGlobalFormatRules = !st.UseGlobalFormatRules, DrawerAction.MarkDirty);
        AddToggle(c, new UiRect(x, y + 54, w, 44), "Fast Disc Load (Form1)", s.ShouldFastLoadData,
            st => st.ShouldFastLoadData = !st.ShouldFastLoadData, DrawerAction.MarkDirty);

        var minus = new UiRect(x, y + 114, 68, 38);
        var plus = new UiRect(x + w - 68, y + 114, 68, 38);
        UiPaint.Stepper(c, minus, plus, x + 92, $"Master Volume {(int)Math.Round(s.MasterVolume * 100)}%");
        _hitBoxes.Add((minus, st => st.MasterVolume = Math.Max(0.0, Math.Round(st.MasterVolume - 0.1, 2)), DrawerAction.MarkDirty));
        _hitBoxes.Add((plus, st => st.MasterVolume = Math.Min(1.0, Math.Round(st.MasterVolume + 0.1, 2)), DrawerAction.MarkDirty));

        AddToggle(c, new UiRect(x, y + 168, w, 44), "Runtime Logging", s.ShouldEnableRuntimeLogs,
            st => st.ShouldEnableRuntimeLogs = !st.ShouldEnableRuntimeLogs, DrawerAction.ToggleRuntimeLogging);

        string logStatus = loggingActive ? "Logs: Writing to runtime.log" : "Logs: Off (in-memory ring only)";
        UiPaint.Text(c, x + 4, y + 224, logStatus, loggingActive ? UiPaint.Good : UiPaint.Muted);

        var qs = new UiRect(x, y + 260, w, 44);
        var ql = new UiRect(x, y + 314, w, 44);
        UiPaint.Pill(c, qs, "Quick Save (F5)", active: false);
        UiPaint.Pill(c, ql, "Quick Load (F9)", active: false);
        _hitBoxes.Add((qs, _ => { }, DrawerAction.QuickSave));
        _hitBoxes.Add((ql, _ => { }, DrawerAction.QuickLoad));
    }

    private void RenderDiscTab(PixelOverlayCanvas c, int x, int y, int w, IReStationModuleManifest? manifest, string discInfo)
    {
        UiPaint.BorderRect(c, new UiRect(x, y, w, 92), UiPaint.Panel, UiPaint.Border);
        string title = manifest is null ? "Module: None loaded" : $"Module: {manifest.DisplayName} ({manifest.SerialNumber})";
        UiPaint.Text(c, x + 14, y + 18, title, UiPaint.AccentText);
        UiPaint.Text(c, x + 14, y + 50, discInfo, UiPaint.Muted);

        var swap = new UiRect(x, y + 112, w, 44);
        var loadMod = new UiRect(x, y + 168, w, 44);
        UiPaint.Pill(c, swap, "Change Disc Image...", active: false);
        UiPaint.Pill(c, loadMod, "Load .recomp Module...", active: false);
        _hitBoxes.Add((swap, _ => { }, DrawerAction.ChangeDisc));
        _hitBoxes.Add((loadMod, _ => { }, DrawerAction.ChangeModule));
    }

    private void AddToggle(PixelOverlayCanvas c, UiRect r, string label, bool on, Action<RecompSettings> onClick, DrawerAction action)
    {
        UiPaint.ToggleRow(c, r, label, on);
        _hitBoxes.Add((r, onClick, action));
    }
}
