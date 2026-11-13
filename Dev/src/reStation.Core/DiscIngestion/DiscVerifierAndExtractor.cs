// @module: reStation.Core.DiscIngestion.DiscVerifierAndExtractor
// @purpose: BYO disc ingestion delegating .iso/.bin+.cue/.chd/.7z/.zip extraction + SHA-256 checks to Foundation.Retro.MediaContainerExtractor.
// @depends-on: Foundation.Retro, reStation.Contracts, reStation.Core
// @exports: DiscVerifierAndExtractor
// @owner: devv-audio-mdec (T3) — IngestAsync signature T1-locked; ISO9660 file-table extraction lands in T3.

using Foundation.Retro;
using reStation.Contracts;

namespace reStation.Core.DiscIngestion;

public static class DiscVerifierAndExtractor
{
    /// <summary>
    /// Verifies/extracts <paramref name="inputPath"/> strictly inside <c>&lt;dataDir&gt;\gamedata\</c>, checks
    /// <see cref="IRecompModuleManifest.RequiredDiscChecksums"/>, and persists a <see cref="DiscManifest"/>.
    /// </summary>
    public static async Task<DiscIngestResult> IngestAsync(
        string inputPath,
        string dataDir,
        IReStationModuleManifest module,
        ReStationLogger? logger = null,
        string? toolsBaseDirectory = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDir);
        ArgumentNullException.ThrowIfNull(module);

        string discDir = Path.Combine(DiscManifest.GetGameDataDir(dataDir), "disc");
        var options = new MediaExtractionOptions(
            ToolsBaseDirectory: toolsBaseDirectory ?? AppContext.BaseDirectory,
            CopyDirectMediaToDestination: true,
            ComputeSha256: true,
            ExpectedChecksums: module.RequiredDiscChecksums);

        logger?.Info("DISC", $"Ingesting '{Path.GetFileName(inputPath)}' for module '{module.ModuleId}'.");
        MediaExtractionResult result = await MediaContainerExtractor.ExtractOrVerifyAsync(inputPath, discDir, options, ct).ConfigureAwait(false);
        if (!result.Success)
        {
            logger?.Warn("DISC", result.DiagnosticMessage);
            return new DiscIngestResult(false, result.DiagnosticMessage, null);
        }

        var manifest = new DiscManifest
        {
            ModuleId = module.ModuleId,
            SerialNumber = module.SerialNumber,
            PrimaryImagePath = result.PrimaryMediaPath,
            CueSheetPath = result.CueSheetPath,
            VerifiedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture)
        };
        manifest.Save(dataDir);
        logger?.Info("DISC", result.DiagnosticMessage);
        return new DiscIngestResult(true, result.DiagnosticMessage, manifest);
    }
}
