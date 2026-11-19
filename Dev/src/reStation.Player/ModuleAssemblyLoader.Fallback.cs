// @module: reStation.Player.ModuleAssemblyLoader.Fallback
// @purpose: Graceful AOT cache lookup and fallback execution-tier handling when external compiler toolchain is absent or JIT-bringup skips compile.
// @depends-on: System.ComponentModel, Foundation.Core, reEngine.Core.Aot, reStation.Contracts, reStation.Core.Aot
// @exports: ModuleAssemblyLoader (partial)

using System.ComponentModel;
using Foundation.Core;
using reEngine.Core.Aot;
using reStation.Contracts;
using reStation.Core.Aot;

namespace reStation.Player;

public sealed partial class ModuleAssemblyLoader
{
    public string? AotFallbackWarning { get; private set; }

    internal static LocalAotEnsureResult? TryEnsureOrFallbackAot(
        ReStationModuleManifest manifestForAot,
        AotRecipeSpec aotRecipe,
        string manifestDirectory,
        LocalAotLoadOptions? aotOptions,
        string? packageArchivePath,
        string resolvedTier,
        bool canLoadNonGeneratedEntry,
        PlayerLocalAotCompiler? aotCompiler,
        out string? fallbackWarning)
    {
        fallbackWarning = null;
        if (string.Equals(resolvedTier, ReStationExecutionTierModes.JitBringup, StringComparison.OrdinalIgnoreCase) &&
            canLoadNonGeneratedEntry)
        {
            return TryResolveCachedAotOnly(manifestForAot, aotRecipe, manifestDirectory, aotOptions);
        }

        PlayerLocalAotCompiler compiler = aotCompiler ?? new PlayerLocalAotCompiler();
        try
        {
            return compiler.EnsureCompiledInCacheAsync(
                manifestForAot, aotRecipe, manifestDirectory, aotOptions, packageArchivePath).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (IsToolchainFallbackCandidate(ex) &&
            canLoadNonGeneratedEntry &&
            !string.Equals(resolvedTier, ReStationExecutionTierModes.StaticAotOnly, StringComparison.OrdinalIgnoreCase))
        {
            fallbackWarning = $"Local AOT compiler unavailable ({ex.Message}); running '{manifestForAot.EntryAssembly}' in execution fallback tier.";
            return null;
        }
    }

    private static LocalAotEnsureResult? TryResolveCachedAotOnly(
        ReStationModuleManifest manifest,
        AotRecipeSpec recipe,
        string manifestDirectory,
        LocalAotLoadOptions? aotOptions)
    {
        try
        {
            ResolvedPlayerMedia media = PlayerMediaResolver.ResolveAndVerifyMedia(recipe, manifestDirectory, aotOptions);
            string dataDir = PlayerLocalAotCompiler.ResolveEffectiveDataDir(manifestDirectory, aotOptions);
            var key = new AotCacheKey(manifest.ModuleId, media.RomSha256Hex, ReStationEngineInfo.EngineId, ReStationEngineInfo.Version, manifest.Version);
            AotCacheEntry? cached = new AotCacheManager(dataDir).TryGetEntryAsync(key).GetAwaiter().GetResult();
            return cached is null
                ? null
                : new LocalAotEnsureResult(cached.GeneratedAssemblyPath, key.ToCacheVersionSegment(), media.RomSha256Hex, CacheHit: true, CleanRoomCheck: null);
        }
        catch (Exception ex) when (IsToolchainFallbackCandidate(ex))
        {
            return null;
        }
    }

    private static bool IsToolchainFallbackCandidate(Exception ex) =>
        ex is InvalidOperationException or Win32Exception ||
        (ex is FileNotFoundException fnf &&
         !fnf.Message.Contains("No verified PS1 media", StringComparison.OrdinalIgnoreCase));
}
