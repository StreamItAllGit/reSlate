// @module: reStation.Core.Jit.RyuJitBlockBackend
// @purpose: CoreCLR RyuJIT DynamicMethod block emitter honoring RuntimeFeature.IsDynamicCodeSupported.
// @depends-on: reStation.Contracts.IReStationCpu, reStation.Core.Ir.IrBasicBlock
// @exports: RyuJitBlockBackend

using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using reStation.Contracts;
using reStation.Core.Ir;

namespace reStation.Core.Jit;

/// <summary>Compiles <see cref="IrBasicBlock"/> instances into collectible RyuJIT <see cref="DynamicMethod"/> delegates.</summary>
public sealed class RyuJitBlockBackend
{
    private static readonly PropertyInfo PropR = typeof(IReStationCpu).GetProperty(nameof(IReStationCpu.R))!;
    private static readonly PropertyInfo PropPc = typeof(IReStationCpu).GetProperty(nameof(IReStationCpu.Pc))!;
    private static readonly PropertyInfo PropHi = typeof(IReStationCpu).GetProperty(nameof(IReStationCpu.Hi))!;
    private static readonly PropertyInfo PropLo = typeof(IReStationCpu).GetProperty(nameof(IReStationCpu.Lo))!;
    private static readonly PropertyInfo PropTotalCycles = typeof(IReStationCpu).GetProperty(nameof(IReStationCpu.TotalCycles))!;
    private static readonly PropertyInfo PropNextEventCycle = typeof(IReStationCpu).GetProperty(nameof(IReStationCpu.NextEventCycle))!;
    private static readonly MethodInfo MethRead8 = typeof(IReStationCpu).GetMethod(nameof(IReStationCpu.Read8))!;
    private static readonly MethodInfo MethRead16 = typeof(IReStationCpu).GetMethod(nameof(IReStationCpu.Read16))!;
    private static readonly MethodInfo MethRead32 = typeof(IReStationCpu).GetMethod(nameof(IReStationCpu.Read32))!;
    private static readonly MethodInfo MethWrite8 = typeof(IReStationCpu).GetMethod(nameof(IReStationCpu.Write8))!;
    private static readonly MethodInfo MethWrite16 = typeof(IReStationCpu).GetMethod(nameof(IReStationCpu.Write16))!;
    private static readonly MethodInfo MethWrite32 = typeof(IReStationCpu).GetMethod(nameof(IReStationCpu.Write32))!;
    private static readonly MethodInfo MethLwl = typeof(IReStationCpu).GetMethod(nameof(IReStationCpu.Lwl))!;
    private static readonly MethodInfo MethLwr = typeof(IReStationCpu).GetMethod(nameof(IReStationCpu.Lwr))!;
    private static readonly MethodInfo MethSwl = typeof(IReStationCpu).GetMethod(nameof(IReStationCpu.Swl))!;
    private static readonly MethodInfo MethSwr = typeof(IReStationCpu).GetMethod(nameof(IReStationCpu.Swr))!;
    private static readonly MethodInfo MethMult = typeof(IReStationCpu).GetMethod(nameof(IReStationCpu.Mult))!;
    private static readonly MethodInfo MethMultu = typeof(IReStationCpu).GetMethod(nameof(IReStationCpu.Multu))!;
    private static readonly MethodInfo MethDiv = typeof(IReStationCpu).GetMethod(nameof(IReStationCpu.Div))!;
    private static readonly MethodInfo MethDivu = typeof(IReStationCpu).GetMethod(nameof(IReStationCpu.Divu))!;
    private static readonly MethodInfo MethGteReadData = typeof(IReStationCpu).GetMethod(nameof(IReStationCpu.GteReadData))!;
    private static readonly MethodInfo MethGteWriteData = typeof(IReStationCpu).GetMethod(nameof(IReStationCpu.GteWriteData))!;
    private static readonly MethodInfo MethGteReadCtrl = typeof(IReStationCpu).GetMethod(nameof(IReStationCpu.GteReadControl))!;
    private static readonly MethodInfo MethGteWriteCtrl = typeof(IReStationCpu).GetMethod(nameof(IReStationCpu.GteWriteControl))!;
    private static readonly MethodInfo MethGteExec = typeof(IReStationCpu).GetMethod(nameof(IReStationCpu.GteExecute))!;
    private static readonly MethodInfo MethPollEvents = typeof(IReStationCpu).GetMethod(nameof(IReStationCpu.PollEvents))!;
    private static readonly MethodInfo MethSlowHelper = typeof(RyuJitBlockBackend).GetMethod(nameof(ExecuteSystemOrUnimpl), BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>True when CoreCLR runtime JIT dynamic code emission is supported on the current host.</summary>
    public bool IsDynamicCodeSupported => RuntimeFeature.IsDynamicCodeSupported;

    /// <summary>Compiles <paramref name="block"/> into a collectible delegate, or returns <see langword="null"/> if dynamic code is disabled.</summary>
    public Func<IReStationCpu, uint>? CompileBlock(IrBasicBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        if (!RuntimeFeature.IsDynamicCodeSupported)
            return null;

        var dm = new DynamicMethod(
            $"reStation_Ir_{block.EntryPc:X8}",
            typeof(uint),
            [typeof(IReStationCpu)],
            restrictedSkipVisibility: false);

        ILGenerator il = dm.GetILGenerator();
        LocalBuilder locR = il.DeclareLocal(typeof(uint[]));
        LocalBuilder locNextPc = il.DeclareLocal(typeof(uint));
        LocalBuilder locTaken = il.DeclareLocal(typeof(bool));

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, PropR.GetMethod!);
        il.Emit(OpCodes.Stloc, locR);
        il.Emit(OpCodes.Ldc_I4, unchecked((int)block.EndPc));
        il.Emit(OpCodes.Stloc, locNextPc);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, locTaken);

