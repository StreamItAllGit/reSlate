// @module: reStation.Player.HostContractBridge
// @purpose: Projects SlideOverSettingsDrawer, UiOverlayPresenter, and SDL3 input/framebuffer state onto reEngine.Host.Contracts (IHostAutomationTarget, HostInputFrame, HostWidgetBounds, F3HudTelemetry).
// @depends-on: System.Drawing, reEngine.Host.Contracts, reEngine.Host.Input, reEngine.Host.Ui, reStation.Contracts, reStation.Core
// @exports: HostContractBridge

using System.Drawing;
using reEngine.Host.Contracts;
using reEngine.Host.Input;
using reEngine.Host.Ui;
using reStation.Contracts;
using reStation.Core;
using ReEngineHostInputMapper = reEngine.Host.Input.HostInputMapper;

namespace reStation.Player;

/// <summary>Bridges reSlate Player UI/input/framebuffer state onto shared <c>reEngine.Host.Contracts</c> DTOs.</summary>
internal sealed class HostContractBridge : IHostAutomationTarget
{
    private const int MaxQueueSize = 256;

    private readonly object _sync = new();
    private readonly ReEngineHostInputMapper _syntheticMapper = new();
    private readonly Queue<(int X, int Y, string? TargetId, bool RightClick)> _pendingClicks = new();
    private readonly Queue<string> _pendingKeys = new();
    private IReadOnlyList<HostWidgetBounds> _widgets = Array.Empty<HostWidgetBounds>();
    private byte[] _framebufferRgba = Array.Empty<byte>();
    private int _fbWidth;
    private int _fbHeight;
#if DEBUG || RESTATION_TEST_AUTOMATION
    internal DebugAutomationDiagnostics? Diagnostics { get; set; }
#endif

    public HostRenderBackendKind ActiveBackendKind { get; private set; } = HostRenderBackendKind.OpenGl;
    public HostDrawerTabId ActiveDrawerTab { get; private set; } = HostDrawerTabId.Video;
    public F3HudTelemetry? LastHudTelemetry { get; private set; }
    public HostInputFrame LastSyntheticInputFrame { get; private set; }

    public static HostRenderBackendKind MapBackendKind(string? backendId)
    {
        string normalized = (backendId ?? string.Empty).Trim();
        if (string.Equals(normalized, PsxRenderBackendIds.Software, StringComparison.OrdinalIgnoreCase)) return HostRenderBackendKind.Software;
        if (string.Equals(normalized, PsxRenderBackendIds.Vulkan, StringComparison.OrdinalIgnoreCase)) return HostRenderBackendKind.Vulkan;
        if (string.Equals(normalized, PsxRenderBackendIds.ParallelCpu, StringComparison.OrdinalIgnoreCase)) return HostRenderBackendKind.ParallelCpu;
        return HostRenderBackendKind.OpenGl;
    }

    public static HostDrawerTabId MapDrawerTab(int activeTab) => activeTab switch
    {
        1 => HostDrawerTabId.Controls,
        2 => HostDrawerTabId.Keybinds,
        3 => HostDrawerTabId.System,
        4 => HostDrawerTabId.DiscMedia,
        _ => HostDrawerTabId.Video
    };

