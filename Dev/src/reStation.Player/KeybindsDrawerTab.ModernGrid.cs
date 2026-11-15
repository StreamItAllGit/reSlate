// @module: reStation.Player.KeybindsDrawerTab.ModernGrid
// @purpose: Scrollable 5-column Modern PC binding table (Action | 1st | 2nd | 3rd | Gamepad) with slot selection and right-click unbind.
// @depends-on: Foundation.Core, reStation.Contracts, reStation.Core, reStation.Core.Input
// @exports: KeybindsDrawerTab (partial)
// @owner: devv-player-ui (T6)

using Foundation.Core;
using reStation.Contracts;
using reStation.Core;
using reStation.Core.Input;

namespace reStation.Player;

internal sealed partial class KeybindsDrawerTab
{
    private int RenderModernTable(
        PixelOverlayCanvas canvas,
        int x,
        int gridY,
        int w,
        RecompSettings settings,
        IModuleInputProfileProvider moduleProfile,
        List<DrawerHitBox> hitBoxes)
    {
        IReadOnlyList<GameActionDescriptor> actions = moduleProfile.ModernActions;
        int total = actions.Count;
        int maxOffset = Math.Max(0, total - VisibleModernRows);
        ScrollRowOffset = Math.Clamp(ScrollRowOffset, 0, maxOffset);

        const int slotW = 96, gap = 5, scrollBtnW = 26, rowH = 35, rowGap = 4;
        int scrollArea = (scrollBtnW * 2) + 4;
        int actW = Math.Max(120, w - (4 * (slotW + gap)) - scrollArea);
        int slotsStartX = x + actW + gap;

        int endRow = Math.Min(total, ScrollRowOffset + VisibleModernRows);
        string rangeLabel = total == 0 ? "Action (0)" : $"Action {ScrollRowOffset + 1}-{endRow}/{total}";
        UiPaint.Text(canvas, x + 4, gridY + 7, FitText(rangeLabel, Math.Max(6, (actW - 4) / 12)), UiPaint.AccentText);

        string[] headers = { "1st", "2nd", "3rd", "Gamepad" };
        for (int s = 0; s < 4; s++)
        {
            int sx = slotsStartX + (s * (slotW + gap));
            UiPaint.TextCentered(canvas, new UiRect(sx, gridY, slotW, 28), headers[s], UiPaint.Muted);
        }

        int scrollX = x + w - scrollArea;
        var upBtn = new UiRect(scrollX, gridY + 1, scrollBtnW, 26);
        var dnBtn = new UiRect(scrollX + scrollBtnW + 4, gridY + 1, scrollBtnW, 26);
        UiPaint.Pill(canvas, upBtn, "^", ScrollRowOffset > 0);
        UiPaint.Pill(canvas, dnBtn, "v", ScrollRowOffset < maxOffset);
        hitBoxes.Add(new DrawerHitBox(upBtn, _ => ScrollBy(-1, total), DrawerAction.MarkDirty));
        hitBoxes.Add(new DrawerHitBox(dnBtn, _ => ScrollBy(1, total), DrawerAction.MarkDirty));

        int rowsStartY = gridY + 32;
        int visible = Math.Min(VisibleModernRows, total - ScrollRowOffset);
        for (int r = 0; r < visible; r++)
        {
            GameActionDescriptor desc = actions[ScrollRowOffset + r];
            int ry = rowsStartY + (r * (rowH + rowGap));
            var rowRect = new UiRect(x, ry, actW, rowH);
            bool anySlotActive = string.Equals(ListeningModernActionId, desc.ActionId, StringComparison.OrdinalIgnoreCase);

            UiPaint.BorderRect(canvas, rowRect, anySlotActive ? new RgbaColor(0x24, 0x20, 0x14) : UiPaint.Panel, anySlotActive ? UiPaint.Warn : UiPaint.Border);
            UiPaint.Text(canvas, x + 8, ry + 10, FitText(desc.Label, Math.Max(6, (actW - 14) / 12)), UiPaint.TextPrimary);

            IReadOnlyList<int> kbm = settings.GetModernActionBindings(desc);
            IReadOnlyList<int> pad = settings.GetModernControllerBindings(desc);
            for (int slotIdx = 0; slotIdx < 4; slotIdx++)
            {
                int sx = slotsStartX + (slotIdx * (slotW + gap));
                var pillRect = new UiRect(sx, ry + 2, slotW, rowH - 4);
                bool active = anySlotActive && ListeningSlotIndex == slotIdx;
                int boundCode = slotIdx < 3
                    ? (slotIdx < kbm.Count ? kbm[slotIdx] : 0)
                    : (pad.Count > 0 ? pad[0] : 0);
                string cellText = active ? "..." : FitText(FormatScancode(boundCode), Math.Max(6, (slotW - 6) / 12));

                UiPaint.Pill(canvas, pillRect, cellText, active);
                int capturedSlot = slotIdx;
                Action<RecompSettings>? rightClear = capturedSlot is >= 1 and <= 3
                    ? st => { st.ClearModernActionSlot(desc, capturedSlot); ClearListening(); }
                    : null;
                hitBoxes.Add(new DrawerHitBox(pillRect, _ =>
                {
                    ListeningAction = null;
                    ListeningModernActionId = desc.ActionId;
                    ListeningSlotIndex = capturedSlot;
                    ListeningLabel = desc.Label;
                }, DrawerAction.MarkDirty, rightClear));
            }
        }

        return rowsStartY + (VisibleModernRows * (rowH + rowGap)) + 6;
    }
}
