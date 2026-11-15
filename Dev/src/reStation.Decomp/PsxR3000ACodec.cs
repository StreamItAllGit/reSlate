// @module: reStation.Decomp.PsxR3000ACodec
// @purpose: Clean-room bidirectional MIPS R3000A + COP0 + PS1 GTE COP2 32-bit instruction decoder and encoder for D5 byte-match verification.
// @depends-on: System
// @exports: R3000AInsnKind, R3000AInsn, PsxR3000ACodec
// SPDX-License-Identifier: MPL-2.0

namespace reStation.Decomp;

/// <summary>Instruction decoding category for MIPS R3000A and PS1 COP2/GTE words.</summary>
public enum R3000AInsnKind
{
    /// <summary>Non-instruction data literal inside mixed code/data executable sections.</summary>
    DataWord = 0,
    /// <summary>Canonical <c>nop</c> encoding (<c>0x00000000</c>).</summary>
    Nop,
    /// <summary>Register-register ALU instruction (<c>op = 0</c>).</summary>
    AluR,
    /// <summary>Immediate shift instruction (<c>sll/srl/sra</c>).</summary>
    ShiftImm,
    /// <summary>Register jump/call (<c>jr/jalr</c>).</summary>
    JumpReg,
    /// <summary>System call (<c>syscall</c>).</summary>
    Syscall,
    /// <summary>Breakpoint (<c>break</c>).</summary>
    Break,
    /// <summary>Multiply/divide and HI/LO move instructions.</summary>
    HiLo,
    /// <summary>REGIMM conditional branch (<c>bltz/bgez/bltzal/bgezal</c>).</summary>
    RegimmBranch,
    /// <summary>26-bit jump/call (<c>j/jal</c>).</summary>
    JumpImm,
    /// <summary>Conditional PC-relative branch (<c>beq/bne/blez/bgtz</c>).</summary>
    BranchImm,
    /// <summary>Immediate ALU instruction (<c>addi..lui</c>).</summary>
    AluImm,
    /// <summary>COP0 system control instruction (<c>mfc0/mtc0/rfe</c>).</summary>
    Cop0,
    /// <summary>Byte/halfword/word load or store instruction.</summary>
    LoadStore,
    /// <summary>COP2 GTE register transfer (<c>mfc2/cfc2/mtc2/ctc2</c>).</summary>
    Cop2Transfer,
    /// <summary>COP2 GTE vector/geometry/lighting command (<c>rtps/rtpt/nclip/avsz3/avsz4/mvmva/...</c>).</summary>
    Cop2Command,
    /// <summary>COP2 GTE memory load/store (<c>lwc2/swc2</c>).</summary>
    Cop2LoadStore
}

/// <summary>Structured MIPS R3000A + GTE COP2 decoded instruction.</summary>
public readonly record struct R3000AInsn(
    uint Address,
    uint RawWord,
    R3000AInsnKind Kind,
    string Mnemonic,
    uint Op,
    int Rs,
    int Rt,
    int Rd,
    int Shamt,
    uint Funct,
    short Simm16,
    uint Target26,
    uint Cop2Command)
{
    /// <summary>True when the word decoded into a known R3000A/COP0/COP2 instruction.</summary>
    public bool IsKnownInstruction => Kind != R3000AInsnKind.DataWord;
}

