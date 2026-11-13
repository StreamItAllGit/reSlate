// @module: reStation.Compiler.MipsEmitHelpers
// @purpose: Instruction-level C# emission for R3000A ALU/shift/LSU/branch/COP0/COP2(GTE) opcodes targeting reStation.Contracts.IReStationCpu.
// @depends-on: reStation.Compiler.MipsDecoder
// @exports: MipsEmitHelpers
// @owner: devv-compiler (T5)

namespace reStation.Compiler;

/// <summary>
/// Emits C# statements for single instructions. Generated methods bind <c>uint[] r = cpu.R;</c> once, then use the
/// flattened <c>IReStationCpu</c> bus (<c>cpu.Read32</c>/<c>cpu.Write32</c>) and COP2 (<c>cpu.GteReadData</c>, <c>cpu.GteExecute</c>) surface.
/// </summary>
internal static class MipsEmitHelpers
{
    internal const string Indent = "        ";

    /// <summary>Effective address expression <c>rs + simm16</c>.</summary>
    internal static string EffAddr(MipsInsn ins) => $"unchecked((uint)((int)r[{ins.Rs}] + {ins.Simm}))";

    internal static string EmitLoadExpr(MipsInsn ins, string target) => ins.Op switch
    {
        32 => $"{target} = unchecked((uint)(int)(sbyte)cpu.Read8({EffAddr(ins)}));",
        33 => $"{target} = unchecked((uint)(int)(short)cpu.Read16({EffAddr(ins)}));",
        35 => $"{target} = cpu.Read32({EffAddr(ins)});",
        36 => $"{target} = cpu.Read8({EffAddr(ins)});",
        37 => $"{target} = cpu.Read16({EffAddr(ins)});",
        _ => string.Empty
    };

    /// <summary>Emits a branch/jump plus its delay slot; returns true when control cannot fall through.</summary>
    internal static bool EmitBranchOrJump(
        List<string> lines, MipsInsn ins, MipsInsn delay,
        uint fnStart, uint fnEnd, uint rangeStart, uint rangeEnd, bool isChunked, HashSet<uint> fnTargets)
    {
        string ret = isChunked ? "0U" : string.Empty;
        if (ins.Op == 3)
        {
            lines.Add($"{Indent}r[31] = 0x{ins.Addr + 8U:X8}U;");
            EmitSimple(lines, delay, ret);
            lines.Add($"{Indent}cpu.Call(0x{ins.BranchTarget:X8}U); if (cpu.ShouldUnwind) return {ret};");
            return false;
        }
        if (ins.Op == 2)
        {
            EmitSimple(lines, delay, ret);
            EmitTransfer(lines, ins.BranchTarget, fnStart, fnEnd, rangeStart, rangeEnd, isChunked, fnTargets, unconditional: true);
            return true;
        }
        if (ins.Op == 0 && ins.Funct == 9)
        {
            lines.Add($"{Indent}_tgt = r[{ins.Rs}];");
            if (ins.Rd != 0) lines.Add($"{Indent}r[{ins.Rd}] = 0x{ins.Addr + 8U:X8}U;");
            EmitSimple(lines, delay, ret);
            lines.Add($"{Indent}cpu.Call(_tgt); if (cpu.ShouldUnwind) return {ret};");
            return false;
        }
        if (ins.Op == 0 && ins.Funct == 8)
        {
            if (ins.Rs == 31)
            {
                EmitSimple(lines, delay, ret);
                lines.Add($"{Indent}return {ret};");
                return true;
            }
            lines.Add($"{Indent}_tgt = r[{ins.Rs}];");
            EmitSimple(lines, delay, ret);
            lines.Add(isChunked
                ? $"{Indent}if (_tgt >= 0x{fnStart:X8}U && _tgt < 0x{fnEnd:X8}U) return _tgt; cpu.JumpIndirect(_tgt); return 0U;"
                : $"{Indent}cpu.JumpIndirect(_tgt); return;");
            return true;
        }

        string cond = ins.Op switch
        {
            1 => (ins.Rt & 1) != 0 ? $"(int)r[{ins.Rs}] >= 0" : $"(int)r[{ins.Rs}] < 0",
            4 => $"r[{ins.Rs}] == r[{ins.Rt}]",
            5 => $"r[{ins.Rs}] != r[{ins.Rt}]",
            6 => $"(int)r[{ins.Rs}] <= 0",
            7 => $"(int)r[{ins.Rs}] > 0",
            _ => "false"
        };
        lines.Add($"{Indent}_take = {cond};");
        if (ins.IsLinkBranch)
        {
            // BLTZAL/BGEZAL: $ra is written unconditionally; a taken branch is a call that resumes at Addr+8.
            lines.Add($"{Indent}r[31] = 0x{ins.Addr + 8U:X8}U;");
            EmitSimple(lines, delay, ret);
            lines.Add($"{Indent}if (_take) {{ cpu.Call(0x{ins.BranchTarget:X8}U); if (cpu.ShouldUnwind) return {ret}; }}");
            return false;
        }
        EmitSimple(lines, delay, ret);
        EmitTransfer(lines, ins.BranchTarget, fnStart, fnEnd, rangeStart, rangeEnd, isChunked, fnTargets, unconditional: false);
        return false;
    }

