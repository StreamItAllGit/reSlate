// @module: reStation.Player.Program.EventPump
// @purpose: SDL3 event polling loop (keyboard repeat filter, mouse wheel/look, gamepad/trigger capture, F1/F3/F5/F9/Esc hotkeys, drag-drop) extracted from Program.MediaGate.
// @depends-on: System.IO, Foundation.Core, reStation.Contracts, reStation.Core, reStation.Core.Cpu, reStation.Core.Input
// @exports: Program (EventPump partial)

using Foundation.Core;
using reStation.Contracts;
using reStation.Core;
using reStation.Core.Cpu;
using reStation.Core.Input;

namespace reStation.Player;

internal static unsafe partial class Program
{
    private static void PumpEvents(HostVideoSurface video, ReStationHost host, PsxCpu cpu, SlideOverSettingsDrawer drawer,
        UiOverlayPresenter overlay, RecompSettings settings, AppEnvironment env, HighResFramePacer pacer, ReStationLogger logger, LoopState st)
    {
        if (!video.SdlAvailable) return;
        byte* ev = stackalloc byte[128];
        while (Sdl3Native.SDL_PollEvent(ev) != 0)
        {
            ProcessSdlEvent(ev, video, host, cpu, drawer, overlay, settings, env, pacer, logger, st);
            if (st.QuitRequested) return;
        }
        PollControllerTriggerCapture(drawer, overlay, settings, env, st);
    }

    internal static void PumpSubTickMouseOnly(HostVideoSurface video, SlideOverSettingsDrawer drawer, LoopState st)
    {
        if (!video.SdlAvailable) return;
        Sdl3Native.SDL_PumpEvents();
        byte* ev = stackalloc byte[128];
        while (Sdl3Native.SDL_PeepEvents(ev, 1, Sdl3Native.EventActionGetEvent, Sdl3Native.EventMouseMotion, Sdl3Native.EventMouseMotion) > 0)
        {
            if (!drawer.IsOpen && st.MouseCaptured)
            {
                (float dx, float dy) = Sdl3Native.EventMouseRelative(ev);
                st.MouseDx += dx;
                st.MouseDy += dy;
            }
        }
    }

    /// <summary>Safe span wrapper forwarding raw SDL event bytes into <see cref="ProcessSdlEvent"/> for isolated unit testing.</summary>
    internal static void ProcessSdlEventSpan(ReadOnlySpan<byte> evBytes, HostVideoSurface video, ReStationHost host, PsxCpu cpu,
        SlideOverSettingsDrawer drawer, UiOverlayPresenter overlay, RecompSettings settings, AppEnvironment env,
        HighResFramePacer pacer, ReStationLogger logger, LoopState st)
    {
        if (evBytes.Length < 40) return;
        fixed (byte* ev = evBytes)
        {
            ProcessSdlEvent(ev, video, host, cpu, drawer, overlay, settings, env, pacer, logger, st);
        }
    }

    internal static void ProcessSdlEvent(byte* ev, HostVideoSurface video, ReStationHost host, PsxCpu cpu, SlideOverSettingsDrawer drawer,
        UiOverlayPresenter overlay, RecompSettings settings, AppEnvironment env, HighResFramePacer pacer, ReStationLogger logger, LoopState st)
    {
        uint type = Sdl3Native.EventType(ev);
        if (type == Sdl3Native.EventQuit) { st.QuitRequested = true; return; }
        if (type == Sdl3Native.EventGamepadAdded && st.Gamepad == IntPtr.Zero) st.Gamepad = HostInputMapper.OpenFirstGamepad();
        else if (type == Sdl3Native.EventGamepadRemoved)
        {
            if (st.Gamepad != IntPtr.Zero) Sdl3Native.SDL_CloseGamepad(st.Gamepad);
            st.Gamepad = HostInputMapper.OpenFirstGamepad();
        }
        else if (type == Sdl3Native.EventGamepadButtonDown && drawer.IsOpen && drawer.ActiveTab == 2 && drawer.KeybindsTab.IsListening)
        {
            int gpCode = GamepadBindingCode.FromSdlButtonIndex(Sdl3Native.EventGamepadButton(ev));
            if (gpCode != GamepadBindingCode.None && drawer.TryHandleGamepadCapture(gpCode, settings, env.ConfigFilePath))
            {
                overlay.MarkDirty(); st.UiDirty = true;
            }
        }
        else if (type == Sdl3Native.EventMouseMotion && !drawer.IsOpen && st.MouseCaptured)
        {
            (float dx, float dy) = Sdl3Native.EventMouseRelative(ev);
            st.MouseDx += dx; st.MouseDy += dy;
        }
        else if (type == Sdl3Native.EventMouseWheel)
        {
            HandleMouseWheelEvent(Sdl3Native.EventMouseWheelY(ev), drawer, overlay, settings, env, st);
        }
        else if (type == Sdl3Native.EventKeyDown)
        {
            if (Sdl3Native.EventKeyRepeat(ev) != 0) return;
            HandleKeyDownScancode(Sdl3Native.EventScancode(ev), video, host, drawer, overlay, settings, env, st);
        }
        else if (type == Sdl3Native.EventMouseButtonDown)
        {
            byte btn = Sdl3Native.EventMouseButton(ev);
            if (drawer.IsOpen && drawer.ActiveTab == 2 && drawer.KeybindsTab.IsListening)
            {
                int captureCode = Sdl3Native.SdlButtonToBindingCode(btn);
                if (captureCode != 0 && drawer.TryHandleMouseCapture(captureCode, settings, env.ConfigFilePath))
                {
                    overlay.MarkDirty(); st.UiDirty = true; return;
                }
            }
            (int winW, int winH) = video.GetWindowSize();
            (float mx, float my) = Sdl3Native.EventMousePosition(ev);
            (int ox, int oy) = UiOverlayPresenter.WindowToOverlayCoords(mx, my, winW, winH);
            HandleOverlayPointerDown(ox, oy, btn, video, host, cpu, drawer, overlay, settings, env, pacer, logger, st);
        }
        else if (type == Sdl3Native.EventDropFile && Sdl3Native.EventDropPath(ev) is { } dropped)
        {
            AttachDiscFromPath(dropped, cpu, env, settings, host, st, logger);
        }
    }

