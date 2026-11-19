// @module: reStation.Core.Cpu.PsxInterpreter
// @purpose: Quarantined MIPS R3000A differential-oracle interpreter, strictly gated to --execution-tier interpreterOracle.
// @depends-on: reStation.Contracts, reStation.Core.Cpu.PsxCpu
// @exports: PsxInterpreter
// @owner: devv-cpu-cd (T2)

using reStation.Contracts;

namespace reStation.Core.Cpu;

public static class PsxInterpreter
{
    public static void ResumeFromSnapshot(PsxCpu cpu)
    {
        ArgumentNullException.ThrowIfNull(cpu);
        cpu.ShouldUnwind = false;
        uint pc = cpu.Pc;
        while (!cpu.ExitRequested && cpu.PendingQuickLoadSlot < 0)
        {
            if (pc == IReStationCpu.ReturnSentinelPc || pc == 0) break;
            uint masked = pc & 0x1FFFFFFFU;
            if (masked is 0xA0U or 0xB0U or 0xC0U) { cpu.Bios.HandleVectorCall(masked); pc = cpu.R[31]; continue; }
            uint canonPc = 0x80000000U | masked;
            IRecompModule? mod = cpu.ResolveModule(canonPc);
            if (mod is not null && cpu.TryExecuteCompiledModule(mod, canonPc, out bool hasTail, out uint tailPc))
            {
                if (cpu.ExitRequested || cpu.PendingQuickLoadSlot >= 0 || (cpu.ShouldUnwind && cpu.Pc == IReStationCpu.ReturnSentinelPc)) break;
                cpu.ShouldUnwind = false;
                pc = hasTail ? tailPc : cpu.R[31];
                continue;
            }
            cpu.Pc = pc;
            if (!StepOne(cpu, ref pc))
            {
                if (cpu.ExitRequested || cpu.PendingQuickLoadSlot >= 0) break;
                if (cpu.ShouldUnwind) { cpu.ShouldUnwind = false; break; }
                continue;
            }
            cpu.R[0] = 0;
            cpu.TotalCycles += 2;
            if (cpu.TotalCycles >= cpu.NextEventCycle) { cpu.PollEvents(); if (cpu.ShouldUnwind) break; }
        }
    }

    public static void ExecuteUntilReturnOrCompiled(PsxCpu cpu, uint startPc, IRecompModule? module)
    {
        if (ExecuteUntilReturnOrCompiled(cpu, startPc, module, out uint pendingTailJumpPc))
            cpu.Call(pendingTailJumpPc);
    }

    internal static bool ExecuteUntilReturnOrCompiled(PsxCpu cpu, uint startPc, IRecompModule? module, out uint pendingTailJumpPc)
    {
        ArgumentNullException.ThrowIfNull(cpu);
        pendingTailJumpPc = 0U;
        uint pc = startPc, stopRa = cpu.R[31];
        for (int steps = 0; steps < 50_000_000; steps++)
        {
            if (cpu.ShouldUnwind || cpu.ExitRequested || cpu.PendingQuickLoadSlot >= 0) return false;
            if (pc == cpu.UnwindTargetPc || pc == IReStationCpu.ReturnSentinelPc)
            {
                cpu.Pc = pc; cpu.ShouldUnwind = true; return false;
            }
            uint masked = pc & 0x1FFFFFFFU;
            if (masked is 0xA0U or 0xB0U or 0xC0U)
            {
                cpu.Bios.HandleVectorCall(masked);
                pc = cpu.R[31];
                if (pc == stopRa) return false;
                continue;
            }
            uint canonPc = 0x80000000U | masked;
            IRecompModule? targetMod = steps > 0 ? cpu.ResolveModule(canonPc) : module;
            if (steps > 0 && targetMod is not null && canonPc >= targetMod.TextStart && canonPc < targetMod.TextEnd &&
                cpu.TryExecuteCompiledModule(targetMod, canonPc, out bool hasTail, out uint tailPc))
            {
                if (hasTail && !cpu.ShouldUnwind && !cpu.ExitRequested && cpu.PendingQuickLoadSlot < 0)
                {
                    pendingTailJumpPc = tailPc;
                    return true;
                }
                return false;
            }
            cpu.Pc = pc;
            if (!StepOne(cpu, ref pc)) return false;
            cpu.R[0] = 0;
            cpu.TotalCycles += 2;
            if (cpu.TotalCycles >= cpu.NextEventCycle) cpu.PollEvents();
        }
        return false;
    }

