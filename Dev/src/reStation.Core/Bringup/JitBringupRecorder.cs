// @module: reStation.Core.Bringup.JitBringupRecorder
// @purpose: Clean-room --jit-bringup telemetry profiler (atomic bringup.profile.json) and 5,644,800-cycle non-VBlank spin watchdog.
// @depends-on: Foundation.Retro.AotCacheLayoutHelper, reStation.Contracts.ReStationEngineInfo, reStation.Core.Jit.Tier3ExecutionCoordinator
// @exports: JitBringupRecorder, BringupProfileDocument, BringupFunctionRecord, BringupIndirectJumpRecord

using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Foundation.Retro;
using reStation.Contracts;
using reStation.Core.Jit;

namespace reStation.Core.Bringup;

/// <summary>Clean-room discovered block/entry telemetry record (SHA-256 + address + size only; zero retail bytes).</summary>
public sealed record BringupFunctionRecord(
    string CodeSha256Hex,
    string EntryPcHex,
    uint EntryPc,
    uint CodeSizeBytes,
    int OverlayId,
    long HitCount);

/// <summary>Clean-room indirect jump edge observed during dynamic bringup execution.</summary>
public sealed record BringupIndirectJumpRecord(
    string SourcePcHex,
    uint SourcePc,
    string TargetPcHex,
    uint TargetPc,
    long HitCount);

/// <summary>Serialized <c>bringup.profile.json</c> snapshot aligned with Standard 18 canonical keys.</summary>
public sealed class BringupProfileDocument
{
    private bool _isPureStaticReady;
    private bool _hasExplicitPureStatic;

    public int SchemaVersion { get; init; } = 1;
    public string ModuleId { get; init; } = string.Empty;
    public string EngineId { get; init; } = ReStationEngineInfo.EngineId;
    public string EngineVersion { get; init; } = ReStationEngineInfo.Version;
    public string ExecutionTierMode { get; init; } = ReStationExecutionTierModes.JitBringup;
    public bool IsDynamicCodeSupported { get; init; }
    public long CompiledDispatchCount { get; init; }
    public long JitDispatchCount { get; init; }
    public long ThreadedIrDispatchCount { get; init; }
    public long InterpreterFallbackCount { get; init; }
    public long RamPageInvalidationCount { get; init; }
    public int DiscoveredFunctionCount { get; init; }

    /// <summary>Canonical Standard 18 R3 Pure Static gate: compiled AOT only with zero JIT/IR/interpreter fallback and no spin trip.</summary>
    public bool IsPureStaticReady
    {
        get => _isPureStaticReady;
        init
        {
            _isPureStaticReady = value;
            _hasExplicitPureStatic = true;
        }
    }

    /// <summary>Deprecated pre-reSlate v0.3.0 transition compatibility alias of <see cref="IsPureStaticReady"/>.</summary>
    [Obsolete("Use IsPureStaticReady (Standard 18 canonical key 'isPureStaticReady'). Retained for v0.3.x transition compatibility.")]
    public bool IsR4PromotionReady
    {
        get => _isPureStaticReady;
        init
        {
            if (!_hasExplicitPureStatic)
                _isPureStaticReady = value;
        }
    }

    public long NonVBlankSpinCycles { get; init; }
    public bool IsSpinWatchdogTripped { get; init; }
    public string UpdatedAtUtc { get; init; } = string.Empty;
    public IReadOnlyList<BringupFunctionRecord> Entries { get; init; } = Array.Empty<BringupFunctionRecord>();
    public IReadOnlyList<BringupIndirectJumpRecord> IndirectJumpTargets { get; init; } = Array.Empty<BringupIndirectJumpRecord>();
}

/// <summary>Records clean-room bringup telemetry and enforces the non-VBlank spin watchdog (<c>ExitCode = 5</c>).</summary>
public sealed class JitBringupRecorder
{
    public const int SpinWatchdogExitCode = 5;
    public const long DefaultSpinWatchdogCycleThreshold = 5_644_800L;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _sync = new();
    private readonly Dictionary<(string Sha, uint Pc, uint Size, int Overlay), long> _blocks = new();
    private readonly Dictionary<(uint Src, uint Dst), long> _indirectJumps = new();
    private volatile Tier3ExecutionCoordinator? _attachedCoordinator;
    private volatile bool _lastSnapshotPureStaticReady;

