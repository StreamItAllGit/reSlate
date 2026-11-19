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

internal readonly record struct DrawerHitBox(
    UiRect Rect,
    Action<RecompSettings> OnClick,
    DrawerAction Action,
    Action<RecompSettings>? OnRightClick = null,
    string ControlId = "",
    string ControlType = "Button",
    string Label = "");

/// <summary>Right-docked 5-tab settings drawer toggled by F1 or the top-right Settings pill.</summary>
internal sealed class SlideOverSettingsDrawer
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

    private void RenderVideoTab(PixelOverlayCanvas c, int x, int y, int w, RecompSettings s)
    {
        UiPaint.Text(c, x, y, "Render Backend", UiPaint.Muted);
        int bw = (w - 8) / 2;
        var glPill = new UiRect(x, y + 20, bw, 32);
        var swPill = new UiRect(x + bw + 8, y + 20, bw, 32);
        UiPaint.Pill(c, glPill, "OpenGL 3.3", string.Equals(s.BackendId, PsxRenderBackendIds.OpenGl, StringComparison.Ordinal));
        UiPaint.Pill(c, swPill, "Software CPU", string.Equals(s.BackendId, PsxRenderBackendIds.Software, StringComparison.Ordinal));
        _hitBoxes.Add(new DrawerHitBox(glPill, st => st.BackendId = PsxRenderBackendIds.OpenGl, DrawerAction.ToggleBackend, ControlId: "Video_BackendOpenGl", Label: "OpenGL 3.3"));
        _hitBoxes.Add(new DrawerHitBox(swPill, st => st.BackendId = PsxRenderBackendIds.Software, DrawerAction.ToggleBackend, ControlId: "Video_BackendSoftware", Label: "Software CPU"));

        UiPaint.Text(c, x, y + 62, "Render Scale", UiPaint.Muted);
        int pw = (w - 18) / 4;
        for (int i = 0; i < RecompSettings.AllowedResolutionScales.Count; i++)
        {
            int sc = RecompSettings.AllowedResolutionScales[i];
            var r = new UiRect(x + (i * (pw + 6)), y + 82, pw, 32);
            UiPaint.Pill(c, r, $"{sc}x", s.ResolutionScale == sc);
            _hitBoxes.Add(new DrawerHitBox(r, st => st.ResolutionScale = sc, DrawerAction.MarkDirty, ControlId: $"Video_Scale{sc}x", Label: $"{sc}x"));
        }

        UiPaint.Text(c, x, y + 124, "Framerate Mode", UiPaint.Muted);
        var origPill = new UiRect(x, y + 144, bw, 32);
        var enhPill = new UiRect(x + bw + 8, y + 144, bw, 32);
        UiPaint.Pill(c, origPill, "Original (60/30)", s.FrameRateMode == FrameRateMode.Original);
        UiPaint.Pill(c, enhPill, "Enhanced FPS", s.FrameRateMode == FrameRateMode.Enhanced);
        _hitBoxes.Add(new DrawerHitBox(origPill, st => st.FrameRateMode = FrameRateMode.Original, DrawerAction.MarkDirty, ControlId: "Video_ModeOriginal", Label: "Original (60/30)"));
        _hitBoxes.Add(new DrawerHitBox(enhPill, st => st.FrameRateMode = FrameRateMode.Enhanced, DrawerAction.MarkDirty, ControlId: "Video_ModeEnhanced", Label: "Enhanced FPS"));

        UiPaint.Text(c, x, y + 186, "Target FPS", UiPaint.Muted);
        int[] targets = { 30, 60, 120, 144, 240, 0 };
        int tw = (w - 30) / 6;
        for (int i = 0; i < targets.Length; i++)
        {
            int tf = targets[i];
            var tr = new UiRect(x + (i * (tw + 6)), y + 206, tw, 32);
            bool selected = s.FrameRateMode == FrameRateMode.Enhanced && s.TargetFps == tf;
            string lbl = tf == 0 ? "Max" : tf.ToString();
            UiPaint.Pill(c, tr, lbl, selected);
            _hitBoxes.Add(new DrawerHitBox(tr, st => { st.FrameRateMode = FrameRateMode.Enhanced; st.TargetFps = tf; }, DrawerAction.MarkDirty, ControlId: $"Video_TargetFps_{lbl}", Label: lbl));
        }

        AddToggle(c, new UiRect(x, y + 250, w, 38), "VSync (Caps to Monitor Hz)", s.IsVsyncEnabled, st => st.IsVsyncEnabled = !st.IsVsyncEnabled, DrawerAction.ToggleVsync, "Video_ToggleVsync");
        AddToggle(c, new UiRect(x, y + 294, w, 38), "16:9 Widescreen", s.IsWidescreen16By9, st => st.IsWidescreen16By9 = !st.IsWidescreen16By9, DrawerAction.MarkDirty, "Video_ToggleWidescreen");
        AddToggle(c, new UiRect(x, y + 338, w, 38), "PGXP Precision", s.IsPgxpEnabled, st => st.IsPgxpEnabled = !st.IsPgxpEnabled, DrawerAction.MarkDirty, "Video_TogglePgxp");
        AddToggle(c, new UiRect(x, y + 382, w, 38), "Fullscreen", s.IsFullscreen, st => st.IsFullscreen = !st.IsFullscreen, DrawerAction.ToggleFullscreen, "Video_ToggleFullscreen");
        AddToggle(c, new UiRect(x, y + 426, w, 38), "FPS Overlay (F3)", s.IsFpsOverlayEnabled, st => st.IsFpsOverlayEnabled = !st.IsFpsOverlayEnabled, DrawerAction.ToggleFpsOverlay, "Video_ToggleFpsOverlay");

        if (!string.IsNullOrWhiteSpace(VideoWarningBanner))
        {
            var warnRect = new UiRect(x, y + 472, w, 34);
            UiPaint.BorderRect(c, warnRect, new RgbaColor(0x36, 0x1E, 0x16), UiPaint.Warn);
            int maxChars = Math.Max(8, (w - 20) / 12);
            string msg = VideoWarningBanner.Length <= maxChars ? VideoWarningBanner : VideoWarningBanner[..maxChars];
            UiPaint.Text(c, x + 10, y + 482, msg, UiPaint.Warn);
        }
    }

    private void RenderControlsTab(PixelOverlayCanvas c, int x, int y, int w, RecompSettings s)
    {
        AddToggle(c, new UiRect(x, y, w, 42), "Modern WASD + Mouse", s.ControlMode == ControlMode.ModernWasdMouse,
            st => st.ControlMode = st.ControlMode == ControlMode.ModernWasdMouse ? ControlMode.ClassicTank : ControlMode.ModernWasdMouse,
            DrawerAction.MarkDirty, "Controls_ToggleModernMouse");

        var minus = new UiRect(x, y + 50, 68, 36);
        var plus = new UiRect(x + w - 68, y + 50, 68, 36);
        UiPaint.Stepper(c, minus, plus, x + 92, $"Mouse Sens {s.MouseSensitivity:F1}");
        _hitBoxes.Add(new DrawerHitBox(minus, st => st.MouseSensitivity = Math.Max(0.2, Math.Round(st.MouseSensitivity - 0.2, 1)), DrawerAction.MarkDirty, ControlId: "Controls_SensMinus", Label: "-"));
        _hitBoxes.Add(new DrawerHitBox(plus, st => st.MouseSensitivity = Math.Min(4.0, Math.Round(st.MouseSensitivity + 0.2, 1)), DrawerAction.MarkDirty, ControlId: "Controls_SensPlus", Label: "+"));

        int curY = y + 96;
        IReadOnlyList<GameControlOptionDescriptor> options = _moduleProfile?.ControlOptions ?? Array.Empty<GameControlOptionDescriptor>();
        foreach (GameControlOptionDescriptor opt in options)
        {
            if (opt.Choices.Count == 0) continue;
            UiPaint.Text(c, x, curY, opt.Label, UiPaint.Muted);
            int count = opt.Choices.Count;
            int cw = (w - ((count - 1) * 6)) / count;
            string current = s.GetModuleOption(opt);
            for (int ci = 0; ci < count; ci++)
            {
                GameControlOptionChoice choice = opt.Choices[ci];
                var r = new UiRect(x + (ci * (cw + 6)), curY + 20, cw, 32);
                UiPaint.Pill(c, r, choice.Label, string.Equals(current, choice.Value, StringComparison.OrdinalIgnoreCase));
                string val = choice.Value;
                string oid = opt.OptionId;
                _hitBoxes.Add(new DrawerHitBox(r, st => st.SetModuleOption(oid, val), DrawerAction.MarkDirty, ControlId: $"Controls_Opt_{oid}_{val}", Label: choice.Label));
            }
            curY += 60;
        }

        curY += 4;
        string tierLabel = s.InputProfileMode switch
        {
            InputProfileMode.GlobalController => "Global 1:1 PS1",
            InputProfileMode.PerGameController => "Game 1:1 PS1",
            _ => _moduleProfile is not null ? _moduleProfile.ProfileDisplayName : "Game 1:1 Fallback"
        };
        UiPaint.Text(c, x, curY, $"Active Tier: {tierLabel}", UiPaint.AccentText);

        (string line1, string line2) = BuildControlSummaryLines(s);
        UiPaint.Text(c, x, curY + 28, line1, UiPaint.Muted);
        UiPaint.Text(c, x, curY + 56, line2, UiPaint.Muted);

        var customBtn = new UiRect(x, curY + 96, w, 42);
        UiPaint.Pill(c, customBtn, "Customize Keybinds (3 Tiers)...", active: false);
        _hitBoxes.Add(new DrawerHitBox(customBtn, _ => ActiveTab = 2, DrawerAction.MarkDirty, ControlId: "Controls_CustomizeKeybinds", Label: "Customize Keybinds"));
    }

    private (string Line1, string Line2) BuildControlSummaryLines(RecompSettings s)
    {
        if (s.InputProfileMode == InputProfileMode.PerGameModern && _moduleProfile is not null && _moduleProfile.ModernActions.Count > 0)
        {
            var items = new List<string>(6);
            foreach (GameActionDescriptor act in _moduleProfile.ModernActions)
            {
                IReadOnlyList<int> codes = s.GetModernActionBindings(act);
                string key = codes.Count > 0 ? KeybindsDrawerTab.FormatScancode(codes[0]) : "---";
                string shortName = act.Label.Length <= 8 ? act.Label : act.Label[..8];
                items.Add($"{key} {shortName}");
                if (items.Count >= 6) break;
            }
            string l1 = FitSummary(string.Join("  ", items.Take(3)), 44);
            string l2 = items.Count > 3 ? FitSummary(string.Join("  ", items.Skip(3)), 44) : string.Empty;
            return (l1, l2);
        }

        KeyBindingsConfig kb = s.InputProfileMode == InputProfileMode.GlobalController ? _globalProfile.KeyBindings : (s.KeyBindings ?? new KeyBindingsConfig());
        string move = $"{KeybindsDrawerTab.FormatScancode(kb.GetScancode(BindableAction.DPadUp))}/{KeybindsDrawerTab.FormatScancode(kb.GetScancode(BindableAction.DPadLeft))}/{KeybindsDrawerTab.FormatScancode(kb.GetScancode(BindableAction.DPadDown))}/{KeybindsDrawerTab.FormatScancode(kb.GetScancode(BindableAction.DPadRight))} D-Pad";
        string acts = $"{KeybindsDrawerTab.FormatScancode(kb.GetScancode(BindableAction.Cross))} Cross  {KeybindsDrawerTab.FormatScancode(kb.GetScancode(BindableAction.Circle))} Circle  {KeybindsDrawerTab.FormatScancode(kb.GetScancode(BindableAction.Square))} Square";
        return (FitSummary(move, 44), FitSummary(acts, 44));
    }

    private static string FitSummary(string text, int maxChars) => text.Length <= maxChars ? text : text[..Math.Max(1, maxChars)];

    private void RenderSystemTab(PixelOverlayCanvas c, int x, int y, int w, RecompSettings s, bool loggingActive)
    {
        AddToggle(c, new UiRect(x, y, w, 44), "Use Global Format Rules", s.UseGlobalFormatRules,
            st => st.UseGlobalFormatRules = !st.UseGlobalFormatRules, DrawerAction.MarkDirty, "System_ToggleGlobalRules");
        AddToggle(c, new UiRect(x, y + 54, w, 44), "Fast Disc Load (Form1)", s.ShouldFastLoadData,
            st => st.ShouldFastLoadData = !st.ShouldFastLoadData, DrawerAction.MarkDirty, "System_ToggleFastLoad");

        var minus = new UiRect(x, y + 114, 68, 38);
        var plus = new UiRect(x + w - 68, y + 114, 68, 38);
        UiPaint.Stepper(c, minus, plus, x + 92, $"Master Volume {(int)Math.Round(s.MasterVolume * 100)}%");
        _hitBoxes.Add(new DrawerHitBox(minus, st => st.MasterVolume = Math.Max(0.0, Math.Round(st.MasterVolume - 0.1, 2)), DrawerAction.MarkDirty, ControlId: "System_VolumeMinus", Label: "-"));
        _hitBoxes.Add(new DrawerHitBox(plus, st => st.MasterVolume = Math.Min(1.0, Math.Round(st.MasterVolume + 0.1, 2)), DrawerAction.MarkDirty, ControlId: "System_VolumePlus", Label: "+"));

        AddToggle(c, new UiRect(x, y + 168, w, 44), "Runtime Logging", s.ShouldEnableRuntimeLogs,
            st => st.ShouldEnableRuntimeLogs = !st.ShouldEnableRuntimeLogs, DrawerAction.ToggleRuntimeLogging, "System_ToggleLogging");

        string logStatus = loggingActive ? "Logs: Writing to runtime.log" : "Logs: Off (in-memory ring only)";
        UiPaint.Text(c, x + 4, y + 224, logStatus, loggingActive ? UiPaint.Good : UiPaint.Muted);

        var qs = new UiRect(x, y + 260, w, 44);
        var ql = new UiRect(x, y + 314, w, 44);
        UiPaint.Pill(c, qs, "Quick Save (F5)", active: false);
        UiPaint.Pill(c, ql, "Quick Load (F9)", active: false);
        _hitBoxes.Add(new DrawerHitBox(qs, _ => { }, DrawerAction.QuickSave, ControlId: "System_QuickSave", Label: "Quick Save (F5)"));
        _hitBoxes.Add(new DrawerHitBox(ql, _ => { }, DrawerAction.QuickLoad, ControlId: "System_QuickLoad", Label: "Quick Load (F9)"));
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
        _hitBoxes.Add(new DrawerHitBox(swap, _ => { }, DrawerAction.ChangeDisc, ControlId: "Disc_ChangeDisc", Label: "Change Disc Image"));
        _hitBoxes.Add(new DrawerHitBox(loadMod, _ => { }, DrawerAction.ChangeModule, ControlId: "Disc_ChangeModule", Label: "Load .recomp Module"));
    }

    private void AddToggle(PixelOverlayCanvas c, UiRect r, string label, bool on, Action<RecompSettings> onClick, DrawerAction action, string controlId = "")
    {
        UiPaint.ToggleRow(c, r, label, on);
        _hitBoxes.Add(new DrawerHitBox(r, onClick, action, ControlId: controlId, ControlType: "CheckBox", Label: label));
    }
}