    /// <summary>Local goto, chunk hand-off (return next PC), or out-of-function indirect jump.</summary>
    internal static void EmitTransfer(
        List<string> lines, uint tgt, uint fnStart, uint fnEnd, uint rangeStart, uint rangeEnd,
        bool isChunked, HashSet<uint> fnTargets, bool unconditional)
    {
        string guard = unconditional ? string.Empty : "if (_take) ";
        if (tgt >= rangeStart && tgt < rangeEnd && fnTargets.Contains(tgt))
            lines.Add($"{Indent}{guard}goto L_{tgt:X8};");
        else if (isChunked && tgt >= fnStart && tgt < fnEnd && fnTargets.Contains(tgt))
            lines.Add($"{Indent}{guard}return 0x{tgt:X8}U;");
        else
            lines.Add($"{Indent}{guard}{{ cpu.JumpIndirect(0x{tgt:X8}U); return {(isChunked ? "0U" : string.Empty)}; }}");
    }

    /// <summary>Emits a non-control-flow instruction (NOPs emit nothing).</summary>
    internal static void EmitSimple(List<string> lines, MipsInsn ins, string retUnwind)
    {
        if (ins.Word == 0) return;
        if (ins.Op == 0)
        {
            EmitSpecial(lines, ins, retUnwind);
            return;
        }
        string a = EffAddr(ins);
        string s = ins.Op switch
        {
            8 or 9 => SetReg(ins.Rt, a),
            10 => SetReg(ins.Rt, $"(int)r[{ins.Rs}] < {ins.Simm} ? 1U : 0U"),
            11 => SetReg(ins.Rt, $"r[{ins.Rs}] < 0x{(uint)(int)ins.Simm:X8}U ? 1U : 0U"),
            12 => SetReg(ins.Rt, $"r[{ins.Rs}] & 0x{ins.Uimm:X}U"),
            13 => SetReg(ins.Rt, $"r[{ins.Rs}] | 0x{ins.Uimm:X}U"),
            14 => SetReg(ins.Rt, $"r[{ins.Rs}] ^ 0x{ins.Uimm:X}U"),
            15 => SetReg(ins.Rt, $"0x{ins.Uimm << 16:X8}U"),
            16 => EmitCop0(ins),
            18 => EmitCop2(ins),
            32 or 36 => ins.Rt == 0 ? $"_ = cpu.Read8({a});" : EmitLoadExpr(ins, $"r[{ins.Rt}]"),
            33 or 37 => ins.Rt == 0 ? $"_ = cpu.Read16({a});" : EmitLoadExpr(ins, $"r[{ins.Rt}]"),
            35 => ins.Rt == 0 ? $"_ = cpu.Read32({a});" : EmitLoadExpr(ins, $"r[{ins.Rt}]"),
            34 => $"cpu.Lwl({ins.Rt}, {a});",
            38 => $"cpu.Lwr({ins.Rt}, {a});",
            40 => $"cpu.Write8({a}, (byte)r[{ins.Rt}]);",
            41 => $"cpu.Write16({a}, (ushort)r[{ins.Rt}]);",
            42 => $"cpu.Swl({a}, r[{ins.Rt}]);",
            43 => $"cpu.Write32({a}, r[{ins.Rt}]);",
            46 => $"cpu.Swr({a}, r[{ins.Rt}]);",
            50 => $"cpu.GteWriteData({ins.Rt}, cpu.Read32({a}));",
            58 => $"cpu.Write32({a}, cpu.GteReadData({ins.Rt}));",
            _ => string.Empty
        };
        if (!string.IsNullOrEmpty(s)) lines.Add(Indent + s);
    }