/// <summary>Stateless bidirectional R3000A + COP2/GTE 32-bit instruction codec.</summary>
public static class PsxR3000ACodec
{
    /// <summary>Decodes one 32-bit little-endian MIPS R3000A / GTE word at <paramref name="address"/>.</summary>
    public static R3000AInsn Decode(uint address, uint word)
    {
        uint op = word >> 26;
        int rs = (int)((word >> 21) & 0x1F);
        int rt = (int)((word >> 16) & 0x1F);
        int rd = (int)((word >> 11) & 0x1F);
        int shamt = (int)((word >> 6) & 0x1F);
        uint funct = word & 0x3F;
        short simm = (short)(word & 0xFFFF);
        uint target26 = word & 0x03FFFFFF;
        uint cop2Cmd = word & 0x01FFFFFF;

        if (word == 0U)
            return new R3000AInsn(address, word, R3000AInsnKind.Nop, "nop", op, rs, rt, rd, shamt, funct, simm, target26, cop2Cmd);

        (R3000AInsnKind kind, string mnemonic) = op switch
        {
            0x00 => DecodeSpecial(funct),
            0x01 => DecodeRegimm(rt),
            0x02 => (R3000AInsnKind.JumpImm, "j"),
            0x03 => (R3000AInsnKind.JumpImm, "jal"),
            0x04 => (R3000AInsnKind.BranchImm, "beq"),
            0x05 => (R3000AInsnKind.BranchImm, "bne"),
            0x06 => (R3000AInsnKind.BranchImm, "blez"),
            0x07 => (R3000AInsnKind.BranchImm, "bgtz"),
            0x08 => (R3000AInsnKind.AluImm, "addi"),
            0x09 => (R3000AInsnKind.AluImm, "addiu"),
            0x0A => (R3000AInsnKind.AluImm, "slti"),
            0x0B => (R3000AInsnKind.AluImm, "sltiu"),
            0x0C => (R3000AInsnKind.AluImm, "andi"),
            0x0D => (R3000AInsnKind.AluImm, "ori"),
            0x0E => (R3000AInsnKind.AluImm, "xori"),
            0x0F => (R3000AInsnKind.AluImm, "lui"),
            0x10 => DecodeCop0(rs, funct, word),
            0x12 => DecodeCop2(rs, word),
            0x20 => (R3000AInsnKind.LoadStore, "lb"),
            0x21 => (R3000AInsnKind.LoadStore, "lh"),
            0x22 => (R3000AInsnKind.LoadStore, "lwl"),
            0x23 => (R3000AInsnKind.LoadStore, "lw"),
            0x24 => (R3000AInsnKind.LoadStore, "lbu"),
            0x25 => (R3000AInsnKind.LoadStore, "lhu"),
            0x26 => (R3000AInsnKind.LoadStore, "lwr"),
            0x28 => (R3000AInsnKind.LoadStore, "sb"),
            0x29 => (R3000AInsnKind.LoadStore, "sh"),
            0x2A => (R3000AInsnKind.LoadStore, "swl"),
            0x2B => (R3000AInsnKind.LoadStore, "sw"),
            0x2E => (R3000AInsnKind.LoadStore, "swr"),
            0x32 => (R3000AInsnKind.Cop2LoadStore, "lwc2"),
            0x3A => (R3000AInsnKind.Cop2LoadStore, "swc2"),
            _ => (R3000AInsnKind.DataWord, ".word")
        };

        return new R3000AInsn(address, word, kind, mnemonic, op, rs, rt, rd, shamt, funct, simm, target26, cop2Cmd);
    }

    /// <summary>Encodes a structured <see cref="R3000AInsn"/> back into its 32-bit machine instruction word.</summary>
    public static uint Encode(in R3000AInsn insn) => insn.Kind switch
    {
        R3000AInsnKind.DataWord => insn.RawWord,
        R3000AInsnKind.Nop => 0U,
        R3000AInsnKind.JumpImm => (insn.Op << 26) | (insn.Target26 & 0x03FFFFFFU),
        R3000AInsnKind.Cop2Command => (0x12U << 26) | (1U << 25) | (insn.Cop2Command & 0x01FFFFFFU),
        R3000AInsnKind.RegimmBranch or R3000AInsnKind.BranchImm or R3000AInsnKind.AluImm
            or R3000AInsnKind.LoadStore or R3000AInsnKind.Cop2LoadStore
            => (insn.Op << 26) | ((uint)insn.Rs << 21) | ((uint)insn.Rt << 16) | (ushort)insn.Simm16,
        _ => (insn.Op << 26) | ((uint)insn.Rs << 21) | ((uint)insn.Rt << 16) | ((uint)insn.Rd << 11) | ((uint)insn.Shamt << 6) | (insn.Funct & 0x3FU)
    };

