// @module: reStation.Core.Jit.Tier3ExecutionCoordinator
// @purpose: Bounded 16,384-block collectible Tier 3 cache coordinating RyuJIT, zero-RWX Threaded-IR, and SMC demotion.
// @depends-on: reStation.Contracts.IReStationCpu, reStation.Core.Ir.MipsIrLowerer, reStation.Core.Jit.RamPageEpochTable, reStation.Core.Bringup.JitBringupRecorder
// @exports: Tier3ExecutionCoordinator

using reStation.Contracts;
using reStation.Core.Bringup;
using reStation.Core.Ir;

namespace reStation.Core.Jit;

/// <summary>Coordinates collectible RyuJIT and zero-RWX Threaded-IR block compilation, LRU caching, and hot-SMC demotion.</summary>
public sealed class Tier3ExecutionCoordinator
{
    public const int MaxCachedBlocks = 16_384;
    public const int SmcDemotionThreshold = 8;

    private sealed class BlockSlot
    {
        public readonly (uint EntryPc, int ExeId) Key;
        public IrBasicBlock Block;
        public BlockEpochStamp Stamp;
        public Func<IReStationCpu, uint>? JitDelegate;
        public int SmcInvalidationCount;
        public bool DemotedToThreadedIr;

        public BlockSlot((uint EntryPc, int ExeId) key, IrBasicBlock block, BlockEpochStamp stamp)
        {
            Key = key;
            Block = block;
            Stamp = stamp;
        }
    }

    private readonly object _sync = new();
    private readonly Dictionary<(uint EntryPc, int ExeId), LinkedListNode<BlockSlot>> _cache = new();
    private readonly LinkedList<BlockSlot> _lru = new();
    private readonly RyuJitBlockBackend _ryuJit;
    private readonly ThreadedIrBackend _threadedIr;
    private volatile JitBringupRecorder? _bringupRecorder;
    private string _executionTierMode;
    private volatile bool _forceZeroRwx;

    private long _compiledDispatchCount;
    private long _jitDispatchCount;
    private long _threadedIrDispatchCount;
    private long _interpreterFallbackCount;
    private long _demotedHotSmcBlockCount;
    private long _blockRecompileCount;

    public Tier3ExecutionCoordinator(
        RamPageEpochTable? epochTable = null,
        string executionTierMode = ReStationExecutionTierModes.Default,
        bool forceZeroRwx = false,
        JitBringupRecorder? bringupRecorder = null,
        Action<IReStationCpu, IrInsn>? copFallback = null)
    {
        EpochTable = epochTable ?? new RamPageEpochTable();
        _executionTierMode = ReStationExecutionTierModes.Normalize(executionTierMode);
        _forceZeroRwx = forceZeroRwx;
        _bringupRecorder = bringupRecorder;
        _ryuJit = new RyuJitBlockBackend();
        _threadedIr = new ThreadedIrBackend(copFallback);
    }

    public RamPageEpochTable EpochTable { get; }
    public string ExecutionTierMode
    {
        get => Volatile.Read(ref _executionTierMode);
        set => Volatile.Write(ref _executionTierMode, ReStationExecutionTierModes.Normalize(value));
    }
    public bool ForceZeroRwx
    {
        get => _forceZeroRwx;
        set => _forceZeroRwx = value;
    }
    public JitBringupRecorder? BringupRecorder
    {
        get => _bringupRecorder;
        set => _bringupRecorder = value;
    }
    public bool IsDynamicCodeSupported => !ForceZeroRwx && _ryuJit.IsDynamicCodeSupported;

    public long CompiledDispatchCount => Volatile.Read(ref _compiledDispatchCount);
    public long JitDispatchCount => Volatile.Read(ref _jitDispatchCount);
    public long ThreadedIrDispatchCount => Volatile.Read(ref _threadedIrDispatchCount);
    public long InterpreterFallbackCount => Volatile.Read(ref _interpreterFallbackCount);
    public long RamPageInvalidationCount => EpochTable.RamPageInvalidationCount;
    public long DemotedHotSmcBlockCount => Volatile.Read(ref _demotedHotSmcBlockCount);
    public long BlockRecompileCount => Volatile.Read(ref _blockRecompileCount);

    public int CachedBlockCount
    {
        get { lock (_sync) return _cache.Count; }
    }

    /// <summary>Executes or compiles+caches a dynamic block at <paramref name="entryPc"/> and returns the next guest PC.</summary>
    public uint ExecuteBlock(
        IReStationCpu cpu,
        uint entryPc,
        Func<uint, uint> readWord,
        int overlayId = 0,
        int maxInstructions = MipsIrLowerer.DefaultMaxBlockInstructions)
    {
        ArgumentNullException.ThrowIfNull(cpu);
        ArgumentNullException.ThrowIfNull(readWord);

        (BlockSlot slot, bool preferRyuJit) = AcquireOrRefreshSlot(entryPc, overlayId, readWord, maxInstructions);

        uint nextPc;
        if (preferRyuJit && slot.JitDelegate is not null)
        {
            nextPc = slot.JitDelegate(cpu);
            Interlocked.Increment(ref _jitDispatchCount);
        }
        else
        {
            nextPc = _threadedIr.ExecuteBlock(cpu, slot.Block);
            Interlocked.Increment(ref _threadedIrDispatchCount);
        }

        if (_bringupRecorder is { } recorder)
        {
            recorder.RecordDiscoveredBlock(slot.Block.CodeSha256Hex, slot.Block.EntryPc, slot.Block.GuestByteSize, overlayId);
            IrInsn[] insns = slot.Block.Instructions;
            for (int i = 0; i < insns.Length; i++)
            {
                if (insns[i].Op is IrOp.Jr or IrOp.Jalr)
                {
                    recorder.RecordIndirectJumpTarget(insns[i].Pc, nextPc);
                    break;
                }
            }
        }

        return nextPc;
    }

