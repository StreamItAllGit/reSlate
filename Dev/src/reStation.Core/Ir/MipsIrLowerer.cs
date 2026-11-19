// @module: reStation.Core.Ir.MipsIrLowerer
// @purpose: Clean-room MIPS R3000A decoder and basic-block IR lowerer preserving delay slots and page footprints.
// @depends-on: reStation.Core.Ir.RecompIrTypes
// @exports: MipsIrLowerer

using System.Buffers.Binary;
using System.Security.Cryptography;

namespace reStation.Core.Ir;

/// <summary>Decodes PS1 MIPS R3000A instruction words into 16-byte <see cref="IrInsn"/> basic blocks.</summary>
public static class MipsIrLowerer
{
    public const int DefaultMaxBlockInstructions = 64;
    private const uint RamAddressMask = 0x001F_FFFFu;
    private const int PageShift = 12;

    /// <summary>Masks a guest virtual/physical RAM address to its 0..511 4 KB RAM page index.</summary>
    public static int ToRamPageIndex(uint address) => (int)((address & RamAddressMask) >> PageShift);

    /// <summary>Returns the single 64-byte cacheline bit (<c>0..63</c>) within a 4 KB page for <paramref name="address"/>.</summary>
    public static ulong ToCachelineBit(uint address) => 1UL << (int)((address & 0x0FFFu) >> 6);

