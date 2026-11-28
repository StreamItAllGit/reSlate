// @module: reStation.Core.Gpu.ParallelBandRasterizer
// @purpose: Deterministic 16px horizontal scanline band rasterizer (Parallel.For) and HD texture lookup cache.
// @depends-on: System, System.Collections.Concurrent, System.Collections.Generic, System.Runtime.InteropServices, System.Threading.Tasks, reStation.Core.Enhancement
// @exports: ParallelBandRasterizer, HdTextureLookupCache, CpuDrawKind, QueuedBandDrawOp, WideBandOp
// @owner: devv-gpu-backends (T3/T4)

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using reStation.Core.Enhancement;

namespace reStation.Core.Gpu;

internal enum CpuDrawKind : byte { Polygon, Line, Rect }

internal struct QueuedBandDrawOp
{
    public CpuDrawKind Kind;
    public PsxDrawState State;
    public PsxVertex V0, V1, V2, V3;
    public int PolyCount;
    public PsxRect Rect;
    public int ClipL, ClipT, ClipR, ClipB;
    public int TexWinMaskX, TexWinMaskY, TexWinOffX, TexWinOffY;
    public bool MaskSetBit, MaskCheckBit;
    public int MinY, MaxY;
    public HdTexOverride HdTex;
}

internal readonly struct WideBandOp
{
    public WideFrameOp FrameOp { get; init; }
    public bool MaskSetBit { get; init; }
    public bool MaskCheckBit { get; init; }
    public HdTexOverride HdTex { get; init; }
}

internal static class ParallelBandRasterizer
{
    public const int BandHeight = 16;
    private static readonly ConcurrentDictionary<int, ParallelOptions> s_parallelOptionsCache = new();

    private static ParallelOptions GetParallelOptions(int workerThreads) =>
        s_parallelOptionsCache.GetOrAdd(workerThreads, static t => new ParallelOptions { MaxDegreeOfParallelism = t });

    public static void ExecuteBands(
        uint[] hiVram, byte[] hiMask, ushort[] vram, int scale, int surfW, int surfH,
        List<QueuedBandDrawOp> ops, int workerThreads,
        uint[]? canonical1x = null, byte[]? canonicalMask1x = null)
    {
        if (ops.Count == 0) return;
        int hiH = surfH * scale;
        int bandCount = (hiH + BandHeight - 1) / BandHeight;

        if (workerThreads <= 1 || bandCount <= 1)
        {
            ExecuteBandRange(hiVram, hiMask, vram, scale, surfW, surfH, CollectionsMarshal.AsSpan(ops), 0, hiH - 1, useHd: true);
        }
        else
        {
            Parallel.For(0, bandCount, GetParallelOptions(workerThreads), bandIdx =>
            {
                int bandMinY = bandIdx * BandHeight;
                int bandMaxY = Math.Min(hiH - 1, bandMinY + BandHeight - 1);
                ExecuteBandRange(hiVram, hiMask, vram, scale, surfW, surfH, CollectionsMarshal.AsSpan(ops), bandMinY, bandMaxY, useHd: true);
            });
        }

        if (canonical1x is not null && canonicalMask1x is not null)
            ExecuteCanonical1x(canonical1x, canonicalMask1x, vram, scale, CollectionsMarshal.AsSpan(ops));
    }

    private static void ExecuteCanonical1x(
        uint[] canonical1x, byte[] canonicalMask1x, ushort[] vram, int srcScale, ReadOnlySpan<QueuedBandDrawOp> items)
    {
        float invScale = 1f / Math.Max(1, srcScale);
        for (int i = 0; i < items.Length; i++)
        {
            ref readonly QueuedBandDrawOp op = ref items[i];
            int minY1x = Math.Max(0, (int)MathF.Floor(op.MinY * invScale) - 1);
            int maxY1x = Math.Min(PsxGpu.VramHeight - 1, (int)MathF.Ceiling(op.MaxY * invScale) + 1);
            ExecuteSingleOp(canonical1x, canonicalMask1x, vram, 1, PsxGpu.VramWidth, PsxGpu.VramHeight, in op, minY1x, maxY1x, useHd: false);
        }
    }

    private static void ExecuteBandRange(
        uint[] hiVram, byte[] hiMask, ushort[] vram, int scale, int surfW, int surfH,
        ReadOnlySpan<QueuedBandDrawOp> items, int bandMinY, int bandMaxY, bool useHd)
    {
        for (int i = 0; i < items.Length; i++)
        {
            ref readonly QueuedBandDrawOp op = ref items[i];
            if (op.MaxY < bandMinY || op.MinY > bandMaxY) continue;
            ExecuteSingleOp(hiVram, hiMask, vram, scale, surfW, surfH, in op, bandMinY, bandMaxY, useHd);
        }
    }

