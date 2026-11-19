// @module: reStation.Decomp.PsxD4CraftsmanshipValidator
// @purpose: Enforces D4_CleanSource 6-tell craftsmanship gate, _01.._NN.c fragment ban, -Wno-* ban, -Wall -Wextra verification, and Standard 18 camelCase reports.
// @depends-on: System, System.Collections.Generic, System.Globalization, System.IO, System.Linq, System.Text, System.Text.RegularExpressions, reEngine.Core.Decomp, reEngine.Core.Manifests
// @exports: PsxD4TellFamilies, PsxD4TellViolation, PsxCompilerDiagnosticsSummary, PsxD4BuildVerificationReport, PsxD4CraftsmanshipValidationResult, PsxD4CraftsmanshipValidator
// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using reEngine.Core.Decomp;
using reEngine.Core.Manifests;

namespace reStation.Decomp;

/// <summary>Canonical tell-family identifiers enforced by the <c>D4_CleanSource</c> craftsmanship gate.</summary>
public static class PsxD4TellFamilies
{
    /// <summary>Family 1: Unfolded s16/s8 ISA sign-extension shift sequences.</summary>
    public const string UnfoldedShift = "T1_UnfoldedShift";
    /// <summary>Family 2: Scalar global byte-offset pointer arithmetic (&amp;g_* + N).</summary>
    public const string GlobalByteMath = "T2_GlobalByteMath";
    /// <summary>Family 3: Redundant address-cast dereferences (*(T *)&amp;(struct.field)) and CONCAT/SUB macros.</summary>
    public const string RedundantAddressCast = "T3_RedundantAddressCast";
    /// <summary>Family 4: Unidiomatic while(true)/goto, three-address x = x + 1, and comma conditions.</summary>
    public const string UnidiomaticControlFlow = "T4_UnidiomaticControlFlow";
    /// <summary>Family 5: Unsigned wraparound range tricks, Yoda comparisons, raw-ASCII switches, and mid-switch default.</summary>
    public const string OptimizerComparison = "T5_OptimizerComparison";
    /// <summary>Family 6: SSA clone suffixes, generic fallback locals, Ghidra stems, warnings, and duplicate headers.</summary>
    public const string MechanicalNaming = "T6_MechanicalNaming";
    /// <summary>Mechanical _01.._NN.c fragment filename rejection.</summary>
    public const string FragmentFile = "FragmentFile";
    /// <summary>Banned -Wno-everything / -Wno-all / -w compiler suppression flag.</summary>
    public const string BannedCompilerFlag = "BannedCompilerFlag";
    /// <summary>Compiler build error, non-zero warning count, or failing build report.</summary>
    public const string CompilerDiagnostic = "CompilerDiagnostic";
    /// <summary>D4b execution lockstep oracle report violation.</summary>
    public const string D4bLockstep = "D4bLockstep";
    /// <summary>Non-Standard-18 JSON report key or malformed schema.</summary>
    public const string ReportSchema = "ReportSchema";
}

/// <summary>Single craftsmanship, build-flag, or compiler-diagnostic violation detected at <c>D4</c>–<c>D5</c>.</summary>
public sealed record PsxD4TellViolation(string Family, string RuleId, string FilePath, int LineNumber, string Snippet, string Description);

/// <summary>Parsed GCC/Clang/Zig compiler diagnostics summary.</summary>
public sealed record PsxCompilerDiagnosticsSummary(bool IsClean, int ErrorCount, int WarningCount, IReadOnlyList<PsxD4TellViolation> Violations);

/// <summary>Standard 18 <c>camelCase</c> <c>d4_build_report.json</c> DTO compatible with <see cref="D4BuildReport"/>.</summary>
public sealed record PsxD4BuildVerificationReport(
    int SchemaVersion, string ManifestType, string ModuleName, string Tier, string CorpusTier, string D4ParityTier,
    IReadOnlyList<string> PreservedTiers, int CompiledFileCount, int CompilerErrorCount, int CompilerWarningCount,
    IReadOnlyList<string> CompilerFlags, int CraftsmanshipTellCount, bool IsZeroWarningsVerified, bool IsZeroDecompilerTellsVerified,
    bool IsPassed, bool MipsElf32Verified, bool HostParityVerified, int AssertionsPassedCount, int AssertionsTotalCount,
    IReadOnlyList<PsxD4TellViolation> Violations, string Summary);

/// <summary>Aggregate outcome of <see cref="PsxD4CraftsmanshipValidator"/>.</summary>
public sealed record PsxD4CraftsmanshipValidationResult(
    bool IsValid, int TellCount, int ErrorCount, int WarningCount, IReadOnlyList<string> DiscoveredTiers,
    IReadOnlyList<PsxD4TellViolation> Violations, PsxD4BuildVerificationReport? Report, string Summary);

