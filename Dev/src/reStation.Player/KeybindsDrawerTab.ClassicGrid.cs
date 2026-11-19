// @module: reStation.Player.KeybindsDrawerTab.ClassicGrid
// @purpose: Shared 2-column 1:1 classic keybinds grid (Global 1:1 and Game 1:1) with side-by-side KBM + Gamepad pills and right-click unbind.
// @depends-on: Foundation.Core, reStation.Contracts, reStation.Core, reStation.Core.Input
// @exports: KeybindsDrawerTab (partial)

using Foundation.Core;
using reStation.Contracts;
using reStation.Core;
using reStation.Core.Input;

namespace reStation.Player;

internal sealed partial class KeybindsDrawerTab
{
    private int RenderClassicGrid(
        PixelOverlayCanvas canvas,
        int x,
        int gridY,
        int w,
        RecompSettings settings,
        GlobalControllerProfile globalProfile,
        string globalProfilePath,
        List<DrawerHitBox> hitBoxes)
    {
        const int cols = 2, colGap = 12, rowH = 40, rowGap = 5, pillW = 88, pillGap = 4;
        int colW = (w - colGap) / cols;
        bool isGlobal = settings.InputProfileMode == InputProfileMode.GlobalController;
        KeyBindingsConfig kb = isGlobal
            ? globalProfile.KeyBindings
            : (settings.KeyBindings ?? new KeyBindingsConfig());

        for (int i = 0; i < Actions.Length; i++)
        {
            BindableAction action = Actions[i];
            int cx = x + (i % cols) * (colW + colGap);
            int cy = gridY + (i / cols) * (rowH + rowGap);
            var cell = new UiRect(cx, cy, colW, rowH);
            bool activeKbm = ListeningAction == action && ListeningSlotIndex == 0;
            bool activePad = ListeningAction == action && ListeningSlotIndex == 1;
            bool anyActive = activeKbm || activePad;
            string label = KeyBindingsConfig.GetActionLabel(action);

            UiPaint.BorderRect(canvas, cell, anyActive ? new RgbaColor(0x24, 0x20, 0x14) : UiPaint.Panel, anyActive ? UiPaint.Warn : UiPaint.Border);
            UiPaint.Text(canvas, cx + 8, cy + 13, FitText(label, 12), UiPaint.TextPrimary);

            int padX = cx + colW - pillW - 6;
            int kbmX = padX - pillW - pillGap;
            var kbmRect = new UiRect(kbmX, cy + 5, pillW, 30);
            var padRect = new UiRect(padX, cy + 5, pillW, 30);

            string kbmText = activeKbm ? "..." : FitText(FormatScancode(kb.GetScancode(action)), 7);
            string padText = activePad ? "..." : FitText(FormatScancode(kb.GetControllerCode(action)), 7);

            UiPaint.Pill(canvas, kbmRect, kbmText, activeKbm);
            UiPaint.Pill(canvas, padRect, padText, activePad);

            Action<RecompSettings> rightClearKbm = st =>
            {
                KeyBindingsConfig targetKb = isGlobal ? globalProfile.KeyBindings : (st.KeyBindings ??= new KeyBindingsConfig());
                targetKb.SetScancode(action, 0);
                if (isGlobal && !string.IsNullOrWhiteSpace(globalProfilePath)) globalProfile.Save(globalProfilePath);
                ClearListening();
            };
            Action<RecompSettings> rightClearPad = st =>
            {
                KeyBindingsConfig targetKb = isGlobal ? globalProfile.KeyBindings : (st.KeyBindings ??= new KeyBindingsConfig());
                targetKb.SetControllerCode(action, 0);
                if (isGlobal && !string.IsNullOrWhiteSpace(globalProfilePath)) globalProfile.Save(globalProfilePath);
                ClearListening();
            };

            hitBoxes.Add(new DrawerHitBox(kbmRect, _ =>
            {
                ListeningModernActionId = null;
                ListeningAction = action;
                ListeningSlotIndex = 0;
                ListeningLabel = label;
            }, DrawerAction.MarkDirty, rightClearKbm, ControlId: $"Keybinds_Classic_{action}_Kbm", Label: $"{label} KBM"));

            hitBoxes.Add(new DrawerHitBox(padRect, _ =>
            {
                ListeningModernActionId = null;
                ListeningAction = action;
                ListeningSlotIndex = 1;
                ListeningLabel = label;
            }, DrawerAction.MarkDirty, rightClearPad, ControlId: $"Keybinds_Classic_{action}_Pad", Label: $"{label} Gamepad"));
        }

        int rows = (Actions.Length + cols - 1) / cols;
        return gridY + (rows * (rowH + rowGap)) + 6;
    }
}