    public static F3HudTelemetry ToF3HudTelemetry(PsxTelemetrySnapshot snapshot, string executionTierMode)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        string tier = string.IsNullOrWhiteSpace(executionTierMode) ? ReStationExecutionTierModes.Default : executionTierMode;
        return new F3HudTelemetry(
            snapshot.CurrentFps,
            snapshot.P99FrameMs,
            snapshot.BackendId,
            snapshot.ResolutionScale,
            tier,
            snapshot.AudioFillRatio);
    }

    public static IReadOnlyList<HostWidgetBounds> BuildDrawerWidgets(
        SlideOverSettingsDrawer drawer,
        RecompSettings settings,
        UiOverlayPresenter overlay)
    {
        ArgumentNullException.ThrowIfNull(drawer);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(overlay);

        var list = new List<HostWidgetBounds>(drawer.HitBoxes.Count + 4)
        {
            new("HostViewport", "Panel", "reSlate Host Viewport", new Rectangle(0, 0, UiOverlayPresenter.OverlayWidth, UiOverlayPresenter.OverlayHeight), true)
        };
        if (drawer.IsOpen && drawer.CurrentDrawerRect.W > 0 && drawer.CurrentDrawerRect.H > 0)
        {
            UiRect dr = drawer.CurrentDrawerRect;
            list.Add(new HostWidgetBounds("F1Drawer", "Panel", $"F1 Drawer ({MapDrawerTab(drawer.ActiveTab)})", new Rectangle(dr.X, dr.Y, dr.W, dr.H), true));
        }

        for (int i = 0; i < drawer.HitBoxes.Count; i++)
        {
            DrawerHitBox hb = drawer.HitBoxes[i];
            string id = string.IsNullOrWhiteSpace(hb.ControlId) ? $"Drawer_Control_{i}" : hb.ControlId;
            string type = string.IsNullOrWhiteSpace(hb.ControlType) ? "Button" : hb.ControlType;
            string label = string.IsNullOrWhiteSpace(hb.Label) ? id : hb.Label;
            list.Add(new HostWidgetBounds(id, type, label, new Rectangle(hb.Rect.X, hb.Rect.Y, hb.Rect.W, hb.Rect.H), true));
        }

        if (settings.IsFpsOverlayEnabled && overlay.CurrentHudRect.W > 0 && overlay.CurrentHudRect.H > 0)
        {
            UiRect hr = overlay.CurrentHudRect;
            list.Add(new HostWidgetBounds("F3Hud", "Label", $"F3 Telemetry ({hr.W}x{hr.H})", new Rectangle(hr.X, hr.Y, hr.W, hr.H), true));
        }
        return list;
    }

    public static IReadOnlyList<HostWidgetBounds> BuildMediaGateWidgets(FirstRunMediaGateView view, bool hasModule)
    {
        ArgumentNullException.ThrowIfNull(view);
        var bounds = view.GetInteractiveBounds(hasModule);
        var list = new List<HostWidgetBounds>(bounds.Count + 1)
        {
            new("HostViewport", "Panel", "reSlate Media Gate", new Rectangle(0, 0, FirstRunMediaGateView.CanvasWidth, FirstRunMediaGateView.CanvasHeight), true)
        };
        foreach (var item in bounds)
        {
            list.Add(new HostWidgetBounds(item.ControlId, item.ControlType, item.Label, new Rectangle(item.Rect.X, item.Rect.Y, item.Rect.W, item.Rect.H), true));
        }
        return list;
    }

    public void SyncWidgets(
        SlideOverSettingsDrawer drawer,
        RecompSettings settings,
        UiOverlayPresenter overlay,
        PsxTelemetrySnapshot telemetry,
        string executionTierMode)
    {
        ActiveBackendKind = MapBackendKind(settings.BackendId);
        ActiveDrawerTab = MapDrawerTab(drawer.ActiveTab);
        LastHudTelemetry = ToF3HudTelemetry(telemetry, executionTierMode);
        IReadOnlyList<HostWidgetBounds> next = BuildDrawerWidgets(drawer, settings, overlay);
        lock (_sync) _widgets = next;
    }

    public void SyncMediaGateWidgets(FirstRunMediaGateView view, bool hasModule, string backendId, string executionTierMode)
    {
        ActiveBackendKind = MapBackendKind(backendId);
        LastHudTelemetry = ToF3HudTelemetry(PsxTelemetrySnapshot.Empty with { BackendId = backendId }, executionTierMode);
        IReadOnlyList<HostWidgetBounds> next = BuildMediaGateWidgets(view, hasModule);
        lock (_sync) _widgets = next;
    }

    public void PublishFramebufferRgba(int width, int height, ReadOnlySpan<byte> rgba)
    {
        int expected = checked(width * height * 4);
        if (width <= 0 || height <= 0 || rgba.Length < expected) return;
        lock (_sync)
        {
            if (_framebufferRgba.Length != expected) _framebufferRgba = new byte[expected];
            rgba[..expected].CopyTo(_framebufferRgba);
            _fbWidth = width;
            _fbHeight = height;
        }
    }

    public IReadOnlyList<HostWidgetBounds> GetOverlayWidgets()
    {
        lock (_sync) return _widgets;
    }

    public void EnqueuePointerClick(int x, int y, string? targetId)
    {
        lock (_sync)
        {
            if (_pendingClicks.Count < MaxQueueSize) _pendingClicks.Enqueue((x, y, targetId, false));
        }
    }

    public void EnqueuePointerRightClick(int x, int y, string? targetId)
    {
        lock (_sync)
        {
            if (_pendingClicks.Count < MaxQueueSize) _pendingClicks.Enqueue((x, y, targetId, true));
        }
    }

    public void EnqueueKeyPress(string key, IReadOnlyList<string>? modifiers = null)
    {
        _ = modifiers;
        if (string.IsNullOrWhiteSpace(key)) return;
        lock (_sync)
        {
            if (_pendingKeys.Count < MaxQueueSize) _pendingKeys.Enqueue(key.Trim());
        }
    }

    public bool TryCaptureFramebufferPng(out int width, out int height, out byte[] rgbaBytes)
    {
        lock (_sync)
        {
            if (_fbWidth <= 0 || _fbHeight <= 0 || _framebufferRgba.Length == 0)
            {
                width = 0; height = 0; rgbaBytes = Array.Empty<byte>();
                return false;
            }
            width = _fbWidth;
            height = _fbHeight;
            rgbaBytes = new byte[_framebufferRgba.Length];
            Buffer.BlockCopy(_framebufferRgba, 0, rgbaBytes, 0, rgbaBytes.Length);
            return true;
        }
    }

    public void DrainCommands(Action<int, int> onPointerClick, Action<int> onSyntheticScancode) =>
        DrainCommands((x, y, _) => onPointerClick(x, y), onSyntheticScancode);

    public void DrainCommands(Action<int, int, byte> onPointerClick, Action<int> onSyntheticScancode)
    {
        ArgumentNullException.ThrowIfNull(onPointerClick);
        ArgumentNullException.ThrowIfNull(onSyntheticScancode);

        for (int i = 0; i < 32; i++)
        {
            (int x, int y, string? targetId, bool rightClick) click;
            IReadOnlyList<HostWidgetBounds> snapshot;
            lock (_sync)
            {
                if (_pendingClicks.Count == 0) break;
                click = _pendingClicks.Dequeue();
                snapshot = _widgets;
            }
            (int cx, int cy) = ResolveClickCoords(click.x, click.y, click.targetId, snapshot);
            onPointerClick(cx, cy, (byte)(click.rightClick ? Sdl3Native.MouseBtnRight : Sdl3Native.MouseBtnLeft));
        }

        for (int i = 0; i < 32; i++)
        {
            string? key;
            lock (_sync)
            {
                if (_pendingKeys.Count == 0) break;
                key = _pendingKeys.Dequeue();
            }
            if (key is null) break;
            _syntheticMapper.InjectSyntheticKey(key);
            LastSyntheticInputFrame = _syntheticMapper.BuildFrame();
            if (TryResolveSdlScancode(key, out int scancode))
            {
                _syntheticMapper.SetScancodeState(scancode, false);
                onSyntheticScancode(scancode);
            }
        }
    }

    private static (int X, int Y) ResolveClickCoords(int x, int y, string? targetId, IReadOnlyList<HostWidgetBounds> widgets)
    {
        if (!string.IsNullOrWhiteSpace(targetId))
        {
            foreach (HostWidgetBounds w in widgets)
            {
                if (string.Equals(w.ControlId, targetId, StringComparison.OrdinalIgnoreCase))
                {
                    if ((x <= 0 && y <= 0) || !w.Bounds.Contains(x, y))
                        return (w.Bounds.X + (w.Bounds.Width / 2), w.Bounds.Y + (w.Bounds.Height / 2));
                    return (x, y);
                }
            }
        }
        return (x, y);
    }

    internal static bool TryResolveSdlScancode(string? keyName, out int scancode)
    {
        if (ReEngineHostInputMapper.TryMapKeyNameToScancode(keyName, out scancode)) return true;
        if (string.IsNullOrWhiteSpace(keyName)) { scancode = 0; return false; }
        scancode = keyName.Trim().ToUpperInvariant() switch
        {
            "F5" => Sdl3Native.ScF5,
            "F9" => Sdl3Native.ScF9,
            "PGUP" or "PAGEUP" => Sdl3Native.ScPgUp,
            "PGDN" or "PAGEDOWN" => Sdl3Native.ScPgDn,
            "DELETE" or "DEL" => Sdl3Native.ScDelete,
            "BACKSPACE" or "BACK" => Sdl3Native.ScBackspace,
            _ => 0
        };
        return scancode != 0;
    }
}
