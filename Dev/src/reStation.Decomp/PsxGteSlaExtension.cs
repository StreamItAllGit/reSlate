// @module: reStation.Decomp.PsxGteSlaExtension
// @purpose: Clean-room MPL-2.0 MIPS R3000A + GTE COP2 SLEIGH specification loader, validator, Ghidra language provisioner, and clean PS1 LP32 hardware type header generator.
// @depends-on: System, System.Collections.Generic, System.IO, System.Reflection, System.Text, Foundation.Core
// @exports: PsxSlaValidationResult, PsxSlaProvisionResult, PsxGteSlaExtension
// SPDX-License-Identifier: MPL-2.0

using System.Reflection;
using System.Text;
using Foundation.Core;

namespace reStation.Decomp;

/// <summary>Validation report for the embedded/external MIPS R3000A + GTE COP2 <c>.slaspec</c>.</summary>
/// <param name="IsValid">True when all required R3000A/GTE tokens exist and zero forbidden markers appear.</param>
/// <param name="MissingTokens">Missing required instruction or header tokens.</param>
/// <param name="ForbiddenMarkers">Detected GPLv3/Sony/external-loader leakage tokens.</param>
/// <param name="Summary">Human-readable validation summary.</param>
public sealed record PsxSlaValidationResult(
    bool IsValid,
    IReadOnlyList<string> MissingTokens,
    IReadOnlyList<string> ForbiddenMarkers,
    string Summary);

/// <summary>Result of provisioning clean-room SLEIGH language files and C types into a target directory.</summary>
/// <param name="RootDirectory">Resolved extension root directory.</param>
/// <param name="LanguageId">SLEIGH language identifier (<c>MIPS:LE:32:R3000A_GTE</c>).</param>
/// <param name="GeneratedFiles">Absolute paths of generated clean-room files.</param>
public sealed record PsxSlaProvisionResult(
    string RootDirectory,
    string LanguageId,
    IReadOnlyList<string> GeneratedFiles);

/// <summary>
/// Manages the clean-room <c>MPL-2.0</c> R3000A + GTE COP2 SLEIGH specification (<c>r3000a_gte.slaspec</c>),
/// companion Ghidra language descriptors, and clean PS1 BIOS/hardware vector C types.
/// </summary>
public static class PsxGteSlaExtension
{
    /// <summary>Canonical SLEIGH language ID for little-endian MIPS R3000A with PS1 GTE COP2.</summary>
    public const string SleighLanguageId = "MIPS:LE:32:R3000A_GTE";

    /// <summary>Logical manifest resource name for the embedded <c>r3000a_gte.slaspec</c>.</summary>
    public const string EmbeddedResourceName = "reStation.Decomp.Resources.r3000a_gte.slaspec";

    /// <summary>Relative file path where <c>r3000a_gte.slaspec</c> is copied alongside build outputs.</summary>
    public const string RelativeSlaSpecPath = "Resources/r3000a_gte.slaspec";

    /// <summary>Required COP2 / GTE and core R3000A tokens verified inside <c>r3000a_gte.slaspec</c>.</summary>
    public static IReadOnlyList<string> RequiredSpecTokens { get; } =
    [
        "define endian=little;",
        "define alignment=4;",
        "Mozilla Public License",
        ":mfc2", ":cfc2", ":mtc2", ":ctc2", ":lwc2", ":swc2",
        ":rtps", ":rtpt", ":nclip", ":avsz3", ":avsz4", ":mvmva"
    ];

    private static readonly string[] ForbiddenMarkers =
    [
        "ghidra_psx_ldr",
        "GPLv3",
        "GNU General Public License",
        "psyq",
        ".gdt",
        ".til",
        "Sony Computer Entertainment"
    ];

