// @module: reStation.Decomp.PsxCleanExeAndOverlayLoader
// @purpose: Clean-room PS-X EXE header parser (t_addr/t_size/pc0/BSS clear), multi-overlay loader, and offline Ghidra batch invocation generator.
// @depends-on: System, System.Buffers.Binary, System.Collections.Generic, System.Diagnostics, System.IO, System.Security.Cryptography, System.Text, Foundation.Core, reEngine.Core.Decomp, reEngine.Core.Manifests
// @exports: PsxCleanExeAndOverlayLoader
// SPDX-License-Identifier: MPL-2.0

using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Foundation.Core;
using reEngine.Core.Decomp;
using reEngine.Core.Manifests;

namespace reStation.Decomp;

/// <summary>
/// Clean-room PS-X EXE parser, multi-overlay RAM loader with BSS clear, and offline Ghidra script generator.
/// </summary>
public static class PsxCleanExeAndOverlayLoader
{
    /// <summary>Standard PS-X EXE header size in bytes (2048).</summary>
    public const int ExeHeaderBytes = 0x800;

    /// <summary>PS1 physical main RAM size (2 MiB).</summary>
    public const int PsxMainRamBytes = 2 * 1024 * 1024;

    private const uint MaxExeBytes = 16U * 1024U * 1024U;
    private static readonly byte[] PsxMagic = "PS-X EXE"u8.ToArray();

    /// <summary>Parses a 2 KB <c>"PS-X EXE"</c> header and validates its text bounds.</summary>
    public static bool TryParseExeHeader(ReadOnlySpan<byte> exeBytes, out PsxExeHeader header)
    {
        header = default;
        if (exeBytes.Length < ExeHeaderBytes || !exeBytes[..8].SequenceEqual(PsxMagic))
            return false;

        uint pc0 = CanonicalKseg0(BinaryPrimitives.ReadUInt32LittleEndian(exeBytes.Slice(0x10, 4)));
        uint gp0 = BinaryPrimitives.ReadUInt32LittleEndian(exeBytes.Slice(0x14, 4));
        uint tAddr = CanonicalKseg0(BinaryPrimitives.ReadUInt32LittleEndian(exeBytes.Slice(0x18, 4)));
        uint tSize = BinaryPrimitives.ReadUInt32LittleEndian(exeBytes.Slice(0x1C, 4));
        uint dAddr = BinaryPrimitives.ReadUInt32LittleEndian(exeBytes.Slice(0x20, 4));
        uint dSize = BinaryPrimitives.ReadUInt32LittleEndian(exeBytes.Slice(0x24, 4));
        uint bAddr = BinaryPrimitives.ReadUInt32LittleEndian(exeBytes.Slice(0x28, 4));
        uint bSize = BinaryPrimitives.ReadUInt32LittleEndian(exeBytes.Slice(0x2C, 4));
        uint sAddr = BinaryPrimitives.ReadUInt32LittleEndian(exeBytes.Slice(0x30, 4));
        uint sSize = BinaryPrimitives.ReadUInt32LittleEndian(exeBytes.Slice(0x34, 4));

        uint available = (uint)Math.Max(0, exeBytes.Length - ExeHeaderBytes);
        uint effectiveSize = (tSize == 0 ? available : Math.Min(tSize, available)) & ~3U;
        if (effectiveSize < 4U)
            return false;

        uint canonicalBss = bSize > 0 ? CanonicalKseg0(bAddr) : 0U;
        header = new PsxExeHeader(pc0, gp0, tAddr, effectiveSize, dAddr, dSize, canonicalBss, bSize, sAddr, sSize);
        return true;
    }

    /// <summary>Loads and parses a single PS-X EXE file from disk.</summary>
    public static PsxExeImage LoadExeFromFile(string exePath, string? baseRootDir = null, bool isBootExe = true, int slotId = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        string full = Path.GetFullPath(exePath);
        byte[] bytes = File.ReadAllBytes(full);
        string rel = !string.IsNullOrWhiteSpace(baseRootDir)
            ? Path.GetRelativePath(Path.GetFullPath(baseRootDir), full).Replace('\\', '/')
            : Path.GetFileName(full);
        return LoadExeFromBytes(bytes, rel, isBootExe, slotId);
    }

