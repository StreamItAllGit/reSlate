// @module: reStation.Player.PreloadRomDetector
// @purpose: Auto-discovers user media inside a module's gitignored data\rom\ via reEngine.Core RecompPackageReader (fail-closed containment).
// @depends-on: reEngine.Core.Manifests
// @exports: PreloadRomDetector
// @owner: devv-player-ui (T6) — public surface T1-locked.

using reEngine.Core.Manifests;

namespace reStation.Player;

public static class PreloadRomDetector
{
    /// <summary>Loads the .recomp package/manifest at <paramref name="packagePath"/> and scans its preload directory.</summary>
    public static PreloadRomDiscoveryResult Detect(string packagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        return RecompPackageReader.Load(packagePath).PreloadMedia;
    }

    /// <summary>Returns the highest-priority preloaded media path, or null when <c>data\rom\</c> is empty/missing.</summary>
    public static string? TryGetPrimaryMedia(string packagePath)
    {
        PreloadRomDiscoveryResult result = Detect(packagePath);
        return result.DirectoryExists ? result.PrimaryMediaPath : null;
    }
}