    private static bool StepOne(PsxCpu cpu, ref uint pc)
    {
        uint insn = cpu.Read32(pc), op = insn >> 26;
        int rs = (int)((insn >> 21) & 0x1F), rt = (int)((insn >> 16) & 0x1F), rd = (int)((insn >> 11) & 0x1F), sh = (int)((insn >> 6) & 0x1F);
        short simm = (short)(insn & 0xFFFF);
        uint uimm = insn & 0xFFFF, nextPc = pc + 4;

        switch (op)
        {
            case 0x00:
                bool keepGoing = ExecSpecial(cpu, insn, rs, rt, rd, sh, insn & 0x3F, pc, ref nextPc);
                pc = nextPc;
                return keepGoing;
            case 0x01:
                bool takeB = (rt & 1) != 0 ? (int)cpu.R[rs] >= 0 : (int)cpu.R[rs] < 0;
                if ((rt & 0x1E) == 0x10) cpu.R[31] = pc + 8;
                uint tgtB = (uint)((int)nextPc + (simm << 2));
                StepDelaySlot(cpu, nextPc);
                pc = takeB ? tgtB : pc + 8;
                return true;
            case 0x02:
                uint jTgt = (nextPc & 0xF0000000U) | ((insn & 0x03FFFFFFU) << 2);
                StepDelaySlot(cpu, nextPc);
                pc = jTgt;
                if (jTgt == cpu.UnwindTargetPc || jTgt == IReStationCpu.ReturnSentinelPc) { cpu.ShouldUnwind = true; return false; }
                return true;
            case 0x03:
                uint jalTgt = (nextPc & 0xF0000000U) | ((insn & 0x03FFFFFFU) << 2);
                cpu.R[31] = pc + 8;
                StepDelaySlot(cpu, nextPc);
                cpu.Call(jalTgt);
                pc = pc + 8;
                return !cpu.ShouldUnwind;
            case 0x04 or 0x05 or 0x06 or 0x07:
                int a = (int)cpu.R[rs], b = (int)cpu.R[rt];
                bool take = op switch { 0x04 => a == b, 0x05 => a != b, 0x06 => a <= 0, _ => a > 0 };
                uint bTgt = (uint)((int)nextPc + (simm << 2));
                StepDelaySlot(cpu, nextPc);
                pc = take ? bTgt : pc + 8;
                return true;
            case 0x08 or 0x09: cpu.R[rt] = (uint)((int)cpu.R[rs] + simm); break;
            case 0x0A: cpu.R[rt] = (int)cpu.R[rs] < simm ? 1U : 0U; break;
            case 0x0B: cpu.R[rt] = cpu.R[rs] < (uint)(int)simm ? 1U : 0U; break;
            case 0x0C: cpu.R[rt] = cpu.R[rs] & uimm; break;
            case 0x0D: cpu.R[rt] = cpu.R[rs] | uimm; break;
            case 0x0E: cpu.R[rt] = cpu.R[rs] ^ uimm; break;
            case 0x0F: cpu.R[rt] = uimm << 16; break;
            case 0x10: ExecCop0(cpu, insn, rs, rt, rd); break;
            case 0x12: ExecCop2(cpu, insn, rs, rt, rd); break;
            case 0x20: cpu.R[rt] = (uint)(sbyte)cpu.Read8((uint)((int)cpu.R[rs] + simm)); break;
            case 0x21: cpu.R[rt] = (uint)(short)cpu.Read16((uint)((int)cpu.R[rs] + simm)); break;
            case 0x22: cpu.Lwl(rt, (uint)((int)cpu.R[rs] + simm)); break;
            case 0x23: cpu.R[rt] = cpu.Read32((uint)((int)cpu.R[rs] + simm)); break;
            case 0x24: cpu.R[rt] = cpu.Read8((uint)((int)cpu.R[rs] + simm)); break;
            case 0x25: cpu.R[rt] = cpu.Read16((uint)((int)cpu.R[rs] + simm)); break;
            case 0x26: cpu.Lwr(rt, (uint)((int)cpu.R[rs] + simm)); break;
            case 0x28: cpu.Write8((uint)((int)cpu.R[rs] + simm), (byte)cpu.R[rt]); break;
            case 0x29: cpu.Write16((uint)((int)cpu.R[rs] + simm), (ushort)cpu.R[rt]); break;
            case 0x2A: cpu.Swl((uint)((int)cpu.R[rs] + simm), cpu.R[rt]); break;
            case 0x2B: cpu.Write32((uint)((int)cpu.R[rs] + simm), cpu.R[rt]); break;
            case 0x2E: cpu.Swr((uint)((int)cpu.R[rs] + simm), cpu.R[rt]); break;
            case 0x32: cpu.GteWriteData(rt, cpu.Read32((uint)((int)cpu.R[rs] + simm))); break;
            case 0x3A: cpu.Write32((uint)((int)cpu.R[rs] + simm), cpu.GteReadData(rt)); break;
        }
        pc = nextPc;
        return true;
    }