/// <summary>
/// Enforces Standard 24 <c>D4_CleanSource</c> craftsmanship across all 6 tell families, rejects <c>_01.._NN.c</c>
/// fragment files, bans warning suppression flags, verifies <c>-Wall -Wextra -Wno-unused-parameter</c>, and emits Standard 18 reports.
/// </summary>
public static partial class PsxD4CraftsmanshipValidator
{
    /// <summary>Canonical Standard 24 corpus directory name for Stage D4.</summary>
    public const string CanonicalD4Tier = "D4_CleanSource";
    /// <summary>Pre-v0.3.1 transition read-alias accepted on input and normalized to <see cref="CanonicalD4Tier"/>.</summary>
    public const string LegacyD4TierAlias = "D4_FunctionalRewrite";
    /// <summary>Canonical ordered corpus tier directories under <c>Corpus\tiers\</c>.</summary>
    public static readonly string[] CanonicalCorpusTiers = ["D1_Raw", "D2_Structured", "D3_Mapped", CanonicalD4Tier, "D5_ByteExact"];
    /// <summary>Mandatory warning flags expected on clean <c>D4</c> compilation invocations.</summary>
    public static readonly string[] RequiredWarningFlags = ["-Wall", "-Wextra", "-Wno-unused-parameter"];

    private static readonly (string Family, string RuleId, Regex Pattern, string Desc)[] CodeTellRules =
    [
        (PsxD4TellFamilies.UnfoldedShift, "D4-T1-01",
            new Regex(@"(?:<<|\*)\s*(?:16|24|0x10|0x18|0x10000|0x1000000)[uUlL]*[\s\)]*>>\s*(?:16|24|0x10|0x18)\b", RegexOptions.CultureInvariant),
            "Unfolded s16/s8 sign-extension shift sequence; fold into (s16)/(s8) cast."),
        (PsxD4TellFamilies.GlobalByteMath, "D4-T2-01",
            new Regex(@"&\s*g_[A-Za-z0-9_]+(?:\s*\))?\s*[+-](?![>=\-])|\(\s*&\s*g_[A-Za-z0-9_]+\s*\)\s*\[|\*\s*\([^);]+\*\)\s*\(\s*(?:\(\s*(?:s32|u32|u8\s*\*|uint8_t\s*\*|void\s*\*|uintptr_t)\s*\)\s*)?&\s*g_[A-Za-z0-9_]+", RegexOptions.CultureInvariant),
            "Scalar global byte-offset pointer math; declare typed extern array and index directly."),
        (PsxD4TellFamilies.RedundantAddressCast, "D4-T3-01",
            new Regex(@"\*\s*\([^);]+\*\)\s*&\s*(?:\(|g_[A-Za-z0-9_]+|[A-Za-z_]\w*\s*(?:\.|->|\[))|\(\s*(?:u8|s8|uint8_t|int8_t|char)\s*\*\s*\)\s*&\s*\([^)]+\)\s*[+-]|\b(?:CONCAT(?:11|12|13|21|22|31|44)|SUB(?:41|42|81|82|84))\s*\(", RegexOptions.CultureInvariant),
            "Redundant address-cast dereference or raw CONCAT/SUB subpiece macro; fix struct field type."),
        (PsxD4TellFamilies.UnidiomaticControlFlow, "D4-T4-01",
            new Regex(@"\bwhile\s*\(\s*(?:true|1)\s*\)|\bgoto\s+[A-Za-z_]\w*\s*;|\b([A-Za-z_]\w*(?:\.[A-Za-z_]\w*|->[A-Za-z_]\w*){0,4})\s*(?<![!<>])=\s*\1\s*[+-](?![>=\-])", RegexOptions.CultureInvariant),
            "Unidiomatic while(true)/goto or three-address x = x +/- n increment."),
        (PsxD4TellFamilies.OptimizerComparison, "D4-T5-01",
            new Regex(@"\(\s*(?:u8|u16|u32|uint8_t|uint16_t|uint32_t|unsigned\s+(?:char|short|int))\s*\)\s*\([^()]*[+-]\s*(?:0x[0-9a-fA-F]+|\d+)[uUlL]*\s*\)\s*(?:<=|>=|<|>)\s*(?:0x[0-9a-fA-F]+|\d+)[uUlL]*\b|\b(?:0x[0-9a-fA-F]+|\d+)[uUlL]*\s*(?:<=|>=|<|>)\s*\(\s*(?:u8|u16|u32|uint8_t|uint16_t|uint32_t|unsigned\s+(?:char|short|int))\s*\)\s*\([^()]*[+-]\s*(?:0x[0-9a-fA-F]+|\d+)[uUlL]*\s*\)|\b\w+\s*<\s*(?:0x[0-9a-fA-F]+|\d+)[uUlL]*\s*\)\s*\^\s*1\b", RegexOptions.CultureInvariant),
            "Compiler-optimized unsigned wraparound range trick or boolean XOR comparison."),
        (PsxD4TellFamilies.OptimizerComparison, "D4-T5-02",
            new Regex(@"(?:^|[\(\{,;:?!=]|&&|\|\||\breturn\b)\s*-?(?:0x[0-9a-fA-F]+|\d+)[uUlL]*(?![\d\.xXa-fA-F])\s*(?:<=|>=|(?<!<)<(?![<=])|(?<!>)>(?![>=]))\s*[A-Za-z_\(]", RegexOptions.CultureInvariant | RegexOptions.Multiline),
            "Yoda constant-on-left inequality comparison."),
        (PsxD4TellFamilies.MechanicalNaming, "D4-T6-01",
            new Regex(@"\b(?:FUN|DAT|UNK|PTR|LAB|SUB|switchD)_[0-9A-Za-z_]+\b|\b[usif]Ram[0-9a-fA-F]{6,8}\b|\bundefined[12348]?\b|\bcode\s*\*|(?<!\.)(?<!->)\b(?:[ap]*[A-Za-z]Var\d+|[A-Za-z_]*Stack[A-Za-z0-9_]*|stack0x[0-9a-fA-F]+|_?local_[A-Za-z0-9_]+|unaff_[A-Za-z0-9_]+|extraout_[A-Za-z0-9_]+|param_?\d+|arg_?\d+|loc_[0-9a-fA-F]+|sub_loc_[0-9a-fA-F]+|code_r0x[0-9a-fA-F]+|saved_s\d+|saved_retaddr|reg_[vat][0-9]+|reg_(?:hi|lo|ra|gp|sp|fp|zero)|r0_\d+)\b", RegexOptions.CultureInvariant),
            "Unmapped Ghidra symbol, mechanical register/stack local, or undefined type."),
        (PsxD4TellFamilies.MechanicalNaming, "D4-T6-02",
            new Regex(@"(?<!\.)(?<!->)\b(?:work_word|work_acc|status_flags|word_cursor|half_cursor|byte_cursor|text_cursor|frame_slot|saved_ctx|caller_slot|abi_reg|cond_flag|carry_word|gte_reg|local_buf|fmt_arg|fmt_args|cfg_stage|skip_branch|cond_ok|loop_active|loop_running|tmp_val|accum_val|step_val|reg_val|hi_half|slot_buf|slot_base|cur_tmp_val|u_val|slot_val|ctx_val|abi_val)\b", RegexOptions.CultureInvariant),
            "Generic heuristic fallback local variable stem."),
        (PsxD4TellFamilies.MechanicalNaming, "D4-T6-03",
            new Regex(@"(?:^|[;(,])\s*(?:const\s+|volatile\s+|unsigned\s+|signed\s+|struct\s+)*(?!return\b|goto\b|else\b|case\b)[A-Za-z_]\w*(?:\s*\*+)?\s+(?!g_|psx_|gte_|spu_|cd_|gpu_|pad_|dma_|tmr_|ovl)([A-Za-z_]\w*_(?:0\d|[2-9]))\b(?:\s*\[|\s*=|\s*[;,\)])", RegexOptions.CultureInvariant | RegexOptions.Multiline),
            "SSA clone numeric suffix on local variable or parameter declaration.")
    ];

