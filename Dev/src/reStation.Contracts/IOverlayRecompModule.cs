// @module: reStation.Contracts.IOverlayRecompModule
// @purpose: Additive overlay module ABI providing RAM load address, size, and SHA-256 hash for shared-slot disambiguation.
// @depends-on: reStation.Contracts.IRecompModule
// @exports: IOverlayRecompModule

namespace reStation.Contracts;

/// <summary>Overlay module extension exposing load address and content SHA-256 for dynamic slot disambiguation.</summary>
public interface IOverlayRecompModule : IRecompModule
{
    /// <summary>Canonical RAM load address where this overlay's payload resides.</summary>
    uint LoadRamAddress { get; }
    /// <summary>Byte length of the hashed overlay payload in guest RAM.</summary>
    int ContentSizeBytes { get; }
    /// <summary>Lowercase SHA-256 hex digest of the overlay payload bytes.</summary>
    string ContentSha256Hex { get; }
}

/// <summary>
/// Optional overlay extension scoping RAM integrity verification strictly to immutable <c>.text</c>
/// so mutable trailing <c>.data</c>/<c>.bss</c> writes never evict compiled overlay modules.
/// </summary>
public interface ITextScopedOverlayRecompModule : IOverlayRecompModule
{
    /// <summary>Canonical RAM start address of the immutable verified <c>.text</c> region.</summary>
    uint VerifiedTextStart { get; }
    /// <summary>Byte length of the immutable verified <c>.text</c> region in guest RAM.</summary>
    int VerifiedTextSizeBytes { get; }
    /// <summary>Lowercase SHA-256 hex digest computed strictly over <c>[VerifiedTextStart, VerifiedTextStart + VerifiedTextSizeBytes)</c>.</summary>
    string TextSha256Hex { get; }
}
