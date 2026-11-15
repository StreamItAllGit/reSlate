// @module: reStation.Decomp.PsxProgramModels
// @purpose: Clean-room data records representing PS-X EXE headers, overlay descriptors, program layouts, and offline Ghidra plans.
// @depends-on: System, System.Collections.Generic, reEngine.Core.Decomp
// @exports: PsxExeHeader, PsxExeImage, PsxOverlayDescriptor, PsxLoadedProgramLayout, PsxGhidraHeadlessPlan
// SPDX-License-Identifier: MPL-2.0

using reEngine.Core.Decomp;

namespace reStation.Decomp;

/// <summary>Parsed 2 KB PlayStation 1 PS-X EXE header (<c>"PS-X EXE"</c>).</summary>
/// <param name="Pc0">Initial program counter (<c>$pc0</c>) canonicalized to KSEG0.</param>
/// <param name="Gp0">Initial global pointer (<c>$gp0</c>).</param>
/// <param name="TextAddr">RAM base address (<c>$t_addr</c>) for payload copy.</param>
/// <param name="TextSize">Effective payload byte length (<c>$t_size</c>).</param>
/// <param name="DataAddr">Optional header data section address (<c>$d_addr</c>).</param>
/// <param name="DataSize">Optional header data section size (<c>$d_size</c>).</param>
/// <param name="BssAddr">BSS start address (<c>$b_addr</c>) canonicalized to KSEG0 when non-empty.</param>
/// <param name="BssSize">BSS byte length (<c>$b_size</c>) zero-cleared on load.</param>
/// <param name="StackAddr">Initial stack pointer (<c>$s_addr</c>).</param>
/// <param name="StackSize">Declared stack byte length (<c>$s_size</c>).</param>
public readonly record struct PsxExeHeader(
    uint Pc0,
    uint Gp0,
    uint TextAddr,
    uint TextSize,
    uint DataAddr,
    uint DataSize,
    uint BssAddr,
    uint BssSize,
    uint StackAddr,
    uint StackSize)
{
    /// <summary>Exclusive end address of the loaded text/data payload.</summary>
    public uint TextEnd => TextAddr + TextSize;

    /// <summary>Exclusive end address of the BSS region to zero-clear at load time.</summary>
    public uint BssEnd => BssAddr + BssSize;
}

/// <summary>Verified PS-X executable image loaded in memory for decompilation analysis.</summary>
/// <param name="SlotId">Zero-based executable slot index (<c>0</c> for boot EXE).</param>
/// <param name="Name">Executable file name.</param>
/// <param name="RelativePath">Normalized disc or workspace relative path.</param>
/// <param name="IsBootExe">Whether this executable is the primary boot binary.</param>
/// <param name="Header">Parsed PS-X EXE header.</param>
/// <param name="RawBytes">Raw verified executable bytes.</param>
/// <param name="Sha256Hex">Lowercase hex SHA-256 digest of <paramref name="RawBytes"/>.</param>
public sealed record PsxExeImage(
    int SlotId,
    string Name,
    string RelativePath,
    bool IsBootExe,
    PsxExeHeader Header,
    byte[] RawBytes,
    string Sha256Hex);

/// <summary>Clean-room descriptor for a PS1 overlay executable or overlay segment.</summary>
/// <param name="SlotId">Overlay slot index (<c>&gt;= 1</c>).</param>
/// <param name="Name">Overlay file or segment name.</param>
/// <param name="RelativePath">Normalized disc or workspace relative path.</param>
/// <param name="LoadAddress">KSEG0 target RAM load address.</param>
/// <param name="EntryAddress">Initial entry address (<c>$pc0</c>).</param>
/// <param name="ByteLength">Payload byte length copied into RAM.</param>
/// <param name="BssAddress">Overlay BSS start address to zero-clear.</param>
/// <param name="BssSize">Overlay BSS byte size.</param>
/// <param name="Sha256Hex">Lowercase hex SHA-256 digest of the overlay binary.</param>
public sealed record PsxOverlayDescriptor(
    int SlotId,
    string Name,
    string RelativePath,
    uint LoadAddress,
    uint EntryAddress,
    uint ByteLength,
    uint BssAddress,
    uint BssSize,
    string Sha256Hex);

/// <summary>Discovered PS1 boot executable and multi-overlay program layout.</summary>
/// <param name="BootExe">Primary boot PS-X EXE image.</param>
/// <param name="Overlays">Ordered secondary overlay descriptors.</param>
/// <param name="AllExeImages">All discovered PS-X EXEs starting with <paramref name="BootExe"/>.</param>
public sealed record PsxLoadedProgramLayout(
    PsxExeImage BootExe,
    IReadOnlyList<PsxOverlayDescriptor> Overlays,
    IReadOnlyList<PsxExeImage> AllExeImages);

/// <summary>Deterministic offline Ghidra headless invocation plan and clean-room loader scripts.</summary>
/// <param name="Toolchain">Resolved offline Ghidra + JDK toolchain status.</param>
/// <param name="LayoutJsonPath">Path to generated <c>psx-layout.json</c>.</param>
/// <param name="ScriptDirectory">Path to generated Jython script directory.</param>
/// <param name="LoaderScriptPath">Path to generated <c>PsxCleanExeLoader.py</c>.</param>
/// <param name="ExtensionDirectory">Path to provisioned clean-room Sleigh extension folder.</param>
/// <param name="ExportDirectory">Path where Ghidra exports <c>functions.json</c> and C files.</param>
/// <param name="HeadlessArguments">Deterministic offline <c>analyzeHeadless</c> argument list.</param>
/// <param name="CommandLinePreview">Human-readable preview of the headless command line.</param>
public sealed record PsxGhidraHeadlessPlan(
    OfflineToolchainStatus Toolchain,
    string LayoutJsonPath,
    string ScriptDirectory,
    string LoaderScriptPath,
    string ExtensionDirectory,
    string ExportDirectory,
    IReadOnlyList<string> HeadlessArguments,
    string CommandLinePreview);