    private static void ExecuteSingleOp(
        uint[] hiVram, byte[] hiMask, ushort[] vram, int scale, int surfW, int surfH,
        in QueuedBandDrawOp op, int bandMinY, int bandMaxY, bool useHd)
    {
        HdTexOverride hd = useHd ? op.HdTex : default;
        switch (op.Kind)
        {
            case CpuDrawKind.Polygon:
                SoftwareCpuRasterizer.RasterizeTriangle(hiVram, vram, scale, in op.V0, in op.V1, in op.V2,
                    in op.State, op.ClipL, op.ClipT, op.ClipR, op.ClipB,
                    op.TexWinMaskX, op.TexWinMaskY, op.TexWinOffX, op.TexWinOffY,
                    surfW, surfH, hiMask, op.MaskSetBit, op.MaskCheckBit, bandMinY, bandMaxY, in hd);
                break;
            case CpuDrawKind.Line:
                SoftwareCpuRasterizer.RasterizeLine(hiVram, scale, in op.V0, in op.V1, in op.State,
                    op.ClipL, op.ClipT, op.ClipR, op.ClipB, surfW, surfH,
                    hiMask, op.MaskSetBit, op.MaskCheckBit, bandMinY, bandMaxY);
                break;
            case CpuDrawKind.Rect:
                SoftwareCpuRasterizer.RasterizeRect(hiVram, vram, scale, in op.Rect, in op.State,
                    op.ClipL, op.ClipT, op.ClipR, op.ClipB,
                    op.TexWinMaskX, op.TexWinMaskY, op.TexWinOffX, op.TexWinOffY,
                    surfW, surfH, hiMask, op.MaskSetBit, op.MaskCheckBit, bandMinY, bandMaxY, in hd);
                break;
        }
    }

    public static void ExecuteWideBands(
        uint[] wideHiBuf, byte[] wideMaskBuf, uint[] hiVram, byte[] hiMask, ushort[] vram, int scale,
        int dispX, int dispY, int dispW, int dispH, int wideW, int leftPad,
        WideBandOp[] ops, int workerThreads, int initMaskX, int initMaskY, int initOffX, int initOffY)
    {
        int outH = dispH * scale;
        int bandCount = (outH + BandHeight - 1) / BandHeight;
        if (workerThreads <= 1 || bandCount <= 1)
        {
            ExecuteWideBandRange(wideHiBuf, wideMaskBuf, hiVram, hiMask, vram, scale, dispX, dispY, dispW, dispH,
                wideW, leftPad, ops, 0, outH - 1, initMaskX, initMaskY, initOffX, initOffY);
            return;
        }

        Parallel.For(0, bandCount, GetParallelOptions(workerThreads), bandIdx =>
        {
            int bandMinY = bandIdx * BandHeight;
            int bandMaxY = Math.Min(outH - 1, bandMinY + BandHeight - 1);
            ExecuteWideBandRange(wideHiBuf, wideMaskBuf, hiVram, hiMask, vram, scale, dispX, dispY, dispW, dispH,
                wideW, leftPad, ops, bandMinY, bandMaxY, initMaskX, initMaskY, initOffX, initOffY);
        });
    }