    private static void EmitSpecial(List<string> lines, MipsInsn ins, string retUnwind)
    {
        uint code = (ins.Word >> 6) & 0xFFFFFU;
        string? s = ins.Funct switch
        {
            0 => SetReg(ins.Rd, $"r[{ins.Rt}] << {ins.Shamt}"),
            2 => SetReg(ins.Rd, $"r[{ins.Rt}] >> {ins.Shamt}"),
            3 => SetReg(ins.Rd, $"unchecked((uint)((int)r[{ins.Rt}] >> {ins.Shamt}))"),
            4 => SetReg(ins.Rd, $"r[{ins.Rt}] << (int)(r[{ins.Rs}] & 31)"),
            6 => SetReg(ins.Rd, $"r[{ins.Rt}] >> (int)(r[{ins.Rs}] & 31)"),
            7 => SetReg(ins.Rd, $"unchecked((uint)((int)r[{ins.Rt}] >> (int)(r[{ins.Rs}] & 31)))"),
            12 => $"cpu.Pc = 0x{ins.Addr:X8}U; cpu.Syscall(0x{code:X}U); if (cpu.ShouldUnwind) return {retUnwind};",
            13 => $"cpu.Pc = 0x{ins.Addr:X8}U; cpu.Break(0x{code:X}U);",
            16 => SetReg(ins.Rd, "cpu.Hi"),
            17 => $"cpu.Hi = r[{ins.Rs}];",
            18 => SetReg(ins.Rd, "cpu.Lo"),
            19 => $"cpu.Lo = r[{ins.Rs}];",
            24 => $"cpu.Mult(r[{ins.Rs}], r[{ins.Rt}]);",
            25 => $"cpu.Multu(r[{ins.Rs}], r[{ins.Rt}]);",
            26 => $"cpu.Div(r[{ins.Rs}], r[{ins.Rt}]);",
            27 => $"cpu.Divu(r[{ins.Rs}], r[{ins.Rt}]);",
            32 or 33 => SetReg(ins.Rd, $"unchecked(r[{ins.Rs}] + r[{ins.Rt}])"),
            34 or 35 => SetReg(ins.Rd, $"unchecked(r[{ins.Rs}] - r[{ins.Rt}])"),
            36 => SetReg(ins.Rd, $"r[{ins.Rs}] & r[{ins.Rt}]"),
            37 => SetReg(ins.Rd, $"r[{ins.Rs}] | r[{ins.Rt}]"),
            38 => SetReg(ins.Rd, $"r[{ins.Rs}] ^ r[{ins.Rt}]"),
            39 => SetReg(ins.Rd, $"~(r[{ins.Rs}] | r[{ins.Rt}])"),
            42 => SetReg(ins.Rd, $"(int)r[{ins.Rs}] < (int)r[{ins.Rt}] ? 1U : 0U"),
            43 => SetReg(ins.Rd, $"r[{ins.Rs}] < r[{ins.Rt}] ? 1U : 0U"),
            _ => null
        };
        if (!string.IsNullOrEmpty(s)) lines.Add(Indent + s);
    }

    /// <summary>MFC0/MTC0 on SR (Cause/EPC read as zero in lifted code) and RFE mode-stack pop.</summary>
    private static string EmitCop0(MipsInsn ins)
    {
        if (ins.Rs == 0) return SetReg(ins.Rt, ins.Rd == 12 ? "cpu.Cop0Sr" : "0U");
        if (ins.Rs == 4 && ins.Rd == 12) return $"cpu.Cop0Sr = r[{ins.Rt}];";
        if (ins.Rs == 16 && ins.Funct == 0x10) return "cpu.Cop0Sr = (cpu.Cop0Sr & ~0xFU) | ((cpu.Cop0Sr >> 2) & 0xFU);";
        return string.Empty;
    }

    private static string EmitCop2(MipsInsn ins) => (ins.Rs & 0x10) != 0
        ? $"cpu.GteExecute(0x{ins.Word & 0x01FFFFFFU:X8}U);"
        : ins.Rs switch
        {
            0 => SetReg(ins.Rt, $"cpu.GteReadData({ins.Rd})"),
            2 => SetReg(ins.Rt, $"cpu.GteReadControl({ins.Rd})"),
            4 => $"cpu.GteWriteData({ins.Rd}, r[{ins.Rt}]);",
            6 => $"cpu.GteWriteControl({ins.Rd}, r[{ins.Rt}]);",
            _ => string.Empty
        };

    private static string SetReg(int reg, string expr) => reg == 0 ? string.Empty : $"r[{reg}] = {expr};";
}