    public JitBringupRecorder(
        string moduleId,
        string executionTierMode = ReStationExecutionTierModes.JitBringup,
        bool? isDynamicCodeSupported = null,
        string? bringupProfilePath = null,
        long spinWatchdogCycleThreshold = DefaultSpinWatchdogCycleThreshold)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        ModuleId = moduleId.Trim();
        ExecutionTierMode = ReStationExecutionTierModes.Normalize(executionTierMode);
        IsDynamicCodeSupported = isDynamicCodeSupported ?? RuntimeFeature.IsDynamicCodeSupported;
        SpinWatchdogCycleThreshold = Math.Max(1L, spinWatchdogCycleThreshold);
        BringupProfilePath = string.IsNullOrWhiteSpace(bringupProfilePath)
            ? Path.Combine(Path.GetTempPath(), "reslate-bringup", ModuleId, AotCacheLayoutHelper.BringupProfileFileName)
            : Path.GetFullPath(bringupProfilePath);
    }

    public string ModuleId { get; }
    public string ExecutionTierMode { get; }
    public bool IsDynamicCodeSupported { get; }
    public string BringupProfilePath { get; }
    public long SpinWatchdogCycleThreshold { get; }
    public long NonVBlankSpinCycles { get; private set; }
    public bool IsSpinWatchdogTripped { get; private set; }

    public int DiscoveredFunctionCount
    {
        get { lock (_sync) return _blocks.Count; }
    }

    /// <summary>In-memory bringup capture completeness signal (never serialized to bringup.profile.json).</summary>
    [JsonIgnore]
    public bool IsBringupCaptureClean => DiscoveredFunctionCount > 0 && !IsSpinWatchdogTripped;

    /// <summary>True only when static AOT ran with zero dynamic JIT/IR/interpreter dispatches and no spin trip.</summary>
    [JsonIgnore]
    public bool IsPureStaticReady => _attachedCoordinator is { } coord
        ? ComputeIsPureStaticReady(
            coord.CompiledDispatchCount,
            coord.JitDispatchCount,
            coord.ThreadedIrDispatchCount,
            coord.InterpreterFallbackCount,
            IsSpinWatchdogTripped)
        : _lastSnapshotPureStaticReady && !IsSpinWatchdogTripped;

    /// <summary>Deprecated pre-reSlate v0.3.0 transition compatibility alias of <see cref="IsPureStaticReady"/>.</summary>
    [Obsolete("Use IsPureStaticReady for R3 Pure Static readiness or IsBringupCaptureClean for bringup capture completeness.")]
    [JsonIgnore]
    public bool IsR4PromotionReady => IsPureStaticReady;

    /// <summary>Computes the canonical Standard 18 <c>isPureStaticReady</c> gate from execution counters.</summary>
    public static bool ComputeIsPureStaticReady(
        long compiledDispatchCount,
        long jitDispatchCount,
        long threadedIrDispatchCount,
        long interpreterFallbackCount,
        bool isSpinWatchdogTripped) =>
        compiledDispatchCount > 0L
        && jitDispatchCount == 0L
        && threadedIrDispatchCount == 0L
        && interpreterFallbackCount == 0L
        && !isSpinWatchdogTripped;

    internal void AttachCoordinator(Tier3ExecutionCoordinator? coordinator) => _attachedCoordinator = coordinator;

    /// <summary>Creates a recorder whose <c>bringup.profile.json</c> path follows <see cref="AotCacheLayoutHelper"/>.</summary>
    public static JitBringupRecorder CreateForModule(
        string dataDir,
        string moduleId,
        string cacheVersionSegment,
        string executionTierMode = ReStationExecutionTierModes.JitBringup,
        long spinWatchdogCycleThreshold = DefaultSpinWatchdogCycleThreshold)
    {
        string profilePath = AotCacheLayoutHelper.ResolveBringupProfilePath(dataDir, moduleId, cacheVersionSegment);
        return new JitBringupRecorder(
            moduleId,
            executionTierMode,
            RuntimeFeature.IsDynamicCodeSupported,
            profilePath,
            spinWatchdogCycleThreshold);
    }

    /// <summary>Records an executed guest code block by clean-room digest, PC, size, and overlay ID.</summary>
    public void RecordDiscoveredBlock(string codeSha256Hex, uint entryPc, uint codeSizeBytes, int overlayId = 0)
    {
        if (string.IsNullOrWhiteSpace(codeSha256Hex) || codeSizeBytes == 0u)
            return;

        string normalizedSha = codeSha256Hex.Trim().ToLowerInvariant();
        var key = (normalizedSha, entryPc, codeSizeBytes, overlayId);
        lock (_sync)
        {
            _blocks[key] = _blocks.TryGetValue(key, out long prev) ? prev + 1L : 1L;
        }
    }

    /// <summary>Records an observed dynamic jump edge (<c>jr</c>/<c>jalr</c>) for AOT promotion recovery.</summary>
    public void RecordIndirectJumpTarget(uint sourcePc, uint targetPc)
    {
        var key = (sourcePc, targetPc);
        lock (_sync)
        {
            _indirectJumps[key] = _indirectJumps.TryGetValue(key, out long prev) ? prev + 1L : 1L;
        }
    }

    /// <summary>Advances non-VBlank spin cycle counter and returns true if the watchdog threshold was crossed.</summary>
    public bool AdvanceCyclesSinceVBlank(long deltaCycles)
    {
        if (deltaCycles > 0L)
            NonVBlankSpinCycles += deltaCycles;
        if (NonVBlankSpinCycles >= SpinWatchdogCycleThreshold)
            IsSpinWatchdogTripped = true;
        return IsSpinWatchdogTripped;
    }

    /// <summary>Resets the non-VBlank spin accumulator on VBlank interrupt/frame edge.</summary>
    public void ResetVBlankSpinCounter()
    {
        NonVBlankSpinCycles = 0L;
    }

    /// <summary>Builds a point-in-time clean-room <see cref="BringupProfileDocument"/> snapshot.</summary>
    public BringupProfileDocument BuildSnapshot(
        Tier3ExecutionCoordinator? coordinator = null,
        long compiledDispatchCount = 0L,
        long interpreterFallbackCount = 0L)
    {
        BringupFunctionRecord[] entries;
        BringupIndirectJumpRecord[] jumps;
        lock (_sync)
        {
            entries = _blocks
                .OrderBy(kv => kv.Key.Overlay)
                .ThenBy(kv => kv.Key.Pc)
                .Select(kv => new BringupFunctionRecord(
                    kv.Key.Sha,
                    $"0x{kv.Key.Pc:X8}",
                    kv.Key.Pc,
                    kv.Key.Size,
                    kv.Key.Overlay,
                    kv.Value))
                .ToArray();

            jumps = _indirectJumps
                .OrderBy(kv => kv.Key.Src)
                .ThenBy(kv => kv.Key.Dst)
                .Select(kv => new BringupIndirectJumpRecord(
                    $"0x{kv.Key.Src:X8}",
                    kv.Key.Src,
                    $"0x{kv.Key.Dst:X8}",
                    kv.Key.Dst,
                    kv.Value))
                .ToArray();
        }

        Tier3ExecutionCoordinator? effectiveCoord = coordinator ?? _attachedCoordinator;
        long compiled = Math.Max(compiledDispatchCount, effectiveCoord?.CompiledDispatchCount ?? 0L);
        long jit = effectiveCoord?.JitDispatchCount ?? 0L;
        long threadedIr = effectiveCoord?.ThreadedIrDispatchCount ?? 0L;
        long interp = Math.Max(interpreterFallbackCount, effectiveCoord?.InterpreterFallbackCount ?? 0L);
        bool pureStaticReady = ComputeIsPureStaticReady(compiled, jit, threadedIr, interp, IsSpinWatchdogTripped);
        _lastSnapshotPureStaticReady = pureStaticReady;

        return new BringupProfileDocument
        {
            SchemaVersion = 1,
            ModuleId = ModuleId,
            EngineId = ReStationEngineInfo.EngineId,
            EngineVersion = ReStationEngineInfo.Version,
            ExecutionTierMode = effectiveCoord?.ExecutionTierMode ?? ExecutionTierMode,
            IsDynamicCodeSupported = effectiveCoord?.IsDynamicCodeSupported ?? IsDynamicCodeSupported,
            CompiledDispatchCount = compiled,
            JitDispatchCount = jit,
            ThreadedIrDispatchCount = threadedIr,
            InterpreterFallbackCount = interp,
            RamPageInvalidationCount = effectiveCoord?.RamPageInvalidationCount ?? 0L,
            DiscoveredFunctionCount = entries.Length,
            IsPureStaticReady = pureStaticReady,
            NonVBlankSpinCycles = NonVBlankSpinCycles,
            IsSpinWatchdogTripped = IsSpinWatchdogTripped,
            UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            Entries = entries,
            IndirectJumpTargets = jumps
        };
    }

    /// <summary>Atomically writes <see cref="BringupProfilePath"/> via temp file + move.</summary>
    public string WriteProfileAtomic(
        Tier3ExecutionCoordinator? coordinator = null,
        long compiledDispatchCount = 0L,
        long interpreterFallbackCount = 0L)
    {
        string targetPath = BringupProfilePath;
        string dir = Path.GetDirectoryName(targetPath) ?? ".";
        Directory.CreateDirectory(dir);
        string tmpPath = $"{targetPath}.tmp.{Guid.NewGuid():N}";
        BringupProfileDocument doc = BuildSnapshot(coordinator, compiledDispatchCount, interpreterFallbackCount);
        string json = JsonSerializer.Serialize(doc, JsonOptions);

        try
        {
            File.WriteAllText(tmpPath, json, new UTF8Encoding(false));
            File.Move(tmpPath, targetPath, overwrite: true);
            return targetPath;
        }
        finally
        {
            if (File.Exists(tmpPath))
            {
                try { File.Delete(tmpPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
