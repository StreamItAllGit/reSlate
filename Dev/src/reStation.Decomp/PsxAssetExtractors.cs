// @module: reStation.Decomp.PsxAssetExtractors
// @purpose: Clean-room PS1 TIM (4/8/16/24bpp -> RGBA PNG), VAB (pBAV soundbank), and SEQ (pQES MIDI) extractors confined strictly to gitignored cache directories.
// @depends-on: System, System.Buffers.Binary, System.Collections.Generic, System.IO, System.Text, System.Text.Json, Foundation.Core
// @exports: PsxExtractedAssetRecord, PsxAssetExtractSummary, PsxAssetExtractors
// SPDX-License-Identifier: MPL-2.0

using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Foundation.Core;

namespace reStation.Decomp;

/// <summary>Metadata record for one extracted PS1 TIM/VAB/SEQ asset written strictly to gitignored cache.</summary>
public sealed record PsxExtractedAssetRecord(
    string Kind,
    string SourceName,
    string CacheMetadataPath,
    IReadOnlyList<string> CacheOutputFiles,
    string Details);

/// <summary>Summary of clean-room local cache extraction of TIM, VAB, and SEQ assets.</summary>
public sealed record PsxAssetExtractSummary(
    string CacheOutputDirectory,
    IReadOnlyList<PsxExtractedAssetRecord> Assets,
    string Summary);

/// <summary>
/// Clean-room PS1 TIM/VAB/SEQ binary parsers and local cache extractors.
/// Refuses to write outside gitignored <c>&lt;dataDir&gt;\cache\</c> or <c>Sandbox\</c> directories.
/// </summary>
public static class PsxAssetExtractors
{
    private static readonly byte[] VabMagic = "pBAV"u8.ToArray();
    private static readonly byte[] SeqMagic = "pQES"u8.ToArray();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Asserts that <paramref name="targetDir"/> resolves strictly inside gitignored <c>cache\</c> or <c>Sandbox\</c>.</summary>
    public static string AssertGitignoredCacheTarget(string targetDir, string dataDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDir);

