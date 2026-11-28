// @module: reStation.Core.DiscIngestion.DiscVerifierAndExtractor
// @purpose: BYO disc ingestion delegating .iso/.bin+.cue/.chd/.7z/.zip extraction to Foundation.Retro.MediaContainerExtractor, unpacking ISO-9660 files with fail-closed containment, and verifying SHA-256 checksums.
// @depends-on: System.Security.Cryptography, Foundation.Core, Foundation.Retro, reStation.Contracts, reStation.Core
// @exports: DiscVerifierAndExtractor
// @owner: devv-audio-mdec (T3) — IngestAsync signature T1-locked.

using System.Globalization;
using System.Security.Cryptography;
using Foundation.Core;
using Foundation.Retro;
using reStation.Contracts;

namespace reStation.Core.DiscIngestion;

public static class DiscVerifierAndExtractor
{
    private readonly record struct VerifiedFileInfo(string Sha256, long SizeBytes);

    /// <summary>
    /// Verifies/extracts <paramref name="inputPath"/> strictly inside <c>&lt;dataDir&gt;\gamedata\</c>, unpacks ISO-9660
    /// entries, checks <see cref="IRecompModuleManifest.RequiredDiscChecksums"/>, and persists <see cref="DiscManifest"/>.
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

        string fullInput = Path.GetFullPath(inputPath);
        if (!File.Exists(fullInput))
            return Fail(logger, $"Disc file was not found: '{fullInput}'.");

        string gameDataDir = DiscManifest.GetGameDataDir(dataDir);
        string discDir = AppEnvironment.AssertPathContained(Path.Combine(gameDataDir, "disc"), gameDataDir);
        var options = new MediaExtractionOptions(
            ToolsBaseDirectory: toolsBaseDirectory ?? AppContext.BaseDirectory,
            CopyDirectMediaToDestination: true,
            ComputeSha256: true,
            ExpectedChecksums: null);

        logger?.Info("DISC", $"Ingesting '{Path.GetFileName(fullInput)}' for module '{module.ModuleId}'.");
        MediaExtractionResult result;
        try
        {
            result = await MediaContainerExtractor.ExtractOrVerifyAsync(fullInput, discDir, options, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Fail(logger, ex.Message);
        }

        if (!result.Success)
            return Fail(logger, result.DiagnosticMessage);

        var containerFiles = BuildContainerIndex(discDir, result);
        var isoFiles = new Dictionary<string, VerifiedFileInfo>(StringComparer.OrdinalIgnoreCase);
        var manifest = new DiscManifest
        {
            ModuleId = module.ModuleId,
            SerialNumber = module.SerialNumber,
            PrimaryImagePath = result.PrimaryMediaPath,
            CueSheetPath = result.CueSheetPath,
            VerifiedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)
        };

        try
        {
            if (Iso9660RawReader.HasIso9660Header(result.PrimaryMediaPath))
            {
                await ExtractIsoEntriesAsync(result.PrimaryMediaPath, gameDataDir, manifest, isoFiles, ct).ConfigureAwait(false);
            }
            else if (!AllRequiredMatchedInContainer(module.RequiredDiscChecksums, containerFiles))
            {
                return Fail(logger, $"'{Path.GetFileName(result.PrimaryMediaPath)}' is not a valid ISO-9660 CD image.");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Fail(logger, ex.Message);
        }

        string? checksumError = VerifyRequiredChecksums(module.RequiredDiscChecksums, isoFiles, containerFiles);
        if (checksumError is not null)
            return Fail(logger, checksumError);

        manifest.Save(dataDir);
        string msg = $"{result.DiagnosticMessage} ({manifest.Files.Count} ISO file(s)).";
        logger?.Info("DISC", msg);
        return new DiscIngestResult(true, msg, manifest);
    }

