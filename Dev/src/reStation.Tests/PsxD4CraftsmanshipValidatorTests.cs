// @module: reStation.Tests.PsxD4CraftsmanshipValidatorTests
// @purpose: Verifies all 6 D4_CleanSource decompiler-tell families, _01.._NN.c fragment ban, -Wno-* ban, -Wall -Wextra diagnostics, D4_CleanSource/D4_FunctionalRewrite normalization, d4b_lockstep_report.json, and Standard 18 camelCase JSON reports.

using System.Text.Json;
using reEngine.Core.Decomp;
using reStation.Decomp;
using Xunit;

namespace reStation.Tests;

public sealed class PsxD4CraftsmanshipValidatorTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "reslate-d4craft-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            try { Directory.Delete(_tempRoot, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Theory]
    [InlineData(PsxD4TellFamilies.UnfoldedShift, "s16 fn(s32 x) { return (s16)((x << 16) >> 16); }")]
    [InlineData(PsxD4TellFamilies.UnfoldedShift, "s16 fn(s32 x) { return (s16)((u32)((x) * 0x10000) >> 0x10); }")]
    [InlineData(PsxD4TellFamilies.UnfoldedShift, "s8 fn(s32 x) { return (s8)((x << 24) >> 24); }")]
    [InlineData(PsxD4TellFamilies.GlobalByteMath, "u16 fn(s32 i) { return *(u16 *)((u8 *)&g_table_head + i * 2); }")]
    [InlineData(PsxD4TellFamilies.GlobalByteMath, "u32 fn(s32 i) { return (&g_scalar_arr)[i]; }")]
    [InlineData(PsxD4TellFamilies.RedundantAddressCast, "void fn(Ctx *c) { (*(u16 *)&(c->field)) = 1; }")]
    [InlineData(PsxD4TellFamilies.RedundantAddressCast, "u16 fn(u8 a, u8 b) { return CONCAT11(a, b); }")]
    [InlineData(PsxD4TellFamilies.UnidiomaticControlFlow, "void fn(void) { while (true) { if (done()) goto lbl_out; } lbl_out: return; }")]
    [InlineData(PsxD4TellFamilies.UnidiomaticControlFlow, "s32 fn(s32 x) { x = x + 1; return x; }")]
    [InlineData(PsxD4TellFamilies.UnidiomaticControlFlow, "s32 fn(void) { s32 v; if (v = read_val(), v != 0) return v; return 0; }")]
    [InlineData(PsxD4TellFamilies.OptimizerComparison, "int fn(u16 tok) { return (u16)(tok + 2) > 1; }")]
    [InlineData(PsxD4TellFamilies.OptimizerComparison, "int fn(s32 idx) { return 6 < idx; }")]
    [InlineData(PsxD4TellFamilies.OptimizerComparison, "int fn(char ch) { switch (ch) { case 10: return 1; case 33: return 2; default: return 0; } }")]
    [InlineData(PsxD4TellFamilies.OptimizerComparison, "int fn(int m) { switch (m) { default: return 0; case 1: return 1; } }")]
    [InlineData(PsxD4TellFamilies.MechanicalNaming, "void fn(void) { u32 tokens_00 = 0; s32 max_glyphs_2 = 1; }")]
    [InlineData(PsxD4TellFamilies.MechanicalNaming, "void fn(void) { u32 work_word = 0; u32 status_flags = 1; }")]
    [InlineData(PsxD4TellFamilies.MechanicalNaming, "/* WARNING: unreachable */\n// Function: fn\nvoid fn(void) { u32 uVar1 = 0; }")]
    [InlineData(PsxD4TellFamilies.MechanicalNaming, "#include \"psx.h\"\n#include \"psx.h\"\nvoid fn(void) {}")]
    public void ScanCSourceText_DetectsAllSixDecompilerTellFamilies(string expectedFamily, string cSource)
    {
        IReadOnlyList<PsxD4TellViolation> hits = PsxD4CraftsmanshipValidator.ScanCSourceText(cSource, "src/gameplay.c");
        Assert.Contains(hits, h => h.Family == expectedFamily);
    }

    [Fact]
    public void ScanCSourceText_AllowsIdiomaticD4CleanSourceWithoutFalsePositives()
    {
        const string cleanC = """
            #include <stdint.h>
            #include "psx_clean_types.h"
            typedef void (*PsxCallbackFn)(void);
            extern u16 g_table_head[16];
            /* string literal traps must not trigger: "goto x; while(true) work_word" */
            s32 clean_step(PsxRect *rect, s16 delta, char ch, u32 cb_addr) {
                s32 idx;
                s32 val;
                if ((val = helper_call(delta, 4)) != 0) {
                    rect->x = (s16)(rect->x + val);
                }
                for (idx = 0; idx < 16; idx++) {
                    if (idx > 6 && rect->w >= 0) {
                        g_table_head[idx] = (u16)(delta << 2);
                    }
                }
                switch (ch) {
                case '\n':
                case '!':
                    ((PsxCallbackFn)(uintptr_t)cb_addr)();
                    return 1;
                default:
                    return 0;
                }
            }
            """;
        IReadOnlyList<PsxD4TellViolation> hits = PsxD4CraftsmanshipValidator.ScanCSourceText(cleanC, "src/gameplay.c");
        Assert.Empty(hits);
    }

    [Theory]
    [InlineData("gameplay_01.c", true)]
    [InlineData("overlays/ovl_12.c", true)]
    [InlineData("chunk_003.c", true)]
    [InlineData("gameplay.c", false)]
    [InlineData("core_mainloop.c", false)]
    public void FragmentFileNameCheck_RejectsMechanicalSuffixesOnly(string path, bool shouldReject)
    {
        Assert.Equal(shouldReject, PsxD4CraftsmanshipValidator.IsMechanicalFragmentFileName(path));
        if (shouldReject)
        {
            IReadOnlyList<PsxD4TellViolation> hits = PsxD4CraftsmanshipValidator.ScanCSourceText("void ok(void) {}\n", path);
            Assert.Contains(hits, h => h.Family == PsxD4TellFamilies.FragmentFile);
        }
    }

    [Theory]
    [InlineData("CMakeLists.txt", "add_compile_options(-Wall -Wno-everything)", true)]
    [InlineData("Makefile", "CFLAGS = -O2 -Wno-all", true)]
    [InlineData(".clangd", "CompileFlags:\n  Add: [-Wall, -w]", true)]
    [InlineData("compile_commands.json", "[{\"command\":\"clang -O2 -c a.c\"}]", true)]
    [InlineData("compile_commands.json", "[{\"arguments\":[\"clang\",\"-Wall\",\"-Wextra\",\"-Wno-unused-parameter\",\"-c\",\"a.c\"]}]", false)]
    public void ScanBuildConfigFlags_BansWarningSuppressionsAndEnforcesWallWextra(string file, string content, bool expectViolation)
    {
        bool requireStrict = file.Equals("compile_commands.json", StringComparison.OrdinalIgnoreCase);
        IReadOnlyList<PsxD4TellViolation> hits = PsxD4CraftsmanshipValidator.ScanBuildConfigFlags(content, file, requireStrict);
        Assert.Equal(expectViolation, hits.Count > 0);
    }

    [Fact]
    public void ParseCompilerDiagnostics_AndD4BuildReport_ValidateZeroWarningsAndStrictCamelCase()
    {
        PsxCompilerDiagnosticsSummary cleanDiag = PsxD4CraftsmanshipValidator.ParseCompilerDiagnostics(string.Empty, "mips-elf32");
        Assert.True(cleanDiag.IsClean);
        Assert.Equal(0, cleanDiag.ErrorCount);
        Assert.Equal(0, cleanDiag.WarningCount);

        PsxCompilerDiagnosticsSummary warnDiag = PsxD4CraftsmanshipValidator.ParseCompilerDiagnostics(
            "src/gameplay.c:14:9: warning: unused variable 'tmp' [-Wunused-variable]\nsrc/gameplay.c:20:5: error: undeclared identifier\n",
            "mips-elf32");
        Assert.False(warnDiag.IsClean);
        Assert.Equal(1, warnDiag.WarningCount);
        Assert.Equal(1, warnDiag.ErrorCount);

        Directory.CreateDirectory(_tempRoot);
        var report = new PsxD4BuildVerificationReport(
            2, "d4-build-report", "sample-ps1", "D4", PsxD4CraftsmanshipValidator.CanonicalD4Tier, "D4a",
            ["D1_Raw", "D2_Structured", "D3_Mapped", "D4_CleanSource"],
            4, 0, 0, PsxD4CraftsmanshipValidator.RequiredWarningFlags, 0,
            true, true, true, true, true, 12, 12, Array.Empty<PsxD4TellViolation>(), "Clean D4 build.");
        string writtenPath = PsxD4CraftsmanshipValidator.WriteD4BuildReport(_tempRoot, report);
        string json = File.ReadAllText(writtenPath);

        using (JsonDocument doc = JsonDocument.Parse(json))
        {
            Assert.Empty(PsxParityAndByteMatchVerifier.FindNonStandardReportKeys(doc.RootElement));
        }
        Assert.True(DecompD4StrictnessValidator.ValidateD4BuildReportJson(json).IsValid);
        Assert.True(PsxD4CraftsmanshipValidator.ValidateD4BuildReportJson(json).IsValid);

        const string nonCamelJson = """
        {
          "schemaVersion": 2,
          "compilerErrorCount": 0,
          "compilerWarningCount": 0,
          "compilerFlags": ["-Wall", "-Wextra", "-Wno-unused-parameter"],
          "craftsmanshipTellCount": 0,
          "isZeroWarningsVerified": true,
          "isZeroDecompilerTellsVerified": true,
          "passed": true
        }
        """;
        Assert.False(PsxD4CraftsmanshipValidator.ValidateD4BuildReportJson(nonCamelJson).IsValid);
    }

    [Fact]
    public void ValidateWorkspace_NormalizesD4FunctionalRewriteAlias_AndVerifiesD4bLockstepReport()
    {
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(Path.Combine(_tempRoot, "Corpus", "tiers", "D4_FunctionalRewrite", "src"));
        Directory.CreateDirectory(Path.Combine(_tempRoot, "Dev", "schemas"));

        File.WriteAllText(Path.Combine(_tempRoot, "Corpus", "tiers", "D4_FunctionalRewrite", "src", "core_mainloop.c"),
            "int step_frame(int x) { return x + 1; }\n");
        File.WriteAllText(Path.Combine(_tempRoot, "Dev", "schemas", "d4_build_report.json"), """
        {
          "schemaVersion": 2,
          "moduleName": "sample-ps1",
          "tier": "D4",
          "compilerErrorCount": 0,
          "compilerWarningCount": 0,
          "compilerFlags": ["-Wall", "-Wextra", "-Wno-unused-parameter"],
          "craftsmanshipTellCount": 0,
          "isZeroWarningsVerified": true,
          "isZeroDecompilerTellsVerified": true,
          "isPassed": true
        }
        """);
        _ = PsxParityAndByteMatchVerifier.WriteD4bLockstepReport(
            Path.Combine(_tempRoot, "Dev", "schemas"),
            new PsxD4bLockstepReport(1, "sample-ps1", "D4b", "pcsx_redux", 1200, 1200, 0, true, "2026-11-18T12:00:00Z"));

        PsxD4CraftsmanshipValidationResult ok = PsxD4CraftsmanshipValidator.ValidateWorkspace(
            _tempRoot, outputDir: Path.Combine(_tempRoot, "out"), moduleName: "sample-ps1", requireD4bLockstep: true);
        Assert.True(ok.IsValid, ok.Summary);
        Assert.Contains("D4_CleanSource", ok.DiscoveredTiers);
        Assert.DoesNotContain("D4_FunctionalRewrite", ok.DiscoveredTiers);
        Assert.Equal("D4b", ok.Report!.D4ParityTier);

        D4StrictnessValidationResult strict = PsxExportAndAssetPipeline.ValidateD4WorkspaceStrictness(_tempRoot);
        Assert.True(strict.IsValid, strict.Summary);
        Assert.Contains("D4_CleanSource", strict.DiscoveredTiers);

        // Failing lockstep (mismatchedFrames > 0) must fail closed.
        File.WriteAllText(Path.Combine(_tempRoot, "Dev", "schemas", "d4b_lockstep_report.json"), """
        {
          "schemaVersion": 1,
          "moduleName": "sample-ps1",
          "d4ParityTier": "D4b",
          "oracleType": "pcsx_redux",
          "totalFrames": 1200,
          "matchedFrames": 1198,
          "mismatchedFrames": 2,
          "isPassed": false
        }
        """);
        PsxD4CraftsmanshipValidationResult badLock = PsxD4CraftsmanshipValidator.ValidateWorkspace(_tempRoot, requireD4bLockstep: true);
        Assert.False(badLock.IsValid);
        Assert.Contains(badLock.Violations, v => v.Family == PsxD4TellFamilies.D4bLockstep);
    }

    [Fact]
    public async Task VerifyCompilerDiagnosticsAsync_RunsRealWallWextraCheck_WhenCompilerAvailable()
    {
        Directory.CreateDirectory(_tempRoot);
        string cleanFile = Path.Combine(_tempRoot, "clean_tu.c");
        string warnFile = Path.Combine(_tempRoot, "warn_tu.c");
        File.WriteAllText(cleanFile, "int add_vals(int a, int unused_param) { return a + 1; }\n");
        File.WriteAllText(warnFile, "int bad_warn(int a) { int unused_local = 7; return a; }\n");

        PsxD4CraftsmanshipValidationResult cleanRes = await PsxD4CraftsmanshipValidator.VerifyCompilerDiagnosticsAsync(
            [cleanFile], Array.Empty<string>(), "cc-clean", Path.Combine(_tempRoot, "out-clean"));
        if (cleanRes.Violations.Any(v => v.RuleId == "D4-CC-NA"))
            return; // Graceful skip if no C compiler is installed on host.

        Assert.True(cleanRes.IsValid, cleanRes.Summary);
        Assert.Equal(0, cleanRes.WarningCount);
        Assert.True(File.Exists(Path.Combine(_tempRoot, "out-clean", "d4_build_report.json")));

        PsxD4CraftsmanshipValidationResult warnRes = await PsxD4CraftsmanshipValidator.VerifyCompilerDiagnosticsAsync(
            [warnFile], Array.Empty<string>(), "cc-warn");
        Assert.False(warnRes.IsValid);
        Assert.True(warnRes.WarningCount > 0);
    }
}
