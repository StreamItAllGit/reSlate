// @module: reStation.Player.UiOverlayPresenter
// @purpose: 1280x720 PixelOverlayCanvas UI layer (F1 drawer + F3 telemetry HUD), viewport mapping, and screenshot compositing.
// @depends-on: Foundation.Core, reStation.Contracts, reStation.Core
// @exports: UiOverlayPresenter
// @owner: devv-player-ui (T6)

using System.Diagnostics;
using Foundation.Core;
using reStation.Contracts;
using reStation.Core;

namespace reStation.Player;

/// <summary>Maintains the 1280x720 UI RGBA layer and composites HUD/drawer surfaces over game video.</summary>
internal sealed class UiOverlayPresenter
{
    public const int OverlayWidth = 1280, OverlayHeight = 720;
    public const int HudStripWidth = 1060, HudStripHeight = 34, HudStripDstX = 14, HudStripDstY = 12;

    private static readonly Core.Input.BindableAction[] AllBindableActions = Enum.GetValues<Core.Input.BindableAction>();

    private readonly PixelOverlayCanvas _uiCanvas = new(OverlayWidth, OverlayHeight);
    private readonly PixelOverlayCanvas _drawerCacheCanvas = new(OverlayWidth, OverlayHeight);
    private readonly PixelOverlayCanvas _hudStripCanvas = new(HudStripWidth, HudStripHeight);
    private readonly byte[] _compositeBuf = new byte[OverlayWidth * OverlayHeight * 4];
    private bool _dirty = true;
    private long _lastHudUpdateTicks;

    private bool _lastOpen, _lastLoggingActive, _lastFpsEnabled, _lastVsync, _lastWidescreen, _lastPgxp, _lastFullscreen, _lastGlobalRules, _lastFastLoad;
    private int _lastTab = -1, _lastScale, _lastScrollOffset, _lastSlotIndex;
    private string _lastBackend = string.Empty, _lastDiscInfo = string.Empty, _lastHud = string.Empty;
    private string _lastDpadMode = string.Empty, _lastIsoMode = string.Empty;
    private string? _lastVideoWarning;
    private FrameRateMode _lastMode;
    private int _lastTargetFps;
    private ControlMode _lastControlMode;
    private Core.Input.InputProfileMode _lastProfileMode;
    private int _lastBindingsDigest;
    private double _lastSens, _lastVolume;
    private Core.Input.BindableAction? _lastListening;
    private string? _lastListeningModern;

    public bool IsCompactHudStripActive { get; private set; }
    internal int LastHudBoxRightEdge { get; private set; }
    internal UiRect CurrentHudRect { get; private set; }
    public ReadOnlySpan<byte> UiPixels => _uiCanvas.Buffer;
    public ReadOnlySpan<byte> ActiveOverlayPixels => IsCompactHudStripActive ? _hudStripCanvas.Buffer : _uiCanvas.Buffer;
    public int ActiveOverlayWidth => IsCompactHudStripActive ? HudStripWidth : OverlayWidth;
    public int ActiveOverlayHeight => IsCompactHudStripActive ? HudStripHeight : OverlayHeight;
    public int ActiveOverlayDstX => IsCompactHudStripActive ? HudStripDstX : 0;
    public int ActiveOverlayDstY => IsCompactHudStripActive ? HudStripDstY : 0;
    public ReadOnlySpan<byte> CompositePixels => _compositeBuf;

    public void MarkDirty() => _dirty = true;

    public static string FormatModeBadge(RecompSettings settings) =>
        settings.FrameRateMode == FrameRateMode.Original
            ? "[ORIG] "
            : settings.TargetFps == 0 ? "[MAX] " : "[ENH] ";