    private static Dictionary<string, VerifiedFileInfo> BuildContainerIndex(string discDir, MediaExtractionResult result)
    {
        var map = new Dictionary<string, VerifiedFileInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in result.ExtractedFiles)
        {
            string safe = AppEnvironment.AssertPathContained(file, discDir);
            string rel = Path.GetRelativePath(discDir, safe).Replace('\\', '/');
            if (!result.Sha256ByRelativePath.TryGetValue(rel, out string? sha)) continue;
            long len = new FileInfo(safe).Length;
            map[rel] = new VerifiedFileInfo(sha, len);
            string name = Path.GetFileName(safe);
            map.TryAdd(name, new VerifiedFileInfo(sha, len));
        }
        return map;
    }

    private static Task ExtractIsoEntriesAsync(
        string imagePath,
        string gameDataDir,
        DiscManifest manifest,
        Dictionary<string, VerifiedFileInfo> isoFiles,
        CancellationToken ct)
    {
        using Iso9660RawReader iso = Iso9660RawReader.Open(imagePath);
        manifest.VolumeId = iso.VolumeId;
        foreach (Iso9660Entry entry in iso.Entries)
        {
            ct.ThrowIfCancellationRequested();
            string outPath = AppEnvironment.AssertPathContained(
                Path.Combine(gameDataDir, entry.FullPath.Replace('/', Path.DirectorySeparatorChar)),
                gameDataDir);
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

            (string sha, long actualLen) = CopyAndHashEntry(iso, entry, outPath);
            isoFiles[entry.FullPath] = new VerifiedFileInfo(sha, actualLen);
            manifest.Files.Add(new DiscFileEntry
            {
                RelativeFile = entry.FullPath,
                Lba = entry.Lba,
                ByteLength = entry.ByteLength,
                SectorCount = Math.Max(1U, (entry.ByteLength + Iso9660RawReader.UserSectorSize - 1U) / Iso9660RawReader.UserSectorSize),
                IsForm2Raw = entry.IsForm2Stream,
                Sha256 = sha
            });
        }
        return Task.CompletedTask;
    }

    private static (string Sha256, long ActualLength) CopyAndHashEntry(Iso9660RawReader iso, Iso9660Entry entry, string outPath)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long actualLen;
        using (var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 65_536))
        {
            using var hashingStream = new HashingWriteStream(fs, hasher);
            iso.CopyEntryTo(entry, hashingStream);
            actualLen = hashingStream.BytesWritten;
        }
        Span<byte> hashBytes = stackalloc byte[32];
        hasher.GetHashAndReset(hashBytes);
        return (Convert.ToHexString(hashBytes).ToLowerInvariant(), actualLen);
    }

    private static bool AllRequiredMatchedInContainer(
        IReadOnlyList<DiscChecksumEntry> expected,
        IReadOnlyDictionary<string, VerifiedFileInfo> containerFiles)
    {
        bool hasRequired = false;
        foreach (DiscChecksumEntry entry in expected)
        {
            if (!entry.IsRequired) continue;
            hasRequired = true;
            if (!containerFiles.ContainsKey(NormalizeRel(entry.RelativePath))) return false;
        }
        return hasRequired;
    }

    private static string? VerifyRequiredChecksums(
        IReadOnlyList<DiscChecksumEntry> expected,
        IReadOnlyDictionary<string, VerifiedFileInfo> isoFiles,
        IReadOnlyDictionary<string, VerifiedFileInfo> containerFiles)
    {
        foreach (DiscChecksumEntry req in expected)
        {
            string norm = NormalizeRel(req.RelativePath);
            if (!isoFiles.TryGetValue(norm, out VerifiedFileInfo actual) &&
                !containerFiles.TryGetValue(norm, out actual))
            {
                if (req.IsRequired) return $"Missing required disc file '{norm}'.";
                continue;
            }
            if (!string.Equals(actual.Sha256, req.ExpectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
                return $"SHA-256 mismatch for '{norm}': expected {req.ExpectedSha256}, actual {actual.Sha256}.";
            if (req.ExpectedSizeBytes.HasValue && actual.SizeBytes != req.ExpectedSizeBytes.Value)
                return $"Size mismatch for '{norm}': expected {req.ExpectedSizeBytes.Value}, actual {actual.SizeBytes}.";
        }
        return null;
    }

    private static string NormalizeRel(string path) => path.Replace('\\', '/').TrimStart('/');

    private static DiscIngestResult Fail(ReStationLogger? logger, string message)
    {
        logger?.Warn("DISC", message);
        return new DiscIngestResult(false, message, null);
    }

    private sealed class HashingWriteStream(Stream inner, IncrementalHash hasher) : Stream
    {
        public long BytesWritten { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => BytesWritten;
        public override long Position { get => BytesWritten; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            hasher.AppendData(buffer, offset, count);
            BytesWritten += count;
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            inner.Write(buffer);
            hasher.AppendData(buffer);
            BytesWritten += buffer.Length;
        }
        public override void WriteByte(byte value)
        {
            inner.WriteByte(value);
            Span<byte> b = [value];
            hasher.AppendData(b);
            BytesWritten++;
        }
    }
}
