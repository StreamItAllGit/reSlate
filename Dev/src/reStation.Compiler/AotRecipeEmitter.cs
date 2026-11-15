// @module: reStation.Compiler.AotRecipeEmitter
// @purpose: Clean-room aot-recipe.json generator and clean .recomp archive packer verified by reEngine.Core.Aot.AotCacheManager.
// @depends-on: reEngine.Core.Aot.AotCacheManager, reStation.Contracts.AotRecipeSpec, reStation.Compiler.DiscExeLoader
// @exports: AotRecipeEmitter

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using reEngine.Core.Aot;
using reStation.Contracts;

namespace reStation.Compiler;

/// <summary>Builds and serializes clean-room <c>aot-recipe.json</c> descriptors and zero-generated-code <c>.recomp</c> packages.</summary>
public static class AotRecipeEmitter
{
    /// <summary>Computes canonical ROM/EXE SHA-256 matching <c>PlayerMediaResolver</c>.</summary>
    public static string ComputeCanonicalRomSha256Hex(IReadOnlyList<DiscoveredExe> exes)
    {
        ArgumentNullException.ThrowIfNull(exes);
        if (exes.Count == 0)
            throw new InvalidDataException("At least one executable is required to compute canonical ROM SHA-256.");

        if (exes.Count == 1)
            return Convert.ToHexString(SHA256.HashData(exes[0].Bytes)).ToLowerInvariant();

        var concat = new StringBuilder();
        foreach (DiscoveredExe exe in exes)
            concat.Append(Convert.ToHexString(SHA256.HashData(exe.Bytes)).ToLowerInvariant());
        return Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(concat.ToString()))).ToLowerInvariant();
    }

    /// <summary>Builds a clean-room <see cref="AotRecipeSpec"/> from discovered executables and lifted entrypoint metadata.</summary>
    public static AotRecipeSpec BuildRecipe(
        RecompCompilerOptions options,
        IReadOnlyList<DiscoveredExe> exes,
        IReadOnlyList<GeneratedExeSpec> specs,
        string generatedCacheAsmName)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(exes);
        ArgumentNullException.ThrowIfNull(specs);
        if (exes.Count == 0)
            throw new InvalidDataException("Cannot construct clean-room AOT recipe without discovered executables.");

        var exeSpecs = new List<AotRecipeExecutableSpec>(exes.Count);
        foreach (DiscoveredExe exe in exes)
        {
            string sha = Convert.ToHexString(SHA256.HashData(exe.Bytes)).ToLowerInvariant();
            exeSpecs.Add(new AotRecipeExecutableSpec
            {
                BootExePath = exe.RelativePath,
                Sha256 = sha,
                LoadAddress = exe.Header.TextAddr,
                EntryAddress = exe.Header.Pc0,
                GlobalPointer = exe.Header.Gp0,
                TextSizeBytes = checked((int)exe.Header.TextSize),
                SleighLanguageId = "psx_r3000a"
            });
        }

        uint[] entryAddresses = specs
            .SelectMany(s => s.Functions.Select(f => f.Addr))
            .Distinct()
            .OrderBy(a => a)
            .ToArray();

        var cleanArtifacts = new List<string> { "module.recomp.json", ReStationEngineInfo.DefaultAotRecipePath };
        if (!string.IsNullOrWhiteSpace(options.ThinShellAssemblyName))
            cleanArtifacts.Add(options.ThinShellAssemblyName);

        var recipe = new AotRecipeSpec
        {
            SchemaVersion = 1,
            PlatformId = ReStationEngineInfo.PlatformId,
            ModuleId = options.ModuleId,
            DiscSerial = options.SerialNumber,
            ExecutionTierMode = ReStationExecutionTierModes.Normalize(options.ExecutionTierMode),
            SleighSpecId = "psx_r3000a",
            TargetArchitecture = "R3000A",
            LittleEndian = true,
            PrimaryBootExecutable = exes[0].RelativePath,
            Executables = exeSpecs,
            EntryAddresses = entryAddresses,
            CleanRoomArtifacts = cleanArtifacts,
            GeneratedCacheAssemblyName = generatedCacheAsmName
        };
        recipe.Validate(ReStationEngineInfo.DefaultAotRecipePath);
        return recipe;
    }

    /// <summary>Writes <c>aot-recipe.json</c> cleanly into <paramref name="outDir"/>.</summary>
    public static string WriteRecipeFile(string outDir, AotRecipeSpec recipe)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outDir);
        ArgumentNullException.ThrowIfNull(recipe);
        recipe.Validate(ReStationEngineInfo.DefaultAotRecipePath);
        string path = Path.Combine(outDir, ReStationEngineInfo.DefaultAotRecipePath);
        string json = JsonSerializer.Serialize(recipe, ReStationModuleManifest.JsonOptions);
        File.WriteAllText(path, json, new UTF8Encoding(false));
        return path;
    }

    /// <summary>
    /// Creates a clean-room <c>.recomp</c> archive containing strictly <c>module.recomp.json</c>, <c>aot-recipe.json</c>,
    /// and an optional thin hand-written shell DLL — never <c>*.g.cs</c> or <c>*.Generated.dll</c>.
    /// </summary>
    public static CleanRoomRecompCheckResult EmitCleanRoomArchive(
        string dataRootForValidation,
        string archivePath,
        string manifestPath,
        string recipePath,
        string? thinShellDllPath = null,
        string? thinShellDepsPath = null)
    {
        string fullArchive = Path.GetFullPath(archivePath);
        string? parentDir = Path.GetDirectoryName(fullArchive);
        if (!string.IsNullOrWhiteSpace(parentDir))
            Directory.CreateDirectory(parentDir);

        string tmpArchive = $"{fullArchive}.tmp.{Guid.NewGuid():N}";
        try
        {
            using (ZipArchive zip = ZipFile.Open(tmpArchive, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(manifestPath, "module.recomp.json", CompressionLevel.Optimal);
                zip.CreateEntryFromFile(recipePath, ReStationEngineInfo.DefaultAotRecipePath, CompressionLevel.Optimal);

                if (!string.IsNullOrWhiteSpace(thinShellDllPath) && File.Exists(thinShellDllPath))
                {
                    string dllName = Path.GetFileName(thinShellDllPath);
                    if (dllName.EndsWith(".Generated.dll", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"Refusing to pack generated assembly '{dllName}' into clean-room .recomp.");
                    zip.CreateEntryFromFile(thinShellDllPath, dllName, CompressionLevel.Optimal);
                }

                if (!string.IsNullOrWhiteSpace(thinShellDepsPath) && File.Exists(thinShellDepsPath))
                {
                    string depsName = Path.GetFileName(thinShellDepsPath);
                    if (!depsName.EndsWith(".Generated.deps.json", StringComparison.OrdinalIgnoreCase))
                        zip.CreateEntryFromFile(thinShellDepsPath, depsName, CompressionLevel.Optimal);
                }
            }

            CleanRoomRecompCheckResult check = new AotCacheManager(dataRootForValidation).ValidateCleanRoomRecompPackage(tmpArchive);
            if (!check.IsClean)
            {
                throw new InvalidDataException(
                    $"Clean-room validation failed for '{Path.GetFileName(fullArchive)}': " +
                    $"{check.Summary} [{string.Join(", ", check.UnexpectedGeneratedAssemblies)}]");
            }

            File.Move(tmpArchive, fullArchive, overwrite: true);
            return check;
        }
        finally
        {
            if (File.Exists(tmpArchive))
            {
                try { File.Delete(tmpArchive); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
