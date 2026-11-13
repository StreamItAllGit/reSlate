// @module: reStation.Compiler.MipsLifter
// @purpose: Lifts PS-X R3000A functions into C# methods targeting IReStationCpu; splits large/switch/mid-entry functions into resumable blocks with grouped coordinators.
// @depends-on: reStation.Compiler.MipsDecoder, reStation.Compiler.MipsEmitHelpers
// @exports: LiftedUnit, MipsLifter
// @owner: devv-compiler (T5)

namespace reStation.Compiler;

/// <summary>One emitted C# member (method) and its source lines.</summary>
public sealed record LiftedUnit(string Name, List<string> Lines);

public static partial class MipsLifter
{
    /// <summary>Default instruction budget per resumable block method.</summary>
    public const int DefaultChunkInstructions = 210;
    /// <summary>Smallest block size the emitter may shrink to when enforcing the generated-file line ceiling.</summary>
    public const int MinChunkInstructions = 8;
    private const int MaxCoordinatorBranches = 200;

    /// <summary>Control-flow facts shared by every block of one function.</summary>
    private sealed record FnAnalysis(HashSet<uint> Targets, HashSet<uint> BackEdges, HashSet<uint> DelaySlotTargets, bool HasDynSwitch, List<uint> MidEntries);

    /// <summary>
    /// Lifts <c>[fnStart, fnEnd)</c>. Small linear functions become one <c>internal static void Name(IReStationCpu cpu)</c>;
    /// otherwise blocks <c>Name_B{n}</c> return the next PC to a coordinator loop (plus <c>Name_At</c> for jump-table mid entries).
    /// </summary>
    public static List<LiftedUnit> LiftFunction(
        string methodName, uint fnStart, uint fnEnd, Func<uint, uint> readWord, IReadOnlySet<uint>? rdataTargets,
        out List<uint> midEntries, int chunkInstructions = DefaultChunkInstructions, bool forceChunk = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);
        ArgumentNullException.ThrowIfNull(readWord);
        int count = fnEnd > fnStart ? Math.Max(1, (int)((fnEnd - fnStart) / 4U)) : 1;
        fnEnd = fnStart + (uint)(count * 4);
        var insns = new MipsInsn[count];
        for (int i = 0; i < count; i++)
        {
            uint addr = fnStart + (uint)(i * 4);
            insns[i] = MipsDecoder.Decode(addr, readWord(addr));
        }

