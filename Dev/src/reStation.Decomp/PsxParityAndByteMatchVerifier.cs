// @module: reStation.Decomp.PsxParityAndByteMatchVerifier
// @purpose: D4 LP32 C struct-layout/ABI parity verifier, D4b lockstep verifier, and D5 MIPS R3000A + GTE byte-level round-trip verifier.
// @depends-on: System, System.Buffers.Binary, System.Collections.Generic, System.IO, System.Security.Cryptography, System.Text, System.Text.Json, System.Text.Json.Serialization, System.Text.RegularExpressions, Foundation.Core
// @exports: PsxStructLayoutCheck, PsxLp32ParityReport, PsxByteMatchReport, PsxD4bLockstepReport, PsxD4bLockstepValidationResult, PsxParityAndByteMatchVerifier
// SPDX-License-Identifier: MPL-2.0

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Foundation.Core;
using reEngine.Core.Decomp;

namespace reStation.Decomp;

/// <summary>One LP32 struct-layout parity comparison result.</summary>
public sealed record PsxStructLayoutCheck(string StructName, int ExpectedSizeBytes, int ComputedSizeBytes, int ExpectedAlignBytes, int ComputedAlignBytes, bool Matched);

/// <summary><c>D4</c> LP32 struct-layout and MIPS o32 ABI parity verification report.</summary>
public sealed record PsxLp32ParityReport(
    [property: JsonPropertyName("isPassed")] bool Passed, int PointerSizeBytes, int LongSizeBytes,
    int ChecksPassed, int TotalChecks, IReadOnlyList<PsxStructLayoutCheck> StructChecks, string Summary);

/// <summary><c>D5</c> MIPS R3000A + GTE COP2 byte-level decode/re-encode round-trip report.</summary>
public sealed record PsxByteMatchReport(
    [property: JsonPropertyName("isPassed")] bool Passed, string TargetName, uint BaseAddress, int TotalWords,
    int DecodedInstructionWords, int DataFallbackWords, int MatchedWords, int MismatchedWords,
    uint? FirstMismatchAddress, string OriginalSha256Hex, string RoundTripSha256Hex, string Summary);

/// <summary>Standard 18 <c>d4b_lockstep_report.json</c> DTO proving RAM/state lockstep parity against a reference oracle.</summary>
public sealed record PsxD4bLockstepReport(
    int SchemaVersion, string ModuleName, string D4ParityTier, string OracleType, int TotalFrames,
    int MatchedFrames, int MismatchedFrames, bool IsPassed, string? VerifiedAtUtc = null, string? Notes = null);

/// <summary>Validation outcome for a <c>d4b_lockstep_report.json</c> payload.</summary>
public sealed record PsxD4bLockstepValidationResult(bool IsValid, string? OracleType, int MismatchedFrames, int TotalFrames, IReadOnlyList<string> Violations, string Summary);