    /// <summary>Invalidates affected RAM pages and 64-byte cachelines across <c>KUSEG/KSEG0/KSEG1</c> writes.</summary>
    public bool TryInvalidateRamRange(uint guestAddress, uint byteLength) =>
        EpochTable.InvalidateRamRange(guestAddress, byteLength);

    public void RecordCompiledDispatch(long count = 1L)
    {
        if (count > 0L) Interlocked.Add(ref _compiledDispatchCount, count);
    }

    public void RecordInterpreterFallback(long count = 1L)
    {
        if (count > 0L) Interlocked.Add(ref _interpreterFallbackCount, count);
    }

    public void RecordIndirectJumpTarget(uint sourcePc, uint targetPc) =>
        _bringupRecorder?.RecordIndirectJumpTarget(sourcePc, targetPc);

    /// <summary>Returns the current SMC invalidation count for <paramref name="entryPc"/> (zero if uncached).</summary>
    public int GetBlockSmcInvalidationCount(uint entryPc, int overlayId = 0)
    {
        lock (_sync)
        {
            return _cache.TryGetValue((entryPc, overlayId), out LinkedListNode<BlockSlot>? node)
                ? node.Value.SmcInvalidationCount
                : 0;
        }
    }

    /// <summary>Returns true if <paramref name="entryPc"/> has been demoted to zero-RWX Threaded-IR due to hot SMC.</summary>
    public bool IsBlockDemotedToThreadedIr(uint entryPc, int overlayId = 0)
    {
        lock (_sync)
        {
            return _cache.TryGetValue((entryPc, overlayId), out LinkedListNode<BlockSlot>? node)
                && node.Value.DemotedToThreadedIr;
        }
    }

    /// <summary>Drops all cached blocks and compiled delegates so dynamic methods can be garbage collected.</summary>
    public void ClearCache()
    {
        lock (_sync)
        {
            foreach (LinkedListNode<BlockSlot> node in _cache.Values)
                node.Value.JitDelegate = null;
            _cache.Clear();
            _lru.Clear();
        }
    }

    private (BlockSlot Slot, bool PreferRyuJit) AcquireOrRefreshSlot(
        uint entryPc,
        int overlayId,
        Func<uint, uint> readWord,
        int maxInstructions)
    {
        (uint EntryPc, int OverlayId) key = (entryPc, overlayId);
        bool allowRyuJitByMode = !ForceZeroRwx
            && _ryuJit.IsDynamicCodeSupported
            && !string.Equals(ExecutionTierMode, ReStationExecutionTierModes.AotWithThreadedIr, StringComparison.Ordinal);

        lock (_sync)
        {
            if (_cache.TryGetValue(key, out LinkedListNode<BlockSlot>? node))
            {
                BlockSlot slot = node.Value;
                _lru.Remove(node);
                _lru.AddFirst(node);

                if (EpochTable.IsStampValid(ref slot.Stamp))
                    return (slot, allowRyuJitByMode && !slot.DemotedToThreadedIr);

                slot.JitDelegate = null;
                slot.SmcInvalidationCount++;
                Interlocked.Increment(ref _blockRecompileCount);

                if (!slot.DemotedToThreadedIr && slot.SmcInvalidationCount > SmcDemotionThreshold)
                {
                    slot.DemotedToThreadedIr = true;
                    Interlocked.Increment(ref _demotedHotSmcBlockCount);
                }

                IrBasicBlock refreshed = MipsIrLowerer.LowerBlock(entryPc, readWord, maxInstructions, overlayId);
                slot.Block = refreshed;
                slot.Stamp = EpochTable.CaptureStamp(refreshed);
                if (allowRyuJitByMode && !slot.DemotedToThreadedIr)
                    slot.JitDelegate = _ryuJit.CompileBlock(refreshed);
                return (slot, allowRyuJitByMode && !slot.DemotedToThreadedIr);
            }

            if (_cache.Count >= MaxCachedBlocks && _lru.Last is not null)
            {
                LinkedListNode<BlockSlot> oldest = _lru.Last;
                _lru.RemoveLast();
                _cache.Remove(oldest.Value.Key);
                oldest.Value.JitDelegate = null;
            }

            IrBasicBlock block = MipsIrLowerer.LowerBlock(entryPc, readWord, maxInstructions, overlayId);
            BlockEpochStamp stamp = EpochTable.CaptureStamp(block);
            var created = new BlockSlot(key, block, stamp);
            if (allowRyuJitByMode)
                created.JitDelegate = _ryuJit.CompileBlock(block);
            var newNode = new LinkedListNode<BlockSlot>(created);
            _lru.AddFirst(newNode);
            _cache[key] = newNode;
            return (created, allowRyuJitByMode);
        }
    }
}
