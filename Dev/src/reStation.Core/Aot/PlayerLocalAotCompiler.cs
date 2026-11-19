// @module: reStation.Core.Aot.PlayerLocalAotCompiler
// @purpose: Clean-room player-local AOT compiler orchestrating AotCacheManager, media verification, and CLI compilation.
// @depends-on: Foundation.Retro.AotCacheLayoutHelper, reEngine.Core.Aot.AotCacheManager, reStation.Contracts.AotRecipeSpec
// @exports: PlayerLocalAotCompiler, LocalAotCompileRequest, LocalAotEnsureResult

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Foundation.Retro;
using reEngine.Core.Aot;
using reStation.Contracts;

namespace reStation.Core.Aot;

/// <summary>Result of resolving or locally compiling <c>&lt;moduleId&gt;.Generated.dll</c> inside <c>&lt;dataDir&gt;\cache\</c>.</summary>
public sealed record LocalAotEnsureResult(
    string AssemblyPath,
    string CacheVersionSegment,
    string RomSha256Hex,
    bool CacheHit,
    CleanRoomRecompCheckResult? CleanRoomCheck);

/// <summary>Parameters passed to a compiler invocation or test delegate on an AOT cache miss.</summary>
public sealed record LocalAotCompileRequest(
    ReStationModuleManifest Manifest,
    AotRecipeSpec Recipe,
    ResolvedPlayerMedia Media,
    AotCacheKey CacheKey,
    string StagingOutputDir);

/// <summary>Compiles clean-room <c>.recomp</c> + verified <c>data/rom/</c> inputs locally into <c>&lt;dataDir&gt;\cache\</c>.</summary>
public sealed class PlayerLocalAotCompiler
{
    private readonly string? _explicitCompilerPath;
    private readonly Func<LocalAotCompileRequest, CancellationToken, Task<string>>? _customCompilerDelegate;

    public PlayerLocalAotCompiler(
        string? compilerPath = null,
        Func<LocalAotCompileRequest, CancellationToken, Task<string>>? customCompilerDelegate = null)
    {
        _explicitCompilerPath = compilerPath;
        _customCompilerDelegate = customCompilerDelegate;
    }

