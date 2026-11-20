// @module: reStation.Core.Gpu.SoftwareCpuRasterizer.Scanline
// @purpose: Deterministic 1x-8x PGXP scanline rasterizer (triangles, lines, rects, 4/8/15-bit CLUT, GP0(E6h) mask bits, band clipping, HD pack override sampling).
// @depends-on: System
// @exports: SoftwareCpuRasterizer, HdTexOverride
// @owner: devv-gpu-backends (T3/T4)

using reStation.Core.Enhancement;
using reStation.Core.Gte;

namespace reStation.Core.Gpu;

internal readonly record struct HdTexOverride(
    byte[]? Rgba,
    int Width,
    int Height,
    int OriginTexelX,
    int OriginTexelY,
    int SpanTexelW,
    int SpanTexelH)
{
    public bool IsActive => Rgba is { Length: > 0 } && Width > 0 && Height > 0;
}

internal static class SoftwareCpuRasterizer
{
    public static void RasterizeTriangle(uint[] hiVram, ushort[] vram, int scale, in PsxVertex a, in PsxVertex b, in PsxVertex c,
        in PsxDrawState st, int clipL, int clipT, int clipR, int clipB, int winMaskX, int winMaskY, int winOffX, int winOffY,
        int surfW = PsxGpu.VramWidth, int surfH = PsxGpu.VramHeight,
        byte[]? hiMask = null, bool setMaskBit = false, bool checkMaskBit = false,
        int bandMinY = 0, int bandMaxY = int.MaxValue, in HdTexOverride hdTex = default)
    {
        if (!PsxPrimitiveDecoder.IsDrawable(a, b, c)) return;
        int hiW = surfW * scale, hiH = surfH * scale;
        float ax = a.X * scale, ay = a.Y * scale, bx = b.X * scale, by = b.Y * scale, cx = c.X * scale, cy = c.Y * scale;
        float area = Edge(ax, ay, bx, by, cx, cy);
        if (MathF.Abs(area) < 1e-4f) return;

        int minX = Math.Max(Math.Max(0, clipL * scale), (int)MathF.Floor(MathF.Min(ax, MathF.Min(bx, cx))));
        int maxX = Math.Min(Math.Min(hiW - 1, ((clipR + 1) * scale) - 1), (int)MathF.Ceiling(MathF.Max(ax, MathF.Max(bx, cx))));
        int minY = Math.Max(Math.Max(bandMinY, Math.Max(0, clipT * scale)), (int)MathF.Floor(MathF.Min(ay, MathF.Min(by, cy))));
        int maxY = Math.Min(Math.Min(bandMaxY, Math.Min(hiH - 1, ((clipB + 1) * scale) - 1)), (int)MathF.Ceiling(MathF.Max(ay, MathF.Max(by, cy))));
        if (minX > maxX || minY > maxY) return;

        float invArea = 1f / area;
        float dw0Dx = (cy - by) * invArea, dw0Dy = (bx - cx) * invArea;
        float dw1Dx = (ay - cy) * invArea, dw1Dy = (cx - ax) * invArea;
        float w0Org = Edge(bx, by, cx, cy, 0.5f, 0.5f) * invArea;
        float w1Org = Edge(cx, cy, ax, ay, 0.5f, 0.5f) * invArea;
        float invWa = 1f / Math.Max(1f, a.W), invWb = 1f / Math.Max(1f, b.W), invWc = 1f / Math.Max(1f, c.W);
        bool pgxpW = MathF.Abs(a.W - b.W) > 0.01f || MathF.Abs(b.W - c.W) > 0.01f || a.W > 1.01f;
        bool flat = a.R == b.R && a.R == c.R && a.G == b.G && a.G == c.G && a.B == b.B && a.B == c.B;
        uint flatAbgr = 0xFF000000U | a.R | ((uint)a.G << 8) | ((uint)a.B << 16);
        bool hasHd = hdTex.IsActive;

        for (int py = minY; py <= maxY; py++)
        {
            float rw0 = w0Org + (py * dw0Dy), rw1 = w1Org + (py * dw1Dy), rw2 = 1f - rw0 - rw1;
            int xs = minX, xe = maxX;
            if (!ClipSpan(rw0, dw0Dx, ref xs, ref xe) || !ClipSpan(rw1, dw1Dx, ref xs, ref xe) || !ClipSpan(rw2, -(dw0Dx + dw1Dx), ref xs, ref xe))
                continue;
            int row = py * hiW;
            for (int px = xs; px <= xe; px++)
            {
                int idx = row + px;
                if (checkMaskBit && hiMask is not null && hiMask[idx] != 0) continue;
                float w0 = rw0 + (px * dw0Dx), w1 = rw1 + (px * dw1Dx), w2 = 1f - w0 - w1;
                if (!st.Textured)
                {
                    uint fg = flat ? flatAbgr : InterpolateColor(w0, w1, w2, a, b, c);
                    hiVram[idx] = st.SemiTransparent ? BlendPs1(hiVram[idx], fg, st.SemiMode) : fg;
                    if (hiMask is not null) hiMask[idx] = setMaskBit ? (byte)1 : (byte)0;
                    continue;
                }

                float fu, fv;
                if (pgxpW)
                {
                    float k0 = w0 * invWa, k1 = w1 * invWb, k2 = w2 * invWc, kSum = k0 + k1 + k2;
                    float invSum = kSum > 1e-8f ? 1f / kSum : 1f;
                    fu = ((a.U * k0) + (b.U * k1) + (c.U * k2)) * invSum;
                    fv = ((a.V * k0) + (b.V * k1) + (c.V * k2)) * invSum;
                }
                else
                {
                    fu = (a.U * w0) + (b.U * w1) + (c.U * w2);
                    fv = (a.V * w0) + (b.V * w1) + (c.V * w2);
                }

                int tu = PsxPrimitiveDecoder.ApplyTextureWindow((int)MathF.Floor(fu + 1e-4f) & 255, winMaskX, winOffX);
                int tv = PsxPrimitiveDecoder.ApplyTextureWindow((int)MathF.Floor(fv + 1e-4f) & 255, winMaskY, winOffY);
                ushort texel = SampleTexel(vram, st.TexBaseX, st.TexBaseY, st.ColorDepth, st.ClutX, st.ClutY, tu, tv);
                if (hasHd)
                {
                    if (!TrySampleHdOverride(in hdTex, fu, fv, winMaskX, winMaskY, winOffX, winOffY, out uint hdAbgr, out byte hdAlpha))
                        continue;
                    uint hdOut = st.RawTexture ? hdAbgr : flat ? Modulate(hdAbgr, a.R, a.G, a.B) : ModulateInterpolated(hdAbgr, w0, w1, w2, a, b, c);
                    hiVram[idx] = ResolveHdBlend(hiVram[idx], hdOut, hdAlpha, st.SemiTransparent, (texel & 0x8000) != 0, st.SemiMode);
                    if (hiMask is not null) hiMask[idx] = (byte)(setMaskBit || (texel & 0x8000) != 0 ? 1 : 0);
                    continue;
                }
                if (texel == 0) continue;

                uint texAbgr = SoftwareCpuRenderBackend.Rgb15Lut[texel];
                uint outC = st.RawTexture ? texAbgr : flat ? Modulate(texAbgr, a.R, a.G, a.B) : ModulateInterpolated(texAbgr, w0, w1, w2, a, b, c);
                hiVram[idx] = (st.SemiTransparent && (texel & 0x8000) != 0) ? BlendPs1(hiVram[idx], outC, st.SemiMode) : outC;
                if (hiMask is not null) hiMask[idx] = (byte)(setMaskBit || (texel & 0x8000) != 0 ? 1 : 0);
            }
        }
    }