    /// <summary>Lowers a single PS1 MIPS instruction word at <paramref name="pc"/> into <see cref="IrInsn"/>.</summary>
    public static IrInsn LowerInstruction(uint pc, uint word, bool inDelaySlot = false)
    {
        IrInsnFlags delayFlag = inDelaySlot ? IrInsnFlags.InDelaySlot : IrInsnFlags.None;
        if (word == 0u)
            return new IrInsn(IrOp.Nop, delayFlag, 0, 0, 0, 0, pc, 0u);

        uint op = word >> 26;
        byte rs = (byte)((word >> 21) & 0x1Fu);
        byte rt = (byte)((word >> 16) & 0x1Fu);
        byte rd = (byte)((word >> 11) & 0x1Fu);
        byte shamt = (byte)((word >> 6) & 0x1Fu);
        uint imm16 = word & 0xFFFFu;
        uint simm32 = unchecked((uint)(int)(short)imm16);

        return op switch
        {
            0x00u => LowerSpecial(pc, word, rs, rt, rd, shamt, delayFlag),
            0x01u => LowerRegImm(pc, rs, rt, simm32, delayFlag),
            0x02u => new IrInsn(IrOp.J, IrInsnFlags.IsBranch | IrInsnFlags.HasDelaySlot | IrInsnFlags.ModifiesPc | delayFlag,
                0, 0, 0, 0, pc, (pc & 0xF000_0000u) | ((word & 0x03FF_FFFFu) << 2)),
            0x03u => new IrInsn(IrOp.Jal, IrInsnFlags.IsBranch | IrInsnFlags.HasDelaySlot | IrInsnFlags.LinksRa | IrInsnFlags.ModifiesPc | delayFlag,
                31, 0, 0, 0, pc, (pc & 0xF000_0000u) | ((word & 0x03FF_FFFFu) << 2)),
            0x04u => Branch(IrOp.Beq, rs, rt, pc, simm32, delayFlag),
            0x05u => Branch(IrOp.Bne, rs, rt, pc, simm32, delayFlag),
            0x06u => Branch(IrOp.Blez, rs, rt, pc, simm32, delayFlag),
            0x07u => Branch(IrOp.Bgtz, rs, rt, pc, simm32, delayFlag),
            0x08u => new IrInsn(IrOp.Addi, delayFlag, rt, rs, rt, 0, pc, simm32),
            0x09u => new IrInsn(IrOp.Addiu, delayFlag, rt, rs, rt, 0, pc, simm32),
            0x0Au => new IrInsn(IrOp.Slti, delayFlag, rt, rs, rt, 0, pc, simm32),
            0x0Bu => new IrInsn(IrOp.Sltiu, delayFlag, rt, rs, rt, 0, pc, simm32),
            0x0Cu => new IrInsn(IrOp.Andi, delayFlag, rt, rs, rt, 0, pc, imm16),
            0x0Du => new IrInsn(IrOp.Ori, delayFlag, rt, rs, rt, 0, pc, imm16),
            0x0Eu => new IrInsn(IrOp.Xori, delayFlag, rt, rs, rt, 0, pc, imm16),
            0x0Fu => new IrInsn(IrOp.Lui, delayFlag, rt, 0, rt, 0, pc, imm16 << 16),
            0x10u => LowerCop0(pc, word, rs, rt, rd, delayFlag),
            0x12u => LowerCop2(pc, word, rs, rt, rd, delayFlag),
            0x20u => new IrInsn(IrOp.Lb, IrInsnFlags.IsLoad | delayFlag, rt, rs, rt, 0, pc, simm32),
            0x21u => new IrInsn(IrOp.Lh, IrInsnFlags.IsLoad | delayFlag, rt, rs, rt, 0, pc, simm32),
            0x22u => new IrInsn(IrOp.Lwl, IrInsnFlags.IsLoad | delayFlag, rt, rs, rt, 0, pc, simm32),
            0x23u => new IrInsn(IrOp.Lw, IrInsnFlags.IsLoad | delayFlag, rt, rs, rt, 0, pc, simm32),
            0x24u => new IrInsn(IrOp.Lbu, IrInsnFlags.IsLoad | delayFlag, rt, rs, rt, 0, pc, simm32),
            0x25u => new IrInsn(IrOp.Lhu, IrInsnFlags.IsLoad | delayFlag, rt, rs, rt, 0, pc, simm32),
            0x26u => new IrInsn(IrOp.Lwr, IrInsnFlags.IsLoad | delayFlag, rt, rs, rt, 0, pc, simm32),
            0x28u => new IrInsn(IrOp.Sb, IrInsnFlags.IsStore | delayFlag, 0, rs, rt, 0, pc, simm32),
            0x29u => new IrInsn(IrOp.Sh, IrInsnFlags.IsStore | delayFlag, 0, rs, rt, 0, pc, simm32),
            0x2Au => new IrInsn(IrOp.Swl, IrInsnFlags.IsStore | delayFlag, 0, rs, rt, 0, pc, simm32),
            0x2Bu => new IrInsn(IrOp.Sw, IrInsnFlags.IsStore | delayFlag, 0, rs, rt, 0, pc, simm32),
            0x2Eu => new IrInsn(IrOp.Swr, IrInsnFlags.IsStore | delayFlag, 0, rs, rt, 0, pc, simm32),
            0x32u => new IrInsn(IrOp.Lwc2, IrInsnFlags.IsLoad | IrInsnFlags.RequiresInterpreter | delayFlag, rt, rs, rt, 0, pc, simm32),
            0x3Au => new IrInsn(IrOp.Swc2, IrInsnFlags.IsStore | IrInsnFlags.RequiresInterpreter | delayFlag, 0, rs, rt, 0, pc, simm32),
            _ => new IrInsn(IrOp.Unimplemented, IrInsnFlags.RequiresInterpreter | delayFlag, rd, rs, rt, shamt, pc, word)
        };
    }

