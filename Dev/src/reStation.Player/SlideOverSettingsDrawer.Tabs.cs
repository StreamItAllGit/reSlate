// @module: reStation.Player.SlideOverSettingsDrawer.Tabs
// @purpose: Extracted Video, Controls, System (Fast-Forward + Cutscene Skip), and Disc tab renderers for SlideOverSettingsDrawer.
// @depends-on: Foundation.Core, reEngine.Host.Contracts, reStation.Contracts, reStation.Core, reStation.Core.Input
// @exports: SlideOverSettingsDrawer (partial)

using System.Globalization;
using Foundation.Core;
using reEngine.Host.Contracts;
using reStation.Contracts;
using reStation.Core;
using reStation.Core.Input;

namespace reStation.Player;

internal sealed partial class SlideOverSettingsDrawer
{
    private void RenderVideoTab(PixelOverlayCanvas c, int x, int y, int w, RecompSettings s)
    {
        UiPaint.Text(c, x, y, "Render Backend", UiPaint.Muted);
        string activeBackend = HostVideoSurface.NormalizeBackendId(ActiveBackendOverride ?? s.BackendId);
        int bw = (w - 8) / 2;
        for (int i = 0; i < PsxDrawerDescriptorProvider.BackendOptions.Count; i++)
        {
            PsxBackendOption opt = PsxDrawerDescriptorProvider.BackendOptions[i];
            int row = i / 2, col = i % 2;
            var rect = new UiRect(x + (col * (bw + 8)), y + 18 + (row * 32), bw, 28);
            bool selected = string.Equals(activeBackend, opt.BackendId, StringComparison.OrdinalIgnoreCase);
            UiPaint.Pill(c, rect, opt.Label, selected);
            string capturedId = opt.BackendId;
            _hitBoxes.Add(new DrawerHitBox(rect, st =>
            {
                PendingRequestedBackendId = capturedId;
                st.BackendId = capturedId;
            }, DrawerAction.ToggleBackend, ControlId: opt.ControlId, Label: opt.Label));
        }

        UiPaint.Text(c, x, y + 84, "Render Scale", UiPaint.Muted);
        int pw = (w - 18) / 4;
        for (int i = 0; i < RecompSettings.AllowedResolutionScales.Count; i++)
        {
            int sc = RecompSettings.AllowedResolutionScales[i];
            var r = new UiRect(x + (i * (pw + 6)), y + 102, pw, 28);
            UiPaint.Pill(c, r, $"{sc}x", s.ResolutionScale == sc);
            _hitBoxes.Add(new DrawerHitBox(r, st => st.ResolutionScale = sc, DrawerAction.MarkDirty, ControlId: $"Video_Scale{sc}x", Label: $"{sc}x"));
        }

        UiPaint.Text(c, x, y + 136, "Framerate Mode", UiPaint.Muted);
        var origPill = new UiRect(x, y + 154, bw, 28);
        var enhPill = new UiRect(x + bw + 8, y + 154, bw, 28);
        UiPaint.Pill(c, origPill, "Original (60/30)", s.FrameRateMode == FrameRateMode.Original);
        UiPaint.Pill(c, enhPill, "Enhanced FPS", s.FrameRateMode == FrameRateMode.Enhanced);
        _hitBoxes.Add(new DrawerHitBox(origPill, st => st.FrameRateMode = FrameRateMode.Original, DrawerAction.MarkDirty, ControlId: "Video_ModeOriginal", Label: "Original (60/30)"));
        _hitBoxes.Add(new DrawerHitBox(enhPill, st => st.FrameRateMode = FrameRateMode.Enhanced, DrawerAction.MarkDirty, ControlId: "Video_ModeEnhanced", Label: "Enhanced FPS"));

        UiPaint.Text(c, x, y + 188, "Target FPS", UiPaint.Muted);
        int[] targets = { 30, 60, 120, 144, 240, 0 };
        int tw = (w - 30) / 6;
        for (int i = 0; i < targets.Length; i++)
        {
            int tf = targets[i];
            var tr = new UiRect(x + (i * (tw + 6)), y + 206, tw, 28);
            bool selected = s.FrameRateMode == FrameRateMode.Enhanced && s.TargetFps == tf;
            string lbl = tf == 0 ? "Max" : tf.ToString(CultureInfo.InvariantCulture);
            UiPaint.Pill(c, tr, lbl, selected);
            _hitBoxes.Add(new DrawerHitBox(tr, st => { st.FrameRateMode = FrameRateMode.Enhanced; st.TargetFps = tf; }, DrawerAction.MarkDirty, ControlId: $"Video_TargetFps_{lbl}", Label: lbl));
        }

        AddToggle(c, new UiRect(x, y + 242, w, 34), "VSync (Caps to Monitor Hz)", s.IsVsyncEnabled,
            st => st.IsVsyncEnabled = !st.IsVsyncEnabled, DrawerAction.ToggleVsync, "Video_ToggleVsync");
        AddToggle(c, new UiRect(x, y + 280, w, 34), "HD Texture Packs", s.IsHdTexturePackEnabled,
            st => st.IsHdTexturePackEnabled = !st.IsHdTexturePackEnabled, DrawerAction.ToggleHdPacks, "Video_ToggleHdPacks");

        HostAspectRatioMode aspect = PsxDrawerDescriptorProvider.ResolveDisplayAspectMode(s);
        string aspectLbl = $"Aspect Mode: {HostAspectRatioModeCodec.ToDisplayLabel(aspect)}";
        var aspectRect = new UiRect(x, y + 318, w, 34);
        UiPaint.Pill(c, aspectRect, aspectLbl, aspect != HostAspectRatioMode.NativePar);
        _hitBoxes.Add(new DrawerHitBox(aspectRect,
            st =>
            {
                HostAspectRatioMode next = HostAspectRatioModeCodec.CycleNext(PsxDrawerDescriptorProvider.ResolveDisplayAspectMode(st));
                st.AspectRatioMode = HostAspectRatioModeCodec.ToCanonicalId(next);
                st.IsWidescreen16By9 = next == HostAspectRatioMode.HorPlus16By9;
            },
            DrawerAction.ToggleAspectMode,
            st =>
            {
                st.AspectRatioMode = HostAspectRatioModeCodec.ToCanonicalId(HostAspectRatioMode.NativePar);
                st.IsWidescreen16By9 = false;
            },
            ControlId: "Video_AspectRatio",
            Label: aspectLbl));

        bool overscanOn = s.OverscanCropPx.Top > 0 || s.OverscanCropPx.Bottom > 0 || s.OverscanCropPx.Left > 0 || s.OverscanCropPx.Right > 0;
        string overscanLbl = $"Overscan Crop: {PsxDrawerDescriptorProvider.FormatOverscanLabel(s.OverscanCropPx)}";
        var overscanRect = new UiRect(x, y + 356, w, 34);
        UiPaint.Pill(c, overscanRect, overscanLbl, overscanOn);
        _hitBoxes.Add(new DrawerHitBox(overscanRect,
            st => st.OverscanCropPx = PsxDrawerDescriptorProvider.CycleOverscan(st.OverscanCropPx),
            DrawerAction.ToggleOverscanCrop,
            st => st.OverscanCropPx = HostOverscanCrop.None,
            ControlId: "Video_ToggleOverscan",
            Label: overscanLbl));

        AddToggle(c, new UiRect(x, y + 394, w, 34), "PGXP Precision", s.IsPgxpEnabled,
            st => st.IsPgxpEnabled = !st.IsPgxpEnabled, DrawerAction.MarkDirty, "Video_TogglePgxp");
        AddToggle(c, new UiRect(x, y + 432, w, 34), "Fullscreen", s.IsFullscreen,
            st => st.IsFullscreen = !st.IsFullscreen, DrawerAction.ToggleFullscreen, "Video_ToggleFullscreen");
        AddToggle(c, new UiRect(x, y + 470, w, 34), "FPS Overlay (F3)", s.IsFpsOverlayEnabled,
            st => st.IsFpsOverlayEnabled = !st.IsFpsOverlayEnabled, DrawerAction.ToggleFpsOverlay, "Video_ToggleFpsOverlay");

        if (!string.IsNullOrWhiteSpace(VideoWarningBanner))
        {
            var warnRect = new UiRect(x, y + 510, w, 32);
            UiPaint.BorderRect(c, warnRect, new RgbaColor(0x36, 0x1E, 0x16), UiPaint.Warn);
            int maxChars = Math.Max(8, (w - 20) / 12);
            string msg = VideoWarningBanner.Length <= maxChars ? VideoWarningBanner : VideoWarningBanner[..maxChars];
            UiPaint.Text(c, x + 10, y + 519, msg, UiPaint.Warn);
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
        AddToggle(c, new UiRect(x, y, w, 38), "Use Global Format Rules", s.UseGlobalFormatRules,
            st => st.UseGlobalFormatRules = !st.UseGlobalFormatRules, DrawerAction.MarkDirty, "System_ToggleGlobalRules");
        AddToggle(c, new UiRect(x, y + 44, w, 38), "Fast Disc Load (Form1)", s.ShouldFastLoadData,
            st => st.ShouldFastLoadData = !st.ShouldFastLoadData, DrawerAction.MarkDirty, "System_ToggleFastLoad");

        var minus = new UiRect(x, y + 90, 68, 34);
        var plus = new UiRect(x + w - 68, y + 90, 68, 34);
        UiPaint.Stepper(c, minus, plus, x + 92, $"Master Volume {(int)Math.Round(s.MasterVolume * 100)}%");
        _hitBoxes.Add(new DrawerHitBox(minus, st => st.MasterVolume = Math.Max(0.0, Math.Round(st.MasterVolume - 0.1, 2)), DrawerAction.MarkDirty, ControlId: "System_VolumeMinus", Label: "-"));
        _hitBoxes.Add(new DrawerHitBox(plus, st => st.MasterVolume = Math.Min(1.0, Math.Round(st.MasterVolume + 0.1, 2)), DrawerAction.MarkDirty, ControlId: "System_VolumePlus", Label: "+"));

        AddToggle(c, new UiRect(x, y + 132, w, 38), "Runtime Logging", s.ShouldEnableRuntimeLogs,
            st => st.ShouldEnableRuntimeLogs = !st.ShouldEnableRuntimeLogs, DrawerAction.ToggleRuntimeLogging, "System_ToggleLogging");
        string logStatus = loggingActive ? "Logs: Writing to runtime.log" : "Logs: Off (in-memory ring only)";
        UiPaint.Text(c, x + 4, y + 178, logStatus, loggingActive ? UiPaint.Good : UiPaint.Muted);

        AddToggle(c, new UiRect(x, y + 204, w, 38), "Fast-Forward (F6)", s.IsFastForwardEnabled,
            st => st.IsFastForwardEnabled = !st.IsFastForwardEnabled, DrawerAction.ToggleFastForward, "System_ToggleFastForward");

        int mw = (w - 12) / 3;
        double activeMult = PsxDrawerDescriptorProvider.NormalizeFastForwardMultiplier(s.FastForwardMultiplier);
        for (int i = 0; i < PsxDrawerDescriptorProvider.FastForwardMultipliers.Count; i++)
        {
            double mult = PsxDrawerDescriptorProvider.FastForwardMultipliers[i];
            int multInt = (int)Math.Round(mult);
            var mr = new UiRect(x + (i * (mw + 6)), y + 248, mw, 32);
            string lbl = $"FF {multInt}x";
            UiPaint.Pill(c, mr, lbl, Math.Abs(activeMult - mult) < 0.15);
            _hitBoxes.Add(new DrawerHitBox(mr, st => st.FastForwardMultiplier = mult, DrawerAction.ToggleFastForward, ControlId: $"System_FastForward{multInt}x", Label: lbl));
        }

        var skipBtn = new UiRect(x, y + 288, w, 38);
        UiPaint.Pill(c, skipBtn, "Skip Cutscene (F7)", active: false);
        _hitBoxes.Add(new DrawerHitBox(skipBtn, _ => { }, DrawerAction.CutsceneSkip, ControlId: "System_SkipCutscene", Label: "Skip Cutscene (F7)"));

        var qs = new UiRect(x, y + 334, w, 38);
        var ql = new UiRect(x, y + 378, w, 38);
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
}