    public static void RasterizeRect(uint[] hiVram, ushort[] vram, int scale, in PsxRect r, in PsxDrawState st,
        int clipL, int clipT, int clipR, int clipB, int winMaskX, int winMaskY, int winOffX, int winOffY,
        int surfW = PsxGpu.VramWidth, int surfH = PsxGpu.VramHeight,
        byte[]? hiMask = null, bool setMaskBit = false, bool checkMaskBit = false,
        int bandMinY = 0, int bandMaxY = int.MaxValue, in HdTexOverride hdTex = default)
    {
        int hiW = surfW * scale, hiH = surfH * scale;
        int minX = Math.Max(Math.Max(0, clipL * scale), r.X * scale);
        int maxX = Math.Min(Math.Min(hiW - 1, ((clipR + 1) * scale) - 1), ((r.X + r.Width) * scale) - 1);
        int minY = Math.Max(Math.Max(bandMinY, Math.Max(0, clipT * scale)), r.Y * scale);
        int maxY = Math.Min(Math.Min(bandMaxY, Math.Min(hiH - 1, ((clipB + 1) * scale) - 1)), ((r.Y + r.Height) * scale) - 1);
        if (minX > maxX || minY > maxY) return;

        uint flat = 0xFF000000U | r.Color;
        byte cr = (byte)(r.Color & 0xFFU), cg = (byte)((r.Color >> 8) & 0xFFU), cb = (byte)((r.Color >> 16) & 0xFFU);
        if (!st.Textured)
        {
            byte maskVal = setMaskBit ? (byte)1 : (byte)0;
            for (int py = minY; py <= maxY; py++)
            {
                int row = py * hiW;
                if (!st.SemiTransparent && !checkMaskBit)
                {
                    hiVram.AsSpan(row + minX, maxX - minX + 1).Fill(flat);
                    hiMask?.AsSpan(row + minX, maxX - minX + 1).Fill(maskVal);
                }
                else
                {
                    for (int px = minX; px <= maxX; px++)
                    {
                        int idx = row + px;
                        if (checkMaskBit && hiMask is not null && hiMask[idx] != 0) continue;
                        hiVram[idx] = st.SemiTransparent ? BlendPs1(hiVram[idx], flat, st.SemiMode) : flat;
                        if (hiMask is not null) hiMask[idx] = maskVal;
                    }
                }
            }
            return;
        }

        int x0s = r.X * scale, y0s = r.Y * scale;
        bool hasHd = hdTex.IsActive;
        float invScale = 1f / scale;
        for (int py = minY; py <= maxY; py++)
        {
            float fv = r.V0 + ((py - y0s) * invScale);
            int tv = PsxPrimitiveDecoder.ApplyTextureWindow((r.V0 + ((py - y0s) / scale)) & 255, winMaskY, winOffY);
            int row = py * hiW;
            for (int px = minX; px <= maxX; px++)
            {
                int idx = row + px;
                if (checkMaskBit && hiMask is not null && hiMask[idx] != 0) continue;
                int tu = PsxPrimitiveDecoder.ApplyTextureWindow((r.U0 + ((px - x0s) / scale)) & 255, winMaskX, winOffX);
                ushort texel = SampleTexel(vram, st.TexBaseX, st.TexBaseY, st.ColorDepth, st.ClutX, st.ClutY, tu, tv);
                if (hasHd)
                {
                    float fu = r.U0 + ((px - x0s) * invScale);
                    if (!TrySampleHdOverride(in hdTex, fu, fv, winMaskX, winMaskY, winOffX, winOffY, out uint hdAbgr, out byte hdAlpha))
                        continue;
                    uint hdOut = st.RawTexture ? hdAbgr : Modulate(hdAbgr, cr, cg, cb);
                    hiVram[idx] = ResolveHdBlend(hiVram[idx], hdOut, hdAlpha, st.SemiTransparent, (texel & 0x8000) != 0, st.SemiMode);
                    if (hiMask is not null) hiMask[idx] = (byte)(setMaskBit || (texel & 0x8000) != 0 ? 1 : 0);
                    continue;
                }
                if (texel == 0) continue;
                uint texAbgr = SoftwareCpuRenderBackend.Rgb15Lut[texel];
                uint outC = st.RawTexture ? texAbgr : Modulate(texAbgr, cr, cg, cb);
                hiVram[idx] = (st.SemiTransparent && (texel & 0x8000) != 0) ? BlendPs1(hiVram[idx], outC, st.SemiMode) : outC;
                if (hiMask is not null) hiMask[idx] = (byte)(setMaskBit || (texel & 0x8000) != 0 ? 1 : 0);
            }
        }
    }

