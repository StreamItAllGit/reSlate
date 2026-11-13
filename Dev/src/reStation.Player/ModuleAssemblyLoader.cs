// @module: reStation.Player.ModuleAssemblyLoader
// @purpose: Non-collectible AssemblyLoadContext loading a .recomp module's entryAssembly while sharing engine contract assemblies for type identity.
// @depends-on: System.Reflection, System.Runtime.Loader, Foundation.Retro, reStation.Contracts
// @exports: ModuleAssemblyLoader
// @owner: devv-player-ui (T6) — public surface T1-locked.

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

    private readonly AssemblyDependencyResolver _resolver;

    public ModuleAssemblyLoader(string entryAssemblyPath)
        : base($"reStation.Module:{Path.GetFileNameWithoutExtension(entryAssemblyPath)}", isCollectible: false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryAssemblyPath);
        EntryAssemblyPath = Path.GetFullPath(entryAssemblyPath);
        if (!File.Exists(EntryAssemblyPath)) throw new FileNotFoundException("Module entry assembly not found.", EntryAssemblyPath);
        _resolver = new AssemblyDependencyResolver(EntryAssemblyPath);
    }

    public string EntryAssemblyPath { get; }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is not null && SharedAssemblyNames.Contains(assemblyName.Name)) return null;
        string? path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }

    /// <summary>
    /// Loads <see cref="IReStationModuleManifest.EntryAssembly"/> (contained under <paramref name="manifestDirectory"/>) and
    /// instantiates <see cref="IReStationModuleManifest.ModuleTypeName"/> as an <see cref="IRecompGameModule"/>.
    /// </summary>
    public static IRecompGameModule LoadGameModule(IReStationModuleManifest manifest, string manifestDirectory, out ModuleAssemblyLoader context)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestDirectory);
        string root = Path.GetFullPath(manifestDirectory);
        string entry = Foundation.Core.SandboxEnvironment.AssertPathContained(Path.Combine(root, manifest.EntryAssembly), root);
        context = new ModuleAssemblyLoader(entry);
        Assembly assembly = context.LoadFromAssemblyPath(entry);
        Type type = assembly.GetType(manifest.ModuleTypeName, throwOnError: false)
            ?? throw new TypeLoadException($"Module type '{manifest.ModuleTypeName}' not found in '{Path.GetFileName(entry)}'.");
        if (!typeof(IRecompGameModule).IsAssignableFrom(type))
            throw new InvalidCastException($"'{type.FullName}' does not implement {nameof(IRecompGameModule)}.");
        return (IRecompGameModule)(Activator.CreateInstance(type)
            ?? throw new InvalidOperationException($"Could not instantiate '{type.FullName}'."));
    }
}