    /// <summary>Resolves the canonical <c>data</c> root directory for cache and bringup storage.</summary>
    public static string ResolveEffectiveDataDir(string manifestDirectory, LocalAotLoadOptions? options = null)
    {
        if (!string.IsNullOrWhiteSpace(options?.DataDir))
            return Path.GetFullPath(options.DataDir);

        string root = Path.GetFullPath(manifestDirectory);
        string? leaf = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.Equals(leaf, "app", StringComparison.OrdinalIgnoreCase))
        {
            string? parent = Path.GetDirectoryName(root);
            if (!string.IsNullOrWhiteSpace(parent))
                return Path.Combine(parent, "data");
        }
        return Path.Combine(root, "data");
    }

    /// <summary>Validates that a clean-room <c>.recomp</c> directory or ZIP archive omits pre-lifted DLLs and generated C#.</summary>
    public CleanRoomRecompCheckResult ValidateCleanRoomPackage(string dataDir, string packageOrDirectoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageOrDirectoryPath);
        return new AotCacheManager(dataDir).ValidateCleanRoomRecompPackage(packageOrDirectoryPath);
    }

    /// <summary>Returns a cached <c>.Generated.dll</c> or compiles one locally into <c>&lt;dataDir&gt;\cache\</c>.</summary>
    public async Task<LocalAotEnsureResult> EnsureCompiledInCacheAsync(
        ReStationModuleManifest manifest,
        AotRecipeSpec recipe,
        string manifestDirectory,
        LocalAotLoadOptions? options = null,
        string? packageArchivePath = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestDirectory);
        ct.ThrowIfCancellationRequested();

        string dataDir = ResolveEffectiveDataDir(manifestDirectory, options);
        var cacheManager = new AotCacheManager(dataDir);

        CleanRoomRecompCheckResult? cleanCheck = null;
        if (!string.IsNullOrWhiteSpace(packageArchivePath))
        {
            cleanCheck = cacheManager.ValidateCleanRoomRecompPackage(packageArchivePath);
            if (!cleanCheck.IsClean)
            {
                throw new InvalidDataException(
                    $"Clean-room .recomp validation failed for '{Path.GetFileName(packageArchivePath)}': " +
                    $"{cleanCheck.Summary} [{string.Join(", ", cleanCheck.UnexpectedGeneratedAssemblies)}]");
            }
        }

        ResolvedPlayerMedia media = PlayerMediaResolver.ResolveAndVerifyMedia(recipe, manifestDirectory, options);
        var cacheKey = new AotCacheKey(
            manifest.ModuleId,
            media.RomSha256Hex,
            ReStationEngineInfo.EngineId,
            ReStationEngineInfo.Version,
            manifest.Version);

        AotCacheEntry? cached = await cacheManager.TryGetEntryAsync(cacheKey, ct).ConfigureAwait(false);
        if (cached is not null)
        {
            return new LocalAotEnsureResult(
                cached.GeneratedAssemblyPath,
                cacheKey.ToCacheVersionSegment(),
                media.RomSha256Hex,
                CacheHit: true,
                cleanCheck);
        }

        string stagingDir = Path.Combine(Path.GetTempPath(), $"reslate-local-aot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDir);
        try
        {
            var req = new LocalAotCompileRequest(manifest, recipe, media, cacheKey, stagingDir);
            string builtDllPath = _customCompilerDelegate is not null
                ? await _customCompilerDelegate(req, ct).ConfigureAwait(false)
                : await CompileWithCliAsync(req, ct).ConfigureAwait(false);

            if (!File.Exists(builtDllPath))
                throw new FileNotFoundException($"Local AOT compiler did not emit '{builtDllPath}'.", builtDllPath);

            AotCompileResult stored = await cacheManager.StoreCompiledAssemblyAsync(cacheKey, builtDllPath, ct).ConfigureAwait(false);
            if (!stored.Success || stored.Entry is null || string.IsNullOrWhiteSpace(stored.Entry.GeneratedAssemblyPath))
            {
                throw new InvalidOperationException(
                    $"Failed to store locally compiled AOT assembly in cache: {stored.ErrorMessage ?? "unknown cache storage failure"}");
            }

            string builtDeps = Path.ChangeExtension(builtDllPath, ".deps.json");
            if (File.Exists(builtDeps))
            {
                string destDeps = Path.ChangeExtension(stored.Entry.GeneratedAssemblyPath, ".deps.json");
                File.Copy(builtDeps, destDeps, overwrite: true);
            }

            return new LocalAotEnsureResult(
                stored.Entry.GeneratedAssemblyPath,
                cacheKey.ToCacheVersionSegment(),
                media.RomSha256Hex,
                CacheHit: false,
                cleanCheck);
        }
        finally
        {
            if (Directory.Exists(stagingDir))
            {
                try { Directory.Delete(stagingDir, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private static readonly string[] RuntimeCompilerProbeNames =
        ["reSlate.Compiler.exe", "reSlate.Compiler.dll", "reStation.Compiler.exe", "reStation.Compiler.dll"];
    private static readonly string[] BinCompilerProbeNames =
        ["reSlate.Compiler.dll", "reSlate.Compiler.exe", "reStation.Compiler.dll", "reStation.Compiler.exe"];

    /// <summary>Locates <c>reSlate.Compiler</c> (with <c>reStation.Compiler</c> fallback) relative to runtime directories.</summary>
    public static string? ResolveCompilerToolPath(string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return File.Exists(explicitPath) ? Path.GetFullPath(explicitPath) : null;

        string? asmDir = Path.GetDirectoryName(typeof(PlayerLocalAotCompiler).Assembly.Location);
        if (ResolveCompilerToolPathInDirs([AppContext.BaseDirectory, asmDir], RuntimeCompilerProbeNames) is { } found)
            return found;

        var cursor = new DirectoryInfo(AppContext.BaseDirectory);
        for (int depth = 0; depth < 6 && cursor is not null; depth++, cursor = cursor.Parent)
        {
            foreach (string cfg in new[] { "Release", "Debug" })
            {
                string[] binDirs =
                [
                    Path.Combine(cursor.FullName, "Dev", "src", "reStation.Compiler", "bin", cfg, "net8.0"),
                    Path.Combine(cursor.FullName, "reStation.Compiler", "bin", cfg, "net8.0")
                ];
                if (ResolveCompilerToolPathInDirs(binDirs, BinCompilerProbeNames) is { } binFound)
                    return binFound;
            }
        }
        return null;
    }

    internal static string? ResolveCompilerToolPathInDirs(IEnumerable<string?> directories, string[]? probeNames = null)
    {
        string[] names = probeNames ?? RuntimeCompilerProbeNames;
        foreach (string? dir in directories)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            foreach (string name in names)
            {
                string candidate = Path.Combine(dir, name);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    private async Task<string> CompileWithCliAsync(LocalAotCompileRequest req, CancellationToken ct)
    {
        string compilerPath = ResolveCompilerToolPath(_explicitCompilerPath)
            ?? throw new FileNotFoundException("Unable to locate 'reSlate.Compiler' (or 'reStation.Compiler') for local AOT compilation.");

        string recipeFile = Path.Combine(req.StagingOutputDir, ReStationEngineInfo.DefaultAotRecipePath);
        File.WriteAllText(recipeFile, JsonSerializer.Serialize(req.Recipe, ReStationModuleManifest.JsonOptions), new UTF8Encoding(false));

        bool isExe = compilerPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        var psi = new ProcessStartInfo(isExe ? compilerPath : "dotnet")
        {
            WorkingDirectory = req.StagingOutputDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (!isExe)
        {
            psi.ArgumentList.Add("exec");
            psi.ArgumentList.Add(compilerPath);
        }

        string mediaFlag = req.Media.Kind switch
        {
            MediaInputKind.ExeFile => "--exe-path",
            MediaInputKind.GameDataDir => "--gamedata-dir",
            _ => "--disc"
        };
        psi.ArgumentList.Add(mediaFlag);
        psi.ArgumentList.Add(req.Media.InputPath);
        psi.ArgumentList.Add("--out-dir");
        psi.ArgumentList.Add(req.StagingOutputDir);
        psi.ArgumentList.Add("--module-id");
        psi.ArgumentList.Add(req.Manifest.ModuleId);
        psi.ArgumentList.Add("--serial");
        psi.ArgumentList.Add(req.Manifest.SerialNumber);
        psi.ArgumentList.Add("--title");
        psi.ArgumentList.Add(req.Manifest.Title);
        psi.ArgumentList.Add("--version");
        psi.ArgumentList.Add(req.Manifest.Version);
        psi.ArgumentList.Add("--aot-recipe");
        psi.ArgumentList.Add(recipeFile);
        psi.ArgumentList.Add("--execution-tier");
        psi.ArgumentList.Add(ReStationExecutionTierModes.Normalize(req.Recipe.ExecutionTierMode));
        psi.ArgumentList.Add("--compile-generated-dll");

        using Process proc = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start '{Path.GetFileName(compilerPath)}'.");
        Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        Task<string> stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"Local AOT compilation failed (exit {proc.ExitCode}): {stderr} {stdout}".Trim());

        string expectedName = !string.IsNullOrWhiteSpace(req.Recipe.GeneratedCacheAssemblyName)
            ? req.Recipe.GeneratedCacheAssemblyName
            : $"{req.Manifest.ModuleId}.Generated.dll";
        string expectedPath = Path.Combine(req.StagingOutputDir, expectedName);
        return File.Exists(expectedPath)
            ? expectedPath
            : Path.Combine(req.StagingOutputDir, $"{req.Manifest.ModuleId}.Generated.dll");
    }
}