        FnAnalysis a = Analyze(insns, fnStart, fnEnd, rdataTargets);
        midEntries = a.MidEntries;
        int chunk = Math.Max(MinChunkInstructions, chunkInstructions);
        if (!forceChunk && count <= chunk + 30 && !a.HasDynSwitch && a.MidEntries.Count == 0)
        {
            List<string> single = EmitRangeMethod($"    internal static void {methodName}(IReStationCpu cpu)",
                insns, 0, count, fnStart, fnEnd, isChunked: false, readWord, a);
            return new List<LiftedUnit> { new(methodName, single) };
        }
        return LiftChunked(methodName, insns, fnStart, fnEnd, readWord, a, chunk);
    }

    private static List<LiftedUnit> LiftChunked(
        string m, MipsInsn[] insns, uint fnStart, uint fnEnd, Func<uint, uint> readWord, FnAnalysis a, int chunk)
    {
        List<(int Start, int End)> ranges = BuildChunkRanges(insns, a.Targets, chunk);
        var units = new List<LiftedUnit>(ranges.Count + 2);
        var bounds = new List<(uint Start, uint End, string Call)>(ranges.Count);
        for (int c = 0; c < ranges.Count; c++)
        {
            (int s, int e) = ranges[c];
            uint cs = insns[s].Addr, ce = e < insns.Length ? insns[e].Addr : fnEnd;
            units.Add(new LiftedUnit($"{m}_B{c}", EmitRangeMethod(
                $"    private static uint {m}_B{c}(IReStationCpu cpu, uint entryPc)", insns, s, e, cs, ce, isChunked: true, readWord, a)));
            bounds.Add((cs, ce, $"{m}_B{c}(cpu, nextPc)"));
        }

        if (bounds.Count > MaxCoordinatorBranches)
        {
            // Keep every coordinator under the generated-file ceiling by routing through grouped sub-dispatchers.
            var groups = new List<(uint Start, uint End, string Call)>();
            for (int g = 0, i = 0; i < bounds.Count; g++, i += MaxCoordinatorBranches)
            {
                List<(uint Start, uint End, string Call)> slice = bounds.GetRange(i, Math.Min(MaxCoordinatorBranches, bounds.Count - i));
                var gl = new List<string>(slice.Count + 4) { $"    private static uint {m}_G{g}(IReStationCpu cpu, uint nextPc)", "    {" };
                AppendRangeChain(gl, slice, "        ", "return ", "cpu.JumpIndirect(nextPc);");
                gl.Add("        return 0U;");
                gl.Add("    }");
                units.Add(new LiftedUnit($"{m}_G{g}", gl));
                groups.Add((slice[0].Start, slice[^1].End, $"{m}_G{g}(cpu, nextPc)"));
            }
            bounds = groups;
        }

        var coord = new List<string>(bounds.Count + 12);
        if (a.MidEntries.Count > 0)
        {
            coord.Add($"    internal static void {m}(IReStationCpu cpu) => {m}_At(cpu, 0x{fnStart:X8}U);");
            coord.Add($"    internal static void {m}_At(IReStationCpu cpu, uint entryPc)");
            coord.Add("    {");
            coord.Add("        uint nextPc = entryPc;");
        }
        else
        {
            coord.Add($"    internal static void {m}(IReStationCpu cpu)");
            coord.Add("    {");
            coord.Add($"        uint nextPc = 0x{fnStart:X8}U;");
        }
        coord.Add("        while (nextPc != 0U && !cpu.ShouldUnwind)");
        coord.Add("        {");
        AppendRangeChain(coord, bounds, "            ", "nextPc = ", "{ cpu.JumpIndirect(nextPc); return; }");
        coord.Add("        }");
        coord.Add("    }");
        units.Add(new LiftedUnit(m, coord));
        return units;
    }

    private static void AppendRangeChain(List<string> lines, List<(uint Start, uint End, string Call)> bounds, string indent, string assign, string fallback)
    {
        for (int i = 0; i < bounds.Count; i++)
        {
            string kw = i == 0 ? "if" : "else if";
            lines.Add($"{indent}{kw} (nextPc >= 0x{bounds[i].Start:X8}U && nextPc < 0x{bounds[i].End:X8}U) {assign}{bounds[i].Call};");
        }
        lines.Add($"{indent}else {fallback}");
    }

    private static List<(int Start, int End)> BuildChunkRanges(MipsInsn[] insns, HashSet<uint> targets, int chunk)
    {
        var ranges = new List<(int, int)>();
        int estCap = chunk + chunk / 3;
        int start = 0;
        while (start < insns.Length)
        {
            int end = start, est = 0;
            while (end < insns.Length && end - start < chunk && est < estCap)
                est += targets.Contains(insns[end++].Addr) ? 3 : 1;
            if (end < insns.Length && insns[end - 1].IsLoad && insns[end - 1].Rt != 0) end++;
            if (end < insns.Length && insns[end - 1].IsBranchOrJump) end++;
            end = Math.Min(insns.Length, Math.Max(start + 1, end));
            ranges.Add((start, end));
            start = end;
        }
        return ranges;
    }

    private static FnAnalysis Analyze(MipsInsn[] insns, uint fnStart, uint fnEnd, IReadOnlySet<uint>? rdataTargets)
    {
        var targets = new HashSet<uint>();
        var backEdges = new HashSet<uint>();
        var delayTargets = new HashSet<uint>();
        var mids = new List<uint>();
        bool dynSwitch = false;
        for (int i = 0; i < insns.Length; i++)
        {
            MipsInsn ins = insns[i];
            if (ins.Op is 1 or 2 or 4 or 5 or 6 or 7 && !ins.IsLinkBranch)
            {
                uint tgt = ins.BranchTarget;
                if (tgt >= fnStart && tgt < fnEnd && (tgt - fnStart) % 4U == 0U)
                {
                    targets.Add(tgt);
                    if (IsDelaySlot(insns, fnStart, tgt)) delayTargets.Add(tgt);
                    if (tgt <= ins.Addr) backEdges.Add(tgt);
                }
            }
            if (ins.Op == 0 && ins.Funct == 8 && ins.Rs != 31) dynSwitch = true;
            if (ins.IsBranchOrJump) i++;
        }

        if (rdataTargets is not null)
        {
            foreach (uint t in rdataTargets)
            {
                if (t < fnStart || t >= fnEnd || (t - fnStart) % 4U != 0U) continue;
                targets.Add(t);
                if (IsDelaySlot(insns, fnStart, t)) delayTargets.Add(t);
                if (t > fnStart) mids.Add(t);
            }
        }

        if (dynSwitch)
        {
            for (int i = 0; i < insns.Length; i++)
            {
                if (insns[i].IsUnconditionalExitOrBranch && i + 2 < insns.Length) targets.Add(insns[i + 2].Addr);
                if (insns[i].IsBranchOrJump) i++;
            }
        }
        mids.Sort();
        return new FnAnalysis(targets, backEdges, delayTargets, dynSwitch, mids);
    }

    private static bool IsDelaySlot(MipsInsn[] insns, uint fnStart, uint tgt)
    {
        int idx = (int)((tgt - fnStart) / 4U);
        return idx > 0 && idx < insns.Length && insns[idx - 1].IsBranchOrJump;
    }
}