    internal static void HandleKeyDownScancode(int sc, HostVideoSurface video, ReStationHost host,
        SlideOverSettingsDrawer drawer, UiOverlayPresenter overlay, RecompSettings settings, AppEnvironment env, LoopState st)
    {
        if (drawer.IsOpen && drawer.TryHandleKeyCapture(sc, settings, env.ConfigFilePath)) { overlay.MarkDirty(); st.UiDirty = true; return; }
        if (drawer.IsOpen && drawer.ActiveTab == 2 && !drawer.KeybindsTab.IsListening)
        {
            if (sc == Sdl3Native.ScPgUp && drawer.TryScrollKeybinds(-KeybindsDrawerTab.VisibleModernRows)) { overlay.MarkDirty(); st.UiDirty = true; return; }
            if (sc == Sdl3Native.ScPgDn && drawer.TryScrollKeybinds(KeybindsDrawerTab.VisibleModernRows)) { overlay.MarkDirty(); st.UiDirty = true; return; }
        }
        if (sc == Sdl3Native.ScF1)
        {
            drawer.IsOpen = !drawer.IsOpen;
            if (!drawer.IsOpen) { drawer.KeybindsTab.ClearListening(); st.UserReleasedMouse = false; }
            st.MouseCaptured = !drawer.IsOpen && settings.ControlMode == ControlMode.ModernWasdMouse;
            if (!st.MouseCaptured) { st.MouseDx = 0f; st.MouseDy = 0f; }
            video.SetRelativeMouse(st.MouseCaptured);
            overlay.MarkDirty(); st.UiDirty = true;
        }
        else if (sc == Sdl3Native.ScF3)
        {
            settings.IsFpsOverlayEnabled = !settings.IsFpsOverlayEnabled;
            settings.Save(env.ConfigFilePath);
            overlay.MarkDirty(); st.UiDirty = true;
        }
        else if (sc == Sdl3Native.ScEsc)
        {
            if (drawer.IsOpen)
            {
                drawer.IsOpen = false;
                drawer.KeybindsTab.ClearListening();
                st.UserReleasedMouse = false;
                overlay.MarkDirty(); st.UiDirty = true;
            }
            else if (settings.InputProfileMode == InputProfileMode.PerGameModern
                     && drawer.ModuleProfile is { } mp
                     && settings.IsBindingUsedByAnyModernAction(Sdl3Native.ScEsc, mp.ModernActions))
            {
                if ((uint)sc < (uint)st.LatchedKeys.Length) st.LatchedKeys[sc] = 1;
            }
            else if (st.MouseCaptured)
            {
                st.MouseCaptured = false; st.UserReleasedMouse = true; st.MouseDx = 0f; st.MouseDy = 0f;
                video.SetRelativeMouse(false);
            }
        }
        else if (sc == Sdl3Native.ScF5) host.RequestQuickSave(0);
        else if (sc == Sdl3Native.ScF9) host.RequestQuickLoad(0);
        else if (!drawer.IsOpen && (uint)sc < (uint)st.LatchedKeys.Length) st.LatchedKeys[sc] = 1;
    }

