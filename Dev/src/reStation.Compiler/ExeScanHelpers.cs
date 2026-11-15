// @module: reStation.Compiler.ExeScanHelpers
// @purpose: PS-X EXE function discovery via functions.json or automatic Pc0/JAL/prologue/jump-table analysis plus gap closure.
// @depends-on: System.Buffers.Binary, System.Text.Json, reStation.Compiler.DiscExeLoader, reStation.Compiler.MipsDecoder
// @exports: GeneratedFnSpec, GeneratedExeSpec, ExeScanHelpers
// @owner: devv-compiler (T5)

using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;

namespace reStation.Compiler;

/// <summary>One discovered MIPS function range inside a PS-X executable.</summary>
public sealed record GeneratedFnSpec(uint Addr, string MethodName, uint EndAddr);

/// <summary>Lifting descriptor for one PS-X executable.</summary>
public sealed record GeneratedExeSpec(
    int ExeId,
    string ExeName,
    string RelativePath,
    string FolderName,
    string ClassName,
    uint TextStart,
    uint TextEnd,
    List<GeneratedFnSpec> Functions)
{
    public bool IsOverlay { get; init; }
    public uint LoadRamAddress { get; init; }
    public int ContentSizeBytes { get; init; }
    public string ContentSha256Hex { get; init; } = string.Empty;
}

