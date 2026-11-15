// @module: reStation.Core.Jit.RamPageEpochTable
// @purpose: 512-page (4 KB) RAM epoch table with 64-byte cacheline sub-tracking across KUSEG/KSEG0/KSEG1 aliases.
// @depends-on: reStation.Core.Ir.IrBasicBlock
// @exports: RamPageEpochTable, BlockEpochStamp

using reStation.Core.Ir;

namespace reStation.Core.Jit;

/// <summary>Snapshot of page epochs and 64-byte code cacheline epochs captured when a block is compiled.</summary>
public readonly struct BlockEpochStamp
{
    public readonly uint StartPage;
    public readonly uint EndPage;
    public readonly ulong StartLineMask;
    public readonly ulong EndLineMask;
    public readonly uint StartPageEpoch;
    public readonly uint EndPageEpoch;
    public readonly ulong StartLineEpochDigest;
    public readonly ulong EndLineEpochDigest;

    public BlockEpochStamp(
        uint startPage,
        uint endPage,
        ulong startLineMask,
        ulong endLineMask,
        uint startPageEpoch,
        uint endPageEpoch,
        ulong startLineEpochDigest,
        ulong endLineEpochDigest)
    {
        StartPage = startPage;
        EndPage = endPage;
        StartLineMask = startLineMask;
        EndLineMask = endLineMask;
        StartPageEpoch = startPageEpoch;
        EndPageEpoch = endPageEpoch;
        StartLineEpochDigest = startLineEpochDigest;
        EndLineEpochDigest = endLineEpochDigest;
    }

    public bool SpansTwoPages => StartPage != EndPage;
}

/// <summary>Tracks executable RAM coherence across PS1's 2 MB main RAM (<c>512 x 4 KB</c> pages, <c>64 B</c> cachelines).</summary>
public sealed class RamPageEpochTable
{
    public const int PageCount = 512;
    public const int PageSizeBytes = 4096;
    public const int CachelinesPerPage = 64;
    public const int CachelineSizeBytes = 64;
    public const uint MainRamSizeBytes = 0x0020_0000u;

    private readonly uint[] _pageEpochs = new uint[PageCount];
    private readonly uint[] _cachelineEpochs = new uint[PageCount * CachelinesPerPage];
    private readonly ulong[] _writtenLineMasks = new ulong[PageCount];
    private readonly ulong[] _codeLineMasks = new ulong[PageCount];
    private long _ramPageInvalidationCount;

    public RamPageEpochTable()
    {
        Array.Fill(_pageEpochs, 1u);
        Array.Fill(_cachelineEpochs, 1u);
    }

    /// <summary>Total number of 4 KB page invalidation transitions recorded across main RAM writes.</summary>
    public long RamPageInvalidationCount => Volatile.Read(ref _ramPageInvalidationCount);

    /// <summary>Returns true if <paramref name="address"/> aliases PS1's 2 MB main RAM (<c>KUSEG/KSEG0/KSEG1</c>).</summary>
    public static bool IsMainRamAddress(uint address) => (address & 0x1FFF_FFFFu) < 0x0080_0000u;

    /// <summary>Masks a guest virtual/physical address into the 2 MB main RAM offset <c>0..0x1FFFFF</c>.</summary>
    public static uint ToMainRamOffset(uint address) => address & 0x001F_FFFFu;

    /// <summary>Returns the canonical physical page index (<c>0..511</c>) for a main RAM address.</summary>
    public static int ToPageIndex(uint address) => (int)(ToMainRamOffset(address) >> 12);

    /// <summary>Returns the current epoch of the 4 KB RAM page containing <paramref name="address"/>.</summary>
    public uint GetPageEpoch(uint address) => Volatile.Read(ref _pageEpochs[ToPageIndex(address)]);

    /// <summary>Returns the cumulative bitmask of 64-byte cachelines invalidated within the page containing <paramref name="address"/>.</summary>
    public ulong GetWrittenCachelineMask(uint address) => Volatile.Read(ref _writtenLineMasks[ToPageIndex(address)]);

    /// <summary>Registers <paramref name="block"/>'s code footprint and captures a fast-path + cacheline stamp.</summary>
    public BlockEpochStamp CaptureStamp(IrBasicBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        int p0 = (int)(block.StartPage % PageCount);
        int p1 = (int)(block.EndPage % PageCount);

        RegisterCodeLines(p0, block.StartPageCachelineMask);
        if (p1 != p0)
            RegisterCodeLines(p1, block.EndPageCachelineMask);

        return new BlockEpochStamp(
            (uint)p0,
            (uint)p1,
            block.StartPageCachelineMask,
            block.EndPageCachelineMask,
            Volatile.Read(ref _pageEpochs[p0]),
            Volatile.Read(ref _pageEpochs[p1]),
            ComputeLineDigest(p0, block.StartPageCachelineMask),
            p1 != p0 ? ComputeLineDigest(p1, block.EndPageCachelineMask) : 0UL);
    }