    /// <summary>Parses a PS-X EXE from in-memory bytes and computes its lowercase SHA-256 digest.</summary>
    public static PsxExeImage LoadExeFromBytes(byte[] exeBytes, string relativePath, bool isBootExe = true, int slotId = 0)
    {
        ArgumentNullException.ThrowIfNull(exeBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (!TryParseExeHeader(exeBytes, out PsxExeHeader header))
            throw new InvalidDataException($"'{relativePath}' does not contain a valid PS-X EXE header.");

        string sha = Convert.ToHexString(SHA256.HashData(exeBytes)).ToLowerInvariant();
        string normRel = relativePath.Replace('\\', '/').TrimStart('/');
        return new PsxExeImage(slotId, Path.GetFileName(normRel), normRel, isBootExe, header, exeBytes, sha);
    }

    /// <summary>Copies <paramref name="exe"/> payload into 2 MiB PS1 RAM at <c>$t_addr</c> and zeroes <c>$b_addr..$b_addr+$b_size</c>.</summary>
    public static void LoadExeIntoRam(PsxExeImage exe, Span<byte> psxRam2Mb)
    {
        ArgumentNullException.ThrowIfNull(exe);
        if (psxRam2Mb.Length < PsxMainRamBytes)
            throw new ArgumentException($"PS1 RAM buffer must be at least {PsxMainRamBytes} bytes.", nameof(psxRam2Mb));

        CopySegmentToRam(exe.RawBytes.AsSpan(ExeHeaderBytes, (int)exe.Header.TextSize), exe.Header.TextAddr, psxRam2Mb);
        ClearBssRegion(exe.Header.BssAddr, exe.Header.BssSize, psxRam2Mb);
    }

    /// <summary>Loads a secondary overlay binary into 2 MiB PS1 RAM and zero-clears its declared BSS span.</summary>
    public static void LoadOverlayIntoRam(PsxOverlayDescriptor overlay, ReadOnlySpan<byte> overlayBytes, Span<byte> psxRam2Mb)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        if (psxRam2Mb.Length < PsxMainRamBytes)
            throw new ArgumentException($"PS1 RAM buffer must be at least {PsxMainRamBytes} bytes.", nameof(psxRam2Mb));

        ReadOnlySpan<byte> payload = overlayBytes.Length >= ExeHeaderBytes && overlayBytes[..8].SequenceEqual(PsxMagic)
            ? overlayBytes.Slice(ExeHeaderBytes, (int)Math.Min(overlay.ByteLength, (uint)(overlayBytes.Length - ExeHeaderBytes)))
            : overlayBytes[..(int)Math.Min(overlay.ByteLength, (uint)overlayBytes.Length)];

        CopySegmentToRam(payload, overlay.LoadAddress, psxRam2Mb);
        ClearBssRegion(overlay.BssAddress, overlay.BssSize, psxRam2Mb);
    }

    /// <summary>Discovers the boot PS-X EXE and multi-overlay program layout from a file, directory, or ISO/BIN image.</summary>
    public static PsxLoadedProgramLayout DiscoverProgramLayout(string sourcePath, string? preferredBootRelPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        string full = Path.GetFullPath(sourcePath);

        if (File.Exists(full))
        {
            byte[] peek = new byte[Math.Min(8, (int)new FileInfo(full).Length)];
            using (var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read))
                _ = fs.Read(peek, 0, peek.Length);

            if (peek.AsSpan().SequenceEqual(PsxMagic))
            {
                PsxExeImage single = LoadExeFromFile(full, Path.GetDirectoryName(full), isBootExe: true, slotId: 0);
                return new PsxLoadedProgramLayout(single, Array.Empty<PsxOverlayDescriptor>(), [single]);
            }

            if (PsxIsoImageScanner.HasIso9660PrimaryDescriptor(full))
            {
                (string? isoBoot, List<PsxDiscFileSlice> isoExes) = PsxIsoImageScanner.ScanIsoExecutables(full, MaxExeBytes);
                return BuildProgramLayout(isoExes, preferredBootRelPath ?? isoBoot, full);
            }

            throw new InvalidDataException($"File '{full}' is neither a PS-X EXE nor a valid PS1 ISO-9660 image.");
        }

        if (!Directory.Exists(full))
            throw new DirectoryNotFoundException($"Media source not found: '{full}'.");