/// <summary>Discovers function boundaries from functions.json or automatic PS-X instruction scanning.</summary>
public static class ExeScanHelpers
{
    /// <summary>Resolves <c>functions.json</c> for an executable if present in <c>--functions-json</c> or <c>--decomp-dir</c>.</summary>
    internal static string? ResolveFunctionsJson(RecompCompilerOptions options, DiscoveredExe exe)
    {
        if (!string.IsNullOrWhiteSpace(options.FunctionsJsonPath))
            return Path.GetFullPath(options.FunctionsJsonPath);
        if (string.IsNullOrWhiteSpace(options.DecompDir)) return null;

        string root = Path.GetFullPath(options.DecompDir);
        string[] candidates =
        {
            Path.Combine(root, exe.ExeName, "functions.json"),
            Path.Combine(root, "decompiled", exe.ExeName, "functions.json"),
            Path.Combine(root, exe.FolderName, "functions.json"),
            Path.Combine(root, "functions.json")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Scans 32-bit aligned words in the EXE image for candidate pointers into <c>[textAddr, textEnd)</c>.</summary>
    public static HashSet<uint> ScanJumpTargets(byte[] exeBytes, uint textAddr, uint textEnd, int startOffset = 0x800)
    {
        var targets = new HashSet<uint>();
        for (int off = startOffset; off + 4 <= exeBytes.Length; off += 4)
        {
            uint w = BinaryPrimitives.ReadUInt32LittleEndian(exeBytes.AsSpan(off, 4));
            if (w >= textAddr && w < textEnd && (w & 3U) == 0U) targets.Add(w);
        }
        return targets;
    }

    /// <summary>Produces a sorted, gap-closed list of functions for <paramref name="exe"/>.</summary>
    internal static (List<GeneratedFnSpec> Functions, HashSet<uint> RdataTargets) DiscoverFunctions(
        DiscoveredExe exe,
        Func<uint, uint> readWord,
        string? functionsJsonPath,
        IReadOnlyCollection<uint>? extraSeedEntries = null)
    {
        uint textAddr = exe.Header.TextAddr;
        uint textEnd = exe.Header.TextEnd;
        HashSet<uint> rdataTargets = ScanJumpTargets(exe.Bytes, textAddr, textEnd, exe.IsOverlay ? 0 : 0x800);
        List<(uint Addr, uint Size)> rawList = functionsJsonPath is not null
            ? LoadFunctionsJson(functionsJsonPath, textAddr, textEnd)
            : new List<(uint Addr, uint Size)>();

        bool autoDiscovered = rawList.Count == 0;
        if (autoDiscovered) SeedAutomaticFunctions(exe.Header, readWord, rawList);

        if (extraSeedEntries is { Count: > 0 })
        {
            var existing = new HashSet<uint>(rawList.Select(r => r.Addr));
            foreach (uint rawSeed in extraSeedEntries)
            {
                uint seed = 0x80000000U | (rawSeed & 0x1FFFFFFFU);
                if (seed >= textAddr && seed < textEnd && (seed & 3U) == 0U && existing.Add(seed))
                    rawList.Add((seed, 4U));
            }
            rawList = rawList.OrderBy(r => r.Addr).ToList();
        }

        uint codeEnd = DiscoverAdditionalEntries(rawList, rdataTargets, readWord, textAddr, textEnd, autoDiscovered);
        var funcs = new List<GeneratedFnSpec>(rawList.Count);
        for (int i = 0; i < rawList.Count; i++)
        {
            uint addr = rawList[i].Addr;
            uint nextLimit = i + 1 < rawList.Count ? rawList[i + 1].Addr : codeEnd;
            uint maxEnd = ComputeFunctionEnd(addr, rawList[i].Size, nextLimit, readWord, rdataTargets, i + 1 < rawList.Count);
            funcs.Add(new GeneratedFnSpec(addr, $"Fn_{addr:X8}", maxEnd));
        }
        return (funcs, rdataTargets);
    }

    private static List<(uint Addr, uint Size)> LoadFunctionsJson(string jsonPath, uint textAddr, uint textEnd)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
        JsonElement arr = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement
            : (doc.RootElement.TryGetProperty("functions", out JsonElement fnProp) ? fnProp : default);
        if (arr.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"Invalid functions manifest '{jsonPath}': expected array or 'functions' property.");

        var list = new List<(uint Addr, uint Size)>();
        foreach (JsonElement item in arr.EnumerateArray())
        {
            if (!TryGetAddress(item, out uint addr)) continue;
            addr = 0x80000000U | (addr & 0x1FFFFFFFU);
            if (addr < textAddr || addr >= textEnd) continue;
            uint size = item.TryGetProperty("size", out JsonElement sz) && sz.TryGetUInt32(out uint s) ? s : 4U;
            list.Add((addr, Math.Max(4U, (size + 3U) & ~3U)));
        }
        return list.OrderBy(x => x.Addr).DistinctBy(x => x.Addr).ToList();
    }

    private static bool TryGetAddress(JsonElement item, out uint addr)
    {
        addr = 0;
        foreach (string key in new[] { "address", "addr", "start" })
        {
            if (!item.TryGetProperty(key, out JsonElement prop)) continue;
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetUInt32(out addr)) return true;
            if (prop.ValueKind == JsonValueKind.String)
            {
                string raw = (prop.GetString() ?? string.Empty).Trim();
                if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    return uint.TryParse(raw.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out addr);
                return uint.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out addr);
            }
        }
        return false;
    }

    private static void SeedAutomaticFunctions(PsxExeHeader hdr, Func<uint, uint> readWord, List<(uint Addr, uint Size)> rawList)
    {
        var seeds = new HashSet<uint>();
        if (hdr.Pc0 >= hdr.TextAddr && hdr.Pc0 < hdr.TextEnd) seeds.Add(hdr.Pc0);
        else seeds.Add(hdr.TextAddr);

        bool inDelay = false;
        for (uint p = hdr.TextAddr; p < hdr.TextEnd; p += 4U)
        {
            uint w = readWord(p);
            if (w == 0U) { inDelay = false; continue; }
            MipsInsn ins = MipsDecoder.Decode(p, w);
            if (!inDelay && (ins.Op == 3 || ins.IsLinkBranch))
            {
                uint tgt = ins.BranchTarget;
                if (tgt >= hdr.TextAddr && tgt < hdr.TextEnd && (tgt & 3U) == 0U && MipsDecoder.IsKnownInstruction(MipsDecoder.Decode(tgt, readWord(tgt))))
                    seeds.Add(tgt);
            }
            // Standard MIPS stack-frame prologue: addiu $sp, $sp, -N
            if (!inDelay && ins.Op == 9 && ins.Rs == 29 && ins.Rt == 29 && ins.Simm < 0 && LooksLikePrologueStart(p, hdr.TextEnd, readWord))
                seeds.Add(p);
            inDelay = !inDelay && ins.IsBranchOrJump;
        }

        foreach (uint s in seeds.OrderBy(x => x)) rawList.Add((s, 4U));
    }

    private static bool LooksLikePrologueStart(uint addr, uint textEnd, Func<uint, uint> readWord)
    {
        for (int i = 0; i < 4 && addr + (uint)(i * 4) < textEnd; i++)
        {
            MipsInsn ins = MipsDecoder.Decode(addr + (uint)(i * 4), readWord(addr + (uint)(i * 4)));
            if (!MipsDecoder.IsKnownInstruction(in ins)) return false;
        }
        return true;
    }

    public static uint InferFunctionEnd(uint addr, uint minSize, uint nextLimit, Func<uint, uint> readWord, bool hasNextFn = false)
    {
        uint furthest = Math.Clamp(addr + (minSize > 4U ? minSize : 4U), addr + 4U, nextLimit);
        bool inDelay = false;
        for (uint p = addr; p < nextLimit; p += 4U)
        {
            MipsInsn ins = MipsDecoder.Decode(p, readWord(p));
            if (!inDelay && (ins.Op is 1 or 4 or 5 or 6 or 7 || ins.Op == 2) && !ins.IsLinkBranch &&
                ins.BranchTarget >= addr && ins.BranchTarget < nextLimit)
                furthest = Math.Max(furthest, Math.Min(nextLimit, ins.BranchTarget + 8U));
            bool isRet = !inDelay && ins.Op == 0 && ins.Funct == 8 && ins.Rs == 31;
            bool isOutJump = !inDelay && ins.Op == 2 && (ins.BranchTarget < addr || ins.BranchTarget >= nextLimit);
            if ((isRet || isOutJump) && p + 4U >= furthest) return Math.Min(nextLimit, p + 8U);
            inDelay = !inDelay && ins.IsBranchOrJump;
        }
        return hasNextFn ? nextLimit : furthest;
    }

    public static uint ComputeFunctionEnd(uint addr, uint size, uint nextLimit, Func<uint, uint> readWord, IReadOnlySet<uint> rdataTargets, bool hasNextFn = false)
    {
        uint maxEnd = InferFunctionEnd(addr, size, nextLimit, readWord, hasNextFn);
        if (maxEnd < nextLimit)
        {
            bool extendsTail = false, hasSwitch = false;
            for (uint p = addr; p < maxEnd; p += 4U)
            {
                MipsInsn ins = MipsDecoder.Decode(p, readWord(p));
                if (ins.Op is 1 or 2 or 4 or 5 or 6 or 7 && !ins.IsLinkBranch && ins.BranchTarget >= maxEnd && ins.BranchTarget < nextLimit)
                { extendsTail = true; break; }
                if (ins.Op == 0 && ins.Funct == 8 && ins.Rs != 31 && ins.Rs != 10) hasSwitch = true;
            }
            if (!extendsTail && hasSwitch && rdataTargets.Any(t => t >= maxEnd && t < nextLimit)) extendsTail = true;
            if (extendsTail) maxEnd = nextLimit;
        }
        return maxEnd;
    }

    private static uint DiscoverAdditionalEntries(
        List<(uint Addr, uint Size)> rawList, HashSet<uint> rdataTargets, Func<uint, uint> readWord,
        uint textAddr, uint textEnd, bool autoDiscovered)
    {
        if (rawList.Count == 0) return textEnd;
        uint codeStart = rawList.Min(x => x.Addr);
        uint codeEnd = rawList.Max(x => x.Addr + x.Size);
        if (autoDiscovered)
        {
            foreach (var fn in rawList)
                codeEnd = Math.Max(codeEnd, InferFunctionEnd(fn.Addr, fn.Size, textEnd, readWord, hasNextFn: false));
        }
        codeEnd = Math.Min(textEnd, codeEnd);

        var entryTargets = new HashSet<uint>(rdataTargets);
        var midTargets = new HashSet<uint>();
        ScanCodePointers(entryTargets, midTargets, readWord, codeStart, codeEnd, textEnd);
        var known = new HashSet<uint>(rawList.Select(x => x.Addr));
        List<(uint Addr, uint End)> intervals;
        bool changed;
        do
        {
            changed = false;
            rawList.Sort((a, b) => a.Addr.CompareTo(b.Addr));
            intervals = BuildIntervals(rawList, codeEnd, readWord, rdataTargets);
            for (int i = 0; i < intervals.Count; i++)
            {
                uint gapEnd = i + 1 < intervals.Count ? Math.Min(intervals[i + 1].Addr, codeEnd) : codeEnd;
                uint gapAddr = intervals[i].End;
                while (gapAddr < gapEnd && readWord(gapAddr) == 0U) gapAddr += 4U;
                if (gapAddr >= gapEnd || !LooksLikeMipsCode(gapAddr, gapEnd, readWord, out List<uint> postRets)) continue;
                if (known.Add(gapAddr)) { rawList.Add((gapAddr, Math.Min(4U, gapEnd - gapAddr))); changed = true; }
                foreach (uint r in postRets) entryTargets.Add(r);
            }
            if (changed) continue;
            foreach (uint tgt in entryTargets)
            {
                if (tgt >= codeStart && tgt < codeEnd && (tgt & 3U) == 0U &&
                    !IsPrevDelayOwner(tgt, codeStart, readWord) && !IsCovered(intervals, tgt) && known.Add(tgt))
                { rawList.Add((tgt, 4U)); changed = true; }
            }
        } while (changed);

        rawList.Sort((a, b) => a.Addr.CompareTo(b.Addr));
        intervals = BuildIntervals(rawList, codeEnd, readWord, rdataTargets);
        rdataTargets.UnionWith(entryTargets);
        rdataTargets.UnionWith(midTargets);
        foreach (var iv in intervals) if (iv.End < codeEnd) rdataTargets.Add(iv.End);
        return codeEnd;
    }

    private static List<(uint Addr, uint End)> BuildIntervals(
        List<(uint Addr, uint Size)> rawList, uint codeEnd, Func<uint, uint> readWord, IReadOnlySet<uint> rdataTargets)
    {
        var list = new List<(uint, uint)>(rawList.Count);
        for (int i = 0; i < rawList.Count; i++)
        {
            bool hasNext = i + 1 < rawList.Count;
            uint nextLimit = hasNext ? rawList[i + 1].Addr : codeEnd;
            list.Add((rawList[i].Addr, ComputeFunctionEnd(rawList[i].Addr, rawList[i].Size, nextLimit, readWord, rdataTargets, hasNext)));
        }
        return list;
    }

    private static bool IsPrevDelayOwner(uint pc, uint start, Func<uint, uint> readWord) =>
        pc >= start + 4U && MipsDecoder.Decode(pc - 4U, readWord(pc - 4U)).IsBranchOrJump;

    private static void ScanCodePointers(
        HashSet<uint> entryTargets, HashSet<uint> midTargets, Func<uint, uint> readWord, uint start, uint scanEnd, uint textEnd)
    {
        uint[] luiHi = new uint[32];
        bool[] luiValid = new bool[32];
        bool inDelay = false;
        for (uint p = start; p < scanEnd; p += 4U)
        {
            MipsInsn ins = MipsDecoder.Decode(p, readWord(p));
            if (!inDelay && ins.Op is 1 or 2 or 3 or 4 or 5 or 6 or 7)
            {
                uint tgt = ins.BranchTarget;
                if (tgt >= start && tgt < scanEnd && (tgt & 3U) == 0U)
                {
                    if (ins.Op is 2 or 3 || ins.IsLinkBranch) entryTargets.Add(tgt);
                    if (!ins.IsLinkBranch) midTargets.Add(tgt);
                }
                if ((ins.Op == 3 || ins.IsLinkBranch) && p + 8U < scanEnd) midTargets.Add(p + 8U);
            }
            else if (!inDelay && ins.Op == 0 && ins.Funct == 9 && p + 8U < scanEnd) midTargets.Add(p + 8U);
            if (ins.Op is 9 or 13 && ins.Rs != 0 && luiValid[ins.Rs])
            {
                uint ptr = ins.Op == 9 ? unchecked((uint)((int)luiHi[ins.Rs] + ins.Simm)) : (luiHi[ins.Rs] | ins.Uimm);
                if (ptr >= start && ptr < textEnd && (ptr & 3U) == 0U) entryTargets.Add(ptr);
            }
            if (ins.Op == 15 && ins.Rt != 0) { luiHi[ins.Rt] = ins.Uimm << 16; luiValid[ins.Rt] = true; }
            else for (int r = 1; r < 32; r++) if (ins.WritesReg(r)) luiValid[r] = false;
            inDelay = !inDelay && ins.IsBranchOrJump;
        }
    }

    private static bool IsCovered(List<(uint Addr, uint End)> intervals, uint pc)
    {
        int lo = 0, hi = intervals.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (pc < intervals[mid].Addr) hi = mid - 1;
            else if (pc >= intervals[mid].End) lo = mid + 1;
            else return true;
        }
        return false;
    }

    private static bool LooksLikeMipsCode(uint gapAddr, uint gapEnd, Func<uint, uint> readWord, out List<uint> postRets)
    {
        postRets = new List<uint>();
        bool hasFlow = false, inDelay = false;
        for (uint p = gapAddr; p < gapEnd; p += 4U)
        {
            uint w = readWord(p);
            if (w == 0U) { inDelay = false; continue; }
            MipsInsn ins = MipsDecoder.Decode(p, w);
            if (!MipsDecoder.IsKnownInstruction(in ins)) return false;
            if (!inDelay && ins.IsBranchOrJump) hasFlow = true;
            if (!inDelay && ins.Op == 0 && ins.Funct == 8 && ins.Rs == 31)
            {
                uint nextPc = p + 8U;
                while (nextPc < gapEnd && readWord(nextPc) == 0U) nextPc += 4U;
                if (nextPc < gapEnd) postRets.Add(nextPc);
            }
            inDelay = !inDelay && ins.IsBranchOrJump;
        }
        return hasFlow;
    }
}
