// @module: reStation.Tests.Wave3Patch021RegressionTests
// @purpose: Wave 3.1 v0.2.1 regression tests for DEFECT-01 (delay-slot chunk label parity + clean DLL build) and DEFECT-03 (consumer settings round-trip).
// @depends-on: System.Buffers.Binary, System.Text.Json, System.Text.RegularExpressions, reStation.Compiler, reStation.Core, Xunit
// @exports: Wave3Patch021RegressionTests
// @owner: devv-tests (Wave 3.1)

using System.Buffers.Binary;
using System.Text.Json;
using System.Text.RegularExpressions;
using reStation.Compiler;
using reStation.Core;

namespace reStation.Tests;

public sealed class Wave3Patch021RegressionTests : IDisposable
{
    private readonly string _tempRoot;

    public Wave3Patch021RegressionTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"reStation-wave3-021-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void MipsLifter_ChunkedDynSwitchAcrossDelaySlots_EmitsEveryGotoTargetLabelAndCompilesWithZeroWarnings()
    {
        const uint BaseAddr = 0x80010000U;
        const int WordCount = 278;
        byte[] exeBytes = BuildDynSwitchRodataPsxExeBytes(BaseAddr, WordCount);
        uint fnEnd = BaseAddr + (uint)(WordCount * 4);

        uint ReadWord(uint vaddr)
        {
            if (vaddr < BaseAddr || vaddr >= BaseAddr + 0x800U) return 0U;
            int offset = 0x800 + (int)(vaddr - BaseAddr);
            return BinaryPrimitives.ReadUInt32LittleEndian(exeBytes.AsSpan(offset, 4));
        }

        HashSet<uint> rdataTargets = ExeScanHelpers.ScanJumpTargets(exeBytes, BaseAddr, fnEnd);
        rdataTargets.Add(BaseAddr + 12U);
        rdataTargets.Add(BaseAddr + 16U);
        rdataTargets.Add(BaseAddr + 840U);
        rdataTargets.Add(BaseAddr + 844U);

        List<LiftedUnit> forcedUnits = MipsLifter.LiftFunction(
            "Fn_SwitchDelayParityForced", BaseAddr, fnEnd, ReadWord, rdataTargets, out _, chunkInstructions: 12, forceChunk: true);
        Assert.True(forcedUnits.Count > 1, "Expected forced chunking to split function into multiple LiftedUnit chunks.");
        AssertAllGotoTargetsHaveLabelsInSameUnit(forcedUnits);

        List<LiftedUnit> defaultUnits = MipsLifter.LiftFunction(
            "Fn_SwitchDelayParityDefault", BaseAddr, fnEnd, ReadWord, rdataTargets, out _);
        Assert.True(defaultUnits.Count > 1, "Expected >210 instruction function to split into multiple LiftedUnit chunks.");
        AssertAllGotoTargetsHaveLabelsInSameUnit(defaultUnits);

        string exePath = Path.Combine(_tempRoot, "SLUS_999.99");
        File.WriteAllBytes(exePath, exeBytes);

        string funcJsonPath = Path.Combine(_tempRoot, "functions.json");
        File.WriteAllText(funcJsonPath, "[{\"address\":\"0x80010000\",\"size\":1112}]");

        string outDir = Path.Combine(_tempRoot, "out");
        string dataDir = Path.Combine(_tempRoot, "data");
        Directory.CreateDirectory(outDir);
        Directory.CreateDirectory(dataDir);

        var options = new RecompCompilerOptions(
            DiscPath: null,
            GameDataDir: null,
            DecompDir: string.Empty,
            OutDir: outDir,
            ModuleId: "patch021test",
            SerialNumber: "SLUS-99999",
            Title: "Patch 021 Regression")
        {
            ExePath = exePath,
            FunctionsJsonPath = funcJsonPath,
            Version = "0.2.1",
            EmitCleanRoomOnly = false,
            CompileGeneratedDll = true,
            DataDir = dataDir
        };

        RecompCompileResult compileResult = new RecompCompilerPipeline(options).Run();
        Assert.True(compileResult.Success, $"Expected RecompCompilerPipeline with CompileGeneratedDll=true to succeed, got: {compileResult.Message}");

        string generatedDllPath = Path.Combine(outDir, "patch021test.Generated.dll");
        Assert.True(File.Exists(generatedDllPath), $"Expected compiled generated DLL at '{generatedDllPath}'.");

        string csprojPath = Path.Combine(outDir, "patch021test.Module.csproj");
        Assert.True(File.Exists(csprojPath), $"Expected generated Module.csproj at '{csprojPath}'.");
        string csproj = File.ReadAllText(csprojPath);
        Assert.Contains("<TreatWarningsAsErrors>true</TreatWarningsAsErrors>", csproj, StringComparison.Ordinal);
        Assert.Contains("<NoWarn>$(NoWarn);CS0162;CS0164</NoWarn>", csproj, StringComparison.Ordinal);
    }