        string fullData = Path.GetFullPath(dataDir);
        string fullTarget = Path.GetFullPath(targetDir);
        if (fullTarget.Contains($"{Path.DirectorySeparatorChar}.git", StringComparison.OrdinalIgnoreCase) ||
            fullTarget.Contains($"{Path.DirectorySeparatorChar}Dev{Path.DirectorySeparatorChar}src", StringComparison.OrdinalIgnoreCase) ||
            fullTarget.Contains($"{Path.DirectorySeparatorChar}Dev{Path.DirectorySeparatorChar}docs", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Refusing asset extraction into tracked source/git path '{fullTarget}'.");
        }

        string allowedCache = Path.Combine(fullData, "cache");
        string? sandboxRoot = SandboxEnvironment.ResolveSiblingSandboxRoot(fullData);
        if (sandboxRoot is not null &&
            fullTarget.StartsWith(sandboxRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return SandboxEnvironment.AssertPathContained(fullTarget, sandboxRoot);
        }

        return SandboxEnvironment.AssertPathContained(fullTarget, allowedCache);
    }

    /// <summary>Extracts a PS1 TIM texture (<c>0x10</c> magic) into a lossless PNG + metadata JSON inside cache.</summary>
    public static PsxExtractedAssetRecord ExtractTim(
        ReadOnlySpan<byte> timBytes, string cacheTargetDir, string dataDir, string assetName)
    {
        string safeDir = AssertGitignoredCacheTarget(cacheTargetDir, dataDir);
        if (timBytes.Length < 20 || BinaryPrimitives.ReadUInt32LittleEndian(timBytes[..4]) != 0x00000010U)
            throw new InvalidDataException($"'{assetName}' is not a valid PS1 TIM image.");

        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(timBytes.Slice(4, 4));
        int bppMode = (int)(flags & 0x7U);
        bool hasClut = (flags & 0x8U) != 0U;
        int pos = 8;

        ushort[]? clut = null;
        if (hasClut)
        {
            uint clutBlockBytes = BinaryPrimitives.ReadUInt32LittleEndian(timBytes.Slice(pos, 4));
            if (clutBlockBytes < 12 || pos + clutBlockBytes > timBytes.Length)
                throw new InvalidDataException($"TIM '{assetName}' CLUT block exceeds file length.");
            int clutColors = (int)((clutBlockBytes - 12U) / 2U);
            clut = new ushort[clutColors];
            for (int i = 0; i < clutColors; i++)
                clut[i] = BinaryPrimitives.ReadUInt16LittleEndian(timBytes.Slice(pos + 12 + i * 2, 2));
            pos += (int)clutBlockBytes;
        }

        if (pos + 12 > timBytes.Length)
            throw new InvalidDataException($"TIM '{assetName}' is missing pixel header.");
        uint pixBlockBytes = BinaryPrimitives.ReadUInt32LittleEndian(timBytes.Slice(pos, 4));
        ushort pixW = BinaryPrimitives.ReadUInt16LittleEndian(timBytes.Slice(pos + 8, 2));
        ushort pixH = BinaryPrimitives.ReadUInt16LittleEndian(timBytes.Slice(pos + 10, 2));
        int pixelDataLen = Math.Min((int)(pixBlockBytes - 12U), timBytes.Length - (pos + 12));
        ReadOnlySpan<byte> pixData = timBytes.Slice(pos + 12, Math.Max(0, pixelDataLen));

        int width = bppMode switch { 0 => pixW * 4, 1 => pixW * 2, 2 => pixW, 3 => (pixW * 2) / 3, _ => throw new InvalidDataException($"Unsupported TIM BPP mode {bppMode}.") };
        int height = pixH;
        if (width <= 0 || height <= 0)
            throw new InvalidDataException($"TIM '{assetName}' has invalid dimensions {width}x{height}.");

        byte[] rgba = DecodeTimToRgba(bppMode, width, height, pixData, clut);
        Directory.CreateDirectory(safeDir);
        string stem = SanitizeFileStem(assetName);
        string pngPath = SandboxEnvironment.AssertPathContained(Path.Combine(safeDir, $"{stem}.png"), safeDir);
        string metaPath = SandboxEnvironment.AssertPathContained(Path.Combine(safeDir, $"{stem}.tim.json"), safeDir);

        RgbaPngWriter.WritePng(pngPath, width, height, rgba);
        File.WriteAllText(metaPath, JsonSerializer.Serialize(new
        {
            kind = "TIM", sourceName = assetName, bpp = bppMode switch { 0 => 4, 1 => 8, 2 => 16, _ => 24 },
            hasClut, clutEntries = clut?.Length ?? 0, width, height, pngFile = Path.GetFileName(pngPath)
        }, JsonOptions), new UTF8Encoding(false));

        return new PsxExtractedAssetRecord("TIM", assetName, metaPath, [metaPath, pngPath], $"TIM {width}x{height} ({bppMode})");
    }

    /// <summary>Extracts a PS1 VAB soundbank (<c>"pBAV"</c>) header and ADPCM VAG sample index strictly into cache.</summary>
    public static PsxExtractedAssetRecord ExtractVab(
        ReadOnlySpan<byte> vabBytes, string cacheTargetDir, string dataDir, string assetName)
    {
        string safeDir = AssertGitignoredCacheTarget(cacheTargetDir, dataDir);
        if (vabBytes.Length < 32 || !vabBytes[..4].SequenceEqual(VabMagic))
            throw new InvalidDataException($"'{assetName}' is not a valid PS1 VAB soundbank.");

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(vabBytes.Slice(4, 4));
        uint vabId = BinaryPrimitives.ReadUInt32LittleEndian(vabBytes.Slice(8, 4));
        uint waveSize = BinaryPrimitives.ReadUInt32LittleEndian(vabBytes.Slice(12, 4));
        ushort programs = BinaryPrimitives.ReadUInt16LittleEndian(vabBytes.Slice(18, 2));
        ushort tones = BinaryPrimitives.ReadUInt16LittleEndian(vabBytes.Slice(20, 2));
        ushort vags = BinaryPrimitives.ReadUInt16LittleEndian(vabBytes.Slice(22, 2));

        Directory.CreateDirectory(safeDir);
        string stem = SanitizeFileStem(assetName);
        string metaPath = SandboxEnvironment.AssertPathContained(Path.Combine(safeDir, $"{stem}.vab.json"), safeDir);
        var outputs = new List<string> { metaPath };

        int headerEst = 32 + 128 * 16 + programs * 16 * 32 + 512;
        if (vabBytes.Length > headerEst && waveSize > 0)
        {
            string vbPath = SandboxEnvironment.AssertPathContained(Path.Combine(safeDir, $"{stem}.vb.cache"), safeDir);
            int copyLen = (int)Math.Min(waveSize, (uint)(vabBytes.Length - headerEst));
            File.WriteAllBytes(vbPath, vabBytes.Slice(headerEst, copyLen).ToArray());
            outputs.Add(vbPath);
        }

        File.WriteAllText(metaPath, JsonSerializer.Serialize(new
        {
            kind = "VAB", sourceName = assetName, version, vabId, waveformSize = waveSize,
            programCount = programs, toneCount = tones, vagCount = vags
        }, JsonOptions), new UTF8Encoding(false));

        return new PsxExtractedAssetRecord("VAB", assetName, metaPath, outputs, $"VAB v{version}: {programs} progs, {tones} tones, {vags} VAGs");
    }

    /// <summary>Extracts a PS1 SEQ MIDI sequence (<c>"pQES"</c>) header and event payload strictly into cache.</summary>
    public static PsxExtractedAssetRecord ExtractSeq(
        ReadOnlySpan<byte> seqBytes, string cacheTargetDir, string dataDir, string assetName)
    {
        string safeDir = AssertGitignoredCacheTarget(cacheTargetDir, dataDir);
        if (seqBytes.Length < 15 || !seqBytes[..4].SequenceEqual(SeqMagic))
            throw new InvalidDataException($"'{assetName}' is not a valid PS1 SEQ sequence.");

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(seqBytes.Slice(4, 4));
        ushort ppqn = BinaryPrimitives.ReadUInt16BigEndian(seqBytes.Slice(8, 2));
        int tempoUs = (seqBytes[10] << 16) | (seqBytes[11] << 8) | seqBytes[12];
        byte rhythmNum = seqBytes[13];
        byte rhythmDen = seqBytes[14];

        Directory.CreateDirectory(safeDir);
        string stem = SanitizeFileStem(assetName);
        string metaPath = SandboxEnvironment.AssertPathContained(Path.Combine(safeDir, $"{stem}.seq.json"), safeDir);
        string bodyPath = SandboxEnvironment.AssertPathContained(Path.Combine(safeDir, $"{stem}.seq.events.bin"), safeDir);
        File.WriteAllBytes(bodyPath, seqBytes[15..].ToArray());
        File.WriteAllText(metaPath, JsonSerializer.Serialize(new
        {
            kind = "SEQ", sourceName = assetName, version, ppqn,
            tempoMicrosecondsPerBeat = tempoUs, timeSignature = $"{rhythmNum}/{1 << Math.Clamp((int)rhythmDen, 0, 8)}",
            eventBytes = seqBytes.Length - 15
        }, JsonOptions), new UTF8Encoding(false));

        return new PsxExtractedAssetRecord("SEQ", assetName, metaPath, [metaPath, bodyPath], $"SEQ v{version}: {ppqn} PPQN, {seqBytes.Length - 15} event B");
    }

    /// <summary>Scans <paramref name="sourceDir"/> for <c>.tim</c>/<c>.vab</c>/<c>.seq</c> assets and extracts them strictly into <paramref name="cacheOutputDir"/>.</summary>
    public static PsxAssetExtractSummary ScanAndExtractDirectory(string sourceDir, string cacheOutputDir, string dataDir)
    {
        string safeCache = AssertGitignoredCacheTarget(cacheOutputDir, dataDir);
        string fullSrc = Path.GetFullPath(sourceDir);
        if (!Directory.Exists(fullSrc))
            return new PsxAssetExtractSummary(safeCache, Array.Empty<PsxExtractedAssetRecord>(), "Asset source directory absent.");

        var records = new List<PsxExtractedAssetRecord>();
        foreach (string file in Directory.EnumerateFiles(fullSrc, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            string ext = Path.GetExtension(file);
            byte[] data = File.ReadAllBytes(file);
            string relName = Path.GetRelativePath(fullSrc, file).Replace('\\', '/');
            if (ext.Equals(".tim", StringComparison.OrdinalIgnoreCase) && data.Length >= 20 && BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0, 4)) == 0x10U)
                records.Add(ExtractTim(data, safeCache, dataDir, relName));
            else if (ext.Equals(".vab", StringComparison.OrdinalIgnoreCase) && data.Length >= 32 && data.AsSpan(0, 4).SequenceEqual(VabMagic))
                records.Add(ExtractVab(data, safeCache, dataDir, relName));
            else if (ext.Equals(".seq", StringComparison.OrdinalIgnoreCase) && data.Length >= 15 && data.AsSpan(0, 4).SequenceEqual(SeqMagic))
                records.Add(ExtractSeq(data, safeCache, dataDir, relName));
        }
        return new PsxAssetExtractSummary(safeCache, records, $"Extracted {records.Count} PS1 asset(s) to gitignored cache '{safeCache}'.");
    }