    /// <summary>Lowers a straight-line basic block starting at <paramref name="entryPc"/> including any terminal delay slot.</summary>
    public static IrBasicBlock LowerBlock(
        uint entryPc,
        Func<uint, uint> readWord,
        int maxInstructions = DefaultMaxBlockInstructions,
        int overlayId = 0)
    {
        ArgumentNullException.ThrowIfNull(readWord);
        int cap = Math.Clamp(maxInstructions, 2, 256);
        var list = new List<IrInsn>(Math.Min(cap, 32));
        byte[] rawBuffer = new byte[cap * 4];
        int startPage = ToRamPageIndex(entryPc);
        int endPage = startPage;
        ulong startMask = 0UL;
        ulong endMask = 0UL;

        uint pc = entryPc;
        bool endsWithTransfer = false;
        bool needDelaySlot = false;

        for (int i = 0; i < cap; i++)
        {
            uint word = readWord(pc);
            BinaryPrimitives.WriteUInt32LittleEndian(rawBuffer.AsSpan(i * 4, 4), word);

            int page = ToRamPageIndex(pc);
            ulong lineBit = ToCachelineBit(pc);
            if (page == startPage)
                startMask |= lineBit;
            else
            {
                endPage = page;
                endMask |= lineBit;
            }

            IrInsn insn = LowerInstruction(pc, word, inDelaySlot: needDelaySlot);
            list.Add(insn);
            pc = unchecked(pc + 4u);

            if (needDelaySlot)
            {
                endsWithTransfer = true;
                break;
            }

            if (insn.HasFlag(IrInsnFlags.HasDelaySlot))
            {
                needDelaySlot = true;
                continue;
            }

            if (insn.HasFlag(IrInsnFlags.ModifiesPc) || insn.Op is IrOp.Unimplemented)
            {
                endsWithTransfer = true;
                break;
            }
        }

        string shaHex = Convert.ToHexString(SHA256.HashData(rawBuffer.AsSpan(0, list.Count * 4))).ToLowerInvariant();
        if (endPage == startPage)
            endMask = startMask;

        return new IrBasicBlock(
            entryPc,
            pc,
            (uint)startPage,
            (uint)endPage,
            startMask,
            endMask,
            list.ToArray(),
            endsWithTransfer,
            shaHex,
            overlayId);
    }

    private static IrInsn Branch(IrOp op, byte rs, byte rt, uint pc, uint simm32, IrInsnFlags extra, bool linksRa = false)
    {
        uint target = unchecked(pc + 4u + (simm32 << 2));
        IrInsnFlags flags = IrInsnFlags.IsBranch | IrInsnFlags.HasDelaySlot | IrInsnFlags.ModifiesPc | extra;
        if (linksRa) flags |= IrInsnFlags.LinksRa;
        return new IrInsn(op, flags, linksRa ? (byte)31 : (byte)0, rs, rt, 0, pc, target);
    }

    private static IrInsn LowerSpecial(uint pc, uint word, byte rs, byte rt, byte rd, byte shamt, IrInsnFlags delayFlag)
    {
        uint funct = word & 0x3Fu;
        return funct switch
        {
            0x00u => new IrInsn(IrOp.Sll, delayFlag, rd, rs, rt, shamt, pc, 0u),
            0x02u => new IrInsn(IrOp.Srl, delayFlag, rd, rs, rt, shamt, pc, 0u),
            0x03u => new IrInsn(IrOp.Sra, delayFlag, rd, rs, rt, shamt, pc, 0u),
            0x04u => new IrInsn(IrOp.Sllv, delayFlag, rd, rs, rt, shamt, pc, 0u),
            0x06u => new IrInsn(IrOp.Srlv, delayFlag, rd, rs, rt, shamt, pc, 0u),
            0x07u => new IrInsn(IrOp.Srav, delayFlag, rd, rs, rt, shamt, pc, 0u),
            0x08u => new IrInsn(IrOp.Jr, IrInsnFlags.IsBranch | IrInsnFlags.HasDelaySlot | IrInsnFlags.ModifiesPc | delayFlag, 0, rs, 0, 0, pc, 0u),
            0x09u => new IrInsn(IrOp.Jalr, IrInsnFlags.IsBranch | IrInsnFlags.HasDelaySlot | (rd != 0 ? IrInsnFlags.LinksRa : IrInsnFlags.None) | IrInsnFlags.ModifiesPc | delayFlag, rd, rs, 0, 0, pc, 0u),
            0x0Cu => new IrInsn(IrOp.Syscall, IrInsnFlags.RequiresInterpreter | IrInsnFlags.ModifiesPc | delayFlag, 0, 0, 0, 0, pc, word),
            0x0Du => new IrInsn(IrOp.Break, IrInsnFlags.RequiresInterpreter | IrInsnFlags.ModifiesPc | delayFlag, 0, 0, 0, 0, pc, word),
            0x10u => new IrInsn(IrOp.Mfhi, delayFlag, rd, 0, 0, 0, pc, 0u),
            0x11u => new IrInsn(IrOp.Mthi, delayFlag, 0, rs, 0, 0, pc, 0u),
            0x12u => new IrInsn(IrOp.Mflo, delayFlag, rd, 0, 0, 0, pc, 0u),
            0x13u => new IrInsn(IrOp.Mtlo, delayFlag, 0, rs, 0, 0, pc, 0u),
            0x18u => new IrInsn(IrOp.Mult, delayFlag, 0, rs, rt, 0, pc, 0u),
            0x19u => new IrInsn(IrOp.Multu, delayFlag, 0, rs, rt, 0, pc, 0u),
            0x1Au => new IrInsn(IrOp.Div, delayFlag, 0, rs, rt, 0, pc, 0u),
            0x1Bu => new IrInsn(IrOp.Divu, delayFlag, 0, rs, rt, 0, pc, 0u),
            0x20u => new IrInsn(IrOp.Add, delayFlag, rd, rs, rt, 0, pc, 0u),
            0x21u => new IrInsn(IrOp.Addu, delayFlag, rd, rs, rt, 0, pc, 0u),
            0x22u => new IrInsn(IrOp.Sub, delayFlag, rd, rs, rt, 0, pc, 0u),
            0x23u => new IrInsn(IrOp.Subu, delayFlag, rd, rs, rt, 0, pc, 0u),
            0x24u => new IrInsn(IrOp.And, delayFlag, rd, rs, rt, 0, pc, 0u),
            0x25u => new IrInsn(IrOp.Or, delayFlag, rd, rs, rt, 0, pc, 0u),
            0x26u => new IrInsn(IrOp.Xor, delayFlag, rd, rs, rt, 0, pc, 0u),
            0x27u => new IrInsn(IrOp.Nor, delayFlag, rd, rs, rt, 0, pc, 0u),
            0x2Au => new IrInsn(IrOp.Slt, delayFlag, rd, rs, rt, 0, pc, 0u),
            0x2Bu => new IrInsn(IrOp.Sltu, delayFlag, rd, rs, rt, 0, pc, 0u),
            _ => new IrInsn(IrOp.Unimplemented, IrInsnFlags.RequiresInterpreter | delayFlag, rd, rs, rt, shamt, pc, word)
        };
    }

