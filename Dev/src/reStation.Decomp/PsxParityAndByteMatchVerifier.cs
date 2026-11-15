// @module: reStation.Decomp.PsxParityAndByteMatchVerifier
// @purpose: D4 LP32 C struct-layout/ABI parity verifier and D5 MIPS R3000A + GTE byte-level round-trip verifier against verified EXEs.
// @depends-on: System, System.Buffers.Binary, System.Collections.Generic, System.IO, System.Security.Cryptography, System.Text, System.Text.Json, System.Text.RegularExpressions, Foundation.Core
// @exports: PsxStructLayoutCheck, PsxLp32ParityReport, PsxByteMatchReport, PsxParityAndByteMatchVerifier
// SPDX-License-Identifier: MPL-2.0

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Foundation.Core;

namespace reStation.Decomp;

/// <summary>One LP32 struct-layout parity comparison result.</summary>
public sealed record PsxStructLayoutCheck(
    string StructName,
    int ExpectedSizeBytes,
    int ComputedSizeBytes,
    int ExpectedAlignBytes,
    int ComputedAlignBytes,
    bool Matched);

/// <summary><c>D4</c> LP32 struct-layout and MIPS o32 ABI parity verification report.</summary>
public sealed record PsxLp32ParityReport(
    bool Passed,
    int PointerSizeBytes,
    int LongSizeBytes,
    int ChecksPassed,
    int TotalChecks,
    IReadOnlyList<PsxStructLayoutCheck> StructChecks,
    string Summary);

/// <summary><c>D5</c> MIPS R3000A + GTE COP2 byte-level decode/re-encode round-trip report.</summary>
public sealed record PsxByteMatchReport(
    bool Passed,
    string TargetName,
    uint BaseAddress,
    int TotalWords,
    int DecodedInstructionWords,
    int DataFallbackWords,
    int MatchedWords,
    int MismatchedWords,
    uint? FirstMismatchAddress,
    string OriginalSha256Hex,
    string RoundTripSha256Hex,
    string Summary);

/// <summary>
/// Implements <c>D4</c> LP32 struct-layout/ABI verification over clean PS1 hardware/BIOS types and
/// <c>D5</c> MIPS R3000A + COP2 byte-exact round-trip verification against verified PS-X EXE binaries.
/// </summary>
public static partial class PsxParityAndByteMatchVerifier
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Canonical MIPS o32 LP32 expected sizes and alignments for clean PS1 BIOS/hardware types.</summary>
    public static IReadOnlyDictionary<string, (int Size, int Align)> ExpectedLp32Layouts { get; } =
        new Dictionary<string, (int, int)>(StringComparer.Ordinal)
        {
            ["PsxRect"] = (0x08, 2),
            ["PsxVector"] = (0x10, 4),
            ["PsxSVector"] = (0x08, 2),
            ["PsxMatrix"] = (0x20, 4),
            ["PsxDispEnv"] = (0x14, 2),
            ["PsxDrawEnv"] = (0x5C, 4),
            ["PsxCdlLoc"] = (0x04, 1),
            ["PsxCdlFile"] = (0x18, 4),
            ["PsxDirEntry"] = (0x28, 4),
            ["PsxExecHeader"] = (0x3C, 4),
            ["PsxSpuVoiceAttr"] = (0x40, 4),
            ["PsxVagAttr"] = (0x20, 4)
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
            string reportPath = SandboxEnvironment.AssertPathContained(Path.Combine(fullOut, "d4-lp32-parity-report.json"), fullOut);
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
            string reportPath = SandboxEnvironment.AssertPathContained(Path.Combine(fullOut, "d5-byte-match-report.json"), fullOut);
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
    /// Enforces containment inside <paramref name="allowedRootDir"/> (e.g. <c>data\rom\</c> or <c>Sandbox\</c>)
    /// and executes <c>D5</c> verification against the verified PS-X EXE file.
    /// </summary>
    public static PsxByteMatchReport VerifyExeFileContained(
        string exeFilePath, string allowedRootDir, bool strictInstructionStream = false, string? outputDir = null)
    {
        string safeExePath = SandboxEnvironment.AssertPathContained(exeFilePath, allowedRootDir);
        PsxExeImage exe = PsxCleanExeAndOverlayLoader.LoadExeFromFile(safeExePath, allowedRootDir);
        return VerifyExeByteMatch(exe, strictInstructionStream, outputDir);
    }

    private static int AlignUp(int value, int align) => align <= 1 ? value : (value + align - 1) & ~(align - 1);
}