    private static void ExecuteWideBandRange(
        uint[] wideHiBuf, byte[] wideMaskBuf, uint[] hiVram, byte[] hiMask, ushort[] vram, int scale,
        int dispX, int dispY, int dispW, int dispH, int wideW, int leftPad,
        WideBandOp[] ops, int bandMinY, int bandMaxY, int wxMask, int wyMask, int wxOff, int wyOff)
    {
        int outW = wideW * scale;
        foreach (WideBandOp entry in ops)
        {
            WideFrameOp op = entry.FrameOp;
            switch (op.Kind)
            {
                case WideFrameOpKind.SetTextureWindow:
                    wxMask = (int)(op.RawU32 & 31U); wyMask = (int)((op.RawU32 >> 5) & 31U);
                    wxOff = (int)((op.RawU32 >> 10) & 31U); wyOff = (int)((op.RawU32 >> 15) & 31U);
                    break;
                case WideFrameOpKind.FillRect:
                    if (PsxWidescreenGeometry.TryComputeWideFillRect(op, dispX, dispY, dispW, dispH, wideW, leftPad, out int fx, out int fy, out int fw, out int fh))
                    {
                        uint abgr = 0xFF000000U | (op.RawU32 & 0xFFFFFFU);
                        int ys = Math.Max(bandMinY, fy * scale), ye = Math.Min(bandMaxY, ((fy + fh) * scale) - 1);
                        int xs = fx * scale, cw = fw * scale;
                        for (int py = ys; py <= ye; py++)
                        {
                            wideHiBuf.AsSpan((py * outW) + xs, cw).Fill(abgr);
                            wideMaskBuf.AsSpan((py * outW) + xs, cw).Clear();
                        }
                    }
                    break;
                case WideFrameOpKind.CopyVram:
                    CopyWideBandSlice(wideHiBuf, wideMaskBuf, hiVram, hiMask, scale, dispX, dispY, dispW, dispH, wideW, leftPad, outW, in op, bandMinY, bandMaxY);
                    break;
                case WideFrameOpKind.Polygon:
                    if (PsxWidescreenGeometry.TryMapClipToWide(op.ClipL, op.ClipT, op.ClipR, op.ClipB, dispX, dispY, dispW, dispH, wideW, leftPad, out int pcl, out int pct, out int pcr, out int pcb))
                    {
                        PsxDrawState pst = op.DrawState;
                        PsxVertex v0 = PsxWidescreenGeometry.ShiftVertex(op.V0, dispX, dispY, leftPad);
                        PsxVertex v1 = PsxWidescreenGeometry.ShiftVertex(op.V1, dispX, dispY, leftPad);
                        PsxVertex v2 = PsxWidescreenGeometry.ShiftVertex(op.V2, dispX, dispY, leftPad);
                        PsxVertex v3 = PsxWidescreenGeometry.ShiftVertex(op.V3, dispX, dispY, leftPad);
                        PsxWidescreenGeometry.ExpandIfFullScreenFadeQuad(in pst, op.PolyCount, dispW, dispH, wideW, leftPad, ref v0, ref v1, ref v2, ref v3);
                        HdTexOverride hd = entry.HdTex;
                        SoftwareCpuRasterizer.RasterizeTriangle(wideHiBuf, vram, scale, in v0, in v1, in v2, in pst,
                            pcl, pct, pcr, pcb, wxMask, wyMask, wxOff, wyOff, wideW, dispH,
                            wideMaskBuf, entry.MaskSetBit, entry.MaskCheckBit, bandMinY, bandMaxY, in hd);
                        if (op.PolyCount == 4)
                            SoftwareCpuRasterizer.RasterizeTriangle(wideHiBuf, vram, scale, in v1, in v2, in v3, in pst,
                                pcl, pct, pcr, pcb, wxMask, wyMask, wxOff, wyOff, wideW, dispH,
                                wideMaskBuf, entry.MaskSetBit, entry.MaskCheckBit, bandMinY, bandMaxY, in hd);
                    }
                    break;
                case WideFrameOpKind.Line:
                    if (PsxWidescreenGeometry.TryMapClipToWide(op.ClipL, op.ClipT, op.ClipR, op.ClipB, dispX, dispY, dispW, dispH, wideW, leftPad, out int lcl, out int lct, out int lcr, out int lcb))
                    {
                        PsxDrawState lst = op.DrawState;
                        PsxVertex a = PsxWidescreenGeometry.ShiftVertex(op.V0, dispX, dispY, leftPad);
                        PsxVertex b = PsxWidescreenGeometry.ShiftVertex(op.V1, dispX, dispY, leftPad);
                        SoftwareCpuRasterizer.RasterizeLine(wideHiBuf, scale, in a, in b, in lst,
                            lcl, lct, lcr, lcb, wideW, dispH, wideMaskBuf, entry.MaskSetBit, entry.MaskCheckBit, bandMinY, bandMaxY);
                    }
                    break;
                case WideFrameOpKind.Rect:
                    if (PsxWidescreenGeometry.TryMapClipToWide(op.ClipL, op.ClipT, op.ClipR, op.ClipB, dispX, dispY, dispW, dispH, wideW, leftPad, out int rcl, out int rct, out int rcr, out int rcb))
                    {
                        PsxDrawState rst = op.DrawState;
                        PsxRect wr = PsxWidescreenGeometry.MapRectToWide(op.Rect, in rst, dispX, dispY, dispW, dispH, wideW, leftPad);
                        HdTexOverride hd = entry.HdTex;
                        SoftwareCpuRasterizer.RasterizeRect(wideHiBuf, vram, scale, in wr, in rst,
                            rcl, rct, rcr, rcb, wxMask, wyMask, wxOff, wyOff, wideW, dispH,
                            wideMaskBuf, entry.MaskSetBit, entry.MaskCheckBit, bandMinY, bandMaxY, in hd);
                    }
                    break;
            }
        }
    }

