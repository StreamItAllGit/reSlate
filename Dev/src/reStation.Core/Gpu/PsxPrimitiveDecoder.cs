// @module: reStation.Core.Gpu.PsxPrimitiveDecoder
// @purpose: Shared GP0 packet sizing + polygon/line/rect decoding (signed 11-bit XY, draw offset, PGXP sub-pixel XY + depth W, texpage/CLUT) so every backend rasterizes identical geometry.
// @depends-on: reStation.Core.Gte
// @exports: PsxVertex, PsxDrawState, PsxRect, PsxPrimitiveDecoder
// @owner: devv-gpu-gl (T4)

using reStation.Core.Gte;

namespace reStation.Core.Gpu;

/// <summary>Decoded vertex in native PS1 VRAM pixel space (draw offset applied). <see cref="W"/> is PGXP view depth (1 = affine).</summary>
public struct PsxVertex
{
    public float X, Y, W, U, V;
    public byte R, G, B;
}

/// <summary>Per-primitive texture/blend state decoded from the GP0 packet (texpage + CLUT attributes).</summary>
public readonly record struct PsxDrawState(bool Textured, bool RawTexture, bool SemiTransparent, int SemiMode, ushort TexPage, ushort Clut)
{
    public int TexBaseX => (TexPage & 0xF) * 64;
    public int TexBaseY => ((TexPage >> 4) & 1) * 256;
    /// <summary>0 = 4-bit CLUT, 1 = 8-bit CLUT, 2 = 15-bit direct (reserved 3 treated as 15-bit).</summary>
    public int ColorDepth => Math.Min(2, (TexPage >> 7) & 3);
    public int ClutX => (Clut & 0x3F) * 16;
    public int ClutY => (Clut >> 6) & 0x1FF;
}

/// <summary>Decoded GP0 60h-7Fh rectangle in native VRAM pixels (draw offset applied).</summary>
public readonly record struct PsxRect(int X, int Y, int Width, int Height, int U0, int V0, uint Color);

public static class PsxPrimitiveDecoder
{
    public const float MaxPolygonWidth = 1023f;
    public const float MaxPolygonHeight = 511f;

    /// <summary>Total GP0 words (including the command word) for fixed-size commands; polylines are framed by PsxGpu.</summary>
    public static int ExpectedWords(uint op)
    {
        if (op is >= 0x20 and <= 0x3F)
        {
            bool shaded = (op & 0x10U) != 0, quad = (op & 0x08U) != 0, textured = (op & 0x04U) != 0;
            int n = quad ? 4 : 3;
            return 1 + (shaded ? n - 1 : 0) + (n * (textured ? 2 : 1));
        }
        if (op is >= 0x40 and <= 0x5F) return (op & 0x10U) != 0 ? 4 : 3;
        if (op is >= 0x60 and <= 0x7F) return 2 + ((op & 0x04U) != 0 ? 1 : 0) + (((op >> 3) & 3U) == 0 ? 1 : 0);
        if (op is >= 0x80 and <= 0x9F) return 4;
        if (op is >= 0xA0 and <= 0xDF) return 3;
        return op == 0x02 ? 3 : 1;
    }

    /// <summary>Decodes GP0 20h-3Fh into 3 or 4 vertices; returns 0 when the packet is malformed.</summary>
    public static int DecodePolygon(uint op, ReadOnlySpan<uint> words, ushort texPage, int offsetX, int offsetY,
        PgxpTracker? pgxp, Span<PsxVertex> verts, out PsxDrawState state)
    {
        bool shaded = (op & 0x10U) != 0, quad = (op & 0x08U) != 0, textured = (op & 0x04U) != 0;
        bool semi = (op & 0x02U) != 0, raw = textured && (op & 0x01U) != 0;
        int n = quad ? 4 : 3;
        state = default;
        if (words.Length < ExpectedWords(op) || verts.Length < n) return 0;

        Span<float> depth = stackalloc float[4];
        bool allPrecise = pgxp is not null;
        ushort clut = 0, tp = texPage;
        uint baseColor = words[0];
        int idx = 1;
        for (int i = 0; i < n; i++)
        {
            uint col = shaded && i > 0 ? words[idx++] : baseColor;
            uint xy = words[idx++];
            PsxVertex v = MakeVertex(xy, col, 0, 0);
            if (pgxp is not null && pgxp.TryResolve((int)xy, out PgxpVertex pv) && pv.Valid)
            {
                v.X = pv.X;
                v.Y = pv.Y;
                depth[i] = Math.Max(1f, pv.DepthZ);
            }
            else
            {
                allPrecise = false;
            }
            v.X += offsetX;
            v.Y += offsetY;
            if (textured)
            {
                uint uv = words[idx++];
                v.U = uv & 0xFFU;
                v.V = (uv >> 8) & 0xFFU;
                if (i == 0) clut = (ushort)(uv >> 16);
                else if (i == 1) tp = (ushort)(uv >> 16);
            }
            verts[i] = v;
        }
        if (allPrecise)
            for (int i = 0; i < n; i++) verts[i].W = depth[i];

        state = new PsxDrawState(textured, raw, semi, (tp >> 5) & 3, tp, clut);
        return n;
    }