    [Fact]
    public void RecompSettings_LoadAndSave_PreservesConsumerSchemaAppIdAndExtensionMetadata()
    {
        string settingsPath = Path.Combine(_tempRoot, "settings.json");
        const string InitialJson = """
        {
          "schemaVersion": 2,
          "appId": "crowndepths-recomp",
          "installProfile": "lightweight",
          "umbrellaSubpath": "StreamItAll\\reEngine\\games\\CrownDepthsRecomp"
        }
        """;
        File.WriteAllText(settingsPath, InitialJson);

        RecompSettings loaded = RecompSettings.Load(settingsPath).Value;
        Assert.Equal(2, loaded.SchemaVersion);
        Assert.Equal("crowndepths-recomp", loaded.AppId);

        loaded.Save(settingsPath);

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
        JsonElement root = doc.RootElement;
        Assert.Equal(2, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("crowndepths-recomp", root.GetProperty("appId").GetString());
        Assert.Equal("lightweight", root.GetProperty("installProfile").GetString());
        Assert.Equal(@"StreamItAll\reEngine\games\CrownDepthsRecomp", root.GetProperty("umbrellaSubpath").GetString());
    }

    private static void AssertAllGotoTargetsHaveLabelsInSameUnit(IReadOnlyList<LiftedUnit> units)
    {
        foreach (LiftedUnit unit in units)
        {
            string body = string.Join('\n', unit.Lines);
            var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in Regex.Matches(body, @"(?m)^\s*L_([0-9A-F]{8}):"))
                labels.Add(m.Groups[1].Value);

            foreach (Match m in Regex.Matches(body, @"\bgoto\s+L_([0-9A-F]{8});"))
            {
                string target = m.Groups[1].Value;
                Assert.True(labels.Contains(target),
                    $"LiftedUnit '{unit.Name}' emits 'goto L_{target};' without matching label 'L_{target}:' in the same unit.");
            }
        }
    }

    private static byte[] BuildDynSwitchRodataPsxExeBytes(uint baseAddr, int wordCount)
    {
        const int HeaderSize = 0x800;
        const int TextSectionSize = 0x800;
        byte[] exe = new byte[HeaderSize + TextSectionSize];
        "PS-X EXE"u8.CopyTo(exe.AsSpan(0, 8));
        BinaryPrimitives.WriteUInt32LittleEndian(exe.AsSpan(0x10, 4), baseAddr);
        BinaryPrimitives.WriteUInt32LittleEndian(exe.AsSpan(0x18, 4), baseAddr);
        BinaryPrimitives.WriteUInt32LittleEndian(exe.AsSpan(0x1C, 4), TextSectionSize);
        BinaryPrimitives.WriteUInt32LittleEndian(exe.AsSpan(0x30, 4), 0x801FFF00U);

        Span<uint> words = new uint[wordCount];
        words[0] = 0x01000008U; // jr $t0 (dynamic switch)
        words[1] = 0x00000000U; // nop delay slot
        for (int i = 2; i < wordCount - 2; i++)
        {
            words[i] = (i & 3) switch
            {
                0 => 0x10000000U, // beq $0, $0, 0 (branches into own delay slot)
                1 => 0x10000001U, // beq $0, $0, +1 (branches across delay slot)
                2 => 0x08000000U | (((baseAddr + (uint)(((i + 3) % (wordCount - 6) + 2) * 4)) & 0x0FFFFFFFU) >> 2),
                _ => 0x1000FFFEU  // beq $0, $0, -2 (branches backward into prior delay slot)
            };
        }
        words[wordCount - 2] = 0x03E00008U; // jr $ra
        words[wordCount - 1] = 0x00000000U; // nop

        for (int i = 0; i < words.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(exe.AsSpan(HeaderSize + (i * 4), 4), words[i]);

        return exe;
    }
}