    private static (R3000AInsnKind, string) DecodeSpecial(uint funct) => funct switch
    {
        0x00 => (R3000AInsnKind.ShiftImm, "sll"),
        0x02 => (R3000AInsnKind.ShiftImm, "srl"),
        0x03 => (R3000AInsnKind.ShiftImm, "sra"),
        0x04 => (R3000AInsnKind.AluR, "sllv"),
        0x06 => (R3000AInsnKind.AluR, "srlv"),
        0x07 => (R3000AInsnKind.AluR, "srav"),
        0x08 => (R3000AInsnKind.JumpReg, "jr"),
        0x09 => (R3000AInsnKind.JumpReg, "jalr"),
        0x0C => (R3000AInsnKind.Syscall, "syscall"),
        0x0D => (R3000AInsnKind.Break, "break"),
        0x10 => (R3000AInsnKind.HiLo, "mfhi"),
        0x11 => (R3000AInsnKind.HiLo, "mthi"),
        0x12 => (R3000AInsnKind.HiLo, "mflo"),
        0x13 => (R3000AInsnKind.HiLo, "mtlo"),
        0x18 => (R3000AInsnKind.HiLo, "mult"),
        0x19 => (R3000AInsnKind.HiLo, "multu"),
        0x1A => (R3000AInsnKind.HiLo, "div"),
        0x1B => (R3000AInsnKind.HiLo, "divu"),
        0x20 => (R3000AInsnKind.AluR, "add"),
        0x21 => (R3000AInsnKind.AluR, "addu"),
        0x22 => (R3000AInsnKind.AluR, "sub"),
        0x23 => (R3000AInsnKind.AluR, "subu"),
        0x24 => (R3000AInsnKind.AluR, "and"),
        0x25 => (R3000AInsnKind.AluR, "or"),
        0x26 => (R3000AInsnKind.AluR, "xor"),
        0x27 => (R3000AInsnKind.AluR, "nor"),
        0x2A => (R3000AInsnKind.AluR, "slt"),
        0x2B => (R3000AInsnKind.AluR, "sltu"),
        _ => (R3000AInsnKind.DataWord, ".word")
    };

    private static (R3000AInsnKind, string) DecodeRegimm(int rt) => rt switch
    {
        0x00 => (R3000AInsnKind.RegimmBranch, "bltz"),
        0x01 => (R3000AInsnKind.RegimmBranch, "bgez"),
        0x10 => (R3000AInsnKind.RegimmBranch, "bltzal"),
        0x11 => (R3000AInsnKind.RegimmBranch, "bgezal"),
        _ => (R3000AInsnKind.DataWord, ".word")
    };

    private static (R3000AInsnKind, string) DecodeCop0(int rs, uint funct, uint word)
    {
        if ((word & (1U << 25)) != 0U && funct == 0x10)
            return (R3000AInsnKind.Cop0, "rfe");
        return rs switch
        {
            0x00 => (R3000AInsnKind.Cop0, "mfc0"),
            0x02 => (R3000AInsnKind.Cop0, "cfc0"),
            0x04 => (R3000AInsnKind.Cop0, "mtc0"),
            0x06 => (R3000AInsnKind.Cop0, "ctc0"),
            _ => (R3000AInsnKind.DataWord, ".word")
        };
    }

    private static (R3000AInsnKind, string) DecodeCop2(int rs, uint word)
    {
        if ((word & (1U << 25)) != 0U)
        {
            string cmdName = (word & 0x3FU) switch
            {
                0x01 => "rtps",
                0x06 => "nclip",
                0x0C => "op",
                0x10 => "dpcs",
                0x11 => "intpl",
                0x12 => "mvmva",
                0x13 => "ncds",
                0x14 => "cdp",
                0x16 => "ncdt",
                0x1B => "nccs",
                0x1C => "cc",
                0x1E => "ncs",
                0x20 => "nct",
                0x28 => "sqr",
                0x29 => "dcpl",
                0x2A => "dpct",
                0x2D => "avsz3",
                0x2E => "avsz4",
                0x30 => "rtpt",
                0x3D => "gpf",
                0x3E => "gpl",
                0x3F => "ncct",
                _ => "cop2"
            };
            return (R3000AInsnKind.Cop2Command, cmdName);
        }

        if ((word & 0x7FFU) != 0U)
            return (R3000AInsnKind.DataWord, ".word");

        return rs switch
        {
            0x00 => (R3000AInsnKind.Cop2Transfer, "mfc2"),
            0x02 => (R3000AInsnKind.Cop2Transfer, "cfc2"),
            0x04 => (R3000AInsnKind.Cop2Transfer, "mtc2"),
            0x06 => (R3000AInsnKind.Cop2Transfer, "ctc2"),
            _ => (R3000AInsnKind.DataWord, ".word")
        };
    }
}