/// <summary>
/// Implements <c>D4</c> LP32 struct-layout/ABI verification, <c>D4b</c> oracle lockstep report validation, and
/// <c>D5</c> MIPS R3000A + COP2 byte-exact round-trip verification against verified PS-X EXE binaries.
/// </summary>
public static partial class PsxParityAndByteMatchVerifier
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
        WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly HashSet<string> AllowedOracleTypes = new(StringComparer.Ordinal) { "pcsx_redux", "mednafen", "mesen", "ares", "blastem", "hardware" };
    private static readonly HashSet<string> BannedLegacyKeys = new(StringComparer.Ordinal)
    {
        "passed", "errors", "warnings", "archiveFile", "archiveBytes", "parityTestsTotal", "frameDiffCount", "frameMismatchCount"
    };

    /// <summary>Canonical MIPS o32 LP32 expected sizes and alignments for clean PS1 BIOS/hardware types.</summary>
    public static IReadOnlyDictionary<string, (int Size, int Align)> ExpectedLp32Layouts { get; } =
        new Dictionary<string, (int, int)>(StringComparer.Ordinal)
        {
            ["PsxRect"] = (0x08, 2), ["PsxVector"] = (0x10, 4), ["PsxSVector"] = (0x08, 2), ["PsxMatrix"] = (0x20, 4),
            ["PsxDispEnv"] = (0x14, 2), ["PsxDrawEnv"] = (0x5C, 4), ["PsxCdlLoc"] = (0x04, 1), ["PsxCdlFile"] = (0x18, 4),
            ["PsxDirEntry"] = (0x28, 4), ["PsxExecHeader"] = (0x3C, 4), ["PsxSpuVoiceAttr"] = (0x40, 4), ["PsxVagAttr"] = (0x20, 4)
        };

    [GeneratedRegex(@"typedef\s+struct\s+(?<name>[A-Za-z_]\w*)\s*\{(?<body>[^}]*)\}\s*[A-Za-z_]\w*\s*;", RegexOptions.Singleline)]
    private static partial Regex StructDefRegex();

    [GeneratedRegex(@"\[(?<dim>\d+)\]")]
    private static partial Regex ArrayDimRegex();

    /// <summary>
    /// Runs <c>D4</c> LP32 ABI and struct-layout verification against <see cref="PsxGteSlaExtension.GenerateCleanPsxTypesHeader"/>
    /// (or <paramref name="customHeaderText"/>), optionally writing <c>d4-lp32-parity-report.json</c>.
    /// </summary>
    public static PsxLp32ParityReport VerifyLp32Parity(string? customHeaderText = null, string? outputDir = null)
    {
        string header = string.IsNullOrWhiteSpace(customHeaderText)
            ? PsxGteSlaExtension.GenerateCleanPsxTypesHeader()
            : customHeaderText;

        Dictionary<string, (int Size, int Align)> computed = ComputeLp32StructLayouts(header);
        var checks = new List<PsxStructLayoutCheck>(ExpectedLp32Layouts.Count);
        foreach ((string name, (int expSize, int expAlign)) in ExpectedLp32Layouts)
        {
            bool found = computed.TryGetValue(name, out (int Size, int Align) actual);
            bool matched = found && actual.Size == expSize && actual.Align == expAlign;
            checks.Add(new PsxStructLayoutCheck(name, expSize, found ? actual.Size : -1, expAlign, found ? actual.Align : -1, matched));
        }

        int passedCount = checks.Count(c => c.Matched);
        bool ok = passedCount == checks.Count;
        string summary = ok
            ? $"D4 LP32 ABI parity passed ({passedCount}/{checks.Count} PS1 structs; ptr=4B, long=4B)."
            : $"D4 LP32 ABI parity failed ({passedCount}/{checks.Count} matched).";
        var report = new PsxLp32ParityReport(ok, 4, 4, passedCount, checks.Count, checks, summary);

        if (!string.IsNullOrWhiteSpace(outputDir))
        {
            string fullOut = Path.GetFullPath(outputDir);
            Directory.CreateDirectory(fullOut);
            string reportPath = AppEnvironment.AssertPathContained(Path.Combine(fullOut, "d4-lp32-parity-report.json"), fullOut);
            File.WriteAllText(reportPath, JsonSerializer.Serialize(report, JsonOptions), new UTF8Encoding(false));
        }
        return report;
    }

    /// <summary>Computes MIPS o32 LP32 sizes and alignments from clean C struct definitions.</summary>
    public static Dictionary<string, (int Size, int Align)> ComputeLp32StructLayouts(string headerText)
    {
        ArgumentNullException.ThrowIfNull(headerText);
        var layouts = new Dictionary<string, (int Size, int Align)>(StringComparer.Ordinal)
        {
            ["s8"] = (1, 1), ["u8"] = (1, 1), ["char"] = (1, 1),
            ["s16"] = (2, 2), ["u16"] = (2, 2), ["short"] = (2, 2),
            ["s32"] = (4, 4), ["u32"] = (4, 4), ["int"] = (4, 4), ["long"] = (4, 4),
            ["s64"] = (8, 8), ["u64"] = (8, 8)
        };

        foreach (Match m in StructDefRegex().Matches(headerText))
        {
            string name = m.Groups["name"].Value;
            string body = m.Groups["body"].Value;
            int offset = 0;
            int maxAlign = 1;
            foreach (string rawStmt in body.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string[] parts = rawStmt.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;
                (int elemSize, int elemAlign) = parts[1].Contains('*') ? (4, 4) : layouts[parts[0]];
                int count = 1;
                foreach (Match dm in ArrayDimRegex().Matches(rawStmt))
                    count *= int.Parse(dm.Groups["dim"].Value, System.Globalization.CultureInfo.InvariantCulture);

                maxAlign = Math.Max(maxAlign, elemAlign);
                offset = AlignUp(offset, elemAlign) + elemSize * count;
            }
            layouts[name] = (AlignUp(offset, maxAlign), maxAlign);
        }
        return layouts;
    }

    /// <summary>
    /// Runs <c>D5</c> MIPS R3000A + GTE COP2 byte-level decode/re-encode verification on raw instruction bytes.
    /// Rejects unknown opcodes when <paramref name="strictInstructionStream"/> is <see langword="true"/>.
    /// </summary>
    public static PsxByteMatchReport VerifyCodeBytesRoundTrip(
        ReadOnlySpan<byte> payloadBytes,
        uint baseAddress,
        string targetName,
        bool strictInstructionStream = false,
        string? outputDir = null)
    {
        if (payloadBytes.Length < 4 || (payloadBytes.Length & 3) != 0)
            throw new InvalidDataException($"'{targetName}' code payload length ({payloadBytes.Length} B) must be a positive multiple of 4.");

        int words = payloadBytes.Length / 4;
        byte[] roundTrip = new byte[payloadBytes.Length];
        int decodedInsns = 0;
        int dataWords = 0;
        int matched = 0;
        int mismatched = 0;
        uint? firstMismatch = null;

        for (int i = 0; i < words; i++)
        {
            uint addr = baseAddress + (uint)(i * 4);
            uint origWord = BinaryPrimitives.ReadUInt32LittleEndian(payloadBytes.Slice(i * 4, 4));
            R3000AInsn insn = PsxR3000ACodec.Decode(addr, origWord);

            if (insn.IsKnownInstruction) decodedInsns++;
            else dataWords++;

            uint encodedWord = PsxR3000ACodec.Encode(in insn);
            BinaryPrimitives.WriteUInt32LittleEndian(roundTrip.AsSpan(i * 4, 4), encodedWord);

            bool wordOk = encodedWord == origWord && (!strictInstructionStream || insn.IsKnownInstruction);
            if (wordOk) matched++;
            else
            {
                mismatched++;
                firstMismatch ??= addr;
            }
        }

        string origSha = Convert.ToHexString(SHA256.HashData(payloadBytes)).ToLowerInvariant();
        string rtSha = Convert.ToHexString(SHA256.HashData(roundTrip)).ToLowerInvariant();
        bool passed = mismatched == 0 && string.Equals(origSha, rtSha, StringComparison.Ordinal);
        string summary = passed
            ? $"D5 MIPS byte-match passed for '{targetName}': {matched}/{words} words ({decodedInsns} decoded insns, {dataWords} data words, SHA-256 {origSha[..12]})."
            : $"D5 MIPS byte-match failed for '{targetName}': {mismatched} mismatch(es), first @ 0x{firstMismatch:X8}.";

        var report = new PsxByteMatchReport(passed, targetName, baseAddress, words, decodedInsns, dataWords, matched, mismatched, firstMismatch, origSha, rtSha, summary);
        if (!string.IsNullOrWhiteSpace(outputDir))
        {
            string fullOut = Path.GetFullPath(outputDir);
            Directory.CreateDirectory(fullOut);
            string reportPath = AppEnvironment.AssertPathContained(Path.Combine(fullOut, "d5-byte-match-report.json"), fullOut);
            File.WriteAllText(reportPath, JsonSerializer.Serialize(report, JsonOptions), new UTF8Encoding(false));
        }
        return report;
    }

    /// <summary>Runs <c>D5</c> byte-level verification on a parsed <see cref="PsxExeImage"/>.</summary>
    public static PsxByteMatchReport VerifyExeByteMatch(
        PsxExeImage exe, bool strictInstructionStream = false, string? outputDir = null)
    {
        ArgumentNullException.ThrowIfNull(exe);
        ReadOnlySpan<byte> payload = exe.RawBytes.AsSpan(PsxCleanExeAndOverlayLoader.ExeHeaderBytes, (int)exe.Header.TextSize);
        return VerifyCodeBytesRoundTrip(payload, exe.Header.TextAddr, exe.Name, strictInstructionStream, outputDir);
    }

    /// <summary>
    /// Enforces containment inside <paramref name="allowedRootDir"/> (e.g. <c>data\rom\</c>)
    /// and executes <c>D5</c> verification against the verified PS-X EXE file.
    /// </summary>
    public static PsxByteMatchReport VerifyExeFileContained(
        string exeFilePath, string allowedRootDir, bool strictInstructionStream = false, string? outputDir = null)
    {
        string safeExePath = AppEnvironment.AssertPathContained(exeFilePath, allowedRootDir);
        PsxExeImage exe = PsxCleanExeAndOverlayLoader.LoadExeFromFile(safeExePath, allowedRootDir);
        return VerifyExeByteMatch(exe, strictInstructionStream, outputDir);
    }

    /// <summary>Validates a Standard 18 <c>d4b_lockstep_report.json</c> file on disk.</summary>
    public static PsxD4bLockstepValidationResult VerifyD4bLockstepFile(string reportPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportPath);
        string full = Path.GetFullPath(reportPath);
        return !File.Exists(full)
            ? new PsxD4bLockstepValidationResult(false, null, -1, 0, [$"Missing lockstep report: '{full}'."], "D4b lockstep report not found.")
            : VerifyD4bLockstepReportJson(File.ReadAllText(full, Encoding.UTF8));
    }

    /// <summary>Validates a Standard 18 <c>d4b_lockstep_report.json</c> payload (<c>mismatchedFrames == 0</c>, <c>isPassed == true</c>, valid <c>oracleType</c>).</summary>
    public static PsxD4bLockstepValidationResult VerifyD4bLockstepReportJson(string reportJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportJson);
        using JsonDocument doc = JsonDocument.Parse(reportJson);
        var violations = new List<string>(FindNonStandardReportKeys(doc.RootElement));

        PsxD4bLockstepReport? report = JsonSerializer.Deserialize<PsxD4bLockstepReport>(reportJson, JsonOptions);
        if (report is null)
            return new PsxD4bLockstepValidationResult(false, null, -1, 0, ["d4b_lockstep_report.json deserialized to null."], "Invalid D4b lockstep report.");

        if (string.IsNullOrWhiteSpace(report.OracleType) || !AllowedOracleTypes.Contains(report.OracleType))
            violations.Add($"Invalid or missing oracleType '{report.OracleType}'; expected one of: {string.Join(", ", AllowedOracleTypes)}.");
        if (report.MismatchedFrames != 0)
            violations.Add($"mismatchedFrames={report.MismatchedFrames} (expected 0).");
        if (report.TotalFrames <= 0)
            violations.Add($"totalFrames={report.TotalFrames} (expected > 0).");
        if (report.MatchedFrames != report.TotalFrames)
            violations.Add($"matchedFrames={report.MatchedFrames} != totalFrames={report.TotalFrames}.");
        if (!report.IsPassed)
            violations.Add("isPassed is false.");

        bool ok = violations.Count == 0;
        string summary = ok
            ? $"D4b lockstep parity verified ({report.MatchedFrames}/{report.TotalFrames} frames matched on oracle '{report.OracleType}')."
            : $"D4b lockstep verification failed ({violations.Count} violation(s)): {string.Join("; ", violations)}";
        return new PsxD4bLockstepValidationResult(ok, report.OracleType, report.MismatchedFrames, report.TotalFrames, violations, summary);
    }

    /// <summary>Writes a Standard 18 <c>camelCase</c> <c>d4b_lockstep_report.json</c> safely under <paramref name="outputDir"/>.</summary>
    public static string WriteD4bLockstepReport(string outputDir, PsxD4bLockstepReport report)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDir);
        ArgumentNullException.ThrowIfNull(report);
        string fullOut = Path.GetFullPath(outputDir);
        Directory.CreateDirectory(fullOut);
        string path = AppEnvironment.AssertPathContained(Path.Combine(fullOut, "d4b_lockstep_report.json"), fullOut);
        File.WriteAllText(path, JsonSerializer.Serialize(report, JsonOptions), new UTF8Encoding(false));
        return path;
    }

    internal static IReadOnlyList<string> FindNonStandardReportKeys(JsonElement element)
    {
        var bad = new HashSet<string>(StringComparer.Ordinal);
        void Walk(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty prop in node.EnumerateObject())
                {
                    if (BannedLegacyKeys.Contains(prop.Name) || prop.Name.Contains('_') ||
                        (prop.Name.Length > 0 && char.IsUpper(prop.Name[0])))
                    {
                        bad.Add($"Non-Standard-18 JSON key '{prop.Name}' detected (expected camelCase).");
                    }
                    Walk(prop.Value);
                }
            }
            else if (node.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in node.EnumerateArray()) Walk(item);
            }
        }
        Walk(element);
        return bad.OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }

    internal static PsxD4CraftsmanshipValidationResult ValidateD4BuildReportJsonCore(string reportJson, string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportJson);
        var violations = new List<PsxD4TellViolation>();
        foreach (string v in DecompD4StrictnessValidator.ValidateD4BuildReportJson(reportJson).Violations)
            violations.Add(new(v.Contains("-w", StringComparison.OrdinalIgnoreCase) ? PsxD4TellFamilies.BannedCompilerFlag : PsxD4TellFamilies.CompilerDiagnostic, "D4-CORE-01", label, 1, v, v));
        using JsonDocument doc = JsonDocument.Parse(reportJson);
        foreach (string kErr in FindNonStandardReportKeys(doc.RootElement))
            violations.Add(new(PsxD4TellFamilies.ReportSchema, "D4-JSON-01", label, 1, kErr, kErr));
        D4BuildReport? rep = JsonSerializer.Deserialize<D4BuildReport>(reportJson, JsonOptions);
        if (rep is not null)
        {
            if (rep.CompilerFlags.Count > 0) violations.AddRange(PsxCleanRoomValidator.ScanBuildConfigFlags(string.Join(' ', rep.CompilerFlags), label, requireStrictWarningFlags: true));
            if (doc.RootElement.TryGetProperty("isPassed", out JsonElement pEl) && pEl.ValueKind == JsonValueKind.False)
                violations.Add(new(PsxD4TellFamilies.CompilerDiagnostic, "D4-BUILD-01", label, 1, "isPassed=false", "d4_build_report.json reported isPassed=false."));
        }
        bool ok = violations.Count == 0;
        return new(ok, rep?.CraftsmanshipTellCount ?? 0, rep?.CompilerErrorCount ?? 0, rep?.CompilerWarningCount ?? 0, Array.Empty<string>(), violations, null,
            ok ? $"Verified '{label}' (0 errors, 0 warnings, 0 tells)." : $"D4 build report '{label}' failed ({violations.Count} issue(s)).");
    }

    internal static string WriteD4BuildReportCore(string outputDir, PsxD4BuildVerificationReport report, string fileName)
    {
        string fullOut = Path.GetFullPath(outputDir);
        Directory.CreateDirectory(fullOut);
        string path = AppEnvironment.AssertPathContained(Path.Combine(fullOut, fileName), fullOut);
        File.WriteAllText(path, JsonSerializer.Serialize(report, JsonOptions), new UTF8Encoding(false));
        return path;
    }

    internal static bool IsMipsElf32Object(string objPath)
    {
        if (!File.Exists(objPath)) return false;
        byte[] hdr = new byte[20];
        using FileStream fs = File.OpenRead(objPath);
        return fs.Read(hdr, 0, 20) == 20 && hdr[0] == 0x7F && hdr[1] == (byte)'E' && hdr[2] == (byte)'L' && hdr[3] == (byte)'F'
            && hdr[4] == 1 && hdr[5] == 1 && (hdr[16] | (hdr[17] << 8)) == 1 && (hdr[18] | (hdr[19] << 8)) == 8;
    }

    private static int AlignUp(int value, int align) => align <= 1 ? value : (value + align - 1) & ~(align - 1);
}
