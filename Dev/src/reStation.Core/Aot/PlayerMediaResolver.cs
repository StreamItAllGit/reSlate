// @module: reStation.Core.Aot.PlayerMediaResolver
// @purpose: Resolves clean-room aot-recipe.json and verifies user-supplied data/rom media by SHA-256 without bundling assets.
// @depends-on: Foundation.Core.SandboxEnvironment, reStation.Contracts.AotRecipeSpec, reStation.Contracts.ReStationModuleManifest
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
    private static readonly string[] DiscExtensions = [".cue", ".bin", ".iso", ".img"];

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
            string fullPath = SandboxEnvironment.AssertPathContained(Path.Combine(root, relPath), root);
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
            $"No verified PS1 media found in '{ReStationEngineInfo.DefaultPreloadRomDir}' for module '{recipe.ModuleId}'. " +
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
        string mRoot = Path.GetFullPath(manifestDirectory);
        Add(Path.Combine(mRoot, "data", "rom"));
        Add(Path.Combine(mRoot, "rom"));
        if (!string.IsNullOrWhiteSpace(options?.DataDir))
            Add(Path.Combine(Path.GetFullPath(options.DataDir), "rom"));
        Add(Path.Combine(AppContext.BaseDirectory, "data", "rom"));
        return roots;
    }

    private static bool TryVerifyExecutablesInDir(AotRecipeSpec recipe, string romDir, out ResolvedPlayerMedia? resolved)
    {
        resolved = null;
        if (recipe.Executables.Count == 0) return false;

        var matchedPaths = new List<string>(recipe.Executables.Count);
        var shaConcat = new StringBuilder();
        foreach (AotRecipeExecutableSpec exe in recipe.Executables)
        {
            string? found = FindExecutableFile(romDir, exe);
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

        if (matchedPaths.Count == 1)
        {
            resolved = new ResolvedPlayerMedia(MediaInputKind.ExeFile, matchedPaths[0], shaConcat.ToString(), romDir);
            return true;
        }

        string combinedSha = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(shaConcat.ToString()))).ToLowerInvariant();
        resolved = new ResolvedPlayerMedia(MediaInputKind.GameDataDir, romDir, combinedSha, romDir);
        return true;
    }

    private static string? FindExecutableFile(string romDir, AotRecipeExecutableSpec exe)
    {
        string normalizedRel = exe.BootExePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        if (!normalizedRel.Contains("..", StringComparison.Ordinal))
        {
            string direct = Path.Combine(romDir, normalizedRel);
            if (File.Exists(direct)) return direct;
        }

        string fileName = Path.GetFileName(normalizedRel);
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            string byName = Path.Combine(romDir, fileName);
            if (File.Exists(byName)) return byName;
        }
        return null;
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
