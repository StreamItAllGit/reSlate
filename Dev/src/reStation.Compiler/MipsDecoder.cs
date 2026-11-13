// @module: reStation.Compiler.MipsDecoder
// @purpose: Clean-room MIPS R3000A + COP0 + COP2/GTE 32-bit instruction decoder with register read/write and control-flow queries.
// @depends-on: System
// @exports: MipsInsn, MipsDecoder
// @owner: devv-compiler (T5)

namespace reStation.Compiler;

/// <summary>Decoded R3000A instruction (all fields pre-extracted from the 32-bit word).</summary>
public readonly record struct MipsInsn(
    uint Addr,
    uint Word,
    uint Op,
    int Rs,
    int Rt,
    int Rd,
    int Shamt,
    uint Funct,
    short Simm,
    uint Uimm,
    uint Target26)
{
    /// <summary>Branch or jump with a delay slot (REGIMM, J, JAL, BEQ, BNE, BLEZ, BGTZ, JR, JALR).</summary>
    public bool IsBranchOrJump =>
        Op is 1 or 2 or 3 or 4 or 5 or 6 or 7 ||
        (Op == 0 && Funct is 8 or 9);

    /// <summary>Control never falls through (J, JR, or BEQ with identical registers).</summary>
    public bool IsUnconditionalExitOrBranch =>
        Op == 2 ||
        (Op == 0 && Funct == 8) ||
        (Op == 4 && Rs == Rt);

    /// <summary>REGIMM BLTZAL/BGEZAL (conditional call that writes $ra).</summary>
    public bool IsLinkBranch => Op == 1 && (Rt & 0x1E) == 0x10;

    /// <summary>Delayed GPR load (LB, LH, LW, LBU, LHU).</summary>
    public bool IsLoad => Op is 32 or 33 or 35 or 36 or 37;

    /// <summary>Absolute target for J/JAL and PC-relative target for conditional branches; 0 otherwise.</summary>
    public uint BranchTarget => Op switch
    {
        2 or 3 => ((Addr + 4U) & 0xF0000000U) | (Target26 << 2),
        1 or 4 or 5 or 6 or 7 => (uint)((int)(Addr + 4U) + (Simm << 2)),
        _ => 0U
    };

    /// <summary>True when this instruction reads GPR <paramref name="reg"/>.</summary>
    public bool ReadsReg(int reg)
    {
        if (reg == 0) return false;
        if (Op == 0)
        {
            return Funct switch
            {
                0 or 2 or 3 => Rt == reg,
                4 or 6 or 7 => Rs == reg || Rt == reg,
                8 or 9 or 17 or 19 => Rs == reg,
                12 or 13 or 16 or 18 => false,
                _ => Rs == reg || Rt == reg
            };
        }
        return Op switch
        {
            1 or 6 or 7 => Rs == reg,
            2 or 3 or 15 => false,
            4 or 5 => Rs == reg || Rt == reg,
            >= 8 and <= 14 => Rs == reg,
            16 => Rs == 4 && Rt == reg,
            18 => Rs is 4 or 6 && Rt == reg,
            32 or 33 or 35 or 36 or 37 or 50 => Rs == reg,
            34 or 38 or 40 or 41 or 42 or 43 or 46 => Rs == reg || Rt == reg,
            58 => Rs == reg,
            _ => false
        };
    }

    /// <summary>True when this instruction writes GPR <paramref name="reg"/>.</summary>
    public bool WritesReg(int reg)
    {
        if (reg == 0) return false;
        if (Op == 0)
        {
            return Funct switch
            {
                8 or 12 or 13 or 17 or 19 or 24 or 25 or 26 or 27 => false,
                _ => Rd == reg
            };
        }
        return Op switch
        {
            1 => (Rt & 0x1E) == 0x10 && reg == 31,
            3 => reg == 31,
            >= 8 and <= 15 => Rt == reg,
            16 => Rs == 0 && Rt == reg,
            18 => Rs is 0 or 2 && Rt == reg,
            >= 32 and <= 38 => Rt == reg,
            _ => false
        };
    }
}

/// <summary>Stateless R3000A word decoder.</summary>
public static class MipsDecoder
{
    /// <summary>Splits <paramref name="word"/> fetched at <paramref name="addr"/> into its instruction fields.</summary>
    public static MipsInsn Decode(uint addr, uint word) => new(
        Addr: addr,
        Word: word,
        Op: word >> 26,
        Rs: (int)((word >> 21) & 0x1F),
        Rt: (int)((word >> 16) & 0x1F),
        Rd: (int)((word >> 11) & 0x1F),
        Shamt: (int)((word >> 6) & 0x1F),
        Funct: word & 0x3F,
        Simm: (short)(word & 0xFFFF),
        Uimm: word & 0xFFFF,
        Target26: word & 0x03FFFFFF);

    /// <summary>True for opcodes the lifter understands (used by gap/seed heuristics to reject data words).</summary>
    public static bool IsKnownInstruction(in MipsInsn ins) => ins.Op switch
    {
        0 => ins.Funct is 0 or 2 or 3 or 4 or 6 or 7 or 8 or 9 or 12 or 13 or 16 or 17 or 18 or 19 or 24 or 25 or 26 or 27
            or (>= 32 and <= 39) or 42 or 43,
        1 => (ins.Rt & 0x1E) is 0x00 or 0x10,
        2 or 3 or 4 or 5 or 6 or 7 or (>= 8 and <= 15) or 16 or 18 or (>= 32 and <= 38) or (>= 40 and <= 43) or 46 or 50 or 58 => true,
        _ => false
    };
}