    public bool EnsureRendered(
        SlideOverSettingsDrawer drawer,
        RecompSettings settings,
        IReStationModuleManifest? manifest,
        string discInfo,
        bool loggingActive,
        PsxTelemetrySnapshot telemetry)
    {
        string hudText = settings.IsFpsOverlayEnabled ? FormatModeBadge(settings) + telemetry.ToHudLine() : string.Empty;
        int bindingsDigest = ComputeBindingsDigest(drawer, settings);
        bool structuralChanged = _dirty
            || _lastOpen != drawer.IsOpen
            || _lastTab != drawer.ActiveTab
            || _lastListening != drawer.KeybindsTab.ListeningAction
            || !string.Equals(_lastListeningModern, drawer.KeybindsTab.ListeningModernActionId, StringComparison.Ordinal)
            || _lastSlotIndex != drawer.KeybindsTab.ListeningSlotIndex
            || _lastScrollOffset != drawer.KeybindsTab.ScrollRowOffset
            || !string.Equals(_lastVideoWarning, drawer.VideoWarningBanner, StringComparison.Ordinal)
            || !string.Equals(_lastDiscInfo, discInfo, StringComparison.Ordinal)
            || _lastLoggingActive != loggingActive
            || _lastFpsEnabled != settings.IsFpsOverlayEnabled
            || !string.Equals(_lastBackend, settings.BackendId, StringComparison.Ordinal)
            || _lastMode != settings.FrameRateMode
            || _lastTargetFps != settings.TargetFps
            || _lastVsync != settings.IsVsyncEnabled
            || _lastScale != settings.ResolutionScale
            || _lastWidescreen != settings.IsWidescreen16By9
            || _lastPgxp != settings.IsPgxpEnabled
            || _lastFullscreen != settings.IsFullscreen
            || _lastGlobalRules != settings.UseGlobalFormatRules
            || _lastFastLoad != settings.ShouldFastLoadData
            || _lastControlMode != settings.ControlMode
            || !string.Equals(_lastDpadMode, settings.ControllerDpadMode, StringComparison.Ordinal)
            || !string.Equals(_lastIsoMode, settings.IsometricMoveMode, StringComparison.Ordinal)
            || _lastProfileMode != settings.InputProfileMode
            || _lastBindingsDigest != bindingsDigest
            || Math.Abs(_lastSens - settings.MouseSensitivity) > 0.001
            || Math.Abs(_lastVolume - settings.MasterVolume) > 0.001;
        bool hudTextChanged = !string.Equals(_lastHud, hudText, StringComparison.Ordinal);
        if (!structuralChanged)
        {
            if (!hudTextChanged) return false;
            if (_lastHudUpdateTicks != 0 && (Stopwatch.GetTimestamp() - _lastHudUpdateTicks) < (Stopwatch.Frequency / 4L))
                return false;
        }

        IsCompactHudStripActive = !drawer.IsOpen && settings.IsFpsOverlayEnabled;
        if (IsCompactHudStripActive)
        {
            _hudStripCanvas.Clear(UiPaint.Transparent);
            DrawHudBox(_hudStripCanvas, 0, 0, HudStripWidth, hudText);
            CurrentHudRect = new UiRect(HudStripDstX, HudStripDstY, ComputeHudBoxWidth(HudStripWidth, hudText), HudStripHeight);
            if (structuralChanged)
            {
                _drawerCacheCanvas.Clear(UiPaint.Transparent);
                drawer.RenderOverlay(_drawerCacheCanvas, settings, manifest, discInfo, loggingActive);
            }
            Buffer.BlockCopy(_drawerCacheCanvas.Buffer, 0, _uiCanvas.Buffer, 0, _uiCanvas.Buffer.Length);
            CopyStripIntoCanvas(_hudStripCanvas, _uiCanvas, HudStripDstX, HudStripDstY);
        }
        else
        {
            if (structuralChanged)
            {
                _drawerCacheCanvas.Clear(UiPaint.Transparent);
                drawer.RenderOverlay(_drawerCacheCanvas, settings, manifest, discInfo, loggingActive);
            }
            Buffer.BlockCopy(_drawerCacheCanvas.Buffer, 0, _uiCanvas.Buffer, 0, _uiCanvas.Buffer.Length);
            if (settings.IsFpsOverlayEnabled)
            {
                int maxW = drawer.IsOpen ? (drawer.ActiveTab == 2 ? 452 : 652) : HudStripWidth;
                DrawHudBox(_uiCanvas, HudStripDstX, HudStripDstY, maxW, hudText);
                int boxW = ComputeHudBoxWidth(maxW, hudText);
                LastHudBoxRightEdge = HudStripDstX + boxW;
                CurrentHudRect = new UiRect(HudStripDstX, HudStripDstY, boxW, HudStripHeight);
            }
            else
            {
                CurrentHudRect = default;
            }
        }

        _dirty = false;
        _lastOpen = drawer.IsOpen;
        _lastTab = drawer.ActiveTab;
        _lastListening = drawer.KeybindsTab.ListeningAction;
        _lastListeningModern = drawer.KeybindsTab.ListeningModernActionId;
        _lastSlotIndex = drawer.KeybindsTab.ListeningSlotIndex;
        _lastScrollOffset = drawer.KeybindsTab.ScrollRowOffset;
        _lastVideoWarning = drawer.VideoWarningBanner;
        _lastDiscInfo = discInfo;
        _lastLoggingActive = loggingActive;
        _lastFpsEnabled = settings.IsFpsOverlayEnabled;
        _lastHud = hudText;
        _lastBackend = settings.BackendId;
        _lastMode = settings.FrameRateMode;
        _lastTargetFps = settings.TargetFps;
        _lastVsync = settings.IsVsyncEnabled;
        _lastScale = settings.ResolutionScale;
        _lastWidescreen = settings.IsWidescreen16By9;
        _lastPgxp = settings.IsPgxpEnabled;
        _lastFullscreen = settings.IsFullscreen;
        _lastGlobalRules = settings.UseGlobalFormatRules;
        _lastFastLoad = settings.ShouldFastLoadData;
        _lastControlMode = settings.ControlMode;
        _lastDpadMode = settings.ControllerDpadMode;
        _lastIsoMode = settings.IsometricMoveMode;
        _lastProfileMode = settings.InputProfileMode;
        _lastBindingsDigest = bindingsDigest;
        _lastSens = settings.MouseSensitivity;
        _lastVolume = settings.MasterVolume;
        _lastHudUpdateTicks = Stopwatch.GetTimestamp();
        return true;
    }