    public static void RasterizeLine(uint[] hiVram, int scale, in PsxVertex a, in PsxVertex b, in PsxDrawState st,
        int clipL, int clipT, int clipR, int clipB, int surfW = PsxGpu.VramWidth, int surfH = PsxGpu.VramHeight,
        byte[]? hiMask = null, bool setMaskBit = false, bool checkMaskBit = false,
        int bandMinY = 0, int bandMaxY = int.MaxValue)
    {
        int hiW = surfW * scale, hiH = surfH * scale;
        int minClipX = Math.Max(0, clipL * scale), maxClipX = Math.Min(hiW - 1, ((clipR + 1) * scale) - 1);
        int minClipY = Math.Max(bandMinY, Math.Max(0, clipT * scale)), maxClipY = Math.Min(bandMaxY, Math.Min(hiH - 1, ((clipB + 1) * scale) - 1));
        if (minClipX > maxClipX || minClipY > maxClipY) return;

        int x0 = (int)MathF.Round(a.X * scale), y0 = (int)MathF.Round(a.Y * scale);
        int x1 = (int)MathF.Round(b.X * scale), y1 = (int)MathF.Round(b.Y * scale);
        int dx = Math.Abs(x1 - x0), dy = Math.Abs(y1 - y0), steps = Math.Max(dx, dy);
        float invSteps = steps > 0 ? 1f / steps : 0f;
        int half = scale >> 1;
        byte maskVal = setMaskBit ? (byte)1 : (byte)0;

        for (int i = 0; i <= steps; i++)
        {
            float t = steps == 0 ? 0f : i * invSteps;
            int px = (int)MathF.Round(x0 + ((x1 - x0) * t)), py = (int)MathF.Round(y0 + ((y1 - y0) * t));
            uint col = 0xFF000000U |
                (uint)(byte)Math.Clamp((int)MathF.Round(a.R + ((b.R - a.R) * t)), 0, 255) |
                ((uint)(byte)Math.Clamp((int)MathF.Round(a.G + ((b.G - a.G) * t)), 0, 255) << 8) |
                ((uint)(byte)Math.Clamp((int)MathF.Round(a.B + ((b.B - a.B) * t)), 0, 255) << 16);

            for (int sy = -half; sy < scale - half; sy++)
            {
                int yy = py + sy;
                if (yy < minClipY || yy > maxClipY) continue;
                int row = yy * hiW;
                for (int sx = -half; sx < scale - half; sx++)
                {
                    int xx = px + sx;
                    if (xx < minClipX || xx > maxClipX) continue;
                    int idx = row + xx;
                    if (checkMaskBit && hiMask is not null && hiMask[idx] != 0) continue;
                    hiVram[idx] = st.SemiTransparent ? BlendPs1(hiVram[idx], col, st.SemiMode) : col;
                    if (hiMask is not null) hiMask[idx] = maskVal;
                }
            }
        }
    }

