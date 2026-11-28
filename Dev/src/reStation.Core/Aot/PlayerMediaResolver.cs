// @module: reStation.Core.Aot.PlayerMediaResolver
// @purpose: Resolves clean-room aot-recipe.json and verifies user-supplied data/rom media by SHA-256 without bundling assets.
// @depends-on: Foundation.Core.AppEnvironment, reStation.Contracts.AotRecipeSpec, reStation.Contracts.ReStationModuleManifest
// @exports: PlayerMediaResolver, ResolvedPlayerMedia, MediaInputKind

using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Foundation.Core;
using reStation.Contracts;

namespace reStation.Core.Aot;

/// <summary>Kind of local player media matched against a clean-room AOT recipe.</summary>
public enum MediaInputKind
{
    ExeFile = 0,
    GameDataDir = 1,
    DiscImage = 2
}

/// <summary>Verified user-supplied media source ready for local AOT compilation or cache lookup.</summary>
public sealed record ResolvedPlayerMedia(
    MediaInputKind Kind,
    string InputPath,
    string RomSha256Hex,
    string MediaRootDir);

/// <summary>Reads clean-room AOT recipes and validates local <c>data/rom/</c> media before cache lookup or lifting.</summary>
public static class PlayerMediaResolver
{
    private static readonly string[] DiscExtensions = [".cue", ".bin", ".iso", ".img", ".chd", ".7z", ".zip"];

