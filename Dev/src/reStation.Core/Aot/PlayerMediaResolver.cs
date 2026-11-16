// @module: reStation.Core.Aot.PlayerMediaResolver
// @purpose: Resolves clean-room aot-recipe.json and verifies user-supplied data/rom media by SHA-256 without bundling assets.
// @depends-on: Foundation.Core.AppEnvironment, reStation.Contracts.AotRecipeSpec, reStation.Contracts.ReStationModuleManifest
// @exports: PlayerMediaResolver, ResolvedPlayerMedia, MediaInputKind

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

            foreach (string ext in DiscExtensions)
            {
                string[] discFiles = Directory.GetFiles(romDir, $"*{ext}", SearchOption.TopDirectoryOnly)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
                if (discFiles.Length > 0)
                {
                    string selected = discFiles[0];
                    string shaHex = ComputeFileSha256Hex(selected);
                    return new ResolvedPlayerMedia(MediaInputKind.DiscImage, selected, shaHex, romDir);
                }
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
                if (!containerCache.TryGetValue(containerPath, out byte[]? containerBytes))
                {
                    containerBytes = File.ReadAllBytes(containerPath);
                    containerCache[containerPath] = containerBytes;
                }
                if (exe.TextSizeBytes <= 0 || !TryFindContainerSliceOffset(containerBytes, exe.TextSizeBytes, exe.Sha256, out _))
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

    /// <summary>Locates a <paramref name="sliceLength"/>-byte segment in <paramref name="containerBytes"/> whose SHA-256 matches <paramref name="expectedSha256Hex"/> (2048-byte sector scan first, then 4-byte scan).</summary>
    public static bool TryFindContainerSliceOffset(
        ReadOnlySpan<byte> containerBytes,
        int sliceLength,
        string expectedSha256Hex,
        out int sliceOffset)
    {
        sliceOffset = -1;
        if (sliceLength <= 0 || sliceLength > containerBytes.Length) return false;
        if (string.IsNullOrWhiteSpace(expectedSha256Hex) || expectedSha256Hex.Length != 64 || !expectedSha256Hex.All(Uri.IsHexDigit))
            return false;

        byte[] expectedDigest = Convert.FromHexString(expectedSha256Hex);
        Span<byte> actualDigest = stackalloc byte[32];
        int limit = containerBytes.Length - sliceLength;

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
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(p => string.Equals(Path.GetFileName(p), fileName, StringComparison.OrdinalIgnoreCase));
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