    /// <summary>Decodes GP0 40h-5Fh single (mono/shaded) lines; polylines arrive pre-split by PsxGpu.</summary>
    public static bool DecodeLine(uint op, ReadOnlySpan<uint> words, ushort texPage, int offsetX, int offsetY,
        out PsxVertex a, out PsxVertex b, out PsxDrawState state)
    {
        bool shaded = (op & 0x10U) != 0;
        state = new PsxDrawState(false, false, (op & 0x02U) != 0, (texPage >> 5) & 3, texPage, 0);
        a = default;
        b = default;
        if (words.Length < (shaded ? 4 : 3)) return false;
        uint c0 = words[0], v0 = words[1];
        uint c1 = shaded ? words[2] : c0, v1 = shaded ? words[3] : words[2];
        a = MakeVertex(v0, c0, offsetX, offsetY);
        b = MakeVertex(v1, c1, offsetX, offsetY);
        return MathF.Abs(b.X - a.X) <= MaxPolygonWidth && MathF.Abs(b.Y - a.Y) <= MaxPolygonHeight;
    }

    /// <summary>Decodes GP0 60h-7Fh variable/1x1/8x8/16x16 rectangles (never PGXP-adjusted).</summary>
    public static bool DecodeRect(uint op, ReadOnlySpan<uint> words, ushort texPage, int offsetX, int offsetY,
        out PsxRect rect, out PsxDrawState state)
    {
        bool textured = (op & 0x04U) != 0;
        uint size = (op >> 3) & 3U;
        rect = default;
        state = default;
        if (words.Length < ExpectedWords(op)) return false;
        int idx = 2, u0 = 0, v0 = 0;
        ushort clut = 0;
        if (textured)
        {
            uint uv = words[idx++];
            u0 = (int)(uv & 0xFFU);
            v0 = (int)((uv >> 8) & 0xFFU);
            clut = (ushort)(uv >> 16);
        }
        int w = size switch { 1 => 1, 2 => 8, 3 => 16, _ => (int)(words[idx] & 0x3FFU) };
        int h = size switch { 1 => 1, 2 => 8, 3 => 16, _ => (int)((words[idx] >> 16) & 0x1FFU) };
        uint xy = words[1];
        rect = new PsxRect(SignExtend11(xy) + offsetX, SignExtend11(xy >> 16) + offsetY, w, h, u0, v0, words[0] & 0xFFFFFFU);
        state = new PsxDrawState(textured, textured && (op & 0x01U) != 0, (op & 0x02U) != 0, (texPage >> 5) & 3, texPage, clut);
        return w > 0 && h > 0;
    }

    /// <summary>PS1 hardware rejects triangles spanning more than 1023x511 native pixels.</summary>
    public static bool IsDrawable(in PsxVertex a, in PsxVertex b, in PsxVertex c)
    {
        float minX = MathF.Min(a.X, MathF.Min(b.X, c.X)), maxX = MathF.Max(a.X, MathF.Max(b.X, c.X));
        float minY = MathF.Min(a.Y, MathF.Min(b.Y, c.Y)), maxY = MathF.Max(a.Y, MathF.Max(b.Y, c.Y));
        return maxX - minX <= MaxPolygonWidth && maxY - minY <= MaxPolygonHeight;
    }

    /// <summary>Applies the GP0(E2h) texture window to an 8-bit texel coordinate.</summary>
    public static int ApplyTextureWindow(int coord, int mask, int offset) =>
        ((coord & ~(mask << 3)) | ((offset & mask) << 3)) & 0xFF;

    public static int SignExtend11(uint v) => ((int)(v << 21)) >> 21;

    private static PsxVertex MakeVertex(uint xy, uint bgr, int offsetX, int offsetY) => new()
    {
        X = SignExtend11(xy) + offsetX,
        Y = SignExtend11(xy >> 16) + offsetY,
        W = 1f,
        R = (byte)(bgr & 0xFFU),
        G = (byte)((bgr >> 8) & 0xFFU),
        B = (byte)((bgr >> 16) & 0xFFU)
    };
}
