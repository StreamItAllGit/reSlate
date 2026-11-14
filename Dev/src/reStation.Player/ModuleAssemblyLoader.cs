// @module: reStation.Player.ModuleAssemblyLoader
// @purpose: Non-collectible AssemblyLoadContext loading a .recomp module's entryAssembly while sharing engine contract assemblies for type identity.
// @depends-on: System.Reflection, System.Runtime.Loader, Foundation.Retro, reStation.Contracts
// @exports: ModuleAssemblyLoader
// @owner: devv-player-ui (T6) — public surface T1-locked.

using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using Foundation.Retro;
using reStation.Contracts;

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

    public ModuleAssemblyLoader(string entryAssemblyPath)
        : base($"reStation.Module:{Path.GetFileNameWithoutExtension(entryAssemblyPath)}", isCollectible: false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryAssemblyPath);
        EntryAssemblyPath = Path.GetFullPath(entryAssemblyPath);
        if (!File.Exists(EntryAssemblyPath)) throw new FileNotFoundException("Module entry assembly not found.", EntryAssemblyPath);
        _resolver = new AssemblyDependencyResolver(EntryAssemblyPath);
    }

    public ModuleAssemblyLoader(string packageArchivePath, string entryAssemblyName)
        : base($"reStation.Module:{Path.GetFileNameWithoutExtension(packageArchivePath)}", isCollectible: false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageArchivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryAssemblyName);
        string fullPkg = Path.GetFullPath(packageArchivePath);
        if (!File.Exists(fullPkg)) throw new FileNotFoundException("Module .recomp cartridge not found.", fullPkg);
        PackageArchivePath = fullPkg;
        EntryAssemblyPath = entryAssemblyName;
        _archiveAssemblies = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        using ZipArchive zip = ZipFile.OpenRead(fullPkg);
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            if (!entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
            string simpleName = Path.GetFileNameWithoutExtension(entry.Name);
            bool isRootEntry = entry.FullName.IndexOfAny(['/', '\\']) < 0;
            if (!isRootEntry && _archiveAssemblies.ContainsKey(simpleName)) continue;
            using Stream s = entry.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            _archiveAssemblies[simpleName] = ms.ToArray();
        }
    }

    public string EntryAssemblyPath { get; }
    public string? PackageArchivePath { get; }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is not null && SharedAssemblyNames.Contains(assemblyName.Name)) return null;
        if (_archiveAssemblies is not null && assemblyName.Name is not null &&
            _archiveAssemblies.TryGetValue(assemblyName.Name, out byte[]? bytes))
        {
            using var ms = new MemoryStream(bytes, writable: false);
            return LoadFromStream(ms);
        }
        string? path = _resolver?.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        string? path = _resolver?.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }

    /// <summary>
    /// Loads <see cref="IReStationModuleManifest.EntryAssembly"/> (from a ZIP cartridge if available, otherwise from disk under
    /// <paramref name="manifestDirectory"/>) and instantiates <see cref="IReStationModuleManifest.ModuleTypeName"/>.
    /// </summary>
    public static IRecompGameModule LoadGameModule(IReStationModuleManifest manifest, string manifestDirectory, out ModuleAssemblyLoader context) =>
        LoadGameModule(manifest, manifestDirectory, (manifest as ReStationModuleManifest)?.ResolvedPackagePath, out context);

    /// <summary>Loads the module either from an explicit <paramref name="packageArchivePath"/> ZIP cartridge or from disk.</summary>
    public static IRecompGameModule LoadGameModule(
        IReStationModuleManifest manifest, string manifestDirectory, string? packageArchivePath, out ModuleAssemblyLoader context)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestDirectory);

        Assembly assembly;
        string entryLabel;
        if (!string.IsNullOrWhiteSpace(packageArchivePath))
        {
            string entrySimple = Path.GetFileNameWithoutExtension(manifest.EntryAssembly);
            context = new ModuleAssemblyLoader(packageArchivePath, manifest.EntryAssembly);
            if (!context._archiveAssemblies!.TryGetValue(entrySimple, out byte[]? entryBytes))
                throw new FileNotFoundException($"Entry assembly '{manifest.EntryAssembly}' not found inside '{Path.GetFileName(packageArchivePath)}'.", manifest.EntryAssembly);
            using var ms = new MemoryStream(entryBytes, writable: false);
            assembly = context.LoadFromStream(ms);
            entryLabel = $"{Path.GetFileName(packageArchivePath)}!{manifest.EntryAssembly}";
        }
        else
        {
            string root = Path.GetFullPath(manifestDirectory);
            string entry = Foundation.Core.SandboxEnvironment.AssertPathContained(Path.Combine(root, manifest.EntryAssembly), root);
            if (!File.Exists(entry))
            {
                string appNested = Foundation.Core.SandboxEnvironment.AssertPathContained(Path.Combine(root, "app", manifest.EntryAssembly), root);
                if (File.Exists(appNested)) entry = appNested;
            }
            context = new ModuleAssemblyLoader(entry);
            assembly = context.LoadFromAssemblyPath(entry);
            entryLabel = Path.GetFileName(entry);
        }

        Type type = assembly.GetType(manifest.ModuleTypeName, throwOnError: false)
            ?? throw new TypeLoadException($"Module type '{manifest.ModuleTypeName}' not found in '{entryLabel}'.");
        if (!typeof(IRecompGameModule).IsAssignableFrom(type))
            throw new InvalidCastException($"'{type.FullName}' does not implement {nameof(IRecompGameModule)}.");
        return (IRecompGameModule)(Activator.CreateInstance(type)
            ?? throw new InvalidOperationException($"Could not instantiate '{type.FullName}'."));
    }
}