    private static bool TrySampleHdOverride(in HdTexOverride hd, float fu, float fv, int winMaskX, int winMaskY, int winOffX, int winOffY, out uint abgr, out byte alpha)
    {
        float uWin = winMaskX == 0 ? fu : ApplyTexWinFloat(fu, winMaskX, winOffX);
        float vWin = winMaskY == 0 ? fv : ApplyTexWinFloat(fv, winMaskY, winOffY);
        int spanW = Math.Max(1, hd.SpanTexelW), spanH = Math.Max(1, hd.SpanTexelH);
        float relU = uWin - hd.OriginTexelX;
        float relV = vWin - hd.OriginTexelY;
        float localU = relU >= 0f && relU <= spanW ? Math.Min(relU, spanW - 1e-3f) : ((relU % spanW) + spanW) % spanW;
        float localV = relV >= 0f && relV <= spanH ? Math.Min(relV, spanH - 1e-3f) : ((relV % spanH) + spanH) % spanH;
        int hx = Math.Clamp((int)MathF.Floor((localU * hd.Width) / spanW), 0, hd.Width - 1);
        int hy = Math.Clamp((int)MathF.Floor((localV * hd.Height) / spanH), 0, hd.Height - 1);
        int off = ((hy * hd.Width) + hx) * 4;
        byte[] rgba = hd.Rgba!;
        alpha = rgba[off + 3];
        if (alpha == 0) { abgr = 0; return false; }
        abgr = 0xFF000000U | rgba[off] | ((uint)rgba[off + 1] << 8) | ((uint)rgba[off + 2] << 16);
        return true;
    }