    /// <summary>Loads and validates <see cref="AotRecipeSpec"/> from <paramref name="manifest"/>, ZIP archive, or disk.</summary>
    public static AotRecipeSpec? TryLoadRecipe(
        IReStationModuleManifest manifest,
        string manifestDirectory,
        string? packageArchivePath)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestDirectory);

        if (manifest is ReStationModuleManifest concrete && concrete.AotRecipeSpec is not null)
        {
            concrete.AotRecipeSpec.Validate($"{manifest.ModuleId}:inline");
            return concrete.AotRecipeSpec;
        }

        string? relPath = (manifest as ReStationModuleManifest)?.AotRecipe;
        if (string.IsNullOrWhiteSpace(relPath))
        {
            if (packageArchivePath is not null)
            {
                if (!ArchiveContainsEntry(packageArchivePath, ReStationEngineInfo.DefaultAotRecipePath))
                    return null;
                relPath = ReStationEngineInfo.DefaultAotRecipePath;
            }
            else
            {
                string defaultPath = Path.Combine(Path.GetFullPath(manifestDirectory), ReStationEngineInfo.DefaultAotRecipePath);
                if (!File.Exists(defaultPath)) return null;
                relPath = ReStationEngineInfo.DefaultAotRecipePath;
            }
        }

        string normalizedRel = relPath.Replace('\\', '/').TrimStart('/');
        if (normalizedRel.Contains("..", StringComparison.Ordinal))
            throw new InvalidDataException($"AOT recipe path '{relPath}' attempts directory traversal.");

        string jsonText;
        string label;
        if (!string.IsNullOrWhiteSpace(packageArchivePath))
        {
            using ZipArchive zip = ZipFile.OpenRead(Path.GetFullPath(packageArchivePath));
            ZipArchiveEntry entry = zip.Entries.FirstOrDefault(e =>
                    string.Equals(e.FullName.Replace('\\', '/'), normalizedRel, StringComparison.OrdinalIgnoreCase))
                ?? throw new FileNotFoundException($"AOT recipe '{normalizedRel}' not found in '{Path.GetFileName(packageArchivePath)}'.", normalizedRel);
            using Stream s = entry.Open();
            using var sr = new StreamReader(s, Encoding.UTF8);
            jsonText = sr.ReadToEnd();
            label = $"{Path.GetFileName(packageArchivePath)}!{normalizedRel}";
        }
        else
        {
            string root = Path.GetFullPath(manifestDirectory);
            string fullPath = AppEnvironment.AssertPathContained(Path.Combine(root, relPath), root);
            if (!File.Exists(fullPath))
                throw new FileNotFoundException($"AOT recipe file '{fullPath}' not found.", fullPath);
            jsonText = File.ReadAllText(fullPath, Encoding.UTF8);
            label = fullPath;
        }

        AotRecipeSpec recipe = JsonSerializer.Deserialize<AotRecipeSpec>(jsonText, ReStationModuleManifest.JsonOptions)
            ?? throw new InvalidDataException($"Failed to parse AOT recipe '{label}'.");
        recipe.Validate(label);
        return recipe;
    }

    /// <summary>Finds and SHA-256-verifies local <c>data/rom/</c> media matching <paramref name="recipe"/>.</summary>
    public static ResolvedPlayerMedia ResolveAndVerifyMedia(
        AotRecipeSpec recipe,
        string manifestDirectory,
        LocalAotLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestDirectory);

        List<string> candidateRoots = BuildCandidateRomRoots(manifestDirectory, options);
        foreach (string romDir in candidateRoots)
        {
            if (!Directory.Exists(romDir)) continue;

            if (TryVerifyExecutablesInDir(recipe, romDir, out ResolvedPlayerMedia? exeMatch))
                return exeMatch!;

            string? selected = SelectBestDiscImage(romDir);
            if (selected is not null)
            {
                string shaHex = ComputeFileSha256Hex(selected);
                return new ResolvedPlayerMedia(MediaInputKind.DiscImage, selected, shaHex, romDir);
            }
        }

        throw new FileNotFoundException(
            $"No verified PS1 media found in '{ReStationEngineInfo.DefaultPreloadRomDir}' or 'data/gamedata' for module '{recipe.ModuleId}'. " +
            $"Checked: {string.Join("; ", candidateRoots)}.");
    }

    internal static List<string> BuildCandidateRomRoots(string manifestDirectory, LocalAotLoadOptions? options)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new List<string>();
        void Add(string? p)
        {
            if (string.IsNullOrWhiteSpace(p)) return;
            string full = Path.GetFullPath(p);
            if (seen.Add(full)) roots.Add(full);
        }

        Add(options?.PreloadRomDir);
        string mRoot = Path.GetFullPath(manifestDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Add(Path.Combine(mRoot, "data", "rom"));
        Add(Path.Combine(mRoot, "rom"));
        Add(Path.Combine(mRoot, "data", "gamedata"));
        Add(Path.Combine(mRoot, "data", "gamedata", "disc"));
        Add(Path.Combine(mRoot, "gamedata"));

        if (string.Equals(Path.GetFileName(mRoot), "app", StringComparison.OrdinalIgnoreCase) &&
            Path.GetDirectoryName(mRoot) is { } appParent)
        {
            Add(Path.Combine(appParent, "data", "rom"));
            Add(Path.Combine(appParent, "data", "gamedata"));
            Add(Path.Combine(appParent, "data", "gamedata", "disc"));
        }

        if (!string.IsNullOrWhiteSpace(options?.DataDir))
        {
            string dataRoot = Path.GetFullPath(options.DataDir);
            Add(Path.Combine(dataRoot, "rom"));
            Add(Path.Combine(dataRoot, "gamedata"));
            Add(Path.Combine(dataRoot, "gamedata", "disc"));
        }

        Add(Path.Combine(AppContext.BaseDirectory, "data", "rom"));
        Add(Path.Combine(AppContext.BaseDirectory, "data", "gamedata"));
        return roots;
    }

    private static string? SelectBestDiscImage(string romDir)
    {
        string? selected = null;
        int bestPriority = int.MaxValue;
        foreach (string candidate in Directory.EnumerateFiles(romDir, "*", SearchOption.TopDirectoryOnly))
        {
            int prio = GetDiscExtensionPriority(Path.GetExtension(candidate));
            if (prio < 0 || prio > bestPriority) continue;
            if (prio < bestPriority || StringComparer.OrdinalIgnoreCase.Compare(candidate, selected) < 0)
            {
                bestPriority = prio;
                selected = candidate;
            }
        }
        return selected;
    }

    private static int GetDiscExtensionPriority(string ext)
    {
        for (int i = 0; i < DiscExtensions.Length; i++)
        {
            if (string.Equals(ext, DiscExtensions[i], StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    private static bool TryVerifyExecutablesInDir(AotRecipeSpec recipe, string romDir, out ResolvedPlayerMedia? resolved)
    {
        resolved = null;
        if (recipe.Executables.Count == 0) return false;

        var matchedPaths = new List<string>(recipe.Executables.Count);
        var containerCache = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var shaConcat = new StringBuilder();
        foreach (AotRecipeExecutableSpec exe in recipe.Executables)
        {
            int hashIdx = exe.BootExePath.IndexOf('#');
            if (hashIdx >= 0)
            {
                if (hashIdx == 0 || hashIdx + 1 >= exe.BootExePath.Length)
                    throw new InvalidDataException($"Invalid overlay container path '{exe.BootExePath}'.");
                string containerRel = exe.BootExePath[..hashIdx];
                string? containerPath = FindMediaFile(romDir, containerRel);
                if (containerPath is null) return false;
                if (exe.TextSizeBytes <= 0)
                {
                    throw new InvalidDataException(
                        $"ROM container slice checksum mismatch for '{exe.BootExePath}' in '{containerPath}': expected {exe.Sha256} ({exe.TextSizeBytes} bytes).");
                }
                if (!containerCache.TryGetValue(containerPath, out byte[]? containerBytes))
                {
                    if (exe.ContainerOffsetBytes is long hint && hint >= 0 &&
                        TryVerifyContainerSliceAtOffset(containerPath, hint, exe.TextSizeBytes, exe.Sha256))
                    {
                        matchedPaths.Add(containerPath);
                        shaConcat.Append(exe.Sha256.ToLowerInvariant());
                        continue;
                    }
                    containerBytes = File.ReadAllBytes(containerPath);
                    containerCache[containerPath] = containerBytes;
                }
                if (!TryFindContainerSliceOffset(containerBytes, exe.TextSizeBytes, exe.Sha256, exe.ContainerOffsetBytes, out _))
                {
                    throw new InvalidDataException(
                        $"ROM container slice checksum mismatch for '{exe.BootExePath}' in '{containerPath}': expected {exe.Sha256} ({exe.TextSizeBytes} bytes).");
                }
                matchedPaths.Add(containerPath);
                shaConcat.Append(exe.Sha256.ToLowerInvariant());
                continue;
            }

            string? found = FindMediaFile(romDir, exe.BootExePath);
            if (found is null) return false;

            string actualSha = ComputeFileSha256Hex(found);
            if (!string.Equals(actualSha, exe.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"ROM EXE checksum mismatch for '{exe.BootExePath}' at '{found}': expected {exe.Sha256}, actual {actualSha}.");
            }

            matchedPaths.Add(found);
            shaConcat.Append(actualSha);
        }

        if (matchedPaths.Count == 1 && !recipe.Executables[0].BootExePath.Contains('#', StringComparison.Ordinal))
        {
            resolved = new ResolvedPlayerMedia(MediaInputKind.ExeFile, matchedPaths[0], shaConcat.ToString(), romDir);
            return true;
        }

        string combinedSha = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(shaConcat.ToString()))).ToLowerInvariant();
        resolved = new ResolvedPlayerMedia(MediaInputKind.GameDataDir, romDir, combinedSha, romDir);
        return true;
    }

    private static bool TryVerifyContainerSliceAtOffset(
        string containerPath,
        long hintOffsetBytes,
        int sliceLength,
        string expectedSha256Hex)
    {
        if (hintOffsetBytes < 0 || sliceLength <= 0) return false;
        Span<byte> expectedDigest = stackalloc byte[32];
        if (!TryDecodeSha256Hex(expectedSha256Hex, expectedDigest))
            return false;

        using var fs = new FileStream(containerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (fs.Length < sliceLength || hintOffsetBytes > fs.Length - sliceLength)
            return false;

        fs.Seek(hintOffsetBytes, SeekOrigin.Begin);
        byte[] rented = ArrayPool<byte>.Shared.Rent(sliceLength);
        try
        {
            Span<byte> slice = rented.AsSpan(0, sliceLength);
            fs.ReadExactly(slice);
            Span<byte> actualDigest = stackalloc byte[32];
            SHA256.HashData(slice, actualDigest);
            return actualDigest.SequenceEqual(expectedDigest);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>Locates a <paramref name="sliceLength"/>-byte segment in <paramref name="containerBytes"/> whose SHA-256 matches <paramref name="expectedSha256Hex"/> (2048-byte sector scan first, then 4-byte scan).</summary>
    public static bool TryFindContainerSliceOffset(
        ReadOnlySpan<byte> containerBytes,
        int sliceLength,
        string expectedSha256Hex,
        out int sliceOffset)
        => TryFindContainerSliceOffset(containerBytes, sliceLength, expectedSha256Hex, preferredOffsetBytes: null, out sliceOffset);

    /// <summary>
    /// Locates a <paramref name="sliceLength"/>-byte segment in <paramref name="containerBytes"/> whose SHA-256 matches <paramref name="expectedSha256Hex"/>,
    /// verifying <paramref name="preferredOffsetBytes"/> in O(1) before falling back to 2048-byte sector and 4-byte scans.
    /// </summary>
    public static bool TryFindContainerSliceOffset(
        ReadOnlySpan<byte> containerBytes,
        int sliceLength,
        string expectedSha256Hex,
        long? preferredOffsetBytes,
        out int sliceOffset)
    {
        sliceOffset = -1;
        if (sliceLength <= 0 || sliceLength > containerBytes.Length) return false;
        Span<byte> expectedDigest = stackalloc byte[32];
        if (!TryDecodeSha256Hex(expectedSha256Hex, expectedDigest))
            return false;

        Span<byte> actualDigest = stackalloc byte[32];
        int limit = containerBytes.Length - sliceLength;

        if (preferredOffsetBytes is long hint && hint >= 0L && hint <= limit)
        {
            int hintOff = (int)hint;
            SHA256.HashData(containerBytes.Slice(hintOff, sliceLength), actualDigest);
            if (actualDigest.SequenceEqual(expectedDigest)) { sliceOffset = hintOff; return true; }
        }

        for (int off = 0; off <= limit; off += 2048)
        {
            SHA256.HashData(containerBytes.Slice(off, sliceLength), actualDigest);
            if (actualDigest.SequenceEqual(expectedDigest)) { sliceOffset = off; return true; }
        }

        for (int off = 0; off <= limit; off += 4)
        {
            if ((off & 2047) == 0) continue;
            SHA256.HashData(containerBytes.Slice(off, sliceLength), actualDigest);
            if (actualDigest.SequenceEqual(expectedDigest)) { sliceOffset = off; return true; }
        }
        return false;
    }

    private static bool TryDecodeSha256Hex(string expectedSha256Hex, Span<byte> destination)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256Hex) || expectedSha256Hex.Length != 64 || destination.Length < 32)
            return false;
        for (int i = 0; i < 32; i++)
        {
            int hi = HexVal(expectedSha256Hex[i * 2]);
            int lo = HexVal(expectedSha256Hex[(i * 2) + 1]);
            if (hi < 0 || lo < 0) return false;
            destination[i] = (byte)((hi << 4) | lo);
        }
        return true;
    }

    private static int HexVal(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1
    };

    private static string? FindMediaFile(string romDir, string relativePath)
    {
        string normalizedRel = relativePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!normalizedRel.Contains("..", StringComparison.Ordinal))
        {
            string direct = AppEnvironment.AssertPathContained(Path.Combine(romDir, normalizedRel), romDir);
            if (File.Exists(direct)) return direct;
        }

        string fileName = Path.GetFileName(normalizedRel);
        if (string.IsNullOrWhiteSpace(fileName)) return null;

        string byName = Path.Combine(romDir, fileName);
        if (File.Exists(byName)) return byName;

        return Directory.EnumerateFiles(romDir, "*", SearchOption.AllDirectories)
            .Where(p => string.Equals(Path.GetFileName(p), fileName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static bool ArchiveContainsEntry(string archivePath, string entryPath)
    {
        using ZipArchive zip = ZipFile.OpenRead(Path.GetFullPath(archivePath));
        string target = entryPath.Replace('\\', '/').TrimStart('/');
        return zip.Entries.Any(e => string.Equals(e.FullName.Replace('\\', '/'), target, StringComparison.OrdinalIgnoreCase));
    }

    public static string ComputeFileSha256Hex(string filePath)
    {
        using FileStream fs = File.OpenRead(filePath);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }
}
