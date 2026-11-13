// @module: reStation.Player.UiPaint
// @purpose: Dark-slate (#0E0E10) Standard 09 widget painter over Foundation.Core.PixelOverlayCanvas: pills, toggle rows, panels, centered and em-dash-safe text.
// @depends-on: Foundation.Core
// @exports: UiRect, UiPaint
// @owner: devv-player-ui (T6)

using Foundation.Core;

namespace reStation.Player;

/// <summary>Integer UI rectangle in 1280x720 overlay space.</summary>
internal readonly record struct UiRect(int X, int Y, int W, int H)
{
    public bool Contains(int px, int py) => px >= X && py >= Y && px < X + W && py < Y + H;
}

/// <summary>Shared palette + primitives so the gate, drawer, HUD, and visible-test showcase look identical.</summary>
internal static class UiPaint
{
    public const int TextScale = 2;
    public const int GlyphAdvance = 6;

    public static readonly RgbaColor Background = new(0x0E, 0x0E, 0x10);
    public static readonly RgbaColor Transparent = new(0, 0, 0, 0);
    public static readonly RgbaColor Scrim = new(0x08, 0x0A, 0x0E, 0x88);
    public static readonly RgbaColor Panel = new(0x16, 0x18, 0x1E);
    public static readonly RgbaColor PanelAlt = new(0x1C, 0x1F, 0x27);
    public static readonly RgbaColor Border = new(0x2B, 0x30, 0x3C);
    public static readonly RgbaColor Accent = new(0x4C, 0xA2, 0xD4);
    public static readonly RgbaColor AccentText = new(0xB6, 0xDF, 0xF2);
    public static readonly RgbaColor TextPrimary = new(0xE6, 0xEB, 0xF5);
    public static readonly RgbaColor Muted = new(0x9A, 0xA7, 0xBD);
    public static readonly RgbaColor Good = new(0x7B, 0xE0, 0xA4);
    public static readonly RgbaColor Warn = new(0xF7, 0xD5, 0x7E);
    public static readonly RgbaColor HudFill = new(0x14, 0x18, 0x22, 0xCC);

    public static int MeasureWidth(string text, int scale = TextScale) => text.Length == 0 ? 0 : ((text.Length * GlyphAdvance) - 1) * scale;

    public static void BorderRect(PixelOverlayCanvas c, UiRect r, RgbaColor fill, RgbaColor border, int thickness = 2)
    {
        c.FillRect(r.X, r.Y, r.W, r.H, fill);
        c.StrokeRect(r.X, r.Y, r.W, r.H, border, thickness);
    }

    /// <summary>Draws text, rendering U+2014 (em dash) as a solid bar since the 5x7 font is ASCII-only.</summary>
    public static void Text(PixelOverlayCanvas c, int x, int y, string text, RgbaColor color, int scale = TextScale)
    {
        int cursor = x;
        foreach (string part in text.Split('\u2014'))
        {
            if (cursor != x)
            {
                c.FillRect(cursor, y + (3 * scale), (GlyphAdvance * scale * 2) - scale, scale, color);
                cursor += GlyphAdvance * scale * 2;
            }
            c.DrawText(cursor, y, part, color, scale);
            cursor += part.Length * GlyphAdvance * scale;
        }
    }

    public static int TextWidth(string text, int scale = TextScale)
    {
        int dashes = text.Count(ch => ch == '\u2014');
        return MeasureWidth(text, scale) + (dashes * GlyphAdvance * scale);
    }

    public static void TextCentered(PixelOverlayCanvas c, UiRect r, string text, RgbaColor color, int scale = TextScale)
    {
        int w = TextWidth(text, scale);
        Text(c, r.X + Math.Max(0, (r.W - w) / 2), r.Y + Math.Max(0, (r.H - (7 * scale)) / 2), text, color, scale);
    }

    public static void Pill(PixelOverlayCanvas c, UiRect r, string label, bool active, bool hover = false)
    {
        RgbaColor fill = active ? new RgbaColor(0x1D, 0x3A, 0x4E) : hover ? PanelAlt : Panel;
        BorderRect(c, r, fill, active || hover ? Accent : Border);
        TextCentered(c, r, label, active ? AccentText : TextPrimary);
    }

    public static void ToggleRow(PixelOverlayCanvas c, UiRect r, string label, bool on)
    {
        BorderRect(c, r, Panel, Border);
        Text(c, r.X + 14, r.Y + ((r.H - 14) / 2), label, TextPrimary);
        var track = new UiRect(r.X + r.W - 74, r.Y + ((r.H - 24) / 2), 60, 24);
        BorderRect(c, track, on ? new RgbaColor(0x1D, 0x5A, 0x3C) : new RgbaColor(0x26, 0x29, 0x31), on ? Good : Border);
        int knobX = on ? track.X + track.W - 22 : track.X + 4;
        c.FillRect(knobX, track.Y + 4, 18, 16, on ? Good : Muted);
    }

    public static void Stepper(PixelOverlayCanvas c, UiRect minus, UiRect plus, int labelX, string label)
    {
        Pill(c, minus, "-", false);
        Pill(c, plus, "+", false);
        Text(c, labelX, minus.Y + ((minus.H - 14) / 2), label, TextPrimary);
    }
}
