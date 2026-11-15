// @module: reStation.Core.Aot.IRecompCompiledEntryLookup
// @purpose: Fast compiled entrypoint lookup contract and local AOT load options for player/runtime integration.
// @depends-on: System
// @exports: IRecompCompiledEntryLookup, LocalAotLoadOptions

namespace reStation.Core.Aot;

/// <summary>Implemented by compiled <c>IRecompModule</c> classes to report exact lifted entrypoints instead of text bounds.</summary>
public interface IRecompCompiledEntryLookup
{
    /// <summary>Returns true if <paramref name="pc"/> corresponds to an exact lifted function or mid-function entry.</summary>
    bool ContainsCompiledEntry(uint pc);
}

/// <summary>Optional runtime overrides supplied by the player CLI for local AOT compilation and Tier 3 bringup.</summary>
public sealed record LocalAotLoadOptions(
    string? DataDir = null,
    string? PreloadRomDir = null,
    bool JitBringup = false,
    bool ForceZeroRwx = false,
    string? ExecutionTierModeOverride = null,
    string? BringupProfilePath = null);