    internal static void HandleOverlayPointerDown(int ox, int oy, byte btn, HostVideoSurface video, ReStationHost host, PsxCpu cpu,
        SlideOverSettingsDrawer drawer, UiOverlayPresenter overlay, RecompSettings settings, AppEnvironment env, HighResFramePacer pacer,
        ReStationLogger logger, LoopState st)
    {
        bool wasOpen = drawer.IsOpen;
        bool isRightClick = btn == Sdl3Native.MouseBtnRight;
        DrawerAction act = drawer.HandleClick(ox, oy, settings, env.ConfigFilePath, isRightClick);
        if (act != DrawerAction.None)
        {
            video.Backend.ScaleFactor = settings.ResolutionScale;
            cpu.Pgxp.IsEnabled = settings.IsPgxpEnabled;
            int targetFps = ResolveHostTargetFps(settings);
            bool unlockFps = settings.FrameRateMode == FrameRateMode.Enhanced && settings.TargetFps <= 0;
            bool fpsChanged = host.TargetFps != targetFps || host.Options.UnlockFps != unlockFps;
            host.SetTargetFps(targetFps, unlockFps);
            if (fpsChanged) host.PresentationTracker?.Reset();
            pacer.TargetFps = ComputeTargetFps(settings, st.IsOffscreen);
            overlay.MarkDirty();
            st.UiDirty = true;
        }
        if (act == DrawerAction.ToggleFullscreen) video.SetFullscreen(settings.IsFullscreen);
        else if (act == DrawerAction.ToggleVsync) video.SetVsync(settings.IsVsyncEnabled);
        else if (act == DrawerAction.ToggleBackend) st.PendingBackendSwitch = settings.BackendId;
        else if (act == DrawerAction.ToggleRuntimeLogging) logger.SetEnabled(settings.ShouldEnableRuntimeLogs);
        else if (act == DrawerAction.QuickSave) host.RequestQuickSave(0);
        else if (act == DrawerAction.QuickLoad) host.RequestQuickLoad(0);
        else if (act == DrawerAction.ChangeDisc && Sdl3Native.ShowOpenFileDialog("Select SlateOne Disc Image", Sdl3Native.DiscImageFilter) is { } dp)
            AttachDiscFromPath(dp, cpu, env, settings, host, st, logger);
        else if (act == DrawerAction.ChangeModule && Sdl3Native.ShowOpenFileDialog("Select .recomp Module", Sdl3Native.ModuleFilter) is { } mp)
        {
            settings.LastModulePath = Path.GetFileName(mp);
            settings.Save(env.ConfigFilePath);
            logger.Info("MODULE", $"Selected next-launch module '{Path.GetFileName(mp)}'.");
        }
        else if (act == DrawerAction.None && !wasOpen && !drawer.IsOpen)
        {
            if (btn is >= 1 and <= 5) st.LatchedMouseMask |= 1u << (btn - 1);
            if (!isRightClick && btn == Sdl3Native.MouseBtnLeft && !st.MouseCaptured)
            {
                st.UserReleasedMouse = false; st.MouseCaptured = true;
                video.SetRelativeMouse(true);
            }
        }
        if (drawer.IsOpen) { st.MouseCaptured = false; st.MouseDx = 0f; st.MouseDy = 0f; video.SetRelativeMouse(false); }
    }

    private static void HandleMouseWheelEvent(float wy, SlideOverSettingsDrawer drawer, UiOverlayPresenter overlay, RecompSettings settings, AppEnvironment env, LoopState st)
    {
        if (drawer.IsOpen && drawer.ActiveTab == 2)
        {
            if (drawer.KeybindsTab.IsListening)
            {
                int wheelCode = wy > 0f ? KeyBindingsConfig.MouseWheelUp : wy < 0f ? KeyBindingsConfig.MouseWheelDown : 0;
                if (wheelCode != 0 && drawer.TryHandleMouseCapture(wheelCode, settings, env.ConfigFilePath)) { overlay.MarkDirty(); st.UiDirty = true; }
            }
            else if (wy != 0f && drawer.TryScrollKeybinds(wy > 0f ? -1 : 1)) { overlay.MarkDirty(); st.UiDirty = true; }
        }
        else if (!drawer.IsOpen)
        {
            if (wy > 0f) st.WheelUp = true; else if (wy < 0f) st.WheelDown = true;
        }
    }

    private static void PollControllerTriggerCapture(
        SlideOverSettingsDrawer drawer, UiOverlayPresenter overlay, RecompSettings settings, AppEnvironment env, LoopState st)
    {
        uint trig = st.Gamepad != IntPtr.Zero
            ? ((Sdl3Native.SDL_GetGamepadAxis(st.Gamepad, 4) >= 13107 ? 1u : 0u)
               | (Sdl3Native.SDL_GetGamepadAxis(st.Gamepad, 5) >= 13107 ? 2u : 0u))
            : 0u;
        uint edge = trig & ~st.PrevCaptureTriggerMask;
        st.PrevCaptureTriggerMask = trig;
        bool padListening = (!string.IsNullOrWhiteSpace(drawer.KeybindsTab.ListeningModernActionId) && drawer.KeybindsTab.ListeningSlotIndex == 3)
            || (drawer.KeybindsTab.ListeningAction.HasValue && drawer.KeybindsTab.ListeningSlotIndex == 1);
        if (!drawer.IsOpen || drawer.ActiveTab != 2 || !drawer.KeybindsTab.IsListening || !padListening)
            return;
        if ((edge & 1u) != 0 && drawer.TryHandleGamepadCapture(GamepadBindingCode.LT, settings, env.ConfigFilePath))
        {
            overlay.MarkDirty(); st.UiDirty = true;
        }
        else if ((edge & 2u) != 0 && drawer.TryHandleGamepadCapture(GamepadBindingCode.RT, settings, env.ConfigFilePath))
        {
            overlay.MarkDirty(); st.UiDirty = true;
        }
    }
}
