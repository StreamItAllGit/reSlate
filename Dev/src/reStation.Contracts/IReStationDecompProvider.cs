// @module: reStation.Contracts.IReStationDecompProvider
// @purpose: PS1 .decomp provider contracts bridging reEngine IDecompPlatformProvider, Foundation.Retro RecompExecutionTierMode, and clean-room AOT recipe DTOs.
// @depends-on: System, System.Collections.Generic, System.IO, System.Text.Json.Serialization, Foundation.Retro, reEngine.Core.Decomp, reEngine.Core.Manifests
// @exports: ReStationExecutionTierModes, AotRecipeExecutableSpec, AotRecipeSpec, IReStationDecompProvider

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Foundation.Retro;
using reEngine.Core.Decomp;
using reEngine.Core.Manifests;

namespace reStation.Contracts;

/// <summary>
/// Standard 18 <c>executionTierMode</c> tokens and lossless converters bridging module manifests
/// to <see cref="RecompExecutionTierMode"/>.
/// </summary>
public static class ReStationExecutionTierModes
{
    /// <summary>AOT-only execution without runtime JIT fallback.</summary>
    public const string StaticAotOnly = "staticAotOnly";

    /// <summary>Default AOT-primary execution with CoreCLR JIT fallback for unlifted dynamic blocks.</summary>
    public const string AotWithJitFallback = "aotWithJitFallback";

    /// <summary>AOT-primary execution paired with threaded-IR bringup/dispatch fallback.</summary>
    public const string AotWithThreadedIr = "aotWithThreadedIr";

    /// <summary>Debug/profiling <c>--jit-bringup</c> JIT execution tier.</summary>
    public const string JitBringup = "jitBringup";

    /// <summary>Reference interpreter oracle execution mode.</summary>
    public const string InterpreterOracle = "interpreterOracle";

    /// <summary>Default PS1 recomp execution tier mode (<see cref="AotWithJitFallback"/>).</summary>
    public const string Default = AotWithJitFallback;

    /// <summary>All canonical Standard 18 <c>executionTierMode</c> values.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        StaticAotOnly, AotWithJitFallback, AotWithThreadedIr, JitBringup, InterpreterOracle
    ];

    /// <summary>Attempts to normalize a raw <c>executionTierMode</c> token (including Foundation enum aliases).</summary>
    public static bool TryNormalize(string? mode, out string normalized)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            normalized = Default;
            return true;
        }

        string trimmed = mode.Trim();
        normalized = trimmed switch
        {
            var m when string.Equals(m, StaticAotOnly, StringComparison.OrdinalIgnoreCase) => StaticAotOnly,
            var m when string.Equals(m, AotWithJitFallback, StringComparison.OrdinalIgnoreCase) => AotWithJitFallback,
            var m when string.Equals(m, "aotPrimary", StringComparison.OrdinalIgnoreCase) => AotWithJitFallback,
            var m when string.Equals(m, AotWithThreadedIr, StringComparison.OrdinalIgnoreCase) => AotWithThreadedIr,
            var m when string.Equals(m, "threadedIr", StringComparison.OrdinalIgnoreCase) => AotWithThreadedIr,
            var m when string.Equals(m, JitBringup, StringComparison.OrdinalIgnoreCase) => JitBringup,
            var m when string.Equals(m, InterpreterOracle, StringComparison.OrdinalIgnoreCase) => InterpreterOracle,
            _ => string.Empty
        };
        return normalized.Length > 0;
    }

    /// <summary>Returns a canonical <c>executionTierMode</c> string, falling back to <see cref="Default"/> when unrecognized.</summary>
    public static string Normalize(string? mode) => TryNormalize(mode, out string normalized) ? normalized : Default;

    /// <summary>Returns <see langword="true"/> when <paramref name="mode"/> matches a recognized tier token.</summary>
    public static bool IsValid(string? mode) => !string.IsNullOrWhiteSpace(mode) && TryNormalize(mode, out _);

    /// <summary>Maps a Standard 18 <c>executionTierMode</c> string to <see cref="RecompExecutionTierMode"/>.</summary>
    public static RecompExecutionTierMode ToFoundationTierMode(string? mode) => Normalize(mode) switch
    {
        AotWithThreadedIr => RecompExecutionTierMode.ThreadedIr,
        JitBringup or InterpreterOracle => RecompExecutionTierMode.JitBringup,
        _ => RecompExecutionTierMode.AotPrimary
    };

    /// <summary>Maps a <see cref="RecompExecutionTierMode"/> enum value to its canonical Standard 18 <c>executionTierMode</c> string.</summary>
    public static string FromFoundationTierMode(RecompExecutionTierMode tier) => tier switch
    {
        RecompExecutionTierMode.ThreadedIr => AotWithThreadedIr,
        RecompExecutionTierMode.JitBringup => JitBringup,
        _ => AotWithJitFallback
    };
}

