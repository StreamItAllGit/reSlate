// @module: reStation.Compiler.MipsLifter.Emit
// @purpose: Range-method emission for MipsLifter: labels, cycle accounting + PollEvents checkpoints, load-delay slots, reachability, block cycle counts.
// @depends-on: reStation.Compiler.MipsDecoder, reStation.Compiler.MipsEmitHelpers
// @exports: MipsLifter (partial)
// @owner: devv-compiler (T5)

namespace reStation.Compiler;

public static partial class MipsLifter
{
    private static List<string> EmitRangeMethod(
        string header, MipsInsn[] insns, int startIdx, int endIdx, uint rangeStart, uint rangeEnd,
        bool isChunked, Func<uint, uint> readWord, FnAnalysis a)
    {
        HashSet<uint> fnTargets = a.Targets;
        var localTargets = new HashSet<uint>();
        var innerTargets = new List<uint>();
        if (isChunked)
        {
            for (int i = startIdx + 1; i < endIdx; i++)
            {
                if (!fnTargets.Contains(insns[i].Addr)) continue;
                innerTargets.Add(insns[i].Addr);
                localTargets.Add(insns[i].Addr);
            }
        }

        bool[]? reach = isChunked ? null : ComputeReachable(insns, rangeStart, rangeEnd, fnTargets);
        bool hasDynJump = false, hasCondBranch = false, hasLoadDelay = false;
        ScanRangeFeatures(insns, startIdx, endIdx, rangeStart, rangeEnd, reach, fnTargets, localTargets,
            ref hasDynJump, ref hasCondBranch, ref hasLoadDelay);

        var lines = new List<string>((endIdx - startIdx) + 28) { header, "    {" };
        int declIdx = lines.Count;
        string ret = isChunked ? "0U" : string.Empty;
        string poll = "if (cpu.TotalCycles >= cpu.NextEventCycle) {{ cpu.Pc = 0x{0:X8}U; cpu.PollEvents(); if (cpu.ShouldUnwind) return " + ret + "; }}";
        int entryCycles = CountBlockCycles(insns, startIdx, endIdx, localTargets);
        if (isChunked && innerTargets.Count > 0)
        {
            lines.Add("        if (cpu.TotalCycles >= cpu.NextEventCycle) { cpu.Pc = entryPc; cpu.PollEvents(); if (cpu.ShouldUnwind) return 0U; }");
            lines.Add("        switch (entryPc)");
            lines.Add("        {");
            foreach (uint lt in innerTargets) lines.Add($"            case 0x{lt:X8}U: goto L_{lt:X8};");
            lines.Add("        }");
            lines.Add($"        cpu.TotalCycles += {entryCycles};");
        }
        else
        {
            if (isChunked) lines.Add("        _ = entryPc;");
            lines.Add($"        cpu.TotalCycles += {entryCycles};");
            lines.Add("        " + string.Format(System.Globalization.CultureInfo.InvariantCulture, poll, rangeStart));
        }

        bool reachable = true;
        for (int i = startIdx; i < endIdx; i++)
        {
            MipsInsn ins = insns[i];
            if (reach is not null && !reach[i])
            {
                if (ins.IsBranchOrJump && i + 1 < endIdx && !localTargets.Contains(insns[i + 1].Addr)) i++;
                continue;
            }
            if (localTargets.Contains(ins.Addr))
            {
                reachable = true;
                int blk = CountBlockCycles(insns, i, endIdx, localTargets);
                if (a.BackEdges.Contains(ins.Addr))
                {
                    lines.Add($"    L_{ins.Addr:X8}:");
                    lines.Add($"        cpu.TotalCycles += {blk}; " + string.Format(System.Globalization.CultureInfo.InvariantCulture, poll, ins.Addr));
                }
                else
                {
                    lines.Add($"    L_{ins.Addr:X8}: cpu.TotalCycles += {blk};");
                }
            }
            if (!reachable)
            {
                if (ins.IsBranchOrJump && i + 1 < endIdx && !localTargets.Contains(insns[i + 1].Addr)) i++;
                continue;
            }

            if (ins.IsBranchOrJump)
            {
                MipsInsn delay = i + 1 < insns.Length ? insns[i + 1] : MipsDecoder.Decode(ins.Addr + 4U, readWord(ins.Addr + 4U));
                bool closed = MipsEmitHelpers.EmitBranchOrJump(lines, ins, delay, insns[0].Addr, insns[^1].Addr + 4U,
                    rangeStart, rangeEnd, isChunked, fnTargets);
                if (i + 1 < endIdx && a.DelaySlotTargets.Contains(delay.Addr))
                {
                    // The delay slot is itself a branch target: re-emit it behind its own label.
                    if (!closed) lines.Add($"        goto L_after_{delay.Addr:X8};");
                    if (a.BackEdges.Contains(delay.Addr))
                    {
                        lines.Add($"    L_{delay.Addr:X8}:");
                        lines.Add("        cpu.TotalCycles += 1; " + string.Format(System.Globalization.CultureInfo.InvariantCulture, poll, delay.Addr));
                    }
                    else
                    {
                        lines.Add($"    L_{delay.Addr:X8}: cpu.TotalCycles += 1;");
                    }
                    MipsEmitHelpers.EmitSimple(lines, delay, ret);
                    if (!closed) lines.Add($"    L_after_{delay.Addr:X8}:");
                    closed = false;
                }
                if (!closed && i + 2 < endIdx && !localTargets.Contains(insns[i + 2].Addr))
                    lines[^1] += $" cpu.TotalCycles += {CountBlockCycles(insns, i + 2, endIdx, localTargets)};";
                if (closed) reachable = false;
                i++;
                continue;
            }

            if (ins.IsLoad && ins.Rt != 0 && i + 1 < endIdx)
            {
                MipsInsn next = insns[i + 1];
                if (!localTargets.Contains(next.Addr) && next.ReadsReg(ins.Rt) && !next.IsBranchOrJump)
                {
                    // R3000A load-delay slot: the next instruction still observes the old register value.
                    lines.Add("        " + MipsEmitHelpers.EmitLoadExpr(ins, "_ld"));
                    MipsEmitHelpers.EmitSimple(lines, next, ret);
                    if (!next.WritesReg(ins.Rt)) lines.Add($"        r[{ins.Rt}] = _ld;");
                    i++;
                    continue;
                }
            }
            MipsEmitHelpers.EmitSimple(lines, ins, ret);
        }

        if (reachable)
            lines.Add(isChunked ? $"        return 0x{rangeEnd:X8}U;" : $"        cpu.JumpIndirect(0x{rangeEnd:X8}U);");
        if (hasLoadDelay && lines.Skip(declIdx).Any(l => l.Contains("_ld", StringComparison.Ordinal))) lines.Insert(declIdx, "        uint _ld = 0;");
        if (hasDynJump && lines.Skip(declIdx).Any(l => l.Contains("_tgt", StringComparison.Ordinal))) lines.Insert(declIdx, "        uint _tgt;");
        if (hasCondBranch && lines.Skip(declIdx).Any(l => l.Contains("_take", StringComparison.Ordinal))) lines.Insert(declIdx, "        bool _take;");
        lines.Insert(declIdx, "        uint[] r = cpu.R;");
        lines.Add("    }");
        return lines;
    }