    private static byte[] DecodeTimToRgba(int bppMode, int width, int height, ReadOnlySpan<byte> pix, ushort[]? clut)
    {
        byte[] rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = y * width + x;
                int dst = idx * 4;
                if (bppMode == 0)
                {
                    int bOff = idx >> 1;
                    int palIdx = bOff < pix.Length ? ((idx & 1) == 0 ? pix[bOff] & 0xF : (pix[bOff] >> 4) & 0xF) : 0;
                    WritePsx15BitToRgba(clut is not null && palIdx < clut.Length ? clut[palIdx] : (ushort)0, rgba, dst);
                }
                else if (bppMode == 1)
                {
                    int palIdx = idx < pix.Length ? pix[idx] : 0;
                    WritePsx15BitToRgba(clut is not null && palIdx < clut.Length ? clut[palIdx] : (ushort)0, rgba, dst);
                }
                else if (bppMode == 2)
                {
                    int off = idx * 2;
                    ushort c = off + 2 <= pix.Length ? BinaryPrimitives.ReadUInt16LittleEndian(pix.Slice(off, 2)) : (ushort)0;
                    WritePsx15BitToRgba(c, rgba, dst);
                }
                else
                {
                    int off = idx * 3;
                    if (off + 3 <= pix.Length)
                    {
                        rgba[dst] = pix[off];
                        rgba[dst + 1] = pix[off + 1];
                        rgba[dst + 2] = pix[off + 2];
                        rgba[dst + 3] = 255;
                    }
                }
            }
        }
        return rgba;
    }

    private static void WritePsx15BitToRgba(ushort c, byte[] rgba, int dst)
    {
        rgba[dst] = (byte)(((c & 0x1F) * 255 + 15) / 31);
        rgba[dst + 1] = (byte)((((c >> 5) & 0x1F) * 255 + 15) / 31);
        rgba[dst + 2] = (byte)((((c >> 10) & 0x1F) * 255 + 15) / 31);
        rgba[dst + 3] = (c & 0x7FFF) == 0 && (c & 0x8000) == 0 ? (byte)0 : (byte)255;
    }

    private static string SanitizeFileStem(string raw) =>
        new(raw.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_').ToArray());
}