    /// <summary>
    /// Validates whether <paramref name="stamp"/> remains coherent. If an unrelated cacheline in the same page mutated,
    /// returns <see langword="true"/> and updates <paramref name="stamp"/>'s page epochs without triggering recompilation.
    /// </summary>
    public bool IsStampValid(ref BlockEpochStamp stamp)
    {
        uint curP0 = Volatile.Read(ref _pageEpochs[stamp.StartPage]);
        uint curP1 = stamp.SpansTwoPages ? Volatile.Read(ref _pageEpochs[stamp.EndPage]) : curP0;
        if (curP0 == stamp.StartPageEpoch && (!stamp.SpansTwoPages || curP1 == stamp.EndPageEpoch))
            return true;

        ulong d0 = ComputeLineDigest((int)stamp.StartPage, stamp.StartLineMask);
        if (d0 != stamp.StartLineEpochDigest)
            return false;

        ulong d1 = stamp.SpansTwoPages ? ComputeLineDigest((int)stamp.EndPage, stamp.EndLineMask) : 0UL;
        if (stamp.SpansTwoPages && d1 != stamp.EndLineEpochDigest)
            return false;

        stamp = new BlockEpochStamp(
            stamp.StartPage,
            stamp.EndPage,
            stamp.StartLineMask,
            stamp.EndLineMask,
            curP0,
            curP1,
            stamp.StartLineEpochDigest,
            stamp.EndLineEpochDigest);
        return true;
    }

    /// <summary>
    /// Invalidates all 4 KB RAM pages and 64-byte cachelines touched by <c>[guestAddress, guestAddress + byteLength)</c>.
    /// Returns true if any registered executable code cacheline was intersected (self-modifying code).
    /// </summary>
    public bool InvalidateRamRange(uint guestAddress, uint byteLength)
    {
        if (byteLength == 0u || !IsMainRamAddress(guestAddress))
            return false;

        uint startOff = ToMainRamOffset(guestAddress);
        uint clampedLen = Math.Min(byteLength, MainRamSizeBytes);
        uint startLineAbs = startOff >> 6;
        uint endOff = unchecked(startOff + clampedLen - 1u);
        uint endLineAbs = (endOff & (MainRamSizeBytes - 1u)) >> 6;
        if (clampedLen >= MainRamSizeBytes)
        {
            startLineAbs = 0u;
            endLineAbs = (PageCount * CachelinesPerPage) - 1u;
        }

        bool touchedCode = false;
        uint lineCursor = startLineAbs;
        uint totalLines = PageCount * CachelinesPerPage;
        uint steps = endLineAbs >= startLineAbs
            ? (endLineAbs - startLineAbs) + 1u
            : (totalLines - startLineAbs) + endLineAbs + 1u;
        steps = Math.Min(steps, totalLines);

        int lastBumpedPage = -1;
        for (uint s = 0; s < steps; s++)
        {
            uint absLine = (lineCursor + s) % totalLines;
            int page = (int)(absLine >> 6);
            int bit = (int)(absLine & 63u);
            ulong lineMask = 1UL << bit;

            Interlocked.Increment(ref _cachelineEpochs[absLine]);
            ulong prevWritten = _writtenLineMasks[page];
            _writtenLineMasks[page] = prevWritten | lineMask;
            if ((Volatile.Read(ref _codeLineMasks[page]) & lineMask) != 0UL)
                touchedCode = true;

            if (page != lastBumpedPage)
            {
                Interlocked.Increment(ref _pageEpochs[page]);
                Interlocked.Increment(ref _ramPageInvalidationCount);
                lastBumpedPage = page;
            }
        }

        return touchedCode;
    }

    /// <summary>Resets all epochs and invalidation counters back to initial power-on state.</summary>
    public void Reset()
    {
        Array.Fill(_pageEpochs, 1u);
        Array.Fill(_cachelineEpochs, 1u);
        Array.Clear(_writtenLineMasks);
        Array.Clear(_codeLineMasks);
        Interlocked.Exchange(ref _ramPageInvalidationCount, 0L);
    }

    private void RegisterCodeLines(int page, ulong mask)
    {
        if (mask != 0UL)
            _codeLineMasks[page] |= mask;
    }

    private ulong ComputeLineDigest(int page, ulong mask)
    {
        ulong hash = 14695981039346656037UL;
        ulong remaining = mask;
        int baseIdx = page << 6;
        while (remaining != 0UL)
        {
            int bit = System.Numerics.BitOperations.TrailingZeroCount(remaining);
            uint epoch = Volatile.Read(ref _cachelineEpochs[baseIdx + bit]);
            hash ^= epoch ^ ((uint)bit * 0x9E37_79B9u);
            hash *= 1099511628211UL;
            remaining &= remaining - 1UL;
        }
        return hash;
    }
}