    private static bool ExecSpecial(PsxCpu cpu, uint insn, int rs, int rt, int rd, int sh, uint funct, uint pc, ref uint nextPc)
    {
        switch (funct)
        {
            case 0x00: cpu.R[rd] = cpu.R[rt] << sh; break;
            case 0x02: cpu.R[rd] = cpu.R[rt] >> sh; break;
            case 0x03: cpu.R[rd] = (uint)((int)cpu.R[rt] >> sh); break;
            case 0x04: cpu.R[rd] = cpu.R[rt] << (int)(cpu.R[rs] & 31); break;
            case 0x06: cpu.R[rd] = cpu.R[rt] >> (int)(cpu.R[rs] & 31); break;
            case 0x07: cpu.R[rd] = (uint)((int)cpu.R[rt] >> (int)(cpu.R[rs] & 31)); break;
            case 0x08:
                uint jrTgt = cpu.R[rs];
                StepDelaySlot(cpu, nextPc);
                nextPc = jrTgt;
                if (jrTgt == cpu.UnwindTargetPc || jrTgt == IReStationCpu.ReturnSentinelPc) { cpu.ShouldUnwind = true; return false; }
                return rs != 31;
            case 0x09:
                uint jalrTgt = cpu.R[rs];
                if (rd != 0) cpu.R[rd] = pc + 8;
                StepDelaySlot(cpu, nextPc);
                cpu.Call(jalrTgt);
                nextPc = pc + 8;
                return !cpu.ShouldUnwind;
            case 0x0C: cpu.Syscall((insn >> 6) & 0xFFFFFU); return !cpu.ShouldUnwind;
            case 0x0D: cpu.Break((insn >> 6) & 0xFFFFFU); break;
            case 0x10: cpu.R[rd] = cpu.Hi; break;
            case 0x11: cpu.Hi = cpu.R[rs]; break;
            case 0x12: cpu.R[rd] = cpu.Lo; break;
            case 0x13: cpu.Lo = cpu.R[rs]; break;
            case 0x18: cpu.Mult(cpu.R[rs], cpu.R[rt]); break;
            case 0x19: cpu.Multu(cpu.R[rs], cpu.R[rt]); break;
            case 0x1A: cpu.Div(cpu.R[rs], cpu.R[rt]); break;
            case 0x1B: cpu.Divu(cpu.R[rs], cpu.R[rt]); break;
            case 0x20 or 0x21: cpu.R[rd] = cpu.R[rs] + cpu.R[rt]; break;
            case 0x22 or 0x23: cpu.R[rd] = cpu.R[rs] - cpu.R[rt]; break;
            case 0x24: cpu.R[rd] = cpu.R[rs] & cpu.R[rt]; break;
            case 0x25: cpu.R[rd] = cpu.R[rs] | cpu.R[rt]; break;
            case 0x26: cpu.R[rd] = cpu.R[rs] ^ cpu.R[rt]; break;
            case 0x27: cpu.R[rd] = ~(cpu.R[rs] | cpu.R[rt]); break;
            case 0x2A: cpu.R[rd] = (int)cpu.R[rs] < (int)cpu.R[rt] ? 1U : 0U; break;
            case 0x2B: cpu.R[rd] = cpu.R[rs] < cpu.R[rt] ? 1U : 0U; break;
        }
        return true;
    }

    private static void StepDelaySlot(PsxCpu cpu, uint delayPc)
    {
        uint dummy = delayPc;
        StepOne(cpu, ref dummy);
        cpu.R[0] = 0;
    }

    private static void ExecCop0(PsxCpu cpu, uint insn, int rs, int rt, int rd)
    {
        if (rs == 0)
            cpu.R[rt] = rd switch { 8 => cpu.Cop0BadVAddr, 12 => cpu.Cop0Sr, 13 => cpu.Cop0Cause, 14 => cpu.Cop0Epc, _ => 0U };
        else if (rs == 4 && rd == 12)
            cpu.Cop0Sr = cpu.R[rt];
        else if (rs == 0x10 && (insn & 0x3F) == 0x10) // RFE
            cpu.Cop0Sr = (cpu.Cop0Sr & ~0xFU) | ((cpu.Cop0Sr >> 2) & 0xFU);
    }

    private static void ExecCop2(PsxCpu cpu, uint insn, int rs, int rt, int rd)
    {
        if ((rs & 0x10) != 0) { cpu.GteExecute(insn & 0x01FFFFFFU); return; }
        switch (rs)
        {
            case 0: cpu.R[rt] = cpu.GteReadData(rd); break;
            case 2: cpu.R[rt] = cpu.GteReadControl(rd); break;
            case 4: cpu.GteWriteData(rd, cpu.R[rt]); break;
            case 6: cpu.GteWriteControl(rd, cpu.R[rt]); break;
        }
    }
}