        string? dirBoot = preferredBootRelPath;
        string? cnfPath = Directory.EnumerateFiles(full, "SYSTEM.CNF", SearchOption.AllDirectories).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(dirBoot) && cnfPath is not null)
            dirBoot = PsxIsoImageScanner.ParseSystemCnfBootPath(File.ReadAllText(cnfPath, Encoding.ASCII));

        var slices = new List<PsxDiscFileSlice>();
        foreach (string file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            long len = new FileInfo(file).Length;
            if (len < ExeHeaderBytes + 4 || len > MaxExeBytes || !HasPsxMagicFile(file))
                continue;
            slices.Add(new PsxDiscFileSlice(Path.GetRelativePath(full, file).Replace('\\', '/'), File.ReadAllBytes(file)));
        }
        return BuildProgramLayout(slices, dirBoot, full);
    }

    /// <summary>Locates the offline Ghidra + OpenJDK toolchain via <see cref="OfflineToolchainLocator"/> with zero internet calls.</summary>
    public static OfflineToolchainStatus LocateOfflineToolchain(
        string baseOrInstallDir,
        ReEngineInstallProfile profile = ReEngineInstallProfile.Full,
        string? explicitToolchainDir = null,
        DecompToolchainRequirements? requirements = null) =>
        OfflineToolchainLocator.Locate(baseOrInstallDir, profile, explicitToolchainDir, requirements);

    /// <summary>
    /// Generates clean-room SLEIGH language files, <c>psx-layout.json</c>, <c>PsxCleanExeLoader.py</c>,
    /// and an offline <c>ghidra-batch-plan.json</c> inside <paramref name="outputDir"/>.
    /// </summary>
    public static PsxGhidraBatchPlan GenerateGhidraBatchArtifacts(
        PsxLoadedProgramLayout layout,
        OfflineToolchainStatus toolchain,
        string outputDir,
        DecompRunOptions options)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(toolchain);
        ArgumentNullException.ThrowIfNull(options);
        string fullOut = Path.GetFullPath(outputDir);
        Directory.CreateDirectory(fullOut);

        string extDir = AppEnvironment.AssertPathContained(Path.Combine(fullOut, "ghidra_ext"), fullOut);
        _ = PsxGteSlaExtension.ProvisionExtensionDirectory(extDir);

        string scriptsDir = AppEnvironment.AssertPathContained(Path.Combine(fullOut, "ghidra_scripts"), fullOut);
        Directory.CreateDirectory(scriptsDir);
        string loaderScript = AppEnvironment.AssertPathContained(
            Path.Combine(scriptsDir, PsxGhidraScriptTemplates.LoaderScriptFileName), fullOut);
        File.WriteAllText(loaderScript, PsxGhidraScriptTemplates.GenerateCleanExeLoaderScript(), new UTF8Encoding(false));

        string exportDir = AppEnvironment.AssertPathContained(Path.Combine(fullOut, "exports"), fullOut);
        Directory.CreateDirectory(exportDir);

        string layoutPath = AppEnvironment.AssertPathContained(Path.Combine(fullOut, "psx-layout.json"), fullOut);
        PsxGhidraScriptTemplates.WriteLayoutJson(layoutPath, layout);

        string analyzeExe = toolchain.AnalyzeHeadlessPath ?? "analyzeHeadless.bat";
        string projectDir = Path.Combine(fullOut, "ghidra_work");
        var args = new List<string>
        {
            projectDir,
            $"reStation_{SanitizeToken(layout.BootExe.Name)}",
            "-import", layout.BootExe.RelativePath,
            "-processor", PsxGteSlaExtension.SleighLanguageId,
            "-cspec", "default",
            "-scriptPath", scriptsDir,
            "-postScript", PsxGhidraScriptTemplates.LoaderScriptFileName, layoutPath, exportDir,
            "-deleteProject"
        };
        string cmdPreview = $"\"{analyzeExe}\" " + string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));

        string planPath = AppEnvironment.AssertPathContained(Path.Combine(fullOut, "ghidra-batch-plan.json"), fullOut);
        PsxGhidraScriptTemplates.WriteBatchPlanJson(planPath, toolchain, analyzeExe, args, cmdPreview);

        return new PsxGhidraBatchPlan(toolchain, layoutPath, scriptsDir, loaderScript, extDir, exportDir, args, cmdPreview);
    }

    /// <summary>Executes Ghidra batch analysis strictly offline when available; fails closed if the toolchain is incomplete.</summary>
    public static async Task<(bool Success, int ExitCode, string Diagnostic)> ExecuteGhidraBatchAsync(
        PsxGhidraBatchPlan plan, string mediaImportPath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.Toolchain.IsAvailable || string.IsNullOrWhiteSpace(plan.Toolchain.AnalyzeHeadlessPath))
            return (false, -1, plan.Toolchain.DiagnosticMessage);
        if (string.IsNullOrWhiteSpace(mediaImportPath) || !File.Exists(mediaImportPath))
            return (false, -1, $"Verified input binary not found for offline Ghidra import: '{mediaImportPath}'.");

        var psi = new ProcessStartInfo(plan.Toolchain.AnalyzeHeadlessPath)
        {
            WorkingDirectory = plan.ExportDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (!string.IsNullOrWhiteSpace(plan.Toolchain.JdkDirectory))
            psi.Environment["JAVA_HOME"] = plan.Toolchain.JdkDirectory;

        for (int i = 0; i < plan.BatchArguments.Count; i++)
            psi.ArgumentList.Add(i == 3 ? Path.GetFullPath(mediaImportPath) : plan.BatchArguments[i]);

        using Process proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to launch offline Ghidra batch.");
        Task<string> errTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        string err = (await errTask.ConfigureAwait(false)).Trim();
        return proc.ExitCode == 0
            ? (true, 0, "Offline Ghidra batch execution completed.")
            : (false, proc.ExitCode, $"Ghidra batch failed (exit {proc.ExitCode}): {err}");
    }

    private static PsxLoadedProgramLayout BuildProgramLayout(List<PsxDiscFileSlice> slices, string? bootRel, string sourceLabel)
    {
        if (slices.Count == 0)
            throw new FileNotFoundException($"No valid PS-X EXE binaries found in '{sourceLabel}'.");

        int bootIdx = slices.FindIndex(s => !string.IsNullOrWhiteSpace(bootRel) &&
            string.Equals(s.RelativePath, bootRel.Replace('\\', '/').TrimStart('/'), StringComparison.OrdinalIgnoreCase));
        if (bootIdx > 0)
        {
            PsxDiscFileSlice first = slices[bootIdx];
            slices.RemoveAt(bootIdx);
            slices.Insert(0, first);
        }

        var exes = new List<PsxExeImage>(slices.Count);
        var overlays = new List<PsxOverlayDescriptor>(Math.Max(0, slices.Count - 1));
        for (int i = 0; i < slices.Count; i++)
        {
            PsxExeImage exe = LoadExeFromBytes(slices[i].Bytes, slices[i].RelativePath, isBootExe: i == 0, slotId: i);
            exes.Add(exe);
            if (i > 0)
            {
                overlays.Add(new PsxOverlayDescriptor(
                    i, exe.Name, exe.RelativePath, exe.Header.TextAddr, exe.Header.Pc0,
                    exe.Header.TextSize, exe.Header.BssAddr, exe.Header.BssSize, exe.Sha256Hex));
            }
        }
        return new PsxLoadedProgramLayout(exes[0], overlays, exes);
    }

    private static void CopySegmentToRam(ReadOnlySpan<byte> payload, uint ksegAddress, Span<byte> psxRam2Mb)
    {
        int offset = (int)(ksegAddress & 0x001F_FFFFU);
        if (offset < 0 || offset + payload.Length > PsxMainRamBytes)
            throw new InvalidDataException($"PS-X payload at 0x{ksegAddress:X8} ({payload.Length} B) exceeds 2 MiB PS1 RAM.");
        payload.CopyTo(psxRam2Mb.Slice(offset, payload.Length));
    }

    private static void ClearBssRegion(uint bssAddr, uint bssSize, Span<byte> psxRam2Mb)
    {
        if (bssSize == 0U) return;
        int offset = (int)(bssAddr & 0x001F_FFFFU);
        int clearLen = (int)Math.Min(bssSize, (uint)Math.Max(0, PsxMainRamBytes - offset));
        if (clearLen > 0) psxRam2Mb.Slice(offset, clearLen).Clear();
    }

    private static bool HasPsxMagicFile(string path)
    {
        Span<byte> head = stackalloc byte[8];
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return fs.Read(head) == 8 && head.SequenceEqual(PsxMagic);
    }

    private static uint CanonicalKseg0(uint addr) => 0x80000000U | (addr & 0x1FFFFFFFU);

    private static string SanitizeToken(string value) =>
        new(value.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
}
