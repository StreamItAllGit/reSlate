// @module: reStation.Tests.CompilerAndModuleLoaderTests
// @purpose: Verifies MIPS R3000A decoder, MipsLifter single/chunked function emission, DiscExeLoader PS-X EXE validation, RecompCompilerPipeline CLI/run, and ModuleAssemblyLoader sandbox checks.
// @depends-on: Xunit, System.Buffers.Binary, System.Text, reStation.Compiler, reStation.Contracts, reStation.Player
// @exports: CompilerAndModuleLoaderTests
// @owner: devv-pkg-test (T8)

using System.Buffers.Binary;
using System.Text;
using reStation.Compiler;
using reStation.Contracts;
using reStation.Player;
using Xunit;

namespace reStation.Tests;

public sealed class CompilerAndModuleLoaderTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"restation-comp-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true);
    }

    [Fact]
    public void MipsDecoder_DecodesBranchTargetAndZeroRegInvariants()
    {
        // JAL 0x80010200 at 0x80010000: opcode=3, target26=(0x00010200>>2)=0x4080
        uint jalWord = (3U << 26) | (0x00010200U >> 2);
        MipsInsn jal = MipsDecoder.Decode(0x80010000U, jalWord);
        Assert.True(jal.IsBranchOrJump);
        Assert.Equal(0x80010200U, jal.BranchTarget);
        Assert.False(jal.WritesReg(0));
        Assert.False(jal.ReadsReg(0));

        // ADDIU $v0 ($2), $a0 ($4), 16
        uint addiuWord = (9U << 26) | (4U << 21) | (2U << 16) | 16U;
        MipsInsn addiu = MipsDecoder.Decode(0x80010004U, addiuWord);
        Assert.True(addiu.ReadsReg(4));
        Assert.True(addiu.WritesReg(2));
        Assert.False(addiu.IsBranchOrJump);
    }

    [Fact]
    public void MipsLifter_LinearFunction_EmitsSingleStaticMethod()
    {
        uint[] code =
        {
            (9U << 26) | (0U << 21) | (2U << 16) | 7U, // ADDIU $v0, $zero, 7
            (31U << 21) | 8U,                          // JR $ra
            0x00000000U                                // NOP delay slot
        };
        uint fnStart = 0x80010000U;
        List<LiftedUnit> units = MipsLifter.LiftFunction(
            "fn_80010000", fnStart, fnStart + (uint)(code.Length * 4),
            addr => code[(addr - fnStart) / 4U],
            rdataTargets: null, out List<uint> midEntries);

        Assert.Empty(midEntries);
        Assert.Single(units);
        Assert.Equal("fn_80010000", units[0].Name);
        Assert.Contains("internal static void fn_80010000(IReStationCpu cpu)", string.Join('\n', units[0].Lines));
    }

    [Fact]
    public void MipsLifter_ForcedChunkedFunction_SplitsBlocksAndEmitsCoordinator()
    {
        uint fnStart = 0x80020000U;
        uint[] code = Enumerable.Repeat(0x00000000U, 24).ToArray();
        code[^2] = (31U << 21) | 8U; // JR $ra

        List<LiftedUnit> units = MipsLifter.LiftFunction(
            "fn_80020000", fnStart, fnStart + (uint)(code.Length * 4),
            addr => code[(addr - fnStart) / 4U],
            rdataTargets: null, out _, chunkInstructions: 8, forceChunk: true);

        Assert.True(units.Count >= 3); // >= 2 block units (_B0, _B1...) + coordinator unit
        Assert.Contains(units, u => u.Name.StartsWith("fn_80020000_B", StringComparison.Ordinal));
        Assert.Equal("fn_80020000", units[^1].Name);
    }

    [Fact]
    public void DiscExeLoader_TryParseHeader_ValidatesPsxMagicAndCanonicalRam()
    {
        byte[] exe = new byte[0x800 + 32];
        Encoding.ASCII.GetBytes("PS-X EXE").CopyTo(exe, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(exe.AsSpan(0x10, 4), 0x80010000U); // Pc0
        BinaryPrimitives.WriteUInt32LittleEndian(exe.AsSpan(0x14, 4), 0x80020000U); // Gp0
        BinaryPrimitives.WriteUInt32LittleEndian(exe.AsSpan(0x18, 4), 0x00010000U); // Physical TextAddr -> canonicalized to 0x80010000
        BinaryPrimitives.WriteUInt32LittleEndian(exe.AsSpan(0x1C, 4), 32U);
        BinaryPrimitives.WriteUInt32LittleEndian(exe.AsSpan(0x30, 4), 0x801FFF00U);

        Assert.True(DiscExeLoader.TryParseHeader(exe, out PsxExeHeader hdr));
        Assert.Equal(0x80010000U, hdr.Pc0);
        Assert.Equal(0x80010000U, hdr.TextAddr);
        Assert.Equal(32U, hdr.TextSize);

        exe[0] = (byte)'X';
        Assert.False(DiscExeLoader.TryParseHeader(exe, out _));
        Assert.False(DiscExeLoader.TryParseHeader(new byte[128], out _));
    }

    [Theory]
    [InlineData("--decomp-dir", "d", "--out-dir", "o", "--module-id", "m", "--serial", "s", "--title", "t")]
    [InlineData("--disc", "a.iso", "--gamedata-dir", "gd", "--decomp-dir", "d", "--out-dir", "o", "--module-id", "m", "--serial", "s", "--title", "t")]
    [InlineData("--disc", "a.iso", "--unknown-flag", "x")]
    public void RecompCompilerPipeline_ParseArgs_RejectsInvalidOrConflictingInputs(params string[] args)
    {
        Assert.Throws<ArgumentException>(() => RecompCompilerPipeline.ParseArgs(args));
    }

    [Fact]
    public void RecompCompilerPipeline_Run_FailsClosedOnMissingDirectoriesAndEmitsManifestWhenValid()
    {
        string gamedata = Path.Combine(_tempRoot, "gamedata");
        string decomp = Path.Combine(_tempRoot, "decomp");
        string outDir = Path.Combine(_tempRoot, "out");
        var missingOpts = new RecompCompilerOptions(null, gamedata, decomp, outDir, "sample", "SLUS-00001", "Sample");

        RecompCompileResult fail = new RecompCompilerPipeline(missingOpts).Run();
        Assert.False(fail.Success);

        Directory.CreateDirectory(gamedata);
        Directory.CreateDirectory(decomp);
        byte[] minimalExe = new byte[0x800 + 12];
        Encoding.ASCII.GetBytes("PS-X EXE").CopyTo(minimalExe, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(minimalExe.AsSpan(0x10, 4), 0x80010000U);
        BinaryPrimitives.WriteUInt32LittleEndian(minimalExe.AsSpan(0x18, 4), 0x80010000U);
        BinaryPrimitives.WriteUInt32LittleEndian(minimalExe.AsSpan(0x1C, 4), 12U);
        BinaryPrimitives.WriteUInt32LittleEndian(minimalExe.AsSpan(0x804, 4), (31U << 21) | 8U); // JR $ra
        File.WriteAllBytes(Path.Combine(gamedata, "SLUS_000.01"), minimalExe);
        File.WriteAllText(Path.Combine(gamedata, "SYSTEM.CNF"), "BOOT = cdrom:\\SLUS_000.01;1\n");

        RecompCompileResult ok = new RecompCompilerPipeline(missingOpts).Run();
        Assert.True(ok.Success);
        Assert.True(File.Exists(ok.ManifestPath));
    }

    [Fact]
    public void ModuleAssemblyLoader_SharedContractsAndFailClosedTraversal()
    {
        foreach (string shared in new[] { "reStation.Contracts", "reStation.Core", "Foundation.Core", "Foundation.Retro", "reEngine.Core" })
        {
            Assert.Contains(shared, ModuleAssemblyLoader.SharedAssemblyNames);
        }

        Directory.CreateDirectory(_tempRoot);
        var traversalManifest = new ReStationModuleManifest
        {
            ModuleId = "evil",
            EntryAssembly = "..\\..\\outside.dll",
            ModuleTypeName = "Evil.Mod"
        };

        Assert.ThrowsAny<Exception>(() => ModuleAssemblyLoader.LoadGameModule(traversalManifest, _tempRoot, out _));
    }
}
