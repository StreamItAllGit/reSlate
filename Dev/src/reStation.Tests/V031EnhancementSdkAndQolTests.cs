// @module: reStation.Tests.V031EnhancementSdkAndQolTests
// @purpose: Verifies MinimalPngDecoder, HdTexturePackStore containment/caching, SubTickInterpolationSdk, v0.3.1 RecompSettings/CliOptions, and PsxSpu Fast-Forward zero-drop audio.
// @depends-on: Xunit, System.Buffers.Binary, System.Diagnostics, System.IO.Compression, System.Text, System.Text.Json, Foundation.Core, reEngine.Host.Contracts, reStation.Contracts, reStation.Core, reStation.Core.Audio, reStation.Core.Enhancement
// @exports: V031EnhancementSdkAndQolTests

using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Foundation.Core;
using reEngine.Host.Contracts;
using reStation.Contracts;
using reStation.Core;
using reStation.Core.Audio;
using reStation.Core.Enhancement;
using Xunit;

namespace reStation.Tests;

public sealed class V031EnhancementSdkAndQolTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"reslate-v031-sdk-qol-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (!Directory.Exists(_tempRoot)) return;
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"Temp cleanup skipped: {ex.Message}");
        }
    }

    [Fact]
    public void MinimalPngDecoder_DecodesRgbaAllFiveFiltersAndColorTypes_WithZeroGdiRefs()
    {
        string[] refs = typeof(MinimalPngDecoder).Assembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty).ToArray();
        Assert.DoesNotContain(refs, r => r.Contains("System.Drawing", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(refs, r => r.Contains("Windows.Forms", StringComparison.OrdinalIgnoreCase));

        // 1. Roundtrip RgbaPngWriter
        byte[] srcRgba =
        [
            255, 0, 0, 255,   0, 255, 0, 128,
            0, 0, 255, 64,    255, 255, 0, 255
        ];
        byte[] encoded = RgbaPngWriter.Encode(2, 2, srcRgba);
        DecodedPngImage roundtrip = MinimalPngDecoder.Decode(encoded);
        Assert.Equal(2, roundtrip.Width);
        Assert.Equal(2, roundtrip.Height);
        Assert.Equal(srcRgba, roundtrip.RgbaPixels);

        // 2. All 5 scanline filter modes (None=0, Sub=1, Up=2, Average=3, Paeth=4) on 3x5 RGBA
        byte[] expected5x3 = new byte[3 * 5 * 4];
        for (int i = 0; i < expected5x3.Length; i++) expected5x3[i] = (byte)((i * 37 + 13) & 0xFF);
        byte[] multiFilterPng = EncodeFilteredPng(3, 5, colorType: 6, bpp: 4, expected5x3, [0, 1, 2, 3, 4]);
        Assert.Equal(expected5x3, MinimalPngDecoder.Decode(multiFilterPng).RgbaPixels);

        // 3. 8-bit RGB + tRNS transparency color key
        byte[] rgbRaw = [10, 20, 30, 40, 50, 60];
        byte[] trnsRgb = [0, 10, 0, 20, 0, 30];
        byte[] rgbPng = EncodeFilteredPng(2, 1, colorType: 2, bpp: 3, rgbRaw, [1], trns: trnsRgb);
        Assert.Equal(new byte[] { 10, 20, 30, 0, 40, 50, 60, 255 }, MinimalPngDecoder.Decode(rgbPng).RgbaPixels);

        // 4. 8-bit Grayscale + tRNS
        byte[] grayRaw = [12, 200];
        byte[] grayPng = EncodeFilteredPng(2, 1, colorType: 0, bpp: 1, grayRaw, [0], trns: [0, 12]);
        Assert.Equal(new byte[] { 12, 12, 12, 0, 200, 200, 200, 255 }, MinimalPngDecoder.Decode(grayPng).RgbaPixels);

        // 5. 8-bit Grayscale + Alpha
        byte[] gaRaw = [90, 123, 180, 255];
        byte[] gaPng = EncodeFilteredPng(2, 1, colorType: 4, bpp: 2, gaRaw, [0]);
        Assert.Equal(new byte[] { 90, 90, 90, 123, 180, 180, 180, 255 }, MinimalPngDecoder.Decode(gaPng).RgbaPixels);

        // 6. 8-bit Palette PLTE + tRNS
        byte[] plte = [255, 0, 0, 0, 255, 0, 0, 0, 255];
        byte[] trnsPlte = [10, 200]; // entry 2 defaults to 255
        byte[] palRaw = [0, 1, 2];
        byte[] palPng = EncodeFilteredPng(3, 1, colorType: 3, bpp: 1, palRaw, [1], plte: plte, trns: trnsPlte);
        Assert.Equal(new byte[] { 255, 0, 0, 10, 0, 255, 0, 200, 0, 0, 255, 255 }, MinimalPngDecoder.Decode(palPng).RgbaPixels);

        // 7. Stream decode & Corrupt PNG fails closed
        using (var ms = new MemoryStream(encoded))
        {
            DecodedPngImage fromStream = MinimalPngDecoder.Decode(ms);
            Assert.Equal(srcRgba, fromStream.RgbaPixels);
        }
        byte[] corrupt = (byte[])encoded.Clone();
        corrupt[^6] ^= 0xFF;
        Assert.Throws<InvalidDataException>(() => MinimalPngDecoder.Decode(corrupt));
        Assert.False(MinimalPngDecoder.TryDecode(corrupt, out _, out _, out _));
    }

    [Fact]
    public void HdTexturePackStore_ResolvesCachesAndEnforcesContainmentAndReparseBlocking()
    {
        string dataDir = Path.Combine(_tempRoot, "hd_data");
        string packsDir = Path.Combine(dataDir, "hdpacks");
        Directory.CreateDirectory(packsDir);

        ushort[] palette = [0x7C00, 0x03E0, 0x001F, 0x7FFF];
        ushort[] rect = [0, 1, 2, 3];
        string clutHex = HdTexturePackStore.ComputeClutSha256Hex(palette);
        string rectHex = HdTexturePackStore.ComputeVramRectSha256Hex(rect);
        Assert.Equal(64, clutHex.Length);
        Assert.Equal(64, rectHex.Length);

        byte[] rgba =
        [
            10, 20, 30, 255,  40, 50, 60, 255,
            70, 80, 90, 255,  100, 110, 120, 255
        ];
        string pngPath = Path.Combine(packsDir, $"{clutHex}_{rectHex}.png");
        RgbaPngWriter.WritePng(pngPath, 2, 2, rgba);

        HdTexturePackStore disabled = HdTexturePackStore.FromDataDirectory(dataDir, isEnabled: false);
        Assert.False(disabled.TryResolveRgba(clutHex, rectHex, out _, out _, out _));

        HdTexturePackStore store = HdTexturePackStore.FromDataDirectory(dataDir, isEnabled: true);
        Assert.Equal(1, store.IndexedCount);
        Assert.Equal(0, store.CachedCount);
        Assert.True(store.TryResolveRgba(clutHex.ToUpperInvariant(), rectHex, out byte[] firstPixels, out int w1, out int h1));
        Assert.Equal(2, w1);
        Assert.Equal(2, h1);
        Assert.Equal(rgba, firstPixels);
        Assert.Equal(1, store.CachedCount);

        // Second lookup hits in-memory decode cache (same array reference, zero re-decode)
        Assert.True(store.TryResolveRgba(clutHex, rectHex, out byte[] secondPixels, out _, out _));
        Assert.Same(firstPixels, secondPixels);

        // Manifest '..' traversal must fail closed with InvalidOperationException ("Containment Violation")
        string travDir = Path.Combine(_tempRoot, "trav_data");
        string travPacks = Path.Combine(travDir, "hdpacks");
        Directory.CreateDirectory(travPacks);
        File.WriteAllText(Path.Combine(travPacks, "hdpack.manifest.json"),
            $$"""{"entries":[{"clutSha256Hex":"{{clutHex}}","rectSha256Hex":"{{rectHex}}","relativeFilePath":"..\\escape.png"}]}""");
        var travEx = Assert.Throws<InvalidOperationException>(() => HdTexturePackStore.FromDataDirectory(travDir, true));
        Assert.Contains("Containment Violation", travEx.Message, StringComparison.OrdinalIgnoreCase);

        // Manifest rooted path must fail closed with InvalidOperationException
        File.WriteAllText(Path.Combine(travPacks, "hdpack.manifest.json"),
            $$"""{"overrides":[{"clutSha256Hex":"{{clutHex}}","rectSha256Hex":"{{rectHex}}","relativeFilePath":"C:\\outside\\evil.png"}]}""");
        var rootedEx = Assert.Throws<InvalidOperationException>(() => HdTexturePackStore.FromDataDirectory(travDir, true));
        Assert.Contains("Containment Violation", rootedEx.Message, StringComparison.OrdinalIgnoreCase);

        // Reparse point (Windows directory junction) must fail closed with InvalidOperationException
        if (OperatingSystem.IsWindows())
        {
            string juncData = Path.Combine(_tempRoot, "junc_data");
            string juncTarget = Path.Combine(_tempRoot, "junc_target");
            Directory.CreateDirectory(juncData);
            Directory.CreateDirectory(juncTarget);
            string juncPacks = Path.Combine(juncData, "hdpacks");
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{juncPacks}\" \"{juncTarget}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            })!;
            proc.WaitForExit(5000);
            Assert.Equal(0, proc.ExitCode);
            var juncEx = Assert.Throws<InvalidOperationException>(() => HdTexturePackStore.FromDataDirectory(juncData, true));
            Assert.Contains("Containment Violation", juncEx.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("reparse", juncEx.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void SubTickInterpolationSdk_AlphaSubFramesAndWatchdog_Preserve60HzSafety()
    {
        Assert.True(SubTickInterpolationSdk.ShouldRunSubTick(isEnhancedFrameRate: true, targetFps: 120, isFastForward: false));
        Assert.True(SubTickInterpolationSdk.ShouldRunSubTick(isEnhancedFrameRate: true, targetFps: 0, isFastForward: false));
        Assert.False(SubTickInterpolationSdk.ShouldRunSubTick(isEnhancedFrameRate: false, targetFps: 120, isFastForward: false));
        Assert.False(SubTickInterpolationSdk.ShouldRunSubTick(isEnhancedFrameRate: true, targetFps: 60, isFastForward: false));
        Assert.False(SubTickInterpolationSdk.ShouldRunSubTick(isEnhancedFrameRate: true, targetFps: 120, isFastForward: true));

        Assert.Equal(1U, SubTickInterpolationSdk.ResolveSubFrameCount(60, isEnhancedFrameRate: true, isFastForward: false));
        Assert.Equal(2U, SubTickInterpolationSdk.ResolveSubFrameCount(120, isEnhancedFrameRate: true, isFastForward: false));
        Assert.Equal(3U, SubTickInterpolationSdk.ResolveSubFrameCount(144, isEnhancedFrameRate: true, isFastForward: false));
        Assert.Equal(4U, SubTickInterpolationSdk.ResolveSubFrameCount(240, isEnhancedFrameRate: true, isFastForward: false));
        Assert.Equal(2U, SubTickInterpolationSdk.ResolveSubFrameCount(0, isEnhancedFrameRate: true, isFastForward: false));
        Assert.Equal(1U, SubTickInterpolationSdk.ResolveSubFrameCount(240, isEnhancedFrameRate: false, isFastForward: false));
        Assert.Equal(1U, SubTickInterpolationSdk.ResolveSubFrameCount(240, isEnhancedFrameRate: true, isFastForward: true));

        Assert.Equal(0.0, SubTickInterpolationSdk.ComputeSubTickAlpha(0, 1), 6);
        Assert.Equal(0.0, SubTickInterpolationSdk.ComputeSubTickAlpha(0, 2), 6);
        Assert.Equal(0.5, SubTickInterpolationSdk.ComputeSubTickAlpha(1, 2), 6);
        Assert.Equal(0.25, SubTickInterpolationSdk.ComputeSubTickAlpha(1, 4), 6);
        Assert.Equal(0.75, SubTickInterpolationSdk.ComputeSubTickAlpha(3, 4), 6);

        var cpu = new StubSubTickCpu { TotalCycles = 1000, SubTickDeadlineCycle = 9999 };
        var hook = new StubSubTickHook(entryPc: 0x8001_0080U, maxCycles: 500U, cycleSpendPerPass: 200);
        Assert.True(SubTickInterpolationSdk.TryExecuteSubTickPass(hook, cpu, 1U, 2U));
        Assert.Equal(9999L, cpu.SubTickDeadlineCycle);
        Assert.True(hook.LastCompletedCleanly);
        Assert.True(cpu.RanInSubTickRender);

        // Over-budget pass trips watchdog cleanly and still restores previous SubTickDeadlineCycle
        hook.CycleSpendPerPass = 600;
        Assert.False(SubTickInterpolationSdk.TryExecuteSubTickPass(hook, cpu, 1U, 2U));
        Assert.Equal(9999L, cpu.SubTickDeadlineCycle);
        Assert.False(hook.LastCompletedCleanly);

        // Fail-closed when already in sub-tick render
        cpu.SetInSubTickRender(true);
        Assert.False(SubTickInterpolationSdk.TryExecuteSubTickPass(hook, cpu, 1U, 2U));
        cpu.SetInSubTickRender(false);
    }

    [Fact]
    public void RecompSettings_V031ClampAndAspectMigration_NormalizesBackendsCropMultiplierAndWidescreen()
    {
        Directory.CreateDirectory(_tempRoot);

        // 1. Legacy settings.json migration: isWidescreen16By9=true without aspectRatioMode -> horPlus16By9
        string legacyWidePath = Path.Combine(_tempRoot, "legacy_wide.json");
        File.WriteAllText(legacyWidePath, """{"schemaVersion":1,"isWidescreen16By9":true,"backendId":"ParallelCpu"}""");
        RecompSettings wide = RecompSettings.Load(legacyWidePath).Value;
        Assert.Equal("horPlus16By9", wide.AspectRatioMode);
        Assert.True(wide.IsWidescreen16By9);
        Assert.Equal(PsxRenderBackendIds.ParallelCpu, wide.BackendId);

        // 2. Legacy settings.json migration: isWidescreen16By9=false without aspectRatioMode -> nativePar
        string legacyNativePath = Path.Combine(_tempRoot, "legacy_native.json");
        File.WriteAllText(legacyNativePath, """{"schemaVersion":1,"isWidescreen16By9":false,"backendId":"VULKAN"}""");
        RecompSettings native = RecompSettings.Load(legacyNativePath).Value;
        Assert.Equal("nativePar", native.AspectRatioMode);
        Assert.False(native.IsWidescreen16By9);
        Assert.Equal(PsxRenderBackendIds.Vulkan, native.BackendId);

        // 3. Explicit aspectRatioMode keeps IsWidescreen16By9 consistent
        var s = new RecompSettings
        {
            BackendId = "parallel-cpu",
            AspectRatioMode = "horPlus16By9",
            IsWidescreen16By9 = false,
            FastForwardMultiplier = double.NaN,
            OverscanCropPx = new HostOverscanCrop(-4, 99, 8, 33)
        };
        s.Clamp();
        Assert.Equal(PsxRenderBackendIds.ParallelCpu, s.BackendId);
        Assert.True(s.IsWidescreen16By9);
        Assert.Equal(2.0, s.FastForwardMultiplier);
        Assert.Equal(new HostOverscanCrop(0, 32, 8, 32), s.OverscanCropPx);

        s.FastForwardMultiplier = 6.5;
        s.AspectRatioMode = "fourByThree";
        s.Clamp();
        Assert.Equal(4.0, s.FastForwardMultiplier);
        Assert.Equal("fourByThree", s.AspectRatioMode);
        Assert.False(s.IsWidescreen16By9);

        // 4. settings.example.json loads cleanly with v0.3.1 defaults
        string devDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string examplePath = Path.Combine(devDir, "data", "settings.example.json");
        if (File.Exists(examplePath))
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(examplePath));
            Assert.Equal("StreamItAll\\reEngine\\engines\\reSlate\\v0.3.1", doc.RootElement.GetProperty("umbrellaSubpath").GetString());
            Assert.Equal("nativePar", doc.RootElement.GetProperty("aspectRatioMode").GetString());
            Assert.False(doc.RootElement.GetProperty("isHdTexturePackEnabled").GetBoolean());
            Assert.False(doc.RootElement.GetProperty("isFastForwardEnabled").GetBoolean());
            Assert.Equal(2.0, doc.RootElement.GetProperty("fastForwardMultiplier").GetDouble());
        }
    }

    [Fact]
    public void CliOptions_V031Flags_ParseNonSwallowingBooleansAspectAndAll4LiveBackends()
    {
        CliOptions parsed = CliOptions.Parse(
        [
            "--backend", "vulkan",
            "--require-backend", "parallelCpu",
            "--hd-packs", "--disc", "harvest.cue",
            "--fast-forward", "on",
            "--aspect-mode", "horPlus16By9"
        ]);

        Assert.Equal(PsxRenderBackendIds.Vulkan, parsed.BackendId);
        Assert.Equal(PsxRenderBackendIds.ParallelCpu, parsed.RequireBackendId);
        Assert.False(parsed.IsDeferredBackendRequired(out _));
        Assert.True(parsed.HdPacks);
        Assert.Equal("harvest.cue", parsed.DiscPath);
        Assert.True(parsed.FastForward);
        Assert.Equal("horPlus16By9", parsed.AspectMode);

        CliOptions inlineParsed = CliOptions.Parse(["--hd-packs=off", "--fast-forward=1", "--aspect-mode=4:3"]);
        Assert.False(inlineParsed.HdPacks);
        Assert.True(inlineParsed.FastForward);
        Assert.Equal("fourByThree", inlineParsed.AspectMode);

        var settings = new RecompSettings();
        parsed.ApplyOverrides(settings);
        Assert.Equal(PsxRenderBackendIds.Vulkan, settings.BackendId);
        Assert.True(settings.IsHdTexturePackEnabled);
        Assert.True(settings.IsFastForwardEnabled);
        Assert.Equal("horPlus16By9", settings.AspectRatioMode);
        Assert.True(settings.IsWidescreen16By9);

        Assert.Contains("vulkan", CliOptions.HelpText, StringComparison.Ordinal);
        Assert.Contains("parallelCpu", CliOptions.HelpText, StringComparison.Ordinal);
        Assert.Contains("--hd-packs", CliOptions.HelpText, StringComparison.Ordinal);
        Assert.Contains("--fast-forward", CliOptions.HelpText, StringComparison.Ordinal);
        Assert.Contains("--aspect-mode", CliOptions.HelpText, StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(() => CliOptions.Parse(["--hd-packs=maybe"]));
        Assert.Throws<ArgumentException>(() => CliOptions.Parse(["--fast-forward=nope"]));
        Assert.Throws<ArgumentException>(() => CliOptions.Parse(["--aspect-mode", "stretched"]));
    }

    [Fact]
    public void PsxSpu_FastForwardDecimationAndXaTrim_MaintainsZeroXaDropsAcross4xBursts()
    {
        // 1. Settings-driven 4x decimation across 240 guest frames (> 2.6x ring capacity)
        var settings = new RecompSettings
        {
            MasterVolume = 1.0,
            IsFastForwardEnabled = true,
            FastForwardMultiplier = 4.0
        };
        var spu = new PsxSpu(settings);
        spu.Write16(0x1F801DAA, 0xC001);
        byte[] groups = CreateXaSectorPayload();
        float[] hostDrain = new float[735 * 2];

        for (int hostFrame = 0; hostFrame < 60; hostFrame++)
        {
            for (int tick = 0; tick < 4; tick++)
            {
                if ((tick & 1) == 0) spu.SubmitXaSectors(groups, codingInfo: 0x01);
                spu.RenderFrames(735);
            }
            int read = spu.Output.Read(hostDrain.AsSpan(0, Math.Min(hostDrain.Length, spu.Output.BufferedSamples) & ~1));
            Assert.True(read > 0);
        }

        Assert.Equal(0L, spu.XaDrops);
        Assert.Equal(0L, spu.OutputOverflows);
        Assert.Equal(0L, spu.AudioUnderruns);

        // 2. Explicit ResampleOrTrimFastForwardAudio + DiscardExcessXaFramesForFastForward with IsFastForwardEnabled=false
        var manualSpu = new PsxSpu(new RecompSettings { MasterVolume = 1.0 });
        manualSpu.Write16(0x1F801DAA, 0xC001);
        for (int i = 0; i < 10; i++) manualSpu.SubmitXaSectors(groups, codingInfo: 0x01);
        int discardedXa = manualSpu.DiscardExcessXaFramesForFastForward(1470);
        Assert.Equal(10 * 2352 - 1470, discardedXa);
        Assert.Equal(1470, manualSpu.PendingXaFrames);
        Assert.Equal(0L, manualSpu.XaDrops);

        for (int burst = 0; burst < 40; burst++)
        {
            manualSpu.SubmitXaSectors(groups, codingInfo: 0x01);
            manualSpu.RenderFrames(4 * 735);
            _ = manualSpu.ResampleOrTrimFastForwardAudio(4.0);
        }
        Assert.Equal(0L, manualSpu.XaDrops);
        Assert.Equal(0L, manualSpu.OutputOverflows);
        Assert.InRange(manualSpu.Output.BufferedSamples, 2, 1470 * 2);
    }

    private static byte[] CreateXaSectorPayload()
    {
        byte[] groups = new byte[XaAdpcmDecoder.SoundGroupsPerSector * XaAdpcmDecoder.SoundGroupSize];
        for (int g = 0; g < XaAdpcmDecoder.SoundGroupsPerSector; g++)
        {
            int o = g * XaAdpcmDecoder.SoundGroupSize;
            for (int u = 0; u < 8; u++) groups[o + 4 + u] = 0x10;
            for (int b = 16; b < XaAdpcmDecoder.SoundGroupSize; b++) groups[o + b] = 0x35;
        }
        return groups;
    }

    private static byte[] EncodeFilteredPng(
        int width,
        int height,
        byte colorType,
        int bpp,
        byte[] pixels,
        byte[] rowFilters,
        byte[]? plte = null,
        byte[]? trns = null)
    {
        int stride = width * bpp;
        byte[] scanlines = new byte[height * (1 + stride)];
        for (int y = 0; y < height; y++)
        {
            byte f = rowFilters[y];
            int dstRow = y * (1 + stride);
            scanlines[dstRow] = f;
            for (int x = 0; x < stride; x++)
            {
                int cur = pixels[y * stride + x];
                int a = x >= bpp ? pixels[y * stride + x - bpp] : 0;
                int b = y > 0 ? pixels[(y - 1) * stride + x] : 0;
                int c = (y > 0 && x >= bpp) ? pixels[(y - 1) * stride + x - bpp] : 0;
                int pred = f switch
                {
                    0 => 0,
                    1 => a,
                    2 => b,
                    3 => (a + b) >> 1,
                    4 => Paeth(a, b, c),
                    _ => throw new ArgumentOutOfRangeException(nameof(rowFilters))
                };
                scanlines[dstRow + 1 + x] = unchecked((byte)(cur - pred));
            }
        }

        using var idatMs = new MemoryStream();
        using (var zlib = new ZLibStream(idatMs, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(scanlines);

        using var pngMs = new MemoryStream();
        pngMs.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr[0..4], width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..8], height);
        ihdr[8] = 8;
        ihdr[9] = colorType;
        WritePngChunk(pngMs, "IHDR", ihdr);
        if (plte is not null) WritePngChunk(pngMs, "PLTE", plte);
        if (trns is not null) WritePngChunk(pngMs, "tRNS", trns);
        WritePngChunk(pngMs, "IDAT", idatMs.ToArray());
        WritePngChunk(pngMs, "IEND", ReadOnlySpan<byte>.Empty);
        return pngMs.ToArray();
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static void WritePngChunk(Stream s, string tag, ReadOnlySpan<byte> payload)
    {
        Span<byte> buf4 = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buf4, payload.Length);
        s.Write(buf4);
        byte[] type = Encoding.ASCII.GetBytes(tag);
        s.Write(type);
        if (!payload.IsEmpty) s.Write(payload);
        uint crc = 0xFFFFFFFFu;
        crc = CrcUpdate(crc, type);
        crc = CrcUpdate(crc, payload);
        BinaryPrimitives.WriteUInt32BigEndian(buf4, crc ^ 0xFFFFFFFFu);
        s.Write(buf4);
    }

    private static uint CrcUpdate(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (0xEDB88320u ^ (crc >> 1)) : (crc >> 1);
        }
        return crc;
    }

    private sealed class StubSubTickHook(uint entryPc, uint maxCycles, int cycleSpendPerPass) : ISubTickFrameHook
    {
        public int CycleSpendPerPass { get; set; } = cycleSpendPerPass;
        public bool LastCompletedCleanly { get; private set; }
        public uint SubTickEntryPc => entryPc;
        public uint MaxSubTickCycles => maxCycles;
        public bool ShouldRunSubTick(uint subFrameIndex, uint totalSubFrames) => true;
        public bool TryInterceptCall(IReStationCpu cpu, uint canonPc) => false;
        public void Reset() { }
        public void OnSubTickBegin(IReStationCpuEx cpu, uint subFrameIndex, uint totalSubFrames)
            => ((StubSubTickCpu)cpu).NextSliceCycles = CycleSpendPerPass;
        public void OnSubTickEnd(IReStationCpuEx cpu, uint subFrameIndex, bool completedCleanly) => LastCompletedCleanly = completedCleanly;
    }

    private sealed class StubSubTickCpu : IReStationCpuEx
    {
        private readonly byte[] _ram = new byte[1024];
        private readonly uint[] _r = new uint[32];
        public int NextSliceCycles { get; set; } = 200;
        public bool RanInSubTickRender { get; private set; }
        public void SetInSubTickRender(bool value) => InSubTickRender = value;
        public Span<byte> Ram => _ram;
        public uint Pc { get; set; }
        public uint Hi { get; set; }
        public uint Lo { get; set; }
        public uint Cop0Sr { get; set; }
        public uint[] R => _r;
        public long TotalCycles { get; set; }
        public long NextEventCycle { get; set; }
        public uint UnwindTargetPc { get; set; }
        public bool SkipPolygonRasterization { get; set; }
        public long SubTickDeadlineCycle { get; set; }
        public bool InSubTickRender { get; private set; }
        public bool ExitRequested { get; private set; }
        public bool ShouldUnwind { get; set; }
        public int ActiveExeId { get; set; }
        public long FrameCount => 0;
        public uint Read32(uint address) => 0;
        public ushort Read16(uint address) => 0;
        public byte Read8(uint address) => 0;
        public void Write32(uint address, uint value) { }
        public void Write16(uint address, ushort value) { }
        public void Write8(uint address, byte value) { }
        public void Call(uint targetPc) { }
        public void JumpIndirect(uint targetPc) { }
        public void DispatchFunction(uint pc) { }
        public void Syscall(uint code) { }
        public void Break(uint code) { }
        public uint GteReadData(int register) => 0;
        public void GteWriteData(int register, uint value) { }
        public uint GteReadControl(int register) => 0;
        public void GteWriteControl(int register, uint value) { }
        public void GteExecute(uint command) { }
        public void Lwl(int rt, uint address) { }
        public void Lwr(int rt, uint address) { }
        public void Swl(uint address, uint value) { }
        public void Swr(uint address, uint value) { }
        public void Mult(uint a, uint b) { }
        public void Multu(uint a, uint b) { }
        public void Div(uint a, uint b) { }
        public void Divu(uint a, uint b) { }
        public void PollEvents() { }
        public void RegisterModule(IRecompModule module) { }
        public void RequestExit() => ExitRequested = true;
        public void BeginSubTickFrame() => SkipPolygonRasterization = false;
        public bool HasCompiledEntry(uint canonPc) => canonPc != 0U;
        public bool TryAdvanceToNextVBlank() => false;
        public void RunSlice(uint entryPc, uint returnSentinelPc = IReStationCpu.ReturnSentinelPc, bool inSubTickRender = false)
        {
            InSubTickRender = inSubTickRender;
            RanInSubTickRender = inSubTickRender;
            TotalCycles += NextSliceCycles;
            InSubTickRender = false;
        }
    }
}