    private static void CopyWideBandSlice(
        uint[] wideHiBuf, byte[] wideMaskBuf, uint[] hiVram, byte[] hiMask, int scale,
        int dispX, int dispY, int dispW, int dispH, int wideW, int leftPad, int outW,
        in WideFrameOp op, int bandMinY, int bandMaxY)
    {
        if (!PsxWidescreenGeometry.OverlapsDisplay(op.C, op.D, op.C + op.E - 1, op.D + op.F - 1, dispX, dispY, dispW, dispH)) return;
        int dstWx = Math.Clamp(op.C - dispX + leftPad, 0, wideW), dstWy = Math.Clamp(op.D - dispY, 0, dispH);
        int cw = Math.Min(op.E, wideW - dstWx) * scale, ch = Math.Min(op.F, dispH - dstWy) * scale;
        if (cw <= 0 || ch <= 0) return;

        int hiW = PsxGpu.VramWidth * scale, hiH = PsxGpu.VramHeight * scale;
        int srcXs = (op.A & 1023) * scale, srcYs = (op.B & 511) * scale, dstXs = dstWx * scale, dstYs = dstWy * scale;
        int rStart = Math.Max(0, bandMinY - dstYs), rEnd = Math.Min(ch - 1, bandMaxY - dstYs);
        for (int r = rStart; r <= rEnd; r++)
        {
            int srcRow = ((srcYs + r) % hiH) * hiW, dstRow = ((dstYs + r) * outW) + dstXs;
            int take = Math.Min(cw, hiW - srcXs);
            hiVram.AsSpan(srcRow + srcXs, take).CopyTo(wideHiBuf.AsSpan(dstRow, take));
            hiMask.AsSpan(srcRow + srcXs, take).CopyTo(wideMaskBuf.AsSpan(dstRow, take));
            if (take < cw)
            {
                hiVram.AsSpan(srcRow, cw - take).CopyTo(wideHiBuf.AsSpan(dstRow + take, cw - take));
                hiMask.AsSpan(srcRow, cw - take).CopyTo(wideMaskBuf.AsSpan(dstRow + take, cw - take));
            }
        }
    }
}

internal sealed class HdTextureLookupCache
{
    private static readonly string EmptyClutSha = HdTexturePackStore.ComputeClutSha256Hex(ReadOnlySpan<ushort>.Empty);
    private readonly List<(int X, int Y, int W, int H)> _uploads = new();
    private readonly Dictionary<(int TexX, int TexY, int Depth, int ClutX, int ClutY, int SpanKey), HdTexOverride> _cache = new();
    private readonly ushort[] _clutScratch = new ushort[256];
    private ushort[] _rectScratch = Array.Empty<ushort>();

    public void RecordUpload(int x, int y, int w, int h)
    {
        Invalidate();
        if (w <= 0 || h <= 0) return;
        int nx = x & 1023, ny = y & 511;
        if (nx == 0 && ny == 0 && w >= PsxGpu.VramWidth && h >= PsxGpu.VramHeight) { _uploads.Clear(); return; }
        _uploads.RemoveAll(u => u.X == nx && u.Y == ny && u.W == w && u.H == h);
        if (_uploads.Count >= 64) _uploads.RemoveAt(0);
        _uploads.Add((nx, ny, w, h));
    }

    public void Invalidate() => _cache.Clear();

    public HdTexOverride ResolveForPolygon(ushort[] vram, HdTexturePackStore? store, in PsxDrawState st, in PsxVertex v0, in PsxVertex v1, in PsxVertex v2, in PsxVertex v3, int count)
    {
        if (!st.Textured || store is not { IsEnabled: true, IndexedCount: > 0 }) return default;
        float minU = MathF.Min(v0.U, MathF.Min(v1.U, count >= 4 ? MathF.Min(v2.U, v3.U) : v2.U));
        float maxU = MathF.Max(v0.U, MathF.Max(v1.U, count >= 4 ? MathF.Max(v2.U, v3.U) : v2.U));
        float minV = MathF.Min(v0.V, MathF.Min(v1.V, count >= 4 ? MathF.Min(v2.V, v3.V) : v2.V));
        float maxV = MathF.Max(v0.V, MathF.Max(v1.V, count >= 4 ? MathF.Max(v2.V, v3.V) : v2.V));
        int u0 = Math.Clamp((int)MathF.Floor(minU), 0, 255), vMin = Math.Clamp((int)MathF.Floor(minV), 0, 255);
        int spanW = Math.Clamp((int)MathF.Ceiling(maxU) - u0, 1, 256);
        int spanH = Math.Clamp((int)MathF.Ceiling(maxV) - vMin, 1, 256);
        return Resolve(vram, store, in st, u0, vMin, spanW, spanH);
    }

