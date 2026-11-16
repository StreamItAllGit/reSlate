// @module: reStation.Core.Gpu.PsxWidescreenGeometry
// @purpose: Shared Hor+ 16:9 widescreen framing math, per-frame GPU op recording, wing scissor widening, and full-screen fade expansion.
// @depends-on: System, System.Collections.Generic
// @exports: WideFrameOpKind, WideFrameOp, PsxWidescreenGeometry
// @owner: devv-gpu-gl (T4)

namespace reStation.Core.Gpu;

internal enum WideFrameOpKind : byte
{
    SetTextureWindow,
    FillRect,
    CopyVram,
    Polygon,
    Line,
    Rect
}

internal readonly struct WideFrameOp
{
    public WideFrameOpKind Kind { get; init; }
    public int A { get; init; }
    public int B { get; init; }
    public int C { get; init; }
    public int D { get; init; }
    public int E { get; init; }
    public int F { get; init; }
    public int ClipL { get; init; }
    public int ClipT { get; init; }
    public int ClipR { get; init; }
    public int ClipB { get; init; }
    public uint RawU32 { get; init; }
    public int PolyCount { get; init; }
    public PsxDrawState DrawState { get; init; }
    public PsxVertex V0 { get; init; }
    public PsxVertex V1 { get; init; }
    public PsxVertex V2 { get; init; }
    public PsxVertex V3 { get; init; }
    public PsxRect Rect { get; init; }
}

internal static class PsxWidescreenGeometry
{
    /// <summary>Computes even-aligned Hor+ 16:9 pixel width and centered 4:3 left padding while preserving 1:1 square pixels.</summary>
    public static void ComputeDimensions(int dispW, int dispH, out int wideW, out int leftPad)
    {
        int dw = Math.Max(1, dispW);
        int dh = Math.Max(1, dispH);
        int target = (int)Math.Round(dh * (16.0 / 9.0));
        wideW = Math.Max(dw, (target + 1) & ~1);
        leftPad = (wideW - dw) / 2;
    }

    public static bool OverlapsDisplay(int cl, int ct, int cr, int cb, int dispX, int dispY, int dispW, int dispH) =>
        cb >= dispY && ct < dispY + dispH && cr >= dispX && cl < dispX + dispW;

    public static bool IsFullWidthFillForDisplay(in WideFrameOp op, int dispX, int dispY, int dispW, int dispH) =>
        op.Kind == WideFrameOpKind.FillRect
        && op.D >= dispH - 4 && op.C >= dispW - 4
        && op.B <= dispY + 2 && op.B + op.D >= dispY + dispH - 2
        && op.A <= dispX + 2 && op.A + op.C >= dispX + dispW - 2;

    public static bool TryComputeWideFillRect(
        in WideFrameOp op, int dispX, int dispY, int dispW, int dispH, int wideW, int leftPad,
        out int wx, out int wy, out int ww, out int wh)
    {
        int y0 = Math.Clamp(op.B - dispY, 0, dispH);
        int y1 = Math.Clamp(op.B + op.D - dispY, 0, dispH);
        if (y1 <= y0 || op.A + op.C <= dispX || op.A >= dispX + dispW)
        {
            wx = wy = ww = wh = 0;
            return false;
        }
        bool fullWidth = op.A <= dispX + 2 && op.A + op.C >= dispX + dispW - 2;
        if (fullWidth)
        {
            wx = 0;
            ww = wideW;
        }
        else
        {
            int x0 = Math.Clamp(op.A - dispX + leftPad, 0, wideW);
            int x1 = Math.Clamp(op.A + op.C - dispX + leftPad, 0, wideW);
            if (x1 <= x0) { wx = wy = ww = wh = 0; return false; }
            wx = x0;
            ww = x1 - x0;
        }
        wy = y0;
        wh = y1 - y0;
        return ww > 0 && wh > 0;
    }

    public static bool TryMapClipToWide(
        int cl, int ct, int cr, int cb,
        int dispX, int dispY, int dispW, int dispH, int wideW, int leftPad,
        out int wcl, out int wct, out int wcr, out int wcb)
    {
        if (!OverlapsDisplay(cl, ct, cr, cb, dispX, dispY, dispW, dispH))
        {
            wcl = wct = wcr = wcb = 0;
            return false;
        }
        bool fullWidthClip = cl <= dispX + 1 && cr >= dispX + dispW - 2;
        wcl = fullWidthClip ? 0 : Math.Clamp(cl - dispX + leftPad, 0, wideW - 1);
        wcr = fullWidthClip ? wideW - 1 : Math.Clamp(cr - dispX + leftPad, 0, wideW - 1);
        wct = Math.Clamp(ct - dispY, 0, dispH - 1);
        wcb = Math.Clamp(cb - dispY, 0, dispH - 1);
        return wcl <= wcr && wct <= wcb;
    }

    public static PsxRect MapRectToWide(in PsxRect r, in PsxDrawState st, int dispX, int dispY, int dispW, int dispH, int wideW, int leftPad)
    {
        bool fullScreenUntexturedFade = !st.Textured
            && r.X <= dispX + 2 && r.X + r.Width >= dispX + dispW - 2
            && r.Y <= dispY + 2 && r.Y + r.Height >= dispY + dispH - 2;
        if (fullScreenUntexturedFade)
            return r with { X = 0, Y = r.Y - dispY, Width = wideW };
        return r with { X = r.X - dispX + leftPad, Y = r.Y - dispY };
    }

    public static PsxVertex ShiftVertex(in PsxVertex v, int dispX, int dispY, int leftPad) =>
        v with { X = v.X - dispX + leftPad, Y = v.Y - dispY };

    public static void ExpandIfFullScreenFadeQuad(
        in PsxDrawState st, int vertCount, int dispW, int dispH, int wideW, int leftPad,
        ref PsxVertex v0, ref PsxVertex v1, ref PsxVertex v2, ref PsxVertex v3)
    {
        if (st.Textured || vertCount < 3) return;
        float maxW = Math.Max(Math.Max(v0.W, v1.W), vertCount >= 4 ? Math.Max(v2.W, v3.W) : v2.W);
        if (maxW > 1.05f) return;
        float minX = Math.Min(Math.Min(v0.X, v1.X), vertCount >= 4 ? Math.Min(v2.X, v3.X) : v2.X);
        float maxX = Math.Max(Math.Max(v0.X, v1.X), vertCount >= 4 ? Math.Max(v2.X, v3.X) : v2.X);
        float minY = Math.Min(Math.Min(v0.Y, v1.Y), vertCount >= 4 ? Math.Min(v2.Y, v3.Y) : v2.Y);
        float maxY = Math.Max(Math.Max(v0.Y, v1.Y), vertCount >= 4 ? Math.Max(v2.Y, v3.Y) : v2.Y);
        if (minX > leftPad + 2f || maxX < leftPad + dispW - 2f || minY > 2f || maxY < dispH - 2f) return;

        v0 = ExpandEdgeX(v0, dispW, wideW, leftPad);
        v1 = ExpandEdgeX(v1, dispW, wideW, leftPad);
        v2 = ExpandEdgeX(v2, dispW, wideW, leftPad);
        if (vertCount >= 4) v3 = ExpandEdgeX(v3, dispW, wideW, leftPad);
    }

    private static PsxVertex ExpandEdgeX(in PsxVertex v, int dispW, int wideW, int leftPad)
    {
        if (v.X <= leftPad + 2f) return v with { X = 0f };
        if (v.X >= leftPad + dispW - 2f) return v with { X = wideW };
        return v;
    }
}
