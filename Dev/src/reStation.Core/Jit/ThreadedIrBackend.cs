// @module: reStation.Core.Jit.ThreadedIrBackend
// @purpose: Zero-RWX pre-decoded IR block interpreter for --force-zero-rwx, non-JIT platforms, and hot-SMC fallback.
// @depends-on: reStation.Contracts.IReStationCpu, reStation.Core.Ir.IrBasicBlock, reStation.Core.Ir.IrInsn
// @exports: ThreadedIrBackend

using reStation.Contracts;
using reStation.Core.Ir;

namespace reStation.Core.Jit;

/// <summary>Executes pre-lowered <see cref="IrBasicBlock"/> instances without runtime dynamic code allocation.</summary>
public sealed class ThreadedIrBackend
{
    private readonly Action<IReStationCpu, IrInsn>? _copFallback;

    public ThreadedIrBackend(Action<IReStationCpu, IrInsn>? copFallback = null)
    {
        _copFallback = copFallback;
    }

    /// <summary>Executes <paramref name="block"/> on <paramref name="cpu"/>, updating <see cref="IReStationCpu.Pc"/> and cycle counters.</summary>
    public uint ExecuteBlock(IReStationCpu cpu, IrBasicBlock block)
    {
        ArgumentNullException.ThrowIfNull(cpu);
        ArgumentNullException.ThrowIfNull(block);

        IrInsn[] insns = block.Instructions;
        uint[] r = cpu.R;
        bool hasPendingJump = false;
        uint pendingNextPc = block.EndPc;

        for (int i = 0; i < insns.Length; i++)
        {
            ref readonly IrInsn insn = ref insns[i];
            cpu.Pc = insn.Pc;

            if (insn.HasFlag(IrInsnFlags.HasDelaySlot))
            {
                (bool taken, uint target) = EvaluateControlTransfer(cpu, r, in insn);
                hasPendingJump = taken;
                pendingNextPc = taken ? target : unchecked(insn.Pc + 8u);
                cpu.TotalCycles++;
                r[0] = 0u;
                continue;
            }

            ExecuteStep(cpu, r, in insn);
            r[0] = 0u;
            cpu.TotalCycles++;

            if (cpu.ExitRequested || cpu.ShouldUnwind)
                return unchecked(insn.Pc + 4u);

            if (insn.HasFlag(IrInsnFlags.InDelaySlot) && hasPendingJump)
            {
                cpu.Pc = pendingNextPc;
                if (cpu.TotalCycles >= cpu.NextEventCycle)
                    cpu.PollEvents();
                return pendingNextPc;
            }

            if (insn.HasFlag(IrInsnFlags.ModifiesPc))
                return cpu.Pc;
        }

        uint fallthrough = hasPendingJump ? pendingNextPc : block.EndPc;
        cpu.Pc = fallthrough;
        if (cpu.TotalCycles >= cpu.NextEventCycle)
            cpu.PollEvents();
        return fallthrough;
    }

    private (bool Taken, uint Target) EvaluateControlTransfer(IReStationCpu cpu, uint[] r, in IrInsn insn)
    {
        switch (insn.Op)
        {
            case IrOp.J:
                return (true, insn.ImmOrTarget);
            case IrOp.Jal:
                r[31] = unchecked(insn.Pc + 8u);
                return (true, insn.ImmOrTarget);
            case IrOp.Jr:
                return (true, r[insn.Rs]);
            case IrOp.Jalr:
            {
                uint target = r[insn.Rs];
                if (insn.Rd != 0)
                    r[insn.Rd] = unchecked(insn.Pc + 8u);
                return (true, target);
            }
            case IrOp.Beq:
                return (r[insn.Rs] == r[insn.Rt], insn.ImmOrTarget);
            case IrOp.Bne:
                return (r[insn.Rs] != r[insn.Rt], insn.ImmOrTarget);
            case IrOp.Blez:
                return ((int)r[insn.Rs] <= 0, insn.ImmOrTarget);
            case IrOp.Bgtz:
                return ((int)r[insn.Rs] > 0, insn.ImmOrTarget);
            case IrOp.Bltz:
                return ((int)r[insn.Rs] < 0, insn.ImmOrTarget);
            case IrOp.Bgez:
                return ((int)r[insn.Rs] >= 0, insn.ImmOrTarget);
            case IrOp.Bltzal:
                r[31] = unchecked(insn.Pc + 8u);
                return ((int)r[insn.Rs] < 0, insn.ImmOrTarget);
            case IrOp.Bgezal:
                r[31] = unchecked(insn.Pc + 8u);
                return ((int)r[insn.Rs] >= 0, insn.ImmOrTarget);
            default:
                _ = cpu;
                return (false, unchecked(insn.Pc + 8u));
        }
    }