    private static float ApplyTexWinFloat(float coord, int mask, int offset)
    {
        float fl = MathF.Floor(coord + 1e-4f);
        int wrapped = PsxPrimitiveDecoder.ApplyTextureWindow((int)fl & 255, mask, offset);
        return wrapped + Math.Clamp(coord - fl, 0f, 0.9999f);
    }

    private static uint ResolveHdBlend(uint bg, uint fg, byte alpha, bool semi, bool texelStp, int semiMode)
    {
        if (semi && texelStp) return BlendPs1(bg, fg, semiMode);
        if (alpha == 255) return fg;
        int a = alpha, inv = 255 - a;
        uint r = (uint)(((int)(bg & 0xFFU) * inv + (int)(fg & 0xFFU) * a + 127) / 255);
        uint g = (uint)(((int)((bg >> 8) & 0xFFU) * inv + (int)((fg >> 8) & 0xFFU) * a + 127) / 255);
        uint b = (uint)(((int)((bg >> 16) & 0xFFU) * inv + (int)((fg >> 16) & 0xFFU) * a + 127) / 255);
        return 0xFF000000U | r | (g << 8) | (b << 16);
    }

    private static ushort SampleTexel(ushort[] vram, int baseX, int baseY, int depth, int clutX, int clutY, int u, int v)
    {
        int vy = ((baseY + v) & 511) * PsxGpu.VramWidth;
        if (depth == 0)
        {
            ushort word = vram[vy + ((baseX + (u >> 2)) & 1023)];
            return vram[(clutY * PsxGpu.VramWidth) + ((clutX + ((word >> ((u & 3) * 4)) & 0xF)) & 1023)];
        }
        if (depth == 1)
        {
            ushort word = vram[vy + ((baseX + (u >> 1)) & 1023)];
            return vram[(clutY * PsxGpu.VramWidth) + ((clutX + ((word >> ((u & 1) * 8)) & 0xFF)) & 1023)];
        }
        return vram[vy + ((baseX + u) & 1023)];
    }

    private static bool ClipSpan(float w0, float dwdx, ref int xs, ref int xe)
    {
        const float Eps = -1e-4f;
        if (MathF.Abs(dwdx) < 1e-8f) return w0 >= Eps;
        if (dwdx > 0f) { int lo = (int)MathF.Ceiling((Eps - w0) / dwdx); if (lo > xs) xs = lo; }
        else { int hi = (int)MathF.Floor((Eps - w0) / dwdx); if (hi < xe) xe = hi; }
        return xs <= xe;
    }

    private static float Edge(float ax, float ay, float bx, float by, float px, float py) =>
        ((px - ax) * (by - ay)) - ((py - ay) * (bx - ax));

    private static uint InterpolateColor(float w0, float w1, float w2, in PsxVertex a, in PsxVertex b, in PsxVertex c)
    {
        uint r = (uint)Math.Clamp((int)MathF.Round((a.R * w0) + (b.R * w1) + (c.R * w2)), 0, 255);
        uint g = (uint)Math.Clamp((int)MathF.Round((a.G * w0) + (b.G * w1) + (c.G * w2)), 0, 255);
        uint bl = (uint)Math.Clamp((int)MathF.Round((a.B * w0) + (b.B * w1) + (c.B * w2)), 0, 255);
        return 0xFF000000U | r | (g << 8) | (bl << 16);
    }

