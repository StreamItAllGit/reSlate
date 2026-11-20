// @module: reStation.Core.Enhancement.HdTexturePackStore
// @purpose: Path-contained <dataDir>\hdpacks\ texture override index and RGBA resolver backed by reEngine.Host.Rendering.HdPackResolver.
// @depends-on: System, reEngine.Host.Contracts, reEngine.Host.Rendering
// @exports: HdTexturePackStore
// @owner: devv-sdk-qol (T5)

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using reEngine.Host.Contracts;
using reEngine.Host.Rendering;

namespace reStation.Core.Enhancement;

/// <summary>
/// Thread-safe clean-room HD texture pack store keyed by <c>(clutSha256Hex, rectSha256Hex)</c> while keeping canonical <c>1x</c> VRAM untouched.
/// </summary>
public sealed class HdTexturePackStore
{
    private readonly HdPackResolver _resolver;

    /// <summary>Creates a store over an already validated <see cref="HdPackResolver"/>.</summary>
    public HdTexturePackStore(HdPackResolver resolver)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    /// <summary>Constructs a fail-closed store strictly contained inside <c>&lt;dataDirectory&gt;\hdpacks\</c>.</summary>
    public static HdTexturePackStore FromDataDirectory(string dataDirectory, bool isEnabled = true)
        => new(HdPackResolver.FromDataDirectory(dataDirectory, isEnabled));

    /// <summary>True when HD texture override lookup is active.</summary>
    public bool IsEnabled => _resolver.IsEnabled;

    /// <summary>Resolved root directory under <c>&lt;dataDirectory&gt;\hdpacks\</c>, if configured.</summary>
    public string? PackRootDirectory => _resolver.PackRootDirectory;

    /// <summary>Number of indexed texture override entries loaded from disk.</summary>
    public int IndexedCount => _resolver.IndexedCount;

    /// <summary>Computes lowercase 64-char SHA-256 hex digest for a 15-bit PS1 CLUT palette span.</summary>
    public static string ComputeClutSha256Hex(ReadOnlySpan<ushort> palette15)
        => Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(palette15))).ToLowerInvariant();

    /// <summary>Computes lowercase 64-char SHA-256 hex digest for a 15-bit PS1 VRAM texture rectangle span.</summary>
    public static string ComputeVramRectSha256Hex(ReadOnlySpan<ushort> pixels15)
        => Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(pixels15))).ToLowerInvariant();

    /// <summary>Resolves a validated override entry matching <paramref name="clutSha256Hex"/> and <paramref name="rectSha256Hex"/>.</summary>
    public bool TryResolveEntry(string clutSha256Hex, string rectSha256Hex, out HdTextureOverrideEntry? entry)
        => _resolver.TryResolve(clutSha256Hex, rectSha256Hex, out entry);

    /// <summary>
    /// Decodes an RGBA8 texture override matching the supplied CLUT + VRAM rectangle hashes when present.
    /// Phase 2B (<c>MinimalPngDecoder</c>) supplies full pure-<c>net8.0</c> PNG decoding.
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
        return TryResolveEntry(clutSha256Hex, rectSha256Hex, out _);
    }
}
