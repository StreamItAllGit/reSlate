// @module: reStation.Player.ModuleAssemblyLoader
// @purpose: Collectible AssemblyLoadContext loading clean-room or local-AOT .recomp modules with zero type-identity drift or unload leaks.
// @depends-on: System.IO.Compression, System.Reflection, System.Runtime.Loader, Foundation.Core, Foundation.Retro, reEngine.Core.Aot, reStation.Contracts, reStation.Core.Aot
// @exports: ModuleAssemblyLoader
// @owner: devv-aot (Wave 3B)

using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using Foundation.Core;
using Foundation.Retro;
using reEngine.Core.Aot;
using reStation.Contracts;
using reStation.Core.Aot;

namespace reStation.Player;

public sealed class ModuleAssemblyLoader : AssemblyLoadContext
{
    /// <summary>Assemblies always resolved from the engine (default) context so interfaces stay identical across the boundary.</summary>
    public static readonly IReadOnlySet<string> SharedAssemblyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "reStation.Contracts", "reStation.Core", "Foundation.Core", "Foundation.Retro", "reEngine.Core"
    };

    private readonly AssemblyDependencyResolver? _resolver;
    private readonly Dictionary<string, byte[]>? _archiveAssemblies;
    private readonly string? _companionCachedAssemblyPath;
    private readonly IReadOnlySet<string>? _companionAliases;
    private readonly object _companionSync = new();
    private volatile Assembly? _loadedCompanionAssembly;

    public ModuleAssemblyLoader(string entryAssemblyPath)
        : this(entryAssemblyPath, companionCachedAssemblyPath: null, companionAliases: null, isDiskEntry: true)
    {
    }

    public ModuleAssemblyLoader(string packageArchivePath, string entryAssemblyName)
        : this(packageArchivePath, entryAssemblyName, companionCachedAssemblyPath: null, companionAliases: null)
    {
    }

    private ModuleAssemblyLoader(string entryAssemblyPath, string? companionCachedAssemblyPath, IReadOnlySet<string>? companionAliases, bool isDiskEntry)
        : base($"reStation.Module:{Path.GetFileNameWithoutExtension(entryAssemblyPath)}", isCollectible: true)
    {
        _ = isDiskEntry;
        ArgumentException.ThrowIfNullOrWhiteSpace(entryAssemblyPath);
        EntryAssemblyPath = Path.GetFullPath(entryAssemblyPath);
        if (!File.Exists(EntryAssemblyPath)) throw new FileNotFoundException("Module entry assembly not found.", EntryAssemblyPath);
        _resolver = new AssemblyDependencyResolver(EntryAssemblyPath);
        _companionCachedAssemblyPath = companionCachedAssemblyPath;
        _companionAliases = NormalizeCompanionAliases(companionCachedAssemblyPath, companionAliases);
        Unloading += static alc => ((ModuleAssemblyLoader)alc).ClearLoadedCompanion();
    }

    private ModuleAssemblyLoader(string packageArchivePath, string entryAssemblyName, string? companionCachedAssemblyPath, IReadOnlySet<string>? companionAliases)
        : base($"reStation.Module:{Path.GetFileNameWithoutExtension(packageArchivePath)}", isCollectible: true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageArchivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryAssemblyName);
        string fullPkg = Path.GetFullPath(packageArchivePath);
        if (!File.Exists(fullPkg)) throw new FileNotFoundException("Module .recomp cartridge not found.", fullPkg);
        PackageArchivePath = fullPkg;
        EntryAssemblyPath = entryAssemblyName;
        _companionCachedAssemblyPath = companionCachedAssemblyPath;
        _companionAliases = NormalizeCompanionAliases(companionCachedAssemblyPath, companionAliases);
        _archiveAssemblies = ReadArchiveAssemblies(fullPkg);
        Unloading += static alc => ((ModuleAssemblyLoader)alc).ClearLoadedCompanion();
    }

    public string EntryAssemblyPath { get; }
    public string? PackageArchivePath { get; }
    public bool LoadedFromLocalAotCache { get; private set; }
    public bool AotCacheHit { get; private set; }
    public string? CachedGeneratedAssemblyPath { get; private set; }
    public string? CacheVersionSegment { get; private set; }
    public string? VerifiedRomSha256Hex { get; private set; }
    public Assembly? CompanionAssembly => _loadedCompanionAssembly;
    public string ResolvedExecutionTierMode { get; private set; } = ReStationExecutionTierModes.Default;
    public bool ForceZeroRwx { get; private set; }
    public bool JitBringupEnabled { get; private set; }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is not null && SharedAssemblyNames.Contains(assemblyName.Name)) return null;
        if (_archiveAssemblies is not null && assemblyName.Name is not null &&
            _archiveAssemblies.TryGetValue(assemblyName.Name, out byte[]? bytes))
        {
            using var ms = new MemoryStream(bytes, writable: false);
            return LoadFromStream(ms);
        }

        if (_companionCachedAssemblyPath is not null &&
            _companionAliases is not null &&
            assemblyName.Name is not null &&
            _companionAliases.Contains(assemblyName.Name) &&
            File.Exists(_companionCachedAssemblyPath))
        {
            Assembly loaded = PreloadCompanionAssembly(_companionCachedAssemblyPath);
            if (string.Equals(loaded.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase))
            {
                return loaded;
            }
        }

        string? path = _resolver?.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadAssemblyBytes(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        string? path = _resolver?.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }

    /// <summary>Loads the module using resolved package path if present on the manifest.</summary>
    public static IRecompGameModule LoadGameModule(IReStationModuleManifest manifest, string manifestDirectory, out ModuleAssemblyLoader context) =>
        LoadGameModule(manifest, manifestDirectory, (manifest as ReStationModuleManifest)?.ResolvedPackagePath, null, out context);

    /// <summary>Loads the module either from an explicit <paramref name="packageArchivePath"/> ZIP cartridge or from disk.</summary>
    public static IRecompGameModule LoadGameModule(
        IReStationModuleManifest manifest, string manifestDirectory, string? packageArchivePath, out ModuleAssemblyLoader context) =>
        LoadGameModule(manifest, manifestDirectory, packageArchivePath, null, out context);

    /// <summary>Loads the module with explicit local AOT / Tier 3 runtime options from disk or resolved cartridge.</summary>
    public static IRecompGameModule LoadGameModule(
        IReStationModuleManifest manifest, string manifestDirectory, LocalAotLoadOptions? aotOptions, out ModuleAssemblyLoader context) =>
        LoadGameModule(manifest, manifestDirectory, (manifest as ReStationModuleManifest)?.ResolvedPackagePath, aotOptions, out context);

    /// <summary>Loads the module with clean-room recipe validation, local <c>&lt;dataDir&gt;\cache\</c> AOT build-on-miss, and collectible ALC.</summary>
    public static IRecompGameModule LoadGameModule(
        IReStationModuleManifest manifest,
        string manifestDirectory,
        string? packageArchivePath,
        LocalAotLoadOptions? aotOptions,
        out ModuleAssemblyLoader context,
        PlayerLocalAotCompiler? aotCompiler = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestDirectory);
        ValidateEntryAssemblyPathSafety(manifest.EntryAssembly);

        var concreteManifest = manifest as ReStationModuleManifest;
        AotRecipeSpec? aotRecipe = PlayerMediaResolver.TryLoadRecipe(manifest, manifestDirectory, packageArchivePath);
        if (concreteManifest is not null && aotRecipe is not null)
        {
            concreteManifest.AotRecipeSpec ??= aotRecipe;
        }

        if (!string.IsNullOrWhiteSpace(packageArchivePath) && aotRecipe is not null)
        {
            string dataDirForCheck = PlayerLocalAotCompiler.ResolveEffectiveDataDir(manifestDirectory, aotOptions);
            CleanRoomRecompCheckResult cleanCheck = new AotCacheManager(dataDirForCheck).ValidateCleanRoomRecompPackage(packageArchivePath);
            if (!cleanCheck.IsClean)
            {
                throw new InvalidDataException(
                    $"Clean-room .recomp validation failed for '{Path.GetFileName(packageArchivePath)}': " +
                    $"{cleanCheck.Summary} [{string.Join(", ", cleanCheck.UnexpectedGeneratedAssemblies)}]");
            }
        }

        string entrySimple = Path.GetFileNameWithoutExtension(manifest.EntryAssembly);
        bool entryExistsInArchive = !string.IsNullOrWhiteSpace(packageArchivePath) &&
            ArchiveContainsSimpleDll(packageArchivePath, entrySimple);
        string? existingDiskEntry = ResolveDiskEntryAssemblyPath(manifest.EntryAssembly, manifestDirectory);
        bool expectsGeneratedEntry = manifest.EntryAssembly.EndsWith(".Generated.dll", StringComparison.OrdinalIgnoreCase);

        LocalAotEnsureResult? aotResult = null;
        if (aotRecipe is not null &&
            (expectsGeneratedEntry ||
             (!entryExistsInArchive && existingDiskEntry is null) ||
             aotOptions?.PreloadRomDir is not null ||
             aotOptions?.DataDir is not null))
        {
            PlayerLocalAotCompiler compiler = aotCompiler ?? new PlayerLocalAotCompiler();
            ReStationModuleManifest manifestForAot = concreteManifest ?? WrapManifest(manifest, aotRecipe);
            aotResult = compiler.EnsureCompiledInCacheAsync(
                manifestForAot, aotRecipe, manifestDirectory, aotOptions, packageArchivePath).GetAwaiter().GetResult();
        }

        IReadOnlySet<string>? companionAliases = aotResult is null
            ? null
            : CreateCompanionAliases(aotResult.AssemblyPath, manifest, aotRecipe);
        Assembly assembly;
        string entryLabel;
        if (aotResult is not null && (expectsGeneratedEntry || (!entryExistsInArchive && existingDiskEntry is null)))
        {
            context = new ModuleAssemblyLoader(aotResult.AssemblyPath);
            assembly = context.LoadAssemblyBytes(aotResult.AssemblyPath);
            entryLabel = Path.GetFileName(aotResult.AssemblyPath);
            context.LoadedFromLocalAotCache = true;
        }
        else if (!string.IsNullOrWhiteSpace(packageArchivePath))
        {
            context = new ModuleAssemblyLoader(packageArchivePath, manifest.EntryAssembly, aotResult?.AssemblyPath, companionAliases);
            if (!context._archiveAssemblies!.TryGetValue(entrySimple, out byte[]? entryBytes))
                throw new FileNotFoundException($"Entry assembly '{manifest.EntryAssembly}' not found inside '{Path.GetFileName(packageArchivePath)}'.", manifest.EntryAssembly);
            using var ms = new MemoryStream(entryBytes, writable: false);
            assembly = context.LoadFromStream(ms);
            entryLabel = $"{Path.GetFileName(packageArchivePath)}!{manifest.EntryAssembly}";
        }
        else
        {
            string entry = existingDiskEntry
                ?? throw new FileNotFoundException("Module entry assembly not found.", Path.Combine(Path.GetFullPath(manifestDirectory), manifest.EntryAssembly));
            context = new ModuleAssemblyLoader(entry, aotResult?.AssemblyPath, companionAliases, isDiskEntry: true);
            assembly = context.LoadAssemblyBytes(entry);
            entryLabel = Path.GetFileName(entry);
        }

        if (!expectsGeneratedEntry &&
            context._companionCachedAssemblyPath is not null &&
            File.Exists(context._companionCachedAssemblyPath))
        {
            context.PreloadCompanionAssembly(context._companionCachedAssemblyPath);
        }

        if (aotResult is not null) context.LoadedFromLocalAotCache = true;
        context.AotCacheHit = aotResult?.CacheHit ?? false;
        context.CachedGeneratedAssemblyPath = aotResult?.AssemblyPath;
        context.CacheVersionSegment = aotResult?.CacheVersionSegment;
        context.VerifiedRomSha256Hex = aotResult?.RomSha256Hex;
        context.ResolvedExecutionTierMode = ResolveTierMode(concreteManifest, aotRecipe, aotOptions);
        context.ForceZeroRwx = aotOptions?.ForceZeroRwx ?? false;
        context.JitBringupEnabled = aotOptions?.JitBringup ?? false;

        Type type = assembly.GetType(manifest.ModuleTypeName, throwOnError: false)
            ?? throw new TypeLoadException($"Module type '{manifest.ModuleTypeName}' not found in '{entryLabel}'.");
        if (!typeof(IRecompGameModule).IsAssignableFrom(type))
            throw new InvalidCastException($"'{type.FullName}' does not implement {nameof(IRecompGameModule)}.");
        return (IRecompGameModule)(Activator.CreateInstance(type)
            ?? throw new InvalidOperationException($"Could not instantiate '{type.FullName}'."));
    }

    /// <summary>Preloads a compiled companion AOT assembly directly into this collectible load context.</summary>
    public Assembly PreloadCompanionAssembly(string assemblyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);
        string fullPath = Path.GetFullPath(assemblyPath);
        lock (_companionSync)
        {
            return _loadedCompanionAssembly ??= LoadAssemblyBytes(fullPath);
        }
    }

    private void ClearLoadedCompanion()
    {
        lock (_companionSync)
        {
            _loadedCompanionAssembly = null;
        }
    }

    /// <summary>Runs GC + finalizers until a weak reference to an unloaded loader context is reclaimed.</summary>
    public static bool WaitForUnload(WeakReference weakLoaderRef, int maxGcPasses = 12)
    {
        ArgumentNullException.ThrowIfNull(weakLoaderRef);
        for (int i = 0; weakLoaderRef.IsAlive && i < maxGcPasses; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        return !weakLoaderRef.IsAlive;
    }

    private Assembly LoadAssemblyBytes(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        using var ms = new MemoryStream(bytes, writable: false);
        return LoadFromStream(ms);
    }

    private static void ValidateEntryAssemblyPathSafety(string entryAssembly)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryAssembly);
        string dummyRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "restation-path-check"));
        SandboxEnvironment.AssertPathContained(Path.Combine(dummyRoot, entryAssembly), dummyRoot);
    }

    private static string? ResolveDiskEntryAssemblyPath(string entryAssembly, string manifestDirectory)
    {
        string root = Path.GetFullPath(manifestDirectory);
        string direct = SandboxEnvironment.AssertPathContained(Path.Combine(root, entryAssembly), root);
        if (File.Exists(direct)) return direct;
        string appNested = SandboxEnvironment.AssertPathContained(Path.Combine(root, "app", entryAssembly), root);
        return File.Exists(appNested) ? appNested : null;
    }

    private static bool ArchiveContainsSimpleDll(string archivePath, string simpleName)
    {
        using ZipArchive zip = ZipFile.OpenRead(Path.GetFullPath(archivePath));
        return zip.Entries.Any(e => e.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.GetFileNameWithoutExtension(e.Name), simpleName, StringComparison.OrdinalIgnoreCase));
    }

    private static Dictionary<string, byte[]> ReadArchiveAssemblies(string fullPkg)
    {
        var map = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        using ZipArchive zip = ZipFile.OpenRead(fullPkg);
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            if (!entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
            string simpleName = Path.GetFileNameWithoutExtension(entry.Name);
            bool isRootEntry = entry.FullName.IndexOfAny(['/', '\\']) < 0;
            if (!isRootEntry && map.ContainsKey(simpleName)) continue;
            using Stream s = entry.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            map[simpleName] = ms.ToArray();
        }
        return map;
    }

    private static string ResolveTierMode(ReStationModuleManifest? manifest, AotRecipeSpec? recipe, LocalAotLoadOptions? options)
    {
        if (!string.IsNullOrWhiteSpace(options?.ExecutionTierModeOverride))
            return ReStationExecutionTierModes.Normalize(options.ExecutionTierModeOverride);
        if (!string.IsNullOrWhiteSpace(recipe?.ExecutionTierMode))
            return ReStationExecutionTierModes.Normalize(recipe.ExecutionTierMode);
        if (!string.IsNullOrWhiteSpace(manifest?.ExecutionTierMode))
            return ReStationExecutionTierModes.Normalize(manifest.ExecutionTierMode);
        return ReStationExecutionTierModes.Default;
    }

    private static IReadOnlySet<string>? CreateCompanionAliases(
        string? companionPath,
        IReStationModuleManifest manifest,
        AotRecipeSpec? aotRecipe)
    {
        if (string.IsNullOrWhiteSpace(companionPath)) return null;
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string cachedSimple = Path.GetFileNameWithoutExtension(companionPath);
        if (!string.IsNullOrWhiteSpace(cachedSimple)) aliases.Add(cachedSimple);
        if (!string.IsNullOrWhiteSpace(manifest.ModuleId)) aliases.Add($"{manifest.ModuleId}.Generated");
        if (!string.IsNullOrWhiteSpace(aotRecipe?.GeneratedCacheAssemblyName))
        {
            string recipeSimple = Path.GetFileNameWithoutExtension(aotRecipe.GeneratedCacheAssemblyName);
            if (!string.IsNullOrWhiteSpace(recipeSimple)) aliases.Add(recipeSimple);
        }
        return aliases;
    }

    private static IReadOnlySet<string>? NormalizeCompanionAliases(string? companionPath, IReadOnlySet<string>? aliases)
    {
        if (string.IsNullOrWhiteSpace(companionPath)) return null;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string stem = Path.GetFileNameWithoutExtension(companionPath);
        if (!string.IsNullOrWhiteSpace(stem)) set.Add(stem);
        if (aliases is not null)
            foreach (string alias in aliases)
                if (!string.IsNullOrWhiteSpace(alias)) set.Add(alias.Trim());
        return set;
    }

    private static ReStationModuleManifest WrapManifest(IReStationModuleManifest manifest, AotRecipeSpec recipe) => new()
    {
        ModuleId = manifest.ModuleId,
        Title = manifest.DisplayName,
        DisplayName = manifest.DisplayName,
        PlatformId = manifest.PlatformId,
        Version = manifest.Version,
        SerialNumber = manifest.SerialNumber,
        EntryAssembly = manifest.EntryAssembly,
        DefaultBootExecutable = manifest.DefaultBootExecutable,
        ModuleTypeName = manifest.ModuleTypeName,
        AotRecipeSpec = recipe,
        ExecutionTierMode = recipe.ExecutionTierMode
    };
}