    private static uint ModulateInterpolated(uint tex, float w0, float w1, float w2, in PsxVertex a, in PsxVertex b, in PsxVertex c)
    {
        byte r = (byte)Math.Clamp((int)MathF.Round((a.R * w0) + (b.R * w1) + (c.R * w2)), 0, 255);
        byte g = (byte)Math.Clamp((int)MathF.Round((a.G * w0) + (b.G * w1) + (c.G * w2)), 0, 255);
        byte bl = (byte)Math.Clamp((int)MathF.Round((a.B * w0) + (b.B * w1) + (c.B * w2)), 0, 255);
        return Modulate(tex, r, g, bl);
    }

    private static uint Modulate(uint tex, byte r, byte g, byte b) =>
        0xFF000000U |
        Math.Min(255U, ((tex & 0xFFU) * r) >> 7) |
        (Math.Min(255U, (((tex >> 8) & 0xFFU) * g) >> 7) << 8) |
        (Math.Min(255U, (((tex >> 16) & 0xFFU) * b) >> 7) << 16);

    private static uint BlendPs1(uint bg, uint fg, int mode)
    {
        int br = (int)(bg & 0xFFU), bgC = (int)((bg >> 8) & 0xFFU), bb = (int)((bg >> 16) & 0xFFU);
        int fr = (int)(fg & 0xFFU), fgC = (int)((fg >> 8) & 0xFFU), fb = (int)((fg >> 16) & 0xFFU);
        (int r, int g, int b) = mode switch
        {
            0 => ((br + fr) >> 1, (bgC + fgC) >> 1, (bb + fb) >> 1),
            1 => (Math.Min(255, br + fr), Math.Min(255, bgC + fgC), Math.Min(255, bb + fb)),
            2 => (Math.Max(0, br - fr), Math.Max(0, bgC - fgC), Math.Max(0, bb - fb)),
            _ => (Math.Min(255, br + (fr >> 2)), Math.Min(255, bgC + (fgC >> 2)), Math.Min(255, bb + (fb >> 2)))
        };
        return 0xFF000000U | (uint)r | ((uint)g << 8) | ((uint)b << 16);
    }