    [GeneratedRegex(@"_\d{2,}\.c$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex MechanicalFragmentFileRegex();
    [GeneratedRegex(@"/\*\s*WARNING:[^\r\n]*|^\s*//\s*Function:[^\r\n]*|^\s*\*\s*@(?:callers|callees)\s+none\b|/\*\s*(?:Possible|Probable)\b", RegexOptions.Multiline | RegexOptions.CultureInvariant)] private static partial Regex GhidraCommentTellRegex();
    [GeneratedRegex(@"^(?<file>.+?):(?<line>\d+)(?::(?<col>\d+))?:\s*(?<sev>fatal error|error|warning):\s*(?<msg>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex CompilerDiagnosticLineRegex();
    [GeneratedRegex(@"\b(?:if|while)\s*\(", RegexOptions.CultureInvariant)] private static partial Regex CondHeadRegex();
    [GeneratedRegex(@"\bswitch\s*\((?<expr>[^)]*)\)\s*\{", RegexOptions.CultureInvariant)] private static partial Regex SwitchHeadRegex();
    [GeneratedRegex(@"\bcase\s+(?<val>0x[0-9a-fA-F]+|\d+)\s*:", RegexOptions.CultureInvariant)] private static partial Regex NumericCaseRegex();
    [GeneratedRegex(@"\b(?:ch|chr|c|byte|char|glyph|ascii|tok|token)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex CharSwitchExprRegex();

    /// <summary>Normalizes <c>D4_FunctionalRewrite</c> transition alias to canonical <c>D4_CleanSource</c>.</summary>
    public static string NormalizeCorpusTierName(string? tier) =>
        string.Equals(tier?.Trim(), LegacyD4TierAlias, StringComparison.OrdinalIgnoreCase) ? CanonicalD4Tier : (tier?.Trim() ?? string.Empty);

    /// <summary>Normalizes an earned tier list, deduplicating and rewriting <c>D4_FunctionalRewrite</c> to <c>D4_CleanSource</c>.</summary>
    public static IReadOnlyList<string> NormalizePreservedTiers(IEnumerable<string>? tiers) =>
        tiers is null ? Array.Empty<string>() : tiers.Select(NormalizeCorpusTierName).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>Discovers preserved tiers under <c>Corpus\tiers\</c>, supporting <c>D4_CleanSource</c> and <c>D4_FunctionalRewrite</c>.</summary>
    public static IReadOnlyList<string> DiscoverCorpusTiers(string rootDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDir);
        string tiersRoot = Path.Combine(Path.GetFullPath(rootDir), "Corpus", "tiers");
        if (!Directory.Exists(tiersRoot)) return Array.Empty<string>();
        string[] probeOrder = ["D1_Raw", "D2_Structured", "D3_Mapped", CanonicalD4Tier, LegacyD4TierAlias, "D5_ByteExact"];
        return NormalizePreservedTiers(probeOrder.Where(t => Directory.Exists(Path.Combine(tiersRoot, t))));
    }

    /// <summary>Returns true when <paramref name="filePath"/> is a mechanical line-count fragment like <c>part_01.c</c>.</summary>
    public static bool IsMechanicalFragmentFileName(string filePath) => MechanicalFragmentFileRegex().IsMatch(Path.GetFileName(filePath));

    /// <summary>Scans a C/H source string across all 6 tell families, comma conditions, raw-ASCII switches, and duplicate headers.</summary>
    public static IReadOnlyList<PsxD4TellViolation> ScanCSourceText(string sourceText, string fileLabel = "source.c")
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        var violations = new List<PsxD4TellViolation>();
        if (IsMechanicalFragmentFileName(fileLabel))
            violations.Add(new(PsxD4TellFamilies.FragmentFile, "D4-FRAG-01", fileLabel, 1, Path.GetFileName(fileLabel), "Mechanical _01.._NN.c fragment filename banned at D4; coalesce into logical modules."));
        string stripped = StripCommentsAndStringsPreserveLines(sourceText);
        foreach ((string family, string ruleId, Regex rx, string desc) in CodeTellRules)
            foreach (Match m in rx.Matches(stripped))
                violations.Add(new(family, ruleId, fileLabel, LineOf(stripped, m.Index), TrimSnippet(m.Value), desc));
        foreach (Match m in GhidraCommentTellRegex().Matches(sourceText))
            violations.Add(new(PsxD4TellFamilies.MechanicalNaming, "D4-T6-04", fileLabel, LineOf(sourceText, m.Index), TrimSnippet(m.Value), "Ghidra warning, duplicate // Function: header, or empty @callers/@callees comment."));
        CheckCommaConditions(stripped, fileLabel, violations);
        CheckSwitchStatements(stripped, fileLabel, violations);
        CheckDuplicateIncludes(sourceText, fileLabel, violations);
        return violations;
    }

    /// <summary>Scans a C/H source file on disk via <see cref="ScanCSourceText"/>.</summary>
    public static IReadOnlyList<PsxD4TellViolation> ScanCSourceFile(string filePath, string? baseRootDir = null)
    {
        string full = Path.GetFullPath(filePath);
        string label = string.IsNullOrWhiteSpace(baseRootDir) ? full : Path.GetRelativePath(Path.GetFullPath(baseRootDir), full).Replace('\\', '/');
        return ScanCSourceText(File.ReadAllText(full, Encoding.UTF8), label);
    }

    /// <summary>Scans build configs (CMake/Makefile/.clangd/compile_commands.json) for banned warning suppression flags.</summary>
    public static IReadOnlyList<PsxD4TellViolation> ScanBuildConfigFlags(string configText, string fileLabel, bool requireStrictWarningFlags = false) =>
        PsxCleanRoomValidator.ScanBuildConfigFlags(configText, fileLabel, requireStrictWarningFlags);

    /// <summary>Parses GCC/Clang/Zig compiler stderr/stdout into exact error and warning counts.</summary>
    public static PsxCompilerDiagnosticsSummary ParseCompilerDiagnostics(string? compilerOutput, string targetLabel = "build")
    {
        if (string.IsNullOrWhiteSpace(compilerOutput)) return new(true, 0, 0, Array.Empty<PsxD4TellViolation>());
        var list = new List<PsxD4TellViolation>();
        int errs = 0, warns = 0;
        foreach (string raw in compilerOutput.Split('\n'))
        {
            Match m = CompilerDiagnosticLineRegex().Match(raw.Trim());
            if (!m.Success) continue;
            bool isErr = m.Groups["sev"].Value.Contains("error", StringComparison.OrdinalIgnoreCase);
            if (isErr) errs++; else warns++;
            int line = int.TryParse(m.Groups["line"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ln) ? ln : 0;
            list.Add(new(PsxD4TellFamilies.CompilerDiagnostic, isErr ? "D4-CC-ERR" : "D4-CC-WARN", $"{targetLabel}:{m.Groups["file"].Value}", line, TrimSnippet(m.Groups["msg"].Value), m.Groups["msg"].Value));
        }
        return new(errs == 0 && warns == 0, errs, warns, list);
    }

    /// <summary>Validates a <c>d4_build_report.json</c> payload via <see cref="DecompD4StrictnessValidator"/> plus strict warning/camelCase checks.</summary>
    public static PsxD4CraftsmanshipValidationResult ValidateD4BuildReportJson(string reportJson, string label = "d4_build_report.json") =>
        PsxParityAndByteMatchVerifier.ValidateD4BuildReportJsonCore(reportJson, label);

    /// <summary>Runs MIPS ELF32 + host parity compilation verification via Zig/Clang when installed.</summary>
    public static async Task<PsxD4CraftsmanshipValidationResult> VerifyCompilerDiagnosticsAsync(
        IEnumerable<string> cFiles, IEnumerable<string> includeDirs, string moduleName = "psx-module", string? outputDir = null, string? explicitCompiler = null, CancellationToken ct = default)
    {
        string[] sources = cFiles.Select(Path.GetFullPath).ToArray();
        string? cc = PsxCleanRoomValidator.FindCompilerExecutable(explicitCompiler);
        if (sources.Length == 0 || cc is null)
            return new(false, 0, 1, 0, Array.Empty<string>(), [new(PsxD4TellFamilies.CompilerDiagnostic, "D4-CC-NA", "compiler", 0, cc ?? "none", "No C sources or Zig/Clang compiler available.")], null, "Compiler executable or C sources unavailable.");
        bool isZig = Path.GetFileNameWithoutExtension(cc).Equals("zig", StringComparison.OrdinalIgnoreCase);
        string[] inc = includeDirs.Where(Directory.Exists).Select(d => $"-I{Path.GetFullPath(d)}").ToArray();
        string[] mipsArgs = isZig ? ["cc", "-target", "mipsel-freestanding-eabi", "-mcpu=mips2", "-ffreestanding", "-std=gnu11", "-O1", "-g0", .. RequiredWarningFlags, .. inc] : ["--target=mipsel-unknown-none-elf", "-ffreestanding", "-std=gnu11", "-O1", "-g0", .. RequiredWarningFlags, .. inc];
        string[] hostArgs = isZig ? ["cc", "-std=gnu11", "-O1", "-g0", .. RequiredWarningFlags, .. inc] : ["-std=gnu11", "-O1", "-g0", .. RequiredWarningFlags, .. inc];
        var violations = new List<PsxD4TellViolation>();
        int errs = 0, warns = 0; bool mipsOk = true, hostOk = true;
        string objDir = Path.Combine(Path.GetTempPath(), "reslate-d4-obj-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(objDir);
        try
        {
            for (int i = 0; i < sources.Length; i++)
                foreach ((string pass, string[] args) in new[] { ("mips-elf32", mipsArgs), ("host-parity", hostArgs) })
                {
                    string objPath = Path.Combine(objDir, $"{pass}_{i}.o");
                    (int rc, string stderr) = await PsxCleanRoomValidator.RunCompilerProcessAsync(cc, [.. args, "-c", sources[i], "-o", objPath], Path.GetDirectoryName(sources[i])!, ct).ConfigureAwait(false);
                    PsxCompilerDiagnosticsSummary diag = ParseCompilerDiagnostics(stderr, pass);
                    int passErrs = diag.ErrorCount + (rc != 0 && diag.ErrorCount == 0 ? 1 : 0);
                    errs += passErrs; warns += diag.WarningCount; violations.AddRange(diag.Violations);
                    if (pass == "mips-elf32" && (passErrs > 0 || diag.WarningCount > 0 || !PsxParityAndByteMatchVerifier.IsMipsElf32Object(objPath))) mipsOk = false;
                    if (pass == "host-parity" && (passErrs > 0 || diag.WarningCount > 0 || !File.Exists(objPath))) hostOk = false;
                }
        }
        finally { try { Directory.Delete(objDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        bool ok = errs == 0 && warns == 0 && violations.Count == 0 && mipsOk && hostOk;
        var report = new PsxD4BuildVerificationReport(2, "d4-build-report", moduleName, "D4", CanonicalD4Tier, "D4a", [CanonicalD4Tier],
            sources.Length, errs, warns, RequiredWarningFlags, 0, errs == 0 && warns == 0, true, ok, mipsOk, hostOk, ok ? 1 : 0, 1, violations,
            ok ? $"Verified {sources.Length} TU(s) under -Wall -Wextra -Wno-unused-parameter (0 errors, 0 warnings)." : $"Compiler diagnostics failed (errors={errs}, warnings={warns}).");
        if (!string.IsNullOrWhiteSpace(outputDir)) _ = WriteD4BuildReport(outputDir, report);
        return new(ok, 0, errs, warns, [CanonicalD4Tier], violations, report, report.Summary);
    }

    /// <summary>Audits a workspace/output root across C sources, build configs, <c>d4_build_report.json</c>, and <c>d4b_lockstep_report.json</c>.</summary>
    public static PsxD4CraftsmanshipValidationResult ValidateWorkspace(
        string rootDir, string? exportsDir = null, string? outputDir = null, string moduleName = "psx-module", bool requireD4bLockstep = false, IEnumerable<string>? manifestPreservedTiers = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDir);
        string fullRoot = Path.GetFullPath(rootDir);
        var violations = new List<PsxD4TellViolation>();
        var tiers = new SortedSet<string>(DiscoverCorpusTiers(fullRoot).Concat(NormalizePreservedTiers(manifestPreservedTiers)), StringComparer.OrdinalIgnoreCase);
        foreach (string v in DecompD4StrictnessValidator.ValidateWorkspace(fullRoot).Violations)
            violations.Add(new(PsxD4TellFamilies.CompilerDiagnostic, "D4-CORE-02", fullRoot, 1, v, v));
        List<string> cSources = CollectD4CSources(fullRoot, exportsDir);
        foreach (string cFile in cSources) violations.AddRange(ScanCSourceFile(cFile, fullRoot));
        foreach (string cfg in PsxCleanRoomValidator.CollectBuildConfigFiles(fullRoot))
            violations.AddRange(ScanBuildConfigFlags(File.ReadAllText(cfg, Encoding.UTF8), Path.GetRelativePath(fullRoot, cfg).Replace('\\', '/'), Path.GetFileName(cfg).Equals("compile_commands.json", StringComparison.OrdinalIgnoreCase)));

        int repErrs = 0, repWarns = 0, repTells = 0; bool hasBuildReport = false;
        foreach (string repPath in FindExistingFiles(fullRoot, ["d4_build_report.json", "Dev/schemas/d4_build_report.json", "Corpus/work/d4_build_report.json"]))
        {
            hasBuildReport = true;
            PsxD4CraftsmanshipValidationResult rr = ValidateD4BuildReportJson(File.ReadAllText(repPath, Encoding.UTF8), Path.GetRelativePath(fullRoot, repPath).Replace('\\', '/'));
            repErrs += rr.ErrorCount; repWarns += rr.WarningCount; repTells += rr.TellCount; violations.AddRange(rr.Violations);
        }
        string d4ParityTier = VerifyLockstepInRoot(fullRoot, requireD4bLockstep, violations);
        int totalTells = Math.Max(repTells, violations.Count(v => v.Family.StartsWith("T", StringComparison.Ordinal) || v.Family == PsxD4TellFamilies.FragmentFile));
        bool ok = violations.Count == 0 && repErrs == 0 && repWarns == 0 && totalTells == 0;
        string summary = ok ? $"D4_CleanSource craftsmanship gate passed (0 tells, {d4ParityTier}, tiers=[{string.Join(", ", tiers)}])."
                            : $"D4_CleanSource craftsmanship gate failed ({violations.Count} violation(s)): {string.Join("; ", violations.Take(5).Select(v => $"{v.RuleId}:{v.FilePath}:{v.LineNumber} {v.Snippet}"))}";
        var report = new PsxD4BuildVerificationReport(2, "d4-craftsmanship-report", moduleName, "D4", CanonicalD4Tier, d4ParityTier, tiers.ToArray(),
            cSources.Count, repErrs, repWarns, RequiredWarningFlags, totalTells, hasBuildReport && repErrs == 0 && repWarns == 0, totalTells == 0,
            ok, hasBuildReport && repErrs == 0 && repWarns == 0, hasBuildReport && repErrs == 0 && repWarns == 0, ok ? 1 : 0, 1, violations, summary);
        if (!string.IsNullOrWhiteSpace(outputDir)) _ = WriteD4BuildReport(outputDir, report, "d4-craftsmanship-report.json");
        return new(ok, totalTells, repErrs, repWarns, tiers.ToArray(), violations, report, summary);
    }

    internal static PsxD4CraftsmanshipValidationResult? EvaluateStageD4GateIfApplicable(
        DecompRunOptions options, DecompPackageManifest manifest, string outDir, string? exportsArg, bool hadPreExistingCSources)
    {
        var roots = new List<string> { outDir };
        void AddRoot(string? p) { if (!string.IsNullOrWhiteSpace(p) && Directory.Exists(p)) { string full = Path.GetFullPath(p); if (!roots.Contains(full, StringComparer.OrdinalIgnoreCase)) roots.Add(full); } }
        AddRoot(exportsArg);
        if (options.PlatformArguments is not null && options.PlatformArguments.TryGetValue("workspaceDir", out string? w)) AddRoot(w);
        string pkgFull = Path.GetFullPath(options.PackagePath);
        AddRoot(Directory.Exists(pkgFull) ? pkgFull : Path.GetDirectoryName(pkgFull));

        bool reqLock = options.PlatformArguments is not null && options.PlatformArguments.TryGetValue("requireD4bLockstep", out string? rl) && string.Equals(rl, "true", StringComparison.OrdinalIgnoreCase);
        bool engaged = hadPreExistingCSources || reqLock || roots.Any(r => CollectD4CSources(r, null, ignoreEmptyNorm: !hadPreExistingCSources && r == outDir).Count > 0 ||
            PsxCleanRoomValidator.CollectBuildConfigFiles(r).Count > 0 || FindExistingFiles(r, ["d4_build_report.json", "Dev/schemas/d4_build_report.json", "Corpus/work/d4_build_report.json", "d4b_lockstep_report.json", "Dev/schemas/d4b_lockstep_report.json"]).Count > 0);
        if (!engaged) return null;

        var allViolations = new List<PsxD4TellViolation>();
        var tiers = new SortedSet<string>(NormalizePreservedTiers(manifest.PreservedTiers), StringComparer.OrdinalIgnoreCase);
        int maxErrs = 0, maxWarns = 0, maxTells = 0; bool anyLockstepFound = false;
        PsxD4BuildVerificationReport? lastRep = null;
        foreach (string r in roots)
        {
            if (FindExistingFiles(r, ["d4b_lockstep_report.json", "Dev/schemas/d4b_lockstep_report.json"]).Count > 0) anyLockstepFound = true;
            PsxD4CraftsmanshipValidationResult res = ValidateWorkspace(r, null, r == outDir ? outDir : null, manifest.ModuleId, false, tiers);
            allViolations.AddRange(res.Violations.Where(v => hadPreExistingCSources || r != outDir || !Path.GetFileName(v.FilePath).Equals("normalized-export.c", StringComparison.OrdinalIgnoreCase)));
            foreach (string t in res.DiscoveredTiers) tiers.Add(t);
            maxErrs = Math.Max(maxErrs, res.ErrorCount); maxWarns = Math.Max(maxWarns, res.WarningCount); maxTells = Math.Max(maxTells, res.TellCount);
            lastRep = res.Report ?? lastRep;
        }
        if (reqLock && !anyLockstepFound)
            allViolations.Add(new(PsxD4TellFamilies.D4bLockstep, "D4B-02", outDir, 0, "d4b_lockstep_report.json", "Missing required d4b_lockstep_report.json."));
        var dedup = allViolations.Distinct().ToList();
        int tellCount = Math.Max(maxTells, dedup.Count(v => v.Family.StartsWith("T", StringComparison.Ordinal) || v.Family == PsxD4TellFamilies.FragmentFile));
        bool ok = dedup.Count == 0 && maxErrs == 0 && maxWarns == 0 && tellCount == 0;
        return new(ok, tellCount, maxErrs, maxWarns, tiers.ToArray(), dedup, lastRep,
            ok ? $"D4 craftsmanship gate passed ({tiers.Count} tier(s))." : $"D4 craftsmanship gate failed ({dedup.Count} violation(s)): {string.Join("; ", dedup.Take(5).Select(v => $"{v.RuleId}:{v.FilePath}:{v.LineNumber} {v.Description}"))}");
    }

    /// <summary>Writes a Standard 18 <c>camelCase</c> D4 build/craftsmanship report cleanly inside <paramref name="outputDir"/>.</summary>
    public static string WriteD4BuildReport(string outputDir, PsxD4BuildVerificationReport report, string fileName = "d4_build_report.json") =>
        PsxParityAndByteMatchVerifier.WriteD4BuildReportCore(outputDir, report, fileName);

    private static string VerifyLockstepInRoot(string fullRoot, bool requireD4bLockstep, List<PsxD4TellViolation> violations)
    {
        string? lockstepPath = FindExistingFiles(fullRoot, ["d4b_lockstep_report.json", "Dev/schemas/d4b_lockstep_report.json"]).FirstOrDefault();
        if (lockstepPath is null)
        {
            if (requireD4bLockstep) violations.Add(new(PsxD4TellFamilies.D4bLockstep, "D4B-02", fullRoot, 0, "d4b_lockstep_report.json", "Missing required d4b_lockstep_report.json."));
            return "D4a";
        }
        PsxD4bLockstepValidationResult lockRes = PsxParityAndByteMatchVerifier.VerifyD4bLockstepFile(lockstepPath);
        if (lockRes.IsValid) return "D4b";
        foreach (string lv in lockRes.Violations)
            violations.Add(new(PsxD4TellFamilies.D4bLockstep, "D4B-01", Path.GetRelativePath(fullRoot, lockstepPath).Replace('\\', '/'), 1, lv, lv));
        return "D4a";
    }

    private static string StripCommentsAndStringsPreserveLines(string text)
    {
        char[] buf = text.ToCharArray();
        for (int i = 0; i < text.Length;)
        {
            char ch = text[i], nxt = i + 1 < text.Length ? text[i + 1] : '\0';
            if (ch == '/' && (nxt == '/' || nxt == '*'))
            {
                bool blk = nxt == '*'; buf[i] = buf[i + 1] = ' ';
                for (i += 2; i < text.Length; i++)
                {
                    if (!blk && text[i] == '\n') break;
                    if (blk && text[i] == '*' && i + 1 < text.Length && text[i + 1] == '/') { buf[i] = buf[i + 1] = ' '; i += 2; break; }
                    if (text[i] != '\n') buf[i] = ' ';
                }
            }
            else if (ch is '"' or '\'')
            {
                char q = ch;
                for (i++; i < text.Length; i++)
                {
                    if (text[i] == '\\' && i + 1 < text.Length) { if (text[i + 1] != '\n') buf[i + 1] = ' '; buf[i] = ' '; i++; continue; }
                    if (text[i] == q) { i++; break; }
                    if (text[i] != '\n') buf[i] = ' ';
                }
            }
            else i++;
        }
        return new string(buf);
    }

    private static void CheckCommaConditions(string stripped, string fileLabel, List<PsxD4TellViolation> violations)
    {
        foreach (Match m in CondHeadRegex().Matches(stripped))
        {
            int openIdx = m.Index + m.Length - 1, depth = 0;
            for (int i = openIdx; i < stripped.Length; i++)
            {
                if (stripped[i] == '(') depth++;
                else if (stripped[i] == ')' && --depth == 0) break;
                else if (stripped[i] == ',' && depth == 1) { violations.Add(new(PsxD4TellFamilies.UnidiomaticControlFlow, "D4-T4-02", fileLabel, LineOf(stripped, i), TrimSnippet(stripped[openIdx..Math.Min(stripped.Length, i + 16)]), "Comma-operator side effect inside if/while condition.")); break; }
            }
        }
    }

    private static void CheckSwitchStatements(string stripped, string fileLabel, List<PsxD4TellViolation> violations)
    {
        foreach (Match sm in SwitchHeadRegex().Matches(stripped))
        {
            int openIdx = sm.Index + sm.Length - 1, depth = 0, closeIdx = -1;
            for (int i = openIdx; i < stripped.Length; i++) { if (stripped[i] == '{') depth++; else if (stripped[i] == '}' && --depth == 0) { closeIdx = i; break; } }
            if (closeIdx <= openIdx) continue;
            string body = stripped[(openIdx + 1)..closeIdx];
            Match defM = Regex.Match(body, @"\bdefault\s*:");
            if (defM.Success && Regex.IsMatch(body[(defM.Index + defM.Length)..], @"\bcase\b"))
                violations.Add(new(PsxD4TellFamilies.OptimizerComparison, "D4-T5-03", fileLabel, LineOf(stripped, openIdx + 1 + defM.Index), "default:", "Mid-switch default: label placed before trailing case labels."));
            int asciiCases = 0, printableCases = 0;
            foreach (Match cm in NumericCaseRegex().Matches(body))
            {
                string raw = cm.Groups["val"].Value;
                bool hex = raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
                if (int.TryParse(hex ? raw[2..] : raw, hex ? NumberStyles.HexNumber : NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                { if (v is 9 or 10 or 13 || (v >= 32 && v <= 126)) asciiCases++; if (v >= 32 && v <= 126) printableCases++; }
            }
            if ((printableCases >= 1 && asciiCases >= 2) || (printableCases >= 1 && CharSwitchExprRegex().IsMatch(sm.Groups["expr"].Value)))
                violations.Add(new(PsxD4TellFamilies.OptimizerComparison, "D4-T5-04", fileLabel, LineOf(stripped, sm.Index), TrimSnippet(sm.Value), "Raw numeric ASCII case values in switch; replace with character literals."));
        }
    }

    private static void CheckDuplicateIncludes(string sourceText, string fileLabel, List<PsxD4TellViolation> violations)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int ifDepth = 0, lineNum = 0;
        foreach (string raw in sourceText.Split('\n'))
        {
            lineNum++; string s = raw.Trim();
            if (s.StartsWith("#if", StringComparison.Ordinal)) ifDepth++;
            else if (s.StartsWith("#endif", StringComparison.Ordinal) && ifDepth > 0) ifDepth--;
            else if (ifDepth == 0 && s.StartsWith("#include", StringComparison.Ordinal) && !seen.Add(Regex.Replace(s, @"\s+", " ")))
                violations.Add(new(PsxD4TellFamilies.MechanicalNaming, "D4-T6-05", fileLabel, lineNum, TrimSnippet(s), "Duplicate #include header directive in translation unit."));
        }
    }

    private static List<string> CollectD4CSources(string root, string? exportsDir, bool ignoreEmptyNorm = false)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string sub in new[] { Path.Combine(root, "Corpus", "tiers", CanonicalD4Tier), Path.Combine(root, "Corpus", "tiers", LegacyD4TierAlias), Path.Combine(root, "Corpus", "readable"), exportsDir ?? string.Empty })
            if (!string.IsNullOrWhiteSpace(sub) && Directory.Exists(sub))
                foreach (string f in Directory.EnumerateFiles(sub, "*.c", SearchOption.AllDirectories)) files.Add(Path.GetFullPath(f));
        foreach (string f in Directory.EnumerateFiles(root, "*.c", SearchOption.AllDirectories))
        {
            string norm = f.Replace('\\', '/');
            if (norm.Contains("/Corpus/tiers/D1_", StringComparison.OrdinalIgnoreCase) || norm.Contains("/Corpus/tiers/D2_", StringComparison.OrdinalIgnoreCase) ||
                norm.Contains("/Corpus/tiers/D3_", StringComparison.OrdinalIgnoreCase) || norm.Contains("/ghidra_ext/", StringComparison.OrdinalIgnoreCase)) continue;
            if (ignoreEmptyNorm && Path.GetFileName(f).Equals("normalized-export.c", StringComparison.OrdinalIgnoreCase) && File.ReadAllLines(f).Length <= 2) continue;
            files.Add(Path.GetFullPath(f));
        }
        return files.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> FindExistingFiles(string root, string[] relPaths) =>
        relPaths.Select(r => Path.Combine(root, r.Replace('/', Path.DirectorySeparatorChar))).Where(File.Exists).ToList();

    private static int LineOf(string text, int index) { int l = 1; for (int i = 0; i < index && i < text.Length; i++) if (text[i] == '\n') l++; return l; }
    private static string TrimSnippet(string s) { string t = Regex.Replace(s, @"\s+", " ").Trim(); return t.Length > 72 ? t[..72] + "..." : t; }
}