        IrInsn[] insns = block.Instructions;
        for (int i = 0; i < insns.Length; i++)
        {
            ref readonly IrInsn insn = ref insns[i];
            if (insn.HasFlag(IrInsnFlags.HasDelaySlot))
            {
                EmitBranchEval(il, locR, locTaken, locNextPc, in insn);
                continue;
            }

            EmitStep(il, locR, in insn);
            if (insn.HasFlag(IrInsnFlags.InDelaySlot))
            {
                Label notTaken = il.DefineLabel();
                il.Emit(OpCodes.Ldloc, locTaken);
                il.Emit(OpCodes.Brfalse, notTaken);
                EmitFooter(il, block.Instructions.Length, locNextPc);
                il.MarkLabel(notTaken);
            }
        }

        il.Emit(OpCodes.Ldc_I4, unchecked((int)block.EndPc));
        il.Emit(OpCodes.Stloc, locNextPc);
        EmitFooter(il, block.Instructions.Length, locNextPc);
        return dm.CreateDelegate<Func<IReStationCpu, uint>>();
    }

    private static void EmitFooter(ILGenerator il, int cycleDelta, LocalBuilder locNextPc)
    {
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, PropTotalCycles.GetMethod!);
        il.Emit(OpCodes.Ldc_I8, (long)cycleDelta);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Callvirt, PropTotalCycles.SetMethod!);

        Label skipPoll = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, PropTotalCycles.GetMethod!);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, PropNextEventCycle.GetMethod!);
        il.Emit(OpCodes.Blt, skipPoll);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, MethPollEvents);
        il.MarkLabel(skipPoll);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, locNextPc);
        il.Emit(OpCodes.Callvirt, PropPc.SetMethod!);
        il.Emit(OpCodes.Ldloc, locNextPc);
        il.Emit(OpCodes.Ret);
    }

    private static void EmitBranchEval(ILGenerator il, LocalBuilder locR, LocalBuilder locTaken, LocalBuilder locNextPc, in IrInsn insn)
    {
        Label setFallthrough = il.DefineLabel();
        Label done = il.DefineLabel();
        switch (insn.Op)
        {
            case IrOp.J:
            case IrOp.Jal:
                if (insn.Op == IrOp.Jal) EmitStoreConst(il, locR, 31, unchecked(insn.Pc + 8u));
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Stloc, locTaken);
                il.Emit(OpCodes.Ldc_I4, unchecked((int)insn.ImmOrTarget));
                il.Emit(OpCodes.Stloc, locNextPc);
                return;
            case IrOp.Jr:
            case IrOp.Jalr:
                EmitLoadReg(il, locR, insn.Rs);
                il.Emit(OpCodes.Stloc, locNextPc);
                if (insn.Op == IrOp.Jalr && insn.Rd != 0)
                    EmitStoreConst(il, locR, insn.Rd, unchecked(insn.Pc + 8u));
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Stloc, locTaken);
                return;
            case IrOp.Beq:
                EmitLoadReg(il, locR, insn.Rs);
                EmitLoadReg(il, locR, insn.Rt);
                il.Emit(OpCodes.Ceq);
                break;
            case IrOp.Bne:
                EmitLoadReg(il, locR, insn.Rs);
                EmitLoadReg(il, locR, insn.Rt);
                il.Emit(OpCodes.Ceq);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Ceq);
                break;
            case IrOp.Blez:
                EmitLoadReg(il, locR, insn.Rs);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Cgt);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Ceq);
                break;
            case IrOp.Bgtz:
                EmitLoadReg(il, locR, insn.Rs);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Cgt);
                break;
            case IrOp.Bltz:
            case IrOp.Bltzal:
                if (insn.Op == IrOp.Bltzal) EmitStoreConst(il, locR, 31, unchecked(insn.Pc + 8u));
                EmitLoadReg(il, locR, insn.Rs);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Clt);
                break;
            case IrOp.Bgez:
            case IrOp.Bgezal:
                if (insn.Op == IrOp.Bgezal) EmitStoreConst(il, locR, 31, unchecked(insn.Pc + 8u));
                EmitLoadReg(il, locR, insn.Rs);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Clt);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Ceq);
                break;
            default:
                il.Emit(OpCodes.Ldc_I4_0);
                break;
        }

        il.Emit(OpCodes.Stloc, locTaken);
        il.Emit(OpCodes.Ldloc, locTaken);
        il.Emit(OpCodes.Brfalse, setFallthrough);
        il.Emit(OpCodes.Ldc_I4, unchecked((int)insn.ImmOrTarget));
        il.Emit(OpCodes.Stloc, locNextPc);
        il.Emit(OpCodes.Br, done);
        il.MarkLabel(setFallthrough);
        il.Emit(OpCodes.Ldc_I4, unchecked((int)(insn.Pc + 8u)));
        il.Emit(OpCodes.Stloc, locNextPc);
        il.MarkLabel(done);
    }

    private static void EmitStep(ILGenerator il, LocalBuilder locR, in IrInsn insn)
    {
        if (insn.Op == IrOp.Nop) return;
        switch (insn.Op)
        {
            case IrOp.Add:
            case IrOp.Addu: EmitAluRR(il, locR, insn, OpCodes.Add); break;
            case IrOp.Sub:
            case IrOp.Subu: EmitAluRR(il, locR, insn, OpCodes.Sub); break;
            case IrOp.And: EmitAluRR(il, locR, insn, OpCodes.And); break;
            case IrOp.Or: EmitAluRR(il, locR, insn, OpCodes.Or); break;
            case IrOp.Xor: EmitAluRR(il, locR, insn, OpCodes.Xor); break;
            case IrOp.Nor:
                EmitStoreRegStart(il, locR, insn.Rd);
                EmitLoadReg(il, locR, insn.Rs); EmitLoadReg(il, locR, insn.Rt);
                il.Emit(OpCodes.Or); il.Emit(OpCodes.Not);
                EmitStoreRegEnd(il, insn.Rd);
                break;
            case IrOp.Slt: EmitAluRR(il, locR, insn, OpCodes.Clt); break;
            case IrOp.Sltu: EmitAluRR(il, locR, insn, OpCodes.Clt_Un); break;
            case IrOp.Sll: EmitShiftConst(il, locR, insn, OpCodes.Shl); break;
            case IrOp.Srl: EmitShiftConst(il, locR, insn, OpCodes.Shr_Un); break;
            case IrOp.Sra: EmitShiftConst(il, locR, insn, OpCodes.Shr); break;
            case IrOp.Sllv: EmitShiftVar(il, locR, insn, OpCodes.Shl); break;
            case IrOp.Srlv: EmitShiftVar(il, locR, insn, OpCodes.Shr_Un); break;
            case IrOp.Srav: EmitShiftVar(il, locR, insn, OpCodes.Shr); break;
            case IrOp.Addi:
            case IrOp.Addiu: EmitAluRI(il, locR, insn, OpCodes.Add); break;
            case IrOp.Slti: EmitAluRI(il, locR, insn, OpCodes.Clt); break;
            case IrOp.Sltiu: EmitAluRI(il, locR, insn, OpCodes.Clt_Un); break;
            case IrOp.Andi: EmitAluRI(il, locR, insn, OpCodes.And); break;
            case IrOp.Ori: EmitAluRI(il, locR, insn, OpCodes.Or); break;
            case IrOp.Xori: EmitAluRI(il, locR, insn, OpCodes.Xor); break;
            case IrOp.Lui: EmitStoreConst(il, locR, insn.Rd, insn.ImmOrTarget); break;
            case IrOp.Mult: EmitCpuCallRR(il, locR, insn, MethMult); break;
            case IrOp.Multu: EmitCpuCallRR(il, locR, insn, MethMultu); break;
            case IrOp.Div: EmitCpuCallRR(il, locR, insn, MethDiv); break;
            case IrOp.Divu: EmitCpuCallRR(il, locR, insn, MethDivu); break;
            case IrOp.Mfhi: EmitLoadProp(il, locR, insn.Rd, PropHi.GetMethod!); break;
            case IrOp.Mflo: EmitLoadProp(il, locR, insn.Rd, PropLo.GetMethod!); break;
            case IrOp.Mthi: EmitStoreProp(il, locR, insn.Rs, PropHi.SetMethod!); break;
            case IrOp.Mtlo: EmitStoreProp(il, locR, insn.Rs, PropLo.SetMethod!); break;
            case IrOp.Lb: EmitMemRead(il, locR, insn, MethRead8, OpCodes.Conv_I1); break;
            case IrOp.Lbu: EmitMemRead(il, locR, insn, MethRead8, null); break;
            case IrOp.Lh: EmitMemRead(il, locR, insn, MethRead16, OpCodes.Conv_I2); break;
            case IrOp.Lhu: EmitMemRead(il, locR, insn, MethRead16, null); break;
            case IrOp.Lw: EmitMemRead(il, locR, insn, MethRead32, null); break;
            case IrOp.Lwl: EmitUnalignedLoad(il, locR, insn, MethLwl); break;
            case IrOp.Lwr: EmitUnalignedLoad(il, locR, insn, MethLwr); break;
            case IrOp.Sb: EmitMemWrite(il, locR, insn, MethWrite8, OpCodes.Conv_U1); break;
            case IrOp.Sh: EmitMemWrite(il, locR, insn, MethWrite16, OpCodes.Conv_U2); break;
            case IrOp.Sw: EmitMemWrite(il, locR, insn, MethWrite32, null); break;
            case IrOp.Swl: EmitMemWrite(il, locR, insn, MethSwl, null); break;
            case IrOp.Swr: EmitMemWrite(il, locR, insn, MethSwr, null); break;
            case IrOp.Mfc2: EmitGteRead(il, locR, insn, MethGteReadData); break;
            case IrOp.Cfc2: EmitGteRead(il, locR, insn, MethGteReadCtrl); break;
            case IrOp.Mtc2: EmitGteWrite(il, locR, insn, MethGteWriteData); break;
            case IrOp.Ctc2: EmitGteWrite(il, locR, insn, MethGteWriteCtrl); break;
            case IrOp.Cop2Cmd:
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldc_I4, unchecked((int)insn.ImmOrTarget));
                il.Emit(OpCodes.Callvirt, MethGteExec);
                break;
            default:
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldc_I4, (int)insn.Op);
                il.Emit(OpCodes.Ldc_I4, (int)insn.Rd);
                il.Emit(OpCodes.Ldc_I4, (int)insn.Rt);
                il.Emit(OpCodes.Ldc_I4, unchecked((int)insn.Pc));
                il.Emit(OpCodes.Ldc_I4, unchecked((int)insn.ImmOrTarget));
                il.Emit(OpCodes.Call, MethSlowHelper);
                break;
        }
    }

    private static void ExecuteSystemOrUnimpl(IReStationCpu cpu, int opCode, int rd, int rt, uint pc, uint immOrTarget)
    {
        cpu.Pc = pc;
        switch ((IrOp)opCode)
        {
            case IrOp.Syscall: cpu.Syscall((immOrTarget >> 6) & 0xFFFFFu); break;
            case IrOp.Break: cpu.Break((immOrTarget >> 6) & 0xFFFFFu); break;
            case IrOp.Mfc0: if (rd == 12 && rt != 0) cpu.R[rt] = cpu.Cop0Sr; break;
            case IrOp.Mtc0: if (rd == 12) cpu.Cop0Sr = cpu.R[rt]; break;
        }
        cpu.R[0] = 0u;
    }

    private static void EmitAluRR(ILGenerator il, LocalBuilder locR, in IrInsn insn, OpCode op)
    { EmitStoreRegStart(il, locR, insn.Rd); EmitLoadReg(il, locR, insn.Rs); EmitLoadReg(il, locR, insn.Rt); il.Emit(op); EmitStoreRegEnd(il, insn.Rd); }

    private static void EmitAluRI(ILGenerator il, LocalBuilder locR, in IrInsn insn, OpCode op)
    { EmitStoreRegStart(il, locR, insn.Rd); EmitLoadReg(il, locR, insn.Rs); il.Emit(OpCodes.Ldc_I4, unchecked((int)insn.ImmOrTarget)); il.Emit(op); EmitStoreRegEnd(il, insn.Rd); }

    private static void EmitShiftConst(ILGenerator il, LocalBuilder locR, in IrInsn insn, OpCode op)
    { EmitStoreRegStart(il, locR, insn.Rd); EmitLoadReg(il, locR, insn.Rt); il.Emit(OpCodes.Ldc_I4, (int)insn.Shamt); il.Emit(op); EmitStoreRegEnd(il, insn.Rd); }

    private static void EmitShiftVar(ILGenerator il, LocalBuilder locR, in IrInsn insn, OpCode op)
    { EmitStoreRegStart(il, locR, insn.Rd); EmitLoadReg(il, locR, insn.Rt); EmitLoadReg(il, locR, insn.Rs); il.Emit(OpCodes.Ldc_I4_S, (sbyte)31); il.Emit(OpCodes.And); il.Emit(op); EmitStoreRegEnd(il, insn.Rd); }

    private static void EmitCpuCallRR(ILGenerator il, LocalBuilder locR, in IrInsn insn, MethodInfo method)
    { il.Emit(OpCodes.Ldarg_0); EmitLoadReg(il, locR, insn.Rs); EmitLoadReg(il, locR, insn.Rt); il.Emit(OpCodes.Callvirt, method); }

    private static void EmitLoadProp(ILGenerator il, LocalBuilder locR, byte rd, MethodInfo getter)
    { EmitStoreRegStart(il, locR, rd); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Callvirt, getter); EmitStoreRegEnd(il, rd); }

    private static void EmitStoreProp(ILGenerator il, LocalBuilder locR, byte rs, MethodInfo setter)
    { il.Emit(OpCodes.Ldarg_0); EmitLoadReg(il, locR, rs); il.Emit(OpCodes.Callvirt, setter); }

    private static void EmitMemRead(ILGenerator il, LocalBuilder locR, in IrInsn insn, MethodInfo reader, OpCode? signExt)
    {
        EmitStoreRegStart(il, locR, insn.Rd);
        il.Emit(OpCodes.Ldarg_0); EmitEffectiveAddr(il, locR, insn.Rs, insn.ImmOrTarget);
        il.Emit(OpCodes.Callvirt, reader);
        if (signExt.HasValue) il.Emit(signExt.Value);
        EmitStoreRegEnd(il, insn.Rd);
    }

    private static void EmitUnalignedLoad(ILGenerator il, LocalBuilder locR, in IrInsn insn, MethodInfo method)
    { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldc_I4, (int)insn.Rt); EmitEffectiveAddr(il, locR, insn.Rs, insn.ImmOrTarget); il.Emit(OpCodes.Callvirt, method); }

    private static void EmitMemWrite(ILGenerator il, LocalBuilder locR, in IrInsn insn, MethodInfo writer, OpCode? castOp)
    {
        il.Emit(OpCodes.Ldarg_0); EmitEffectiveAddr(il, locR, insn.Rs, insn.ImmOrTarget);
        EmitLoadReg(il, locR, insn.Rt); if (castOp.HasValue) il.Emit(castOp.Value);
        il.Emit(OpCodes.Callvirt, writer);
    }

    private static void EmitGteRead(ILGenerator il, LocalBuilder locR, in IrInsn insn, MethodInfo reader)
    { EmitStoreRegStart(il, locR, insn.Rt); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldc_I4, (int)insn.Rd); il.Emit(OpCodes.Callvirt, reader); EmitStoreRegEnd(il, insn.Rt); }

    private static void EmitGteWrite(ILGenerator il, LocalBuilder locR, in IrInsn insn, MethodInfo writer)
    { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldc_I4, (int)insn.Rd); EmitLoadReg(il, locR, insn.Rt); il.Emit(OpCodes.Callvirt, writer); }

    private static void EmitEffectiveAddr(ILGenerator il, LocalBuilder locR, byte rs, uint offset)
    { EmitLoadReg(il, locR, rs); if (offset != 0u) { il.Emit(OpCodes.Ldc_I4, unchecked((int)offset)); il.Emit(OpCodes.Add); } }

    private static void EmitStoreConst(ILGenerator il, LocalBuilder locR, byte rd, uint val)
    { if (rd != 0) { EmitStoreRegStart(il, locR, rd); il.Emit(OpCodes.Ldc_I4, unchecked((int)val)); EmitStoreRegEnd(il, rd); } }

    private static void EmitStoreRegStart(ILGenerator il, LocalBuilder locR, byte reg)
    { if (reg != 0) { il.Emit(OpCodes.Ldloc, locR); il.Emit(OpCodes.Ldc_I4, (int)reg); } }

    private static void EmitStoreRegEnd(ILGenerator il, byte reg)
    { il.Emit(reg == 0 ? OpCodes.Pop : OpCodes.Stelem_I4); }

    private static void EmitLoadReg(ILGenerator il, LocalBuilder locR, byte reg)
    {
        if (reg == 0) { il.Emit(OpCodes.Ldc_I4_0); return; }
        il.Emit(OpCodes.Ldloc, locR); il.Emit(OpCodes.Ldc_I4, (int)reg); il.Emit(OpCodes.Ldelem_U4);
    }
}
