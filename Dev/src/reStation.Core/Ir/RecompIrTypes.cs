// @module: reStation.Core.Ir.RecompIrTypes
// @purpose: Unmanaged 16-byte PS1 MIPS IR instruction and basic-block CFG metadata types for Tier 3 bringup execution.
// @depends-on: System.Runtime.InteropServices
// @exports: IrOp, IrInsnFlags, IrInsn, IrBasicBlock

using System.Runtime.InteropServices;

namespace reStation.Core.Ir;

/// <summary>Compact IR opcode set covering PS1 R3000A integer, memory, branch, and coprocessor dispatch.</summary>
public enum IrOp : byte
{
    Nop = 0,
    Add = 1,
    Addu = 2,
    Sub = 3,
    Subu = 4,
    And = 5,
    Or = 6,
    Xor = 7,
    Nor = 8,
    Slt = 9,
    Sltu = 10,
    Sll = 11,
    Srl = 12,
    Sra = 13,
    Sllv = 14,
    Srlv = 15,
    Srav = 16,
    Addi = 17,
    Addiu = 18,
    Slti = 19,
    Sltiu = 20,
    Andi = 21,
    Ori = 22,
    Xori = 23,
    Lui = 24,
    Mult = 25,
    Multu = 26,
    Div = 27,
    Divu = 28,
    Mfhi = 29,
    Mflo = 30,
    Mthi = 31,
    Mtlo = 32,
    Lb = 33,
    Lbu = 34,
    Lh = 35,
    Lhu = 36,
    Lw = 37,
    Lwl = 38,
    Lwr = 39,
    Sb = 40,
    Sh = 41,
    Sw = 42,
    Swl = 43,
    Swr = 44,
    J = 45,
    Jal = 46,
    Jr = 47,
    Jalr = 48,
    Beq = 49,
    Bne = 50,
    Blez = 51,
    Bgtz = 52,
    Bltz = 53,
    Bgez = 54,
    Bltzal = 55,
    Bgezal = 56,
    Syscall = 57,
    Break = 58,
    Mfc0 = 59,
    Mtc0 = 60,
    Cfc0 = 61,
    Ctc0 = 62,
    Rfe = 63,
    Cop2Cmd = 64,
    Mfc2 = 65,
    Mtc2 = 66,
    Cfc2 = 67,
    Ctc2 = 68,
    Lwc2 = 69,
    Swc2 = 70,
    Unimplemented = 255
}

/// <summary>Execution behavior flags on a lowered 16-byte <see cref="IrInsn"/>.</summary>
[Flags]
public enum IrInsnFlags : byte
{
    None = 0,
    IsBranch = 1 << 0,
    HasDelaySlot = 1 << 1,
    InDelaySlot = 1 << 2,
    IsLoad = 1 << 3,
    IsStore = 1 << 4,
    LinksRa = 1 << 5,
    ModifiesPc = 1 << 6,
    RequiresInterpreter = 1 << 7
}

/// <summary>Fixed 16-byte unmanaged IR instruction node emitted from PS1 R3000A words.</summary>
[StructLayout(LayoutKind.Sequential, Size = 16)]
public readonly struct IrInsn
{
    /// <summary>Expected unmanaged size of <see cref="IrInsn"/> in bytes.</summary>
    public const int ExpectedByteSize = 16;

    public readonly IrOp Op;
    public readonly IrInsnFlags Flags;
    public readonly byte Rd;
    public readonly byte Rs;
    public readonly byte Rt;
    public readonly byte Shamt;
    public readonly ushort SubMeta;
    public readonly uint Pc;
    public readonly uint ImmOrTarget;

    public IrInsn(
        IrOp op,
        IrInsnFlags flags,
        byte rd,
        byte rs,
        byte rt,
        byte shamt,
        uint pc,
        uint immOrTarget,
        ushort subMeta = 0)
    {
        Op = op;
        Flags = flags;
        Rd = rd;
        Rs = rs;
        Rt = rt;
        Shamt = shamt;
        SubMeta = subMeta;
        Pc = pc;
        ImmOrTarget = immOrTarget;
    }

    public bool HasFlag(IrInsnFlags flag) => (Flags & flag) != 0;
}

/// <summary>Lowered straight-line basic block with dual-page invalidation footprint and clean-room code digest.</summary>
public sealed class IrBasicBlock
{
    public IrBasicBlock(
        uint entryPc,
        uint endPc,
        uint startPage,
        uint endPage,
        ulong startPageCachelineMask,
        ulong endPageCachelineMask,
        IrInsn[] instructions,
        bool endsWithTerminalTransfer,
        string codeSha256Hex,
        int overlayId = 0)
    {
        EntryPc = entryPc;
        EndPc = endPc;
        StartPage = startPage;
        EndPage = endPage;
        StartPageCachelineMask = startPageCachelineMask;
        EndPageCachelineMask = endPageCachelineMask;
        Instructions = instructions ?? throw new ArgumentNullException(nameof(instructions));
        EndsWithTerminalTransfer = endsWithTerminalTransfer;
        CodeSha256Hex = codeSha256Hex ?? string.Empty;
        OverlayId = overlayId;
    }

    public uint EntryPc { get; }
    public uint EndPc { get; }
    public uint StartPage { get; }
    public uint EndPage { get; }
    public ulong StartPageCachelineMask { get; }
    public ulong EndPageCachelineMask { get; }
    public IrInsn[] Instructions { get; }
    public bool EndsWithTerminalTransfer { get; }
    public string CodeSha256Hex { get; }
    public int OverlayId { get; }
    public bool SpansTwoPages => StartPage != EndPage;
    public int GuestWordCount => Instructions.Length;
    public uint GuestByteSize => (uint)(Instructions.Length * 4);
}