    internal static int ComputeHudBoxWidth(int maxW, string hudText) => Math.Clamp(UiPaint.TextWidth(hudText) + 24, 240, maxW);

    private static void DrawHudBox(PixelOverlayCanvas canvas, int x, int y, int maxW, string hudText)
    {
        int boxW = ComputeHudBoxWidth(maxW, hudText);
        var box = new UiRect(x, y, boxW, HudStripHeight);
        UiPaint.BorderRect(canvas, box, UiPaint.HudFill, UiPaint.Accent);
        string fit = hudText.Length * 12 > boxW - 20 ? hudText[..Math.Max(1, (boxW - 24) / 12)] : hudText;
        UiPaint.Text(canvas, box.X + 12, box.Y + 10, fit, UiPaint.AccentText);
    }

    private static void CopyStripIntoCanvas(PixelOverlayCanvas strip, PixelOverlayCanvas target, int dstX, int dstY)
    {
        int rowBytes = strip.Width * 4;
        for (int r = 0; r < strip.Height; r++)
            Buffer.BlockCopy(strip.Buffer, r * rowBytes, target.Buffer, ((dstY + r) * target.Width + dstX) * 4, rowBytes);
    }

    private static int ComputeBindingsDigest(SlideOverSettingsDrawer drawer, RecompSettings s)
    {
        var hc = new HashCode();
        hc.Add(s.InputProfileMode);
        if (s.KeyBindings is not null)
        {
            foreach (Core.Input.BindableAction a in AllBindableActions)
                hc.Add(s.KeyBindings.GetScancode(a));
        }
        foreach (Core.Input.BindableAction a in AllBindableActions)
            hc.Add(drawer.GlobalProfile.KeyBindings.GetScancode(a));
        if (drawer.ModuleProfile is not null)
        {
            foreach (GameControlOptionDescriptor opt in drawer.ModuleProfile.ControlOptions)
                hc.Add(s.GetModuleOption(opt));
            foreach (GameActionDescriptor d in drawer.ModuleProfile.ModernActions)
            {
                foreach (int code in s.GetModernActionBindings(d)) hc.Add(code);
                foreach (int code in s.GetModernControllerBindings(d)) hc.Add(code);
            }
        }
        return hc.ToHashCode();
    }

    /// <summary>Composites the game RGBA image (4:3 or 16:9) with the transparent UI canvas into <see cref="CompositePixels"/>.</summary>
    public void Composite(ReadOnlySpan<byte> gameRgba, int gw, int gh, RecompSettings settings) =>
        Composite(gameRgba, gw, gh, settings, settings.IsWidescreen16By9);