/// <summary>
/// Clean-room PS-X executable metadata entry inside an <see cref="AotRecipeSpec"/> (no retail executable bytes).
/// </summary>
public sealed record AotRecipeExecutableSpec
{
    /// <summary>Relative PS-X executable path inside the disc filesystem (e.g. <c>SLUS_001.58</c>).</summary>
    [JsonPropertyName("bootExePath")]
    public string BootExePath { get; init; } = string.Empty;

    /// <summary>Expected SHA-256 hex digest of the retail PS-X executable (empty when optional).</summary>
    [JsonPropertyName("sha256")]
    public string Sha256 { get; init; } = string.Empty;

    /// <summary>PS-X RAM load address from the PS-X EXE header.</summary>
    [JsonPropertyName("loadAddress")]
    public uint LoadAddress { get; init; }

    /// <summary>PS-X initial program counter (<c>PC</c>) from the PS-X EXE header.</summary>
    [JsonPropertyName("entryAddress")]
    public uint EntryAddress { get; init; }

    /// <summary>PS-X initial global pointer (<c>$gp</c>) from the PS-X EXE header.</summary>
    [JsonPropertyName("globalPointer")]
    public uint GlobalPointer { get; init; }

    /// <summary>PS-X text/data byte length reported in the PS-X EXE header.</summary>
    [JsonPropertyName("textSizeBytes")]
    public int TextSizeBytes { get; init; }

    /// <summary>SLEIGH language/spec identifier targeting this executable.</summary>
    [JsonPropertyName("sleighLanguageId")]
    public string SleighLanguageId { get; init; } = "psx_r3000a";
    /// <summary>Optional raw byte offset inside a container/disc image for $O(1)$ slice verification (Standard 18 <c>containerOffsetBytes</c>).</summary>
    [JsonPropertyName("containerOffsetBytes")]
    public long? ContainerOffsetBytes { get; init; }
    /// <summary>Optional per-executable uint32 function entry-point address hints emitted in <c>aot-recipe.json</c> (Standard 18 <c>entryAddresses</c>).</summary>
    [JsonPropertyName("entryAddresses")]
    public IReadOnlyList<uint>? EntryAddresses { get; init; }
}

/// <summary>
/// Clean-room ahead-of-time lift recipe (<c>aot-recipe.json</c>) carried inside <c>.recomp</c> modules so
/// local hosts can emit gitignored <c>&lt;dataDir&gt;\cache\&lt;Game&gt;.Generated.dll</c> from user media.
/// </summary>
public sealed class AotRecipeSpec
{
    /// <summary>Recipe schema version.</summary>
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Target platform identifier (<c>ps1</c>).</summary>
    [JsonPropertyName("platformId")]
    public string PlatformId { get; set; } = "ps1";

    /// <summary>Lowercase module identifier matching <see cref="IReStationModuleManifest"/>.</summary>
    [JsonPropertyName("moduleId")]
    public string ModuleId { get; set; } = string.Empty;

    /// <summary>SlateOne (PS1) disc product/serial code.</summary>
    [JsonPropertyName("discSerial")]
    public string DiscSerial { get; set; } = string.Empty;

    /// <summary>Standard 18 <c>executionTierMode</c> selector for this recipe.</summary>
    [JsonPropertyName("executionTierMode")]
    public string ExecutionTierMode { get; set; } = ReStationExecutionTierModes.Default;

    /// <summary>Clean-room SLEIGH specification identifier (e.g. <c>psx_r3000a</c>).</summary>
    [JsonPropertyName("sleighSpecId")]
    public string SleighSpecId { get; set; } = "psx_r3000a";

