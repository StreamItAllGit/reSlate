// @module: reStation.Core.Enhancement.HdTexturePackStore
// @purpose: Path-contained <dataDir>\hdpacks\ texture override index, decode cache, and RGBA resolver backed by reEngine.Host.Rendering.HdPackResolver.
// @depends-on: System, System.Collections.Concurrent, System.Diagnostics, System.Runtime.InteropServices, System.Security.Cryptography, Foundation.Core, reEngine.Host.Contracts, reEngine.Host.Rendering
// @exports: HdTexturePackStore
// @owner: devv-sdk-qol (T5)

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Foundation.Core;
using reEngine.Host.Contracts;
using reEngine.Host.Rendering;

namespace reStation.Core.Enhancement;

/// <summary>
/// Thread-safe clean-room HD texture pack store keyed by <c>(clutSha256Hex, rectSha256Hex)</c> while keeping canonical <c>1x</c> VRAM untouched.
/// </summary>
public sealed class HdTexturePackStore : IHdTexturePackResolver
{
    private readonly HdPackResolver _resolver;
    private readonly ConcurrentDictionary<HdTextureOverrideKey, DecodedPngImage> _decodedCache = new();

    /// <summary>Creates a store over an already validated <see cref="HdPackResolver"/>.</summary>
    public HdTexturePackStore(HdPackResolver resolver)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    /// <summary>Constructs a fail-closed store strictly contained inside <c>&lt;dataDirectory&gt;\hdpacks\</c>.</summary>
    public static HdTexturePackStore FromDataDirectory(string dataDirectory, bool isEnabled = true)
    {
        try
        {
            return new HdTexturePackStore(HdPackResolver.FromDataDirectory(dataDirectory, isEnabled));
        }
        catch (InvalidOperationException ex) when (!ex.Message.Contains("Containment Violation", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Fail-Closed Path Containment Violation: {ex.Message}", ex);
        }
    }

    /// <summary>True when HD texture override lookup is active.</summary>
    public bool IsEnabled => _resolver.IsEnabled;

    /// <summary>Resolved root directory under <c>&lt;dataDirectory&gt;\hdpacks\</c>, if configured.</summary>
    public string? PackRootDirectory => _resolver.PackRootDirectory;

    /// <summary>Number of indexed texture override entries loaded from disk.</summary>
    public int IndexedCount => _resolver.IndexedCount;

    /// <summary>Number of decoded RGBA overrides currently cached in memory.</summary>
    public int CachedCount => _decodedCache.Count;

    /// <summary>Computes lowercase 64-char SHA-256 hex digest for a 15-bit PS1 CLUT palette span.</summary>
    public static string ComputeClutSha256Hex(ReadOnlySpan<ushort> palette15)
        => Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(palette15))).ToLowerInvariant();

    /// <summary>Computes lowercase 64-char SHA-256 hex digest for a 15-bit PS1 VRAM texture rectangle span.</summary>
    public static string ComputeVramRectSha256Hex(ReadOnlySpan<ushort> pixels15)
        => Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(pixels15))).ToLowerInvariant();

    /// <summary>Clears decoded RGBA textures cached in memory without discarding indexed manifest paths.</summary>
    public void ClearCache() => _decodedCache.Clear();

    /// <inheritdoc/>
    public bool TryResolve(string clutSha256Hex, string rectSha256Hex, out HdTextureOverrideEntry? entry)
        => TryResolveEntry(clutSha256Hex, rectSha256Hex, out entry);

    /// <summary>Resolves a validated override entry matching <paramref name="clutSha256Hex"/> and <paramref name="rectSha256Hex"/>.</summary>
    public bool TryResolveEntry(string clutSha256Hex, string rectSha256Hex, out HdTextureOverrideEntry? entry)
        => _resolver.TryResolve(clutSha256Hex, rectSha256Hex, out entry);

    /// <summary>
    /// Resolves and caches a decoded RGBA8 texture override matching the supplied CLUT + VRAM rectangle hashes when present.
    /// Returned pixel buffers are cached and must be treated as read-only by callers.
    /// </summary>
    public bool TryResolveRgba(
        string clutSha256Hex,
        string rectSha256Hex,
        out byte[] rgbaPixels,
        out int width,
        out int height)
    {
        rgbaPixels = Array.Empty<byte>();
        width = 0;
        height = 0;
        if (!IsEnabled || string.IsNullOrWhiteSpace(clutSha256Hex) || string.IsNullOrWhiteSpace(rectSha256Hex))
        {
            return false;
        }

        var key = new HdTextureOverrideKey(
            clutSha256Hex.Trim().ToLowerInvariant(),
            rectSha256Hex.Trim().ToLowerInvariant());
        if (_decodedCache.TryGetValue(key, out DecodedPngImage? cached))
        {
            rgbaPixels = cached.RgbaPixels;
            width = cached.Width;
            height = cached.Height;
            return true;
        }

        if (!TryResolveEntry(key.ClutSha256Hex, key.RectSha256Hex, out HdTextureOverrideEntry? entry) || entry is null)
        {
            return false;
        }

        string safePath = AssertSafeOverrideFile(entry.FullFilePath);
        try
        {
            DecodedPngImage decoded = MinimalPngDecoder.DecodeFile(safePath);
            DecodedPngImage stored = _decodedCache.GetOrAdd(key, decoded);
            rgbaPixels = stored.RgbaPixels;
            width = stored.Width;
            height = stored.Height;
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException)
        {
            Trace.WriteLine($"HdTexturePackStore failed decoding '{entry.RelativeFilePath}': {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private string AssertSafeOverrideFile(string fullFilePath)
    {
        string full = Path.GetFullPath(fullFilePath);
        if (!string.IsNullOrWhiteSpace(PackRootDirectory))
        {
            full = AppEnvironment.AssertPathContained(full, PackRootDirectory);
        }
        if (File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                $"Fail-Closed Path Containment Violation: HD pack file '{full}' cannot be a reparse point.");
        }
        return full;
    }
}