    public HdTexOverride ResolveForRect(ushort[] vram, HdTexturePackStore? store, in PsxDrawState st, in PsxRect rect)
    {
        if (!st.Textured || store is not { IsEnabled: true, IndexedCount: > 0 }) return default;
        return Resolve(vram, store, in st, rect.U0 & 255, rect.V0 & 255, Math.Clamp(rect.Width, 1, 256), Math.Clamp(rect.Height, 1, 256));
    }

    private HdTexOverride Resolve(ushort[] vram, HdTexturePackStore store, in PsxDrawState st, int u0, int v0, int spanU, int spanV)
    {
        int spanKey = (u0 & 0xFF) | ((v0 & 0xFF) << 8) | (((spanU - 1) & 0xFF) << 16) | (((spanV - 1) & 0xFF) << 24);
        var key = (st.TexBaseX, st.TexBaseY, st.ColorDepth, st.ClutX, st.ClutY, spanKey);
        if (_cache.TryGetValue(key, out HdTexOverride cached)) return cached;

        string clutSha = ComputeClutHash(vram, in st);
        int shift = st.ColorDepth == 0 ? 2 : st.ColorDepth == 1 ? 1 : 0;
        int texelsPerWord = 1 << shift;
        int sampleWordX = (st.TexBaseX + (u0 >> shift)) & 1023, sampleWordY = (st.TexBaseY + v0) & 511;

        HdTexOverride resolved = default;
        for (int i = _uploads.Count - 1; i >= 0; i--)
        {
            (int rx, int ry, int rw, int rh) = _uploads[i];
            if (sampleWordX >= rx && sampleWordX < rx + rw && sampleWordY >= ry && sampleWordY < ry + rh &&
                TryHashAndResolve(vram, store, clutSha, rx, ry, rw, rh, st.TexBaseX, st.TexBaseY, texelsPerWord, out resolved))
                break;
        }

        if (!resolved.IsActive)
        {
            int primWordsW = Math.Max(1, (spanU + texelsPerWord - 1) >> shift);
            if (!TryHashAndResolve(vram, store, clutSha, sampleWordX, sampleWordY, primWordsW, spanV, st.TexBaseX, st.TexBaseY, texelsPerWord, out resolved))
                _ = TryHashAndResolve(vram, store, clutSha, st.TexBaseX, st.TexBaseY, 256 >> shift, 256, st.TexBaseX, st.TexBaseY, texelsPerWord, out resolved);
        }

        _cache[key] = resolved;
        return resolved;
    }

    private string ComputeClutHash(ushort[] vram, in PsxDrawState st)
    {
        if (st.ColorDepth >= 2) return EmptyClutSha;
        int count = st.ColorDepth == 0 ? 16 : 256;
        int row = (st.ClutY & 511) * PsxGpu.VramWidth;
        for (int i = 0; i < count; i++) _clutScratch[i] = vram[row + ((st.ClutX + i) & 1023)];
        return HdTexturePackStore.ComputeClutSha256Hex(_clutScratch.AsSpan(0, count));
    }

    private bool TryHashAndResolve(
        ushort[] vram, HdTexturePackStore store, string clutSha,
        int rx, int ry, int rw, int rh, int texBaseX, int texBaseY, int texelsPerWord, out HdTexOverride result)
    {
        int total = rw * rh;
        if (_rectScratch.Length < total) _rectScratch = new ushort[total];
        for (int r = 0, idx = 0; r < rh; r++)
        {
            int row = ((ry + r) & 511) * PsxGpu.VramWidth;
            for (int c = 0; c < rw; c++, idx++) _rectScratch[idx] = vram[row + ((rx + c) & 1023)];
        }
        string rectSha = HdTexturePackStore.ComputeVramRectSha256Hex(_rectScratch.AsSpan(0, total));
        if (store.TryResolveRgba(clutSha, rectSha, out byte[] rgba, out int hw, out int hh) &&
            hw > 0 && hh > 0 && rgba.Length >= hw * hh * 4)
        {
            result = new HdTexOverride(rgba, hw, hh, (rx - texBaseX) * texelsPerWord, ry - texBaseY, rw * texelsPerWord, rh);
            return true;
        }
        result = default;
        return false;
    }
}