    /// <summary>Target guest CPU architecture label (<c>R3000A</c>).</summary>
    [JsonPropertyName("targetArchitecture")]
    public string TargetArchitecture { get; set; } = "R3000A";

    /// <summary>True when guest instruction words and memory accesses are little-endian.</summary>
    [JsonPropertyName("littleEndian")]
    public bool LittleEndian { get; set; } = true;

    /// <summary>Primary boot PS-X executable path relative to disc root.</summary>
    [JsonPropertyName("primaryBootExecutable")]
    public string PrimaryBootExecutable { get; set; } = string.Empty;

    /// <summary>Declared clean-room executable entries discovered during decompilation.</summary>
    [JsonPropertyName("executables")]
    public IReadOnlyList<AotRecipeExecutableSpec> Executables { get; set; } = Array.Empty<AotRecipeExecutableSpec>();

    /// <summary>Known guest code entry addresses (PS-X virtual addresses) seeded for AOT lifting.</summary>
    [JsonPropertyName("entryAddresses")]
    public IReadOnlyList<uint> EntryAddresses { get; set; } = Array.Empty<uint>();

    /// <summary>Relative clean-room metadata artifact paths associated with the recipe (never generated C#/DLL binaries).</summary>
    [JsonPropertyName("cleanRoomArtifacts")]
    public IReadOnlyList<string> CleanRoomArtifacts { get; set; } = Array.Empty<string>();

    /// <summary>Logical assembly name for the locally materialized gitignored AOT cache DLL.</summary>
    [JsonPropertyName("generatedCacheAssemblyName")]
    public string? GeneratedCacheAssemblyName { get; set; }

    /// <summary>Forward-compatible extension keys preserved across round-trips.</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>Validates clean-room containment and required recipe invariants; throws <see cref="InvalidDataException"/> on violation.</summary>
    public void Validate(string sourceLabel = "aot-recipe.json")
    {
        if (SchemaVersion < 1)
            throw new InvalidDataException($"'{sourceLabel}' has invalid schemaVersion ({SchemaVersion}).");
        if (!ReStationExecutionTierModes.TryNormalize(ExecutionTierMode, out _))
            throw new InvalidDataException($"'{sourceLabel}' has unsupported executionTierMode '{ExecutionTierMode}'.");
        if (!string.IsNullOrWhiteSpace(PrimaryBootExecutable) && Path.IsPathRooted(PrimaryBootExecutable))
            throw new InvalidDataException($"'{sourceLabel}' primaryBootExecutable must be relative.");

        foreach (string artifact in CleanRoomArtifacts)
        {
            if (string.IsNullOrWhiteSpace(artifact))
                continue;
            if (Path.IsPathRooted(artifact))
                throw new InvalidDataException($"'{sourceLabel}' cleanRoomArtifacts entry '{artifact}' must be relative.");
            if (artifact.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
                || artifact.EndsWith(".Generated.dll", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"'{sourceLabel}' forbids generated binary/source artifact '{artifact}' inside clean-room recipes.");
            }
        }
    }
}

/// <summary>
/// PS1 specialization of <see cref="IDecompPlatformProvider"/> implemented by <c>reStation.Decomp</c>
/// for clean-room R3000A SLEIGH decoding, <c>D0..D5</c> decomp stage execution, and AOT recipe generation.
/// </summary>
public interface IReStationDecompProvider : IDecompPlatformProvider
{
    /// <summary>Target engine identifier (canonical <c>reSlate</c>).</summary>
    string EngineId { get; }

    /// <summary>Clean-room SLEIGH specification identifier used for R3000A instruction lifting.</summary>
    string SleighLanguageId { get; }

    /// <summary>Default Foundation execution tier bridged to AOT compilation.</summary>
    RecompExecutionTierMode DefaultExecutionTier { get; }

    /// <summary>Synthesizes a clean-room <see cref="AotRecipeSpec"/> for a PS1 module or disc workspace.</summary>
    Task<AotRecipeSpec> BuildAotRecipeAsync(DecompRunOptions options, CancellationToken ct = default);

    /// <summary>Validates a compiled <c>.recomp</c> archive or directory for zero retail/generated-code contamination.</summary>
    DecompCleanRoomValidationResult ValidateCleanRoomPackage(string packageOrDirectory);
}