    private static IrInsn LowerRegImm(uint pc, byte rs, byte rt, uint simm32, IrInsnFlags delayFlag)
    {
        bool link = (rt & 0x1Eu) == 0x10u;
        IrOp op = (rt & 1u) == 0u
            ? (link ? IrOp.Bltzal : IrOp.Bltz)
            : (link ? IrOp.Bgezal : IrOp.Bgez);
        return Branch(op, rs, rt, pc, simm32, delayFlag, linksRa: link);
    }

    private static IrInsn LowerCop0(uint pc, uint word, byte rs, byte rt, byte rd, IrInsnFlags delayFlag)
    {
        if ((rs & 0x10u) != 0u && (word & 0x3Fu) == 0x10u)
            return new IrInsn(IrOp.Rfe, IrInsnFlags.RequiresInterpreter | IrInsnFlags.ModifiesPc | delayFlag, 0, 0, 0, 0, pc, word);
        IrOp op = rs switch
        {
            0x00 => IrOp.Mfc0,
            0x02 => IrOp.Cfc0,
            0x04 => IrOp.Mtc0,
            0x06 => IrOp.Ctc0,
            _ => IrOp.Unimplemented
        };
        return new IrInsn(op, IrInsnFlags.RequiresInterpreter | delayFlag, rd, rs, rt, 0, pc, word);
    }

    private static IrInsn LowerCop2(uint pc, uint word, byte rs, byte rt, byte rd, IrInsnFlags delayFlag)
    {
        if ((rs & 0x10u) != 0u)
            return new IrInsn(IrOp.Cop2Cmd, IrInsnFlags.RequiresInterpreter | delayFlag, 0, rs, rt, 0, pc, word);
        IrOp op = rs switch
        {
            0x00 => IrOp.Mfc2,
            0x02 => IrOp.Cfc2,
            0x04 => IrOp.Mtc2,
            0x06 => IrOp.Ctc2,
            _ => IrOp.Unimplemented
        };
        return new IrInsn(op, IrInsnFlags.RequiresInterpreter | delayFlag, rd, rs, rt, 0, pc, word);
    }
}