    private void ExecuteStep(IReStationCpu cpu, uint[] r, in IrInsn insn)
    {
        uint addr;
        switch (insn.Op)
        {
            case IrOp.Nop: break;
            case IrOp.Add:
            case IrOp.Addu: r[insn.Rd] = unchecked(r[insn.Rs] + r[insn.Rt]); break;
            case IrOp.Sub:
            case IrOp.Subu: r[insn.Rd] = unchecked(r[insn.Rs] - r[insn.Rt]); break;
            case IrOp.And: r[insn.Rd] = r[insn.Rs] & r[insn.Rt]; break;
            case IrOp.Or: r[insn.Rd] = r[insn.Rs] | r[insn.Rt]; break;
            case IrOp.Xor: r[insn.Rd] = r[insn.Rs] ^ r[insn.Rt]; break;
            case IrOp.Nor: r[insn.Rd] = ~(r[insn.Rs] | r[insn.Rt]); break;
            case IrOp.Slt: r[insn.Rd] = (int)r[insn.Rs] < (int)r[insn.Rt] ? 1u : 0u; break;
            case IrOp.Sltu: r[insn.Rd] = r[insn.Rs] < r[insn.Rt] ? 1u : 0u; break;
            case IrOp.Sll: r[insn.Rd] = r[insn.Rt] << insn.Shamt; break;
            case IrOp.Srl: r[insn.Rd] = r[insn.Rt] >> insn.Shamt; break;
            case IrOp.Sra: r[insn.Rd] = (uint)((int)r[insn.Rt] >> insn.Shamt); break;
            case IrOp.Sllv: r[insn.Rd] = r[insn.Rt] << (int)(r[insn.Rs] & 0x1Fu); break;
            case IrOp.Srlv: r[insn.Rd] = r[insn.Rt] >> (int)(r[insn.Rs] & 0x1Fu); break;
            case IrOp.Srav: r[insn.Rd] = (uint)((int)r[insn.Rt] >> (int)(r[insn.Rs] & 0x1Fu)); break;
            case IrOp.Addi:
            case IrOp.Addiu: r[insn.Rd] = unchecked(r[insn.Rs] + insn.ImmOrTarget); break;
            case IrOp.Slti: r[insn.Rd] = (int)r[insn.Rs] < (int)insn.ImmOrTarget ? 1u : 0u; break;
            case IrOp.Sltiu: r[insn.Rd] = r[insn.Rs] < insn.ImmOrTarget ? 1u : 0u; break;
            case IrOp.Andi: r[insn.Rd] = r[insn.Rs] & insn.ImmOrTarget; break;
            case IrOp.Ori: r[insn.Rd] = r[insn.Rs] | insn.ImmOrTarget; break;
            case IrOp.Xori: r[insn.Rd] = r[insn.Rs] ^ insn.ImmOrTarget; break;
            case IrOp.Lui: r[insn.Rd] = insn.ImmOrTarget; break;
            case IrOp.Mult: cpu.Mult(r[insn.Rs], r[insn.Rt]); break;
            case IrOp.Multu: cpu.Multu(r[insn.Rs], r[insn.Rt]); break;
            case IrOp.Div: cpu.Div(r[insn.Rs], r[insn.Rt]); break;
            case IrOp.Divu: cpu.Divu(r[insn.Rs], r[insn.Rt]); break;
            case IrOp.Mfhi: r[insn.Rd] = cpu.Hi; break;
            case IrOp.Mflo: r[insn.Rd] = cpu.Lo; break;
            case IrOp.Mthi: cpu.Hi = r[insn.Rs]; break;
            case IrOp.Mtlo: cpu.Lo = r[insn.Rs]; break;
            case IrOp.Lb: addr = unchecked(r[insn.Rs] + insn.ImmOrTarget); r[insn.Rd] = (uint)(int)(sbyte)cpu.Read8(addr); break;
            case IrOp.Lbu: addr = unchecked(r[insn.Rs] + insn.ImmOrTarget); r[insn.Rd] = cpu.Read8(addr); break;
            case IrOp.Lh: addr = unchecked(r[insn.Rs] + insn.ImmOrTarget); r[insn.Rd] = (uint)(int)(short)cpu.Read16(addr); break;
            case IrOp.Lhu: addr = unchecked(r[insn.Rs] + insn.ImmOrTarget); r[insn.Rd] = cpu.Read16(addr); break;
            case IrOp.Lw: addr = unchecked(r[insn.Rs] + insn.ImmOrTarget); r[insn.Rd] = cpu.Read32(addr); break;
            case IrOp.Lwl: cpu.Lwl(insn.Rt, unchecked(r[insn.Rs] + insn.ImmOrTarget)); break;
            case IrOp.Lwr: cpu.Lwr(insn.Rt, unchecked(r[insn.Rs] + insn.ImmOrTarget)); break;
            case IrOp.Sb: cpu.Write8(unchecked(r[insn.Rs] + insn.ImmOrTarget), (byte)r[insn.Rt]); break;
            case IrOp.Sh: cpu.Write16(unchecked(r[insn.Rs] + insn.ImmOrTarget), (ushort)r[insn.Rt]); break;
            case IrOp.Sw: cpu.Write32(unchecked(r[insn.Rs] + insn.ImmOrTarget), r[insn.Rt]); break;
            case IrOp.Swl: cpu.Swl(unchecked(r[insn.Rs] + insn.ImmOrTarget), r[insn.Rt]); break;
            case IrOp.Swr: cpu.Swr(unchecked(r[insn.Rs] + insn.ImmOrTarget), r[insn.Rt]); break;
            case IrOp.Syscall: cpu.Syscall((insn.ImmOrTarget >> 6) & 0xFFFFFu); break;
            case IrOp.Break: cpu.Break((insn.ImmOrTarget >> 6) & 0xFFFFFu); break;
            case IrOp.Mfc0:
                if (_copFallback is not null) _copFallback(cpu, insn);
                else if (insn.Rd == 12) r[insn.Rt] = cpu.Cop0Sr;
                break;
            case IrOp.Mtc0:
                if (_copFallback is not null) _copFallback(cpu, insn);
                else if (insn.Rd == 12) cpu.Cop0Sr = r[insn.Rt];
                break;
            case IrOp.Mfc2: r[insn.Rt] = cpu.GteReadData(insn.Rd); break;
            case IrOp.Mtc2: cpu.GteWriteData(insn.Rd, r[insn.Rt]); break;
            case IrOp.Cfc2: r[insn.Rt] = cpu.GteReadControl(insn.Rd); break;
            case IrOp.Ctc2: cpu.GteWriteControl(insn.Rd, r[insn.Rt]); break;
            case IrOp.Cop2Cmd: cpu.GteExecute(insn.ImmOrTarget); break;
            case IrOp.Lwc2: cpu.GteWriteData(insn.Rt, cpu.Read32(unchecked(r[insn.Rs] + insn.ImmOrTarget))); break;
            case IrOp.Swc2: cpu.Write32(unchecked(r[insn.Rs] + insn.ImmOrTarget), cpu.GteReadData(insn.Rt)); break;
            default:
                _copFallback?.Invoke(cpu, insn);
                break;
        }
    }
}