    public void Composite(ReadOnlySpan<byte> gameRgba, int gw, int gh, RecompSettings settings, bool effectiveWidescreen)
    {
        for (int i = 0; i < _compositeBuf.Length; i += 4)
        {
            _compositeBuf[i] = UiPaint.Background.R;
            _compositeBuf[i + 1] = UiPaint.Background.G;
            _compositeBuf[i + 2] = UiPaint.Background.B;
            _compositeBuf[i + 3] = 255;
        }

        if (gw > 0 && gh > 0 && gameRgba.Length >= gw * gh * 4)
        {
            SdlFRect dst = ComputeGameDstRect(OverlayWidth, OverlayHeight, effectiveWidescreen);
            int dx0 = Math.Clamp((int)MathF.Round(dst.X), 0, OverlayWidth);
            int dy0 = Math.Clamp((int)MathF.Round(dst.Y), 0, OverlayHeight);
            int dw = Math.Max(1, Math.Min(OverlayWidth - dx0, (int)MathF.Round(dst.W)));
            int dh = Math.Max(1, Math.Min(OverlayHeight - dy0, (int)MathF.Round(dst.H)));

            for (int y = 0; y < dh; y++)
            {
                int sy = Math.Clamp(y * gh / dh, 0, gh - 1);
                int dstRow = ((dy0 + y) * OverlayWidth + dx0) * 4;
                int srcRow = sy * gw * 4;
                for (int x = 0; x < dw; x++)
                {
                    int sx = Math.Clamp(x * gw / dw, 0, gw - 1) * 4;
                    int d = dstRow + (x * 4);
                    int s = srcRow + sx;
                    _compositeBuf[d] = gameRgba[s];
                    _compositeBuf[d + 1] = gameRgba[s + 1];
                    _compositeBuf[d + 2] = gameRgba[s + 2];
                    _compositeBuf[d + 3] = 255;
                }
            }
        }

        byte[] ui = _uiCanvas.Buffer;
        for (int i = 0; i < _compositeBuf.Length; i += 4)
        {
            byte a = ui[i + 3];
            if (a == 0) continue;
            if (a == 255)
            {
                _compositeBuf[i] = ui[i];
                _compositeBuf[i + 1] = ui[i + 1];
                _compositeBuf[i + 2] = ui[i + 2];
                continue;
            }
            int inv = 255 - a;
            _compositeBuf[i] = (byte)(((ui[i] * a) + (_compositeBuf[i] * inv)) / 255);
            _compositeBuf[i + 1] = (byte)(((ui[i + 1] * a) + (_compositeBuf[i + 1] * inv)) / 255);
            _compositeBuf[i + 2] = (byte)(((ui[i + 2] * a) + (_compositeBuf[i + 2] * inv)) / 255);
        }
    }

    public void SaveCompositePng(string path) =>
        RgbaPngWriter.WritePng(path, OverlayWidth, OverlayHeight, _compositeBuf);

    public static SdlFRect ComputeGameDstRect(int winW, int winH, RecompSettings settings, IReStationCpu? cpu, IRecompWidescreenProvider? widescreenProvider)
    {
        ArgumentNullException.ThrowIfNull(settings);
        bool wide = settings.IsWidescreen16By9 && (widescreenProvider is null || cpu is null || widescreenProvider.IsActiveTrueWidescreenSupported(cpu));
        return ComputeGameDstRect(winW, winH, wide);
    }

    public static SdlFRect ComputeGameDstRect(int winW, int winH, bool widescreen16By9)
    {
        float w = Math.Max(1, winW), h = Math.Max(1, winH);
        if (widescreen16By9) return new SdlFRect { X = 0f, Y = 0f, W = w, H = h };
        float targetW = h * (4.0f / 3.0f);
        if (targetW <= w) return new SdlFRect { X = (w - targetW) * 0.5f, Y = 0f, W = targetW, H = h };
        float targetH = w * (3.0f / 4.0f);
        return new SdlFRect { X = 0f, Y = (h - targetH) * 0.5f, W = w, H = targetH };
    }

    public static (int X, int Y) WindowToOverlayCoords(float mx, float my, int winW, int winH) =>
        (Math.Clamp((int)(mx * OverlayWidth / Math.Max(1, winW)), 0, OverlayWidth - 1),
         Math.Clamp((int)(my * OverlayHeight / Math.Max(1, winH)), 0, OverlayHeight - 1));
}
