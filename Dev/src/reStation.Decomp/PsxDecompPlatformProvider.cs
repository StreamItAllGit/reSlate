// @module: reStation.Decomp.PsxDecompPlatformProvider
// @purpose: Clean-room MPL-2.0 PS1 IDecompPlatformProvider / IReStationDecompProvider orchestrating D0-D5 decompilation and AOT recipe synthesis.
// @depends-on: System, System.Collections.Generic, System.Diagnostics, System.IO, System.Text, System.Text.Json, Foundation.Core, Foundation.Retro, reEngine.Core.Decomp, reEngine.Core.Manifests, reStation.Contracts
// @exports: PsxDecompPlatformProvider
// SPDX-License-Identifier: MPL-2.0

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Foundation.Core;
using Foundation.Retro;
using reEngine.Core.Decomp;
using reEngine.Core.Manifests;
using reStation.Contracts;

namespace reStation.Decomp;

/// <summary>
/// Clean-room <c>MPL-2.0</c> PlayStation 1 (<c>ps1</c>) implementation of <see cref="IReStationDecompProvider"/>
/// executing <c>D0</c>–<c>D5</c> intake, offline Ghidra SLEIGH scripting, C/asset normalization, LP32 parity, and MIPS byte-match checks.
/// </summary>
public sealed class PsxDecompPlatformProvider : IReStationDecompProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Initializes a new instance of <see cref="PsxDecompPlatformProvider"/>.</summary>
    public PsxDecompPlatformProvider() { }

    /// <inheritdoc/>
    public string PlatformId => ReStationEngineInfo.PlatformId;

    /// <inheritdoc/>
    public string DisplayName => "reStation PS1 Clean-Room Decomp Provider";

    /// <inheritdoc/>
    public string EngineId => ReStationEngineInfo.EngineId;

    /// <inheritdoc/>
    public string MinimumEngineVersion => ReStationEngineInfo.Version;

    /// <inheritdoc/>
    public string SleighLanguageId => PsxGteSlaExtension.SleighLanguageId;

    /// <inheritdoc/>
    public RecompExecutionTierMode DefaultExecutionTier => RecompExecutionTierMode.AotPrimary;

    /// <inheritdoc/>
    public async Task<DecompStageResult> RunDecompAsync(DecompRunOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ct.ThrowIfCancellationRequested();
        var sw = Stopwatch.StartNew();

        DecompPackageManifest manifest = LoadDecompManifest(options.PackagePath);
        if (!IsSupportedPlatform(manifest.PlatformId))
            return Fail(options.TargetStage, options.OutputDirectory, sw, $"Unsupported platformId '{manifest.PlatformId}'; expected 'ps1' or 'psx'.", "PSX_PLATFORM_MISMATCH");

        DecompCleanRoomValidationResult pkgCheck = ValidateCleanRoomPackage(options.PackagePath);
        if (!pkgCheck.IsValid)
            return Fail(options.TargetStage, options.OutputDirectory, sw, pkgCheck.Summary, "PSX_CLEANROOM_PACKAGE_VIOLATION");

        PsxSlaValidationResult slaCheck = PsxGteSlaExtension.ValidateSlaSpec(PsxGteSlaExtension.LoadSlaSpecText());
        if (!slaCheck.IsValid)
            return Fail(options.TargetStage, options.OutputDirectory, sw, slaCheck.Summary, "PSX_SLA_INVALID");

        OfflineToolchainStatus toolchain = ResolveToolchain(options, manifest);
        if (options.DryRun)
        {
            return new DecompStageResult(
                options.TargetStage, true,
                $"[DRY-RUN] PS1 clean-room pipeline ready ({SleighLanguageId}, offlineToolchain={toolchain.IsAvailable}).",
                options.OutputDirectory, Array.Empty<string>(), sw.Elapsed);
        }

        string outDir = Path.GetFullPath(options.OutputDirectory);
        Directory.CreateDirectory(outDir);
        string? mediaSource = ResolveMediaSource(options);
        string? exportsArg = GetArg(options, "exportsDir");

        PsxLoadedProgramLayout? layout = null;
        if (mediaSource is not null)
        {
            layout = PsxCleanExeAndOverlayLoader.DiscoverProgramLayout(mediaSource);
            VerifyBootAndOverlayRamMapping(layout);
            WriteIntakeJson(outDir, layout);
        }
        else if (options.TargetStage is DecompStage.D0 or DecompStage.D1 or DecompStage.D5 ||
                 (string.IsNullOrWhiteSpace(exportsArg) && options.TargetStage != DecompStage.D4))
        {
            return Fail(options.TargetStage, outDir, sw,
                $"Stage {options.TargetStage} requires verified PS-X EXE or PS1 disc media under '{options.DataDirectory}' (data\\rom or gamedata).",
                "PSX_MEDIA_NOT_FOUND");
        }

        if (options.TargetStage >= DecompStage.D2)
        {
            (bool d2Ok, string d2Diag, string d2Code) = await ExecuteStageD2Async(layout, toolchain, outDir, options, mediaSource, ct).ConfigureAwait(false);
            if (!d2Ok)
                return Fail(options.TargetStage, outDir, sw, d2Diag, d2Code);
        }

        if (options.TargetStage >= DecompStage.D3)
            ExecuteStageD3(manifest, layout, exportsArg, outDir, options);

        if (options.TargetStage >= DecompStage.D4)
        {
            PsxLp32ParityReport d4 = PsxParityAndByteMatchVerifier.VerifyLp32Parity(outputDir: outDir);
            if (!d4.Passed)
                return Fail(options.TargetStage, outDir, sw, d4.Summary, "PSX_D4_PARITY_FAILED");
        }

        if (options.TargetStage >= DecompStage.D5)
        {
            bool strict = string.Equals(GetArg(options, "strictByteMatch"), "true", StringComparison.OrdinalIgnoreCase);
            foreach (PsxExeImage exe in layout!.AllExeImages)
            {
                PsxByteMatchReport d5 = PsxParityAndByteMatchVerifier.VerifyExeByteMatch(exe, strict, outDir);
                if (!d5.Passed)
                    return Fail(options.TargetStage, outDir, sw, d5.Summary, "PSX_D5_BYTEMATCH_FAILED");
            }
        }

        DecompCleanRoomValidationResult outCheck = ValidateCleanRoomOutput(outDir);
        if (!outCheck.IsValid)
            return Fail(options.TargetStage, outDir, sw, outCheck.Summary, "PSX_CLEANROOM_OUTPUT_VIOLATION");

        string[] files = Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        return new DecompStageResult(options.TargetStage, true,
            $"Completed PS1 clean-room stage {options.TargetStage} ({files.Length} clean-room output file(s)).",
            outDir, files, sw.Elapsed);
    }

    /// <inheritdoc/>
    public Task<AotRecipeSpec> BuildAotRecipeAsync(DecompRunOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ct.ThrowIfCancellationRequested();

        DecompPackageManifest manifest = LoadDecompManifest(options.PackagePath);
        if (!IsSupportedPlatform(manifest.PlatformId))
            throw new InvalidDataException($"Unsupported platformId '{manifest.PlatformId}'; expected 'ps1' or 'psx'.");

        string? mediaSource = ResolveMediaSource(options);
        PsxLoadedProgramLayout? layout = mediaSource is not null ? PsxCleanExeAndOverlayLoader.DiscoverProgramLayout(mediaSource) : null;

        string? exportsDir = GetArg(options, "exportsDir");
        PsxNormalizedExportSummary? exports = null;
        if (!string.IsNullOrWhiteSpace(exportsDir) && Directory.Exists(exportsDir) && !string.IsNullOrWhiteSpace(options.OutputDirectory))
            exports = PsxExportAndAssetPipeline.NormalizeExportDirectory(exportsDir, options.OutputDirectory);

        string? outDir = options.DryRun || string.IsNullOrWhiteSpace(options.OutputDirectory) ? null : options.OutputDirectory;
        AotRecipeSpec recipe = PsxExportAndAssetPipeline.BuildDeterministicAotRecipe(
            manifest, layout, exports, outDir, GetArg(options, "executionTierMode"));
        return Task.FromResult(recipe);
    }

    /// <inheritdoc/>
    public DecompCleanRoomValidationResult ValidateCleanRoomOutput(string outputDirectory) =>
        PsxExportAndAssetPipeline.ValidateCleanRoomOutput(outputDirectory);

    /// <inheritdoc/>
    public DecompCleanRoomValidationResult ValidateCleanRoomPackage(string packageOrDirectory) =>
        PsxExportAndAssetPipeline.ValidateCleanRoomPackage(packageOrDirectory);

    private static async Task<(bool Ok, string Diagnostic, string Code)> ExecuteStageD2Async(
        PsxLoadedProgramLayout? layout,
        OfflineToolchainStatus toolchain,
        string outDir,
        DecompRunOptions options,
        string? mediaSource,
        CancellationToken ct)
    {
        if (layout is null)
        {
            string extDir = AppEnvironment.AssertPathContained(Path.Combine(outDir, "ghidra_ext"), outDir);
            _ = PsxGteSlaExtension.ProvisionExtensionDirectory(extDir);
            return (true, "Provisioned clean-room SLEIGH R3000A_GTE extension.", string.Empty);
        }

        PsxGhidraBatchPlan plan = PsxCleanExeAndOverlayLoader.GenerateGhidraBatchArtifacts(layout, toolchain, outDir, options);
        bool invokeGhidra = string.Equals(GetArg(options, "invokeGhidra"), "true", StringComparison.OrdinalIgnoreCase);
        if (invokeGhidra)
        {
            string rel = layout.BootExe.RelativePath.Replace('/', Path.DirectorySeparatorChar);
            string importTarget = mediaSource is not null && File.Exists(mediaSource)
                ? mediaSource
                : Path.Combine(mediaSource ?? options.DataDirectory, rel);
            (bool ok, _, string diag) = await PsxCleanExeAndOverlayLoader.ExecuteGhidraBatchAsync(plan, importTarget, ct).ConfigureAwait(false);
            if (!ok) return (false, diag, "PSX_GHIDRA_BATCH_FAILED");
        }
        return (true, "Generated clean-room Ghidra batch artifacts.", string.Empty);
    }

    private static void ExecuteStageD3(
        DecompPackageManifest manifest,
        PsxLoadedProgramLayout? layout,
        string? exportsArg,
        string outDir,
        DecompRunOptions options)
    {
        string exportSrc = !string.IsNullOrWhiteSpace(exportsArg) ? exportsArg : Path.Combine(outDir, "exports");
        PsxNormalizedExportSummary exports = PsxExportAndAssetPipeline.NormalizeExportDirectory(exportSrc, outDir);
        _ = PsxExportAndAssetPipeline.BuildDeterministicAotRecipe(
            manifest, layout, exports, outDir, GetArg(options, "executionTierMode"));

        if (string.Equals(GetArg(options, "extractAssets"), "true", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(options.DataDirectory))
        {
            string assetSrc = GetArg(options, "assetSourceDir") ?? options.DataDirectory;
            string cacheDir = Path.Combine(Path.GetFullPath(options.DataDirectory), "cache", "assets");
            _ = PsxAssetExtractors.ScanAndExtractDirectory(assetSrc, cacheDir, options.DataDirectory);
        }
    }

    private static DecompPackageManifest LoadDecompManifest(string packagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        string full = Path.GetFullPath(packagePath);
        if (Directory.Exists(full))
        {
            string candidate = Path.Combine(full, "module.decomp.json");
            if (!File.Exists(candidate))
                candidate = Directory.EnumerateFiles(full, "*.decomp.json", SearchOption.TopDirectoryOnly).FirstOrDefault()
                    ?? throw new FileNotFoundException($"No module.decomp.json found in '{full}'.", candidate);
            return DecompPipelineCoordinator.ParseManifest(candidate);
        }
        return DecompPipelineCoordinator.ParseManifest(full);
    }

    private static OfflineToolchainStatus ResolveToolchain(DecompRunOptions options, DecompPackageManifest manifest)
    {
        string baseDir = !string.IsNullOrWhiteSpace(options.EngineDirectoryOverride)
            ? options.EngineDirectoryOverride : AppContext.BaseDirectory;
        string? profArg = GetArg(options, "profile");
        ReEngineInstallProfile profile = string.Equals(profArg, "Lightweight", StringComparison.OrdinalIgnoreCase)
            ? ReEngineInstallProfile.Lightweight : ReEngineInstallProfile.Full;
        return PsxCleanExeAndOverlayLoader.LocateOfflineToolchain(baseDir, profile, options.ToolchainDirectory, manifest.ToolchainRequirements);
    }

    private static string? ResolveMediaSource(DecompRunOptions options)
    {
        foreach (string? arg in new[] { GetArg(options, "exePath"), GetArg(options, "gamedataDir"), GetArg(options, "discPath") })
            if (!string.IsNullOrWhiteSpace(arg) && (File.Exists(arg) || Directory.Exists(arg)))
                return Path.GetFullPath(arg);

        if (string.IsNullOrWhiteSpace(options.DataDirectory) || !Directory.Exists(options.DataDirectory))
            return null;

        string fullData = Path.GetFullPath(options.DataDirectory);
        foreach (string sub in new[] { Path.Combine(fullData, "gamedata"), Path.Combine(fullData, "data", "rom"), Path.Combine(fullData, "rom"), fullData })
        {
            if (!Directory.Exists(sub)) continue;
            if (Directory.EnumerateFiles(sub, "*", SearchOption.AllDirectories).Any(IsCandidatePsxMedia))
                return sub;
        }
        return null;
    }

    private static bool IsCandidatePsxMedia(string path)
    {
        string ext = Path.GetExtension(path);
        if (ext.Equals(".iso", StringComparison.OrdinalIgnoreCase) || ext.Equals(".bin", StringComparison.OrdinalIgnoreCase))
            return PsxIsoImageScanner.HasIso9660PrimaryDescriptor(path);

        if (new FileInfo(path).Length < PsxCleanExeAndOverlayLoader.ExeHeaderBytes + 4) return false;
        Span<byte> head = stackalloc byte[8];
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return fs.Read(head) == 8 && head.SequenceEqual("PS-X EXE"u8);
    }

    private static void VerifyBootAndOverlayRamMapping(PsxLoadedProgramLayout layout)
    {
        byte[] ram = new byte[PsxCleanExeAndOverlayLoader.PsxMainRamBytes];
        PsxCleanExeAndOverlayLoader.LoadExeIntoRam(layout.BootExe, ram);
        foreach (PsxOverlayDescriptor ov in layout.Overlays)
        {
            PsxExeImage? match = layout.AllExeImages.FirstOrDefault(e => e.SlotId == ov.SlotId);
            if (match is not null)
                PsxCleanExeAndOverlayLoader.LoadOverlayIntoRam(ov, match.RawBytes, ram);
        }
    }

    private static void WriteIntakeJson(string outDir, PsxLoadedProgramLayout layout)
    {
        string path = AppEnvironment.AssertPathContained(Path.Combine(outDir, "d0-media-intake.json"), outDir);
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            bootExe = layout.BootExe.RelativePath,
            bootSha256 = layout.BootExe.Sha256Hex,
            overlayCount = layout.Overlays.Count,
            executables = layout.AllExeImages.Select(e => new { e.SlotId, e.Name, e.RelativePath, e.Sha256Hex, tAddr = $"0x{e.Header.TextAddr:X8}", tSize = e.Header.TextSize, pc0 = $"0x{e.Header.Pc0:X8}", bSize = e.Header.BssSize })
        }, JsonOptions), new UTF8Encoding(false));
    }

    private static bool IsSupportedPlatform(string platformId) =>
        string.Equals(platformId, "ps1", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(platformId, "psx", StringComparison.OrdinalIgnoreCase);

    private static string? GetArg(DecompRunOptions options, string key) =>
        options.PlatformArguments is not null && options.PlatformArguments.TryGetValue(key, out string? v) ? v : null;

    private static DecompStageResult Fail(DecompStage stage, string? outDir, Stopwatch sw, string message, string code) =>
        new(stage, false, message, outDir, Array.Empty<string>(), sw.Elapsed, code);
}