    /// <summary>Loads <c>r3000a_gte.slaspec</c> from embedded assembly resources or disk fallback.</summary>
    public static string LoadSlaSpecText()
    {
        Assembly asm = typeof(PsxGteSlaExtension).Assembly;
        using Stream? stream = asm.GetManifestResourceStream(EmbeddedResourceName);
        if (stream is not null)
        {
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        string diskPath = Path.Combine(AppContext.BaseDirectory, "Resources", "r3000a_gte.slaspec");
        if (File.Exists(diskPath))
            return File.ReadAllText(diskPath, Encoding.UTF8);

        throw new FileNotFoundException(
            $"Embedded resource '{EmbeddedResourceName}' and fallback '{diskPath}' were not found.", diskPath);
    }

    /// <summary>Validates that <paramref name="slaText"/> covers R3000A + GTE COP2 and contains zero GPL/Sony leaks.</summary>
    public static PsxSlaValidationResult ValidateSlaSpec(string slaText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slaText);
        var missing = RequiredSpecTokens
            .Where(t => !slaText.Contains(t, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var forbidden = ForbiddenMarkers
            .Where(m => slaText.Contains(m, StringComparison.OrdinalIgnoreCase))
            .ToList();

        bool ok = missing.Count == 0 && forbidden.Count == 0;
        string summary = ok
            ? $"Verified clean-room MPL-2.0 SLEIGH spec ({SleighLanguageId}, {RequiredSpecTokens.Count} checks)."
            : $"SLEIGH spec validation failed: missing=[{string.Join(", ", missing)}], forbidden=[{string.Join(", ", forbidden)}].";
        return new PsxSlaValidationResult(ok, missing, forbidden, summary);
    }

    /// <summary>Generates the clean-room <c>MPL-2.0</c> Ghidra <c>.ldefs</c> descriptor for <c>MIPS:LE:32:R3000A_GTE</c>.</summary>
    public static string GenerateLanguageDefinitionsXml() => """
        <?xml version="1.0" encoding="UTF-8"?>
        <!-- SPDX-License-Identifier: MPL-2.0 (reStation clean-room PS1 SLEIGH definition) -->
        <language_definitions>
          <language processor="MIPS"
                    endian="little"
                    size="32"
                    variant="R3000A_GTE"
                    version="1.0"
                    slafile="r3000a_gte.sla"
                    processorspec="r3000a_gte.pspec"
                    id="MIPS:LE:32:R3000A_GTE">
            <description>Clean-room MIPS R3000A 32-bit little-endian with PS1 GTE COP2</description>
            <compiler name="default" spec="r3000a_gte.cspec" id="default"/>
          </language>
        </language_definitions>
        """;

    /// <summary>Generates the clean-room <c>MPL-2.0</c> Ghidra <c>.pspec</c> processor memory/register metadata.</summary>
    public static string GenerateProcessorSpecXml() => """
        <?xml version="1.0" encoding="UTF-8"?>
        <!-- SPDX-License-Identifier: MPL-2.0 (reStation clean-room PS1 processor spec) -->
        <processor_spec>
          <programcounter register="pc"/>
          <default_memory_blocks>
            <memory_block name="scratchpad" start_address="ram:0x1f800000" length="0x400" initialized="false"/>
            <memory_block name="io_ports" start_address="ram:0x1f801000" length="0x2000" initialized="false"/>
            <memory_block name="bios_rom" start_address="ram:0xbfc00000" length="0x80000" initialized="false"/>
          </default_memory_blocks>
        </processor_spec>
        """;

    /// <summary>Generates the clean-room <c>MPL-2.0</c> Ghidra <c>.cspec</c> o32 LP32 calling convention descriptor.</summary>
    public static string GenerateCompilerSpecXml() => """
        <?xml version="1.0" encoding="UTF-8"?>
        <!-- SPDX-License-Identifier: MPL-2.0 (reStation clean-room PS1 o32 LP32 cspec) -->
        <compiler_spec>
          <data_organization>
            <absolute_max_alignment value="8"/>
            <machine_alignment value="4"/>
            <default_alignment value="1"/>
            <default_pointer_alignment value="4"/>
            <pointer_size value="4"/>
            <short_size value="2"/>
            <integer_size value="4"/>
            <long_size value="4"/>
            <long_long_size value="8"/>
          </data_organization>
          <global><range space="ram"/></global>
          <stackpointer register="sp" space="ram"/>
          <default_proto>
            <prototype name="__stdcall" extrapop="0" stackshift="0">
              <input>
                <pentry minsize="1" maxsize="4"><register name="a0"/></pentry>
                <pentry minsize="1" maxsize="4"><register name="a1"/></pentry>
                <pentry minsize="1" maxsize="4"><register name="a2"/></pentry>
                <pentry minsize="1" maxsize="4"><register name="a3"/></pentry>
                <pentry minsize="1" maxsize="500" align="4"><addr offset="16" space="stack"/></pentry>
              </input>
              <output>
                <pentry minsize="1" maxsize="4"><register name="v0"/></pentry>
                <pentry minsize="5" maxsize="8"><addr offset="0" space="join" piece1="v1" piece2="v0"/></pentry>
              </output>
              <unaffected>
                <register name="s0"/><register name="s1"/><register name="s2"/><register name="s3"/>
                <register name="s4"/><register name="s5"/><register name="s6"/><register name="s7"/>
                <register name="gp"/><register name="sp"/><register name="fp"/>
              </unaffected>
            </prototype>
          </default_proto>
        </compiler_spec>
        """;

    /// <summary>
    /// Generates clean-room <c>MPL-2.0</c> PS1 BIOS and hardware vector C types (<c>psx_clean_types.h</c>)
    /// adhering to MIPS o32 LP32 layout without any copyrighted headers.
    /// </summary>
    public static string GenerateCleanPsxTypesHeader() => """
        /* Clean-Room PS1 Hardware & BIOS Vector Types (o32 LP32) — SPDX-License-Identifier: MPL-2.0 */
        #ifndef RESTATION_PSX_CLEAN_TYPES_H
        #define RESTATION_PSX_CLEAN_TYPES_H

        typedef signed char        s8;
        typedef unsigned char      u8;
        typedef signed short       s16;
        typedef unsigned short     u16;
        typedef signed int         s32;
        typedef unsigned int       u32;
        typedef signed long long   s64;
        typedef unsigned long long u64;

        typedef struct PsxRect { s16 x; s16 y; s16 w; s16 h; } PsxRect;
        typedef struct PsxVector { s32 vx; s32 vy; s32 vz; s32 pad; } PsxVector;
        typedef struct PsxSVector { s16 vx; s16 vy; s16 vz; s16 pad; } PsxSVector;
        typedef struct PsxMatrix { s16 m[3][3]; s16 pad; s32 t[3]; } PsxMatrix;
        typedef struct PsxDispEnv { PsxRect disp; PsxRect screen; u8 isinter; u8 isrgb24; u8 pad0; u8 pad1; } PsxDispEnv;
        typedef struct PsxDrawEnv {
            PsxRect clip; s16 ofs[2]; PsxRect tw; u16 tpage; u8 dtd; u8 dfe; u8 isbg;
            u8 r0; u8 g0; u8 b0; u32 dr_env[16];
        } PsxDrawEnv;
        typedef struct PsxCdlLoc { u8 minute; u8 second; u8 sector; u8 track; } PsxCdlLoc;
        typedef struct PsxCdlFile { PsxCdlLoc pos; u32 size; char name[16]; } PsxCdlFile;
        typedef struct PsxDirEntry { char name[20]; s32 attr; s32 size; u32 next; s32 head; u8 system[4]; } PsxDirEntry;
        typedef struct PsxExecHeader {
            u32 pc0; u32 gp0; u32 t_addr; u32 t_size; u32 d_addr; u32 d_size;
            u32 b_addr; u32 b_size; u32 s_addr; u32 s_size; u32 sp; u32 fp; u32 gp; u32 ret; u32 base;
        } PsxExecHeader;
        typedef struct PsxSpuVoiceAttr {
            u32 voice; u32 mask; u16 vol_left; u16 vol_right; s32 vol_mode_l; s32 vol_mode_r;
            u16 pitch; u16 note; u16 sample_note; s16 envx; u32 addr; u32 loop_addr;
            s32 a_mode; s32 s_mode; s32 r_mode; u16 ar; u16 dr; u16 sr; u16 rr; u16 sl; u16 pad; u32 adsr_raw;
        } PsxSpuVoiceAttr;
        typedef struct PsxVagAttr {
            u32 addr; u32 size; u16 pitch; u16 volume_l; u16 volume_r; u16 adsr1; u16 adsr2; u16 pad; u32 reserved[3];
        } PsxVagAttr;

        #endif
        """;

    /// <summary>Writes clean-room SLEIGH language files and <c>psx_clean_types.h</c> inside <paramref name="targetExtensionDir"/>.</summary>
    public static PsxSlaProvisionResult ProvisionExtensionDirectory(string targetExtensionDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetExtensionDir);
        string root = Path.GetFullPath(targetExtensionDir);
        string slaText = LoadSlaSpecText();
        PsxSlaValidationResult check = ValidateSlaSpec(slaText);
        if (!check.IsValid)
            throw new InvalidDataException(check.Summary);

        string langDir = AppEnvironment.AssertPathContained(Path.Combine(root, "data", "languages"), root);
        string incDir = AppEnvironment.AssertPathContained(Path.Combine(root, "include"), root);
        Directory.CreateDirectory(langDir);
        Directory.CreateDirectory(incDir);

        var written = new List<string>(5);
        WriteContained(Path.Combine(langDir, "r3000a_gte.slaspec"), root, slaText, written);
        WriteContained(Path.Combine(langDir, "r3000a_gte.ldefs"), root, GenerateLanguageDefinitionsXml(), written);
        WriteContained(Path.Combine(langDir, "r3000a_gte.pspec"), root, GenerateProcessorSpecXml(), written);
        WriteContained(Path.Combine(langDir, "r3000a_gte.cspec"), root, GenerateCompilerSpecXml(), written);
        WriteContained(Path.Combine(incDir, "psx_clean_types.h"), root, GenerateCleanPsxTypesHeader(), written);

        return new PsxSlaProvisionResult(root, SleighLanguageId, written);
    }

    private static void WriteContained(string candidatePath, string root, string content, List<string> written)
    {
        string safePath = AppEnvironment.AssertPathContained(candidatePath, root);
        File.WriteAllText(safePath, content, new UTF8Encoding(false));
        written.Add(safePath);
    }
}