    public static bool TrySubmitDirectPrimitive(
        uint gp0Opcode, ReadOnlySpan<uint> words, ushort texPage, int drawOffsetX, int drawOffsetY,
        int clipL, int clipT, int clipR, int clipB,
        uint[] hiVram, byte[] hiMask, ushort[] vram, int scale, uint[]? canonical1x, byte[]? canonicalMask1x,
        int winMaskX, int winMaskY, int winOffX, int winOffY, bool setMaskBit, bool checkMaskBit,
        PgxpTracker? pgxp, HdTexturePackStore? hdPack, HdTextureLookupCache hdLookup,
        out WideBandOp wideOp)
    {
        wideOp = default;
        uint op = gp0Opcode & 0xFFU;
        if (op is >= 0x20 and <= 0x3F)
        {
            Span<PsxVertex> verts = stackalloc PsxVertex[4];
            int count = PsxPrimitiveDecoder.DecodePolygon(op, words, texPage, drawOffsetX, drawOffsetY, pgxp, verts, out PsxDrawState state);
            if (count < 3) return false;
            HdTexOverride hd = hdLookup.ResolveForPolygon(vram, hdPack, in state, in verts[0], in verts[1], in verts[2], in verts[3], count);
            RasterizeTriangle(hiVram, vram, scale, in verts[0], in verts[1], in verts[2], in state,
                clipL, clipT, clipR, clipB, winMaskX, winMaskY, winOffX, winOffY,
                PsxGpu.VramWidth, PsxGpu.VramHeight, hiMask, setMaskBit, checkMaskBit, hdTex: in hd);
            if (count == 4)
                RasterizeTriangle(hiVram, vram, scale, in verts[1], in verts[2], in verts[3], in state,
                    clipL, clipT, clipR, clipB, winMaskX, winMaskY, winOffX, winOffY,
                    PsxGpu.VramWidth, PsxGpu.VramHeight, hiMask, setMaskBit, checkMaskBit, hdTex: in hd);
            if (canonical1x is not null && canonicalMask1x is not null)
            {
                RasterizeTriangle(canonical1x, vram, 1, in verts[0], in verts[1], in verts[2], in state,
                    clipL, clipT, clipR, clipB, winMaskX, winMaskY, winOffX, winOffY,
                    PsxGpu.VramWidth, PsxGpu.VramHeight, canonicalMask1x, setMaskBit, checkMaskBit);
                if (count == 4)
                    RasterizeTriangle(canonical1x, vram, 1, in verts[1], in verts[2], in verts[3], in state,
                        clipL, clipT, clipR, clipB, winMaskX, winMaskY, winOffX, winOffY,
                        PsxGpu.VramWidth, PsxGpu.VramHeight, canonicalMask1x, setMaskBit, checkMaskBit);
            }
            wideOp = new WideBandOp
            {
                FrameOp = new WideFrameOp { Kind = WideFrameOpKind.Polygon, DrawState = state, PolyCount = count, V0 = verts[0], V1 = verts[1], V2 = verts[2], V3 = verts[3], ClipL = clipL, ClipT = clipT, ClipR = clipR, ClipB = clipB },
                MaskSetBit = setMaskBit, MaskCheckBit = checkMaskBit, HdTex = hd
            };
            return true;
        }
        if (op is >= 0x40 and <= 0x5F && PsxPrimitiveDecoder.DecodeLine(op, words, texPage, drawOffsetX, drawOffsetY, out PsxVertex a, out PsxVertex b, out PsxDrawState ls))
        {
            RasterizeLine(hiVram, scale, in a, in b, in ls, clipL, clipT, clipR, clipB,
                PsxGpu.VramWidth, PsxGpu.VramHeight, hiMask, setMaskBit, checkMaskBit);
            if (canonical1x is not null && canonicalMask1x is not null)
                RasterizeLine(canonical1x, 1, in a, in b, in ls, clipL, clipT, clipR, clipB,
                    PsxGpu.VramWidth, PsxGpu.VramHeight, canonicalMask1x, setMaskBit, checkMaskBit);
            wideOp = new WideBandOp
            {
                FrameOp = new WideFrameOp { Kind = WideFrameOpKind.Line, DrawState = ls, V0 = a, V1 = b, ClipL = clipL, ClipT = clipT, ClipR = clipR, ClipB = clipB },
                MaskSetBit = setMaskBit, MaskCheckBit = checkMaskBit
            };
            return true;
        }
        if (op is >= 0x60 and <= 0x7F && PsxPrimitiveDecoder.DecodeRect(op, words, texPage, drawOffsetX, drawOffsetY, out PsxRect rect, out PsxDrawState rs))
        {
            HdTexOverride hd = hdLookup.ResolveForRect(vram, hdPack, in rs, in rect);
            RasterizeRect(hiVram, vram, scale, in rect, in rs, clipL, clipT, clipR, clipB,
                winMaskX, winMaskY, winOffX, winOffY, PsxGpu.VramWidth, PsxGpu.VramHeight, hiMask, setMaskBit, checkMaskBit, hdTex: in hd);
            if (canonical1x is not null && canonicalMask1x is not null)
                RasterizeRect(canonical1x, vram, 1, in rect, in rs, clipL, clipT, clipR, clipB,
                    winMaskX, winMaskY, winOffX, winOffY, PsxGpu.VramWidth, PsxGpu.VramHeight, canonicalMask1x, setMaskBit, checkMaskBit);
            wideOp = new WideBandOp
            {
                FrameOp = new WideFrameOp { Kind = WideFrameOpKind.Rect, DrawState = rs, Rect = rect, ClipL = clipL, ClipT = clipT, ClipR = clipR, ClipB = clipB },
                MaskSetBit = setMaskBit, MaskCheckBit = checkMaskBit, HdTex = hd
            };
            return true;
        }
        return false;
    }
}
