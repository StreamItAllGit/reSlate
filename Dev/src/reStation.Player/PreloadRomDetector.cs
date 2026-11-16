// @module: reStation.Player.PreloadRomDetector
// @purpose: Auto-discovers user media inside a module's gitignored data\rom\ via reEngine RecompPackageReader with fail-closed fallback scan.
// @depends-on: Foundation.Core, reEngine.Core.Manifests, reStation.Contracts
// @exports: PreloadRomDetector
// @owner: devv-player-ui (T6)

using Foundation.Core;
using reEngine.Core.Manifests;
using reStation.Contracts;

namespace reStation.Player;

public static class PreloadRomDetector
{
    private static readonly Dictionary<string, int> ExtensionPriority = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cue"] = 0, [".chd"] = 1, [".iso"] = 2, [".7z"] = 3, [".zip"] = 4, [".bin"] = 5
    };

    /// <summary>Loads the .recomp package/manifest at <paramref name="packagePath"/> and scans its preload directory.</summary>
    public static PreloadRomDiscoveryResult Detect(string packagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        string full = Path.GetFullPath(packagePath);
        if (Directory.Exists(full)) return DiscoverInDirectory(full);

        try
        {
            PreloadRomDiscoveryResult primary = RecompPackageReader.Load(full).PreloadMedia;
            if (primary.DiscoveredMediaFiles.Count > 0) return primary;
        }
        catch (InvalidDataException)
        {
            // Minimal test/dev manifests missing reEngine header fields still fall back to data\rom\ scan below.
        }

        string moduleRoot = RecompPackageReader.ResolveModuleRootDirectory(full);
        return DiscoverInDirectory(moduleRoot);
    }

    /// <summary>Discovers preloaded optical media for an already-parsed <see cref="IReStationModuleManifest"/>.</summary>
    public static PreloadRomDiscoveryResult Detect(string moduleRootDir, IReStationModuleManifest manifest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleRootDir);
        ArgumentNullException.ThrowIfNull(manifest);

        var pkg = new RecompPackageManifest
        {
            ModuleId = manifest.ModuleId,
            Title = manifest.DisplayName,
            Version = manifest.Version,
            EngineId = ReStationEngineInfo.EngineId,
            EngineVersion = manifest.EngineVersion,
            CompatibleEngineTrack = manifest.CompatibleEngineTrack,
            PreloadRomDir = string.IsNullOrWhiteSpace(manifest.PreloadRomDir) ? ReStationEngineInfo.DefaultPreloadRomDir : manifest.PreloadRomDir
        };
        PreloadRomDiscoveryResult result = RecompPackageReader.DiscoverPreloadMedia(moduleRootDir, pkg);
        return result.DiscoveredMediaFiles.Count > 0 ? result : DiscoverInDirectory(moduleRootDir, manifest.PreloadRomDir);
    }

    /// <summary>Fallback scan of <c>&lt;moduleRoot&gt;\data\rom\</c> (contained via AppEnvironment) for PS1 disc/archive formats.</summary>
    public static PreloadRomDiscoveryResult DiscoverInDirectory(string moduleRootDirectory, string preloadRelativeDir = ReStationEngineInfo.DefaultPreloadRomDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleRootDirectory);
        string root = Path.GetFullPath(moduleRootDirectory);
        string rel = string.IsNullOrWhiteSpace(preloadRelativeDir) ? ReStationEngineInfo.DefaultPreloadRomDir : preloadRelativeDir.Trim();
        if (Path.IsPathRooted(rel))
            throw new InvalidOperationException($"Fail-Closed Containment Violation: preloadRomDir '{rel}' must be relative.");

        string candidate = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)));
        string contained = AppEnvironment.AssertPathContained(candidate, root);
        if (!Directory.Exists(contained)) return new PreloadRomDiscoveryResult(contained, false, Array.Empty<string>(), null);

        List<string> files = Directory.EnumerateFiles(contained, "*.*", SearchOption.AllDirectories)
            .Where(path => ExtensionPriority.ContainsKey(Path.GetExtension(path)))
            .OrderBy(path => ExtensionPriority[Path.GetExtension(path)])
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new PreloadRomDiscoveryResult(contained, true, files, files.Count > 0 ? files[0] : null);
    }

    /// <summary>Returns the highest-priority preloaded media path, or null when <c>data\rom\</c> is empty/missing.</summary>
    public static string? TryGetPrimaryMedia(string packagePath)
    {
        PreloadRomDiscoveryResult result = Detect(packagePath);
        return result.DirectoryExists ? result.PrimaryMediaPath : null;
    }
}