    private static void ScanRangeFeatures(
        MipsInsn[] insns, int startIdx, int endIdx, uint rangeStart, uint rangeEnd,
        bool[]? reach, HashSet<uint> fnTargets, HashSet<uint> localTargets,
        ref bool hasDynJump, ref bool hasCondBranch, ref bool hasLoadDelay)
    {
        for (int i = startIdx; i < endIdx; i++)
        {
            MipsInsn ins = insns[i];
            if (reach is not null && !reach[i])
            {
                if (ins.IsBranchOrJump) i++;
                continue;
            }
            if (ins.Op is 1 or 4 or 5 or 6 or 7) hasCondBranch = true;
            if (ins.Op is 1 or 2 or 4 or 5 or 6 or 7 && !ins.IsLinkBranch)
            {
                uint bt = ins.BranchTarget;
                if (bt >= rangeStart && bt < rangeEnd && fnTargets.Contains(bt)) localTargets.Add(bt);
            }
            if (ins.Op == 0 && ((ins.Funct == 8 && ins.Rs != 31) || ins.Funct == 9)) hasDynJump = true;
            if (ins.IsLoad && ins.Rt != 0 && i + 1 < endIdx)
            {
                MipsInsn next = insns[i + 1];
                if (!localTargets.Contains(next.Addr) && !fnTargets.Contains(next.Addr) &&
                    next.ReadsReg(ins.Rt) && !next.IsBranchOrJump) hasLoadDelay = true;
            }
            if (ins.IsBranchOrJump) i++;
        }
    }

    private static bool[] ComputeReachable(MipsInsn[] insns, uint fnStart, uint fnEnd, HashSet<uint> fnTargets)
    {
        var reach = new bool[insns.Length];
        var tracedFrom = new bool[insns.Length];
        var queue = new Queue<int>();
        queue.Enqueue(0);
        while (queue.Count > 0)
        {
            int start = queue.Dequeue();
            if (start >= insns.Length || tracedFrom[start]) continue;
            tracedFrom[start] = true;
            if (reach[start] && start > 0 && insns[start - 1].IsBranchOrJump && start + 1 < insns.Length)
            {
                queue.Enqueue(start + 1);
                continue;
            }
            for (int i = start; i < insns.Length && !reach[i]; i++)
            {
                reach[i] = true;
                MipsInsn ins = insns[i];
                if (!ins.IsBranchOrJump) continue;
                if (i + 1 < insns.Length) reach[i + 1] = true;
                uint bt = ins.BranchTarget;
                if (ins.Op is 1 or 2 or 4 or 5 or 6 or 7 && !ins.IsLinkBranch && bt >= fnStart && bt < fnEnd && fnTargets.Contains(bt))
                    queue.Enqueue((int)((bt - fnStart) / 4U));
                if (ins.Op == 2 || (ins.Op == 0 && ins.Funct == 8)) break;
                i++;
            }
        }
        return reach;
    }

    private static int CountBlockCycles(MipsInsn[] insns, int start, int endIdx, HashSet<uint> localTargets)
    {
        int count = 0;
        for (int k = start; k < endIdx; k++)
        {
            if (k > start && localTargets.Contains(insns[k].Addr)) break;
            count++;
            if (insns[k].IsBranchOrJump)
            {
                if (k + 1 < endIdx) count++;
                break;
            }
        }
        return Math.Max(1, count);
    }
}
