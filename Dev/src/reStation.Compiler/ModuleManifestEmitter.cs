// @module: reStation.Compiler.ModuleManifestEmitter
// @purpose: Emits top-level IRecompGameModule entrypoint, zero-PII net8.0 .Module.csproj, Standard 18 module.recomp.json, and optional DLL build.
// @depends-on: System.Diagnostics, System.Text, Foundation.Retro, reStation.Contracts, reStation.Compiler.DiscExeLoader, reStation.Compiler.ExeScanHelpers
// @exports: ModuleManifestEmitter
// @owner: devv-compiler (T5)

using System.Diagnostics;
using System.Security;
using System.Text;
using reStation.Contracts;

namespace reStation.Compiler;

/// <summary>Emits the module's top-level <c>IRecompGameModule</c>, <c>.Module.csproj</c>, and <c>module.recomp.json</c>.</summary>
public static class ModuleManifestEmitter
{
    /// <summary>Writes the game module wrapper, project file, and manifest into <paramref name="outDir"/>.</summary>
    internal static List<string> EmitAll(
        string outDir,
        RecompCompilerOptions options,
        string rootNamespace,
        string pascalId,
        IReadOnlyList<GeneratedExeSpec> specs,
        out string manifestPath,
        out string csprojPath)
    {
        var emitted = new List<string>(4);
        string assemblyName = $"{options.ModuleId}.recomp";
        string moduleClassName = $"{pascalId}GameModule";
        string moduleTypeName = $"{rootNamespace}.{moduleClassName}";
        string bootExe = specs.Count > 0 ? specs[0].RelativePath : string.Empty;

        string entrySourcePath = Path.Combine(outDir, $"{moduleClassName}.g.cs");
        EmitGameModuleSource(entrySourcePath, options, rootNamespace, moduleClassName, bootExe, assemblyName, moduleTypeName, specs);
        emitted.Add(entrySourcePath);

        csprojPath = Path.Combine(outDir, $"{options.ModuleId}.Module.csproj");
        EmitProjectFile(csprojPath, assemblyName, rootNamespace);
        emitted.Add(csprojPath);

        var manifest = new ReStationModuleManifest
        {
            SchemaVersion = 1,
            ModuleId = options.ModuleId,
            Title = options.Title,
            DisplayName = options.Title,
            PlatformId = ReStationEngineInfo.PlatformId,
            Version = string.IsNullOrWhiteSpace(options.Version) ? "0.1.5" : options.Version.Trim(),
            EngineId = ReStationEngineInfo.EngineId,
            EngineVersion = ReStationEngineInfo.Version,
            CompatibleEngineTrack = ReStationEngineInfo.CompatibleEngineTrack,
            PreloadRomDir = ReStationEngineInfo.DefaultPreloadRomDir,
            SerialNumber = options.SerialNumber,
            EntryAssembly = assemblyName + ".dll",
            DefaultBootExecutable = bootExe,
            ModuleTypeName = moduleTypeName
        };

        manifestPath = Path.Combine(outDir, "module.recomp.json");
        manifest.SaveToFile(manifestPath);
        emitted.Add(manifestPath);
        return emitted;
    }

    /// <summary>Compiles <paramref name="csprojPath"/> into <paramref name="outDir"/> using <c>dotnet build</c>.</summary>
    internal static (bool Success, string? DllPath, string Diagnostic) CompileModuleAssembly(
        string csprojPath, string outDir, string assemblyName, CancellationToken ct)
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"restation-modbuild-{Guid.NewGuid():N}");
        string tempOut = Path.Combine(tempRoot, "bin");
        string tempObj = Path.Combine(tempRoot, "obj") + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(tempOut);
        Directory.CreateDirectory(tempObj);

        try
        {
            string libDir = Path.GetDirectoryName(typeof(IReStationCpu).Assembly.Location) ?? AppContext.BaseDirectory;
            var psi = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = outDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("build");
            psi.ArgumentList.Add(csprojPath);
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("Release");
            psi.ArgumentList.Add("--nologo");
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add(tempOut);
            psi.ArgumentList.Add($"-p:BaseIntermediateOutputPath={tempObj}");
            psi.ArgumentList.Add($"-p:ReStationLibDir={libDir}");

            using Process proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to invoke 'dotnet build'.");
            Task<string> outTask = proc.StandardOutput.ReadToEndAsync(ct);
            Task<string> errTask = proc.StandardError.ReadToEndAsync(ct);
            proc.WaitForExitAsync(ct).GetAwaiter().GetResult();
            string stdout = outTask.GetAwaiter().GetResult();
            string stderr = errTask.GetAwaiter().GetResult();
            if (proc.ExitCode != 0)
                return (false, null, $"Module build failed (exit {proc.ExitCode}): {stderr} {stdout}".Trim());

            string builtDll = Path.Combine(tempOut, $"{assemblyName}.dll");
            if (!File.Exists(builtDll))
                return (false, null, $"Module build succeeded but output '{assemblyName}.dll' was not found.");

            string destDll = Path.Combine(outDir, $"{assemblyName}.dll");
            File.Copy(builtDll, destDll, overwrite: true);
            string builtDeps = Path.Combine(tempOut, $"{assemblyName}.deps.json");
            if (File.Exists(builtDeps)) File.Copy(builtDeps, Path.Combine(outDir, $"{assemblyName}.deps.json"), overwrite: true);
            return (true, destDll, $"Compiled '{Path.GetFileName(destDll)}'.");
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                try { Directory.Delete(tempRoot, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"[WARN] [COMPILER] Failed to clean temp dir '{tempRoot}': {ex.Message}");
                }
            }
        }
    }

    private static void EmitGameModuleSource(
        string path, RecompCompilerOptions options, string rootNamespace, string className,
        string bootExe, string assemblyName, string moduleTypeName, IReadOnlyList<GeneratedExeSpec> specs)
    {
        string version = string.IsNullOrWhiteSpace(options.Version) ? "0.1.5" : options.Version.Trim();
        var sb = new StringBuilder();
        sb.AppendLine($"// <auto-generated /> Top-level IRecompGameModule entrypoint for {Escape(options.Title)}");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using Foundation.Retro;");
        sb.AppendLine("using reStation.Contracts;");
        sb.AppendLine("using reStation.Core.Cpu;");
        sb.AppendLine("using reStation.Core.Saves;");
        sb.AppendLine();
        sb.AppendLine($"namespace {rootNamespace};");
        sb.AppendLine();
        sb.AppendLine($"public sealed class {className} : IRecompGameModule");
        sb.AppendLine("{");
        sb.AppendLine("    private IReStationHost? _host;");
        sb.AppendLine("    private readonly ReStationModuleManifest _fallbackManifest = new()");
        sb.AppendLine("    {");
        sb.AppendLine($"        ModuleId = \"{Escape(options.ModuleId)}\",");
        sb.AppendLine($"        Title = \"{Escape(options.Title)}\",");
        sb.AppendLine($"        DisplayName = \"{Escape(options.Title)}\",");
        sb.AppendLine($"        Version = \"{Escape(version)}\",");
        sb.AppendLine($"        SerialNumber = \"{Escape(options.SerialNumber)}\",");
        sb.AppendLine($"        EntryAssembly = \"{Escape(assemblyName)}.dll\",");
        sb.AppendLine($"        DefaultBootExecutable = \"{Escape(bootExe)}\",");
        sb.AppendLine($"        ModuleTypeName = \"{Escape(moduleTypeName)}\"");
        sb.AppendLine("    };");
        sb.AppendLine();
        sb.AppendLine("    public IRecompModuleManifest Manifest => _host?.ModuleManifest ?? _fallbackManifest;");
        sb.AppendLine();
        sb.AppendLine("    public IReadOnlyList<RecompPatchDescriptor> TimingPatches { get; } = new[]");
        sb.AppendLine("    {");
        sb.AppendLine("        new RecompPatchDescriptor(\"original-60hz\", \"Original 60 Hz\", 60.0, false, true, \"Hardware-paced PS1 VBlank cadence\")");
        sb.AppendLine("    };");
        sb.AppendLine();
        sb.AppendLine("    public Task InitializeAsync(IRecompHost host, CancellationToken ct = default)");
        sb.AppendLine("    {");
        sb.AppendLine("        ArgumentNullException.ThrowIfNull(host);");
        sb.AppendLine("        ct.ThrowIfCancellationRequested();");
        sb.AppendLine("        _host = host as IReStationHost ?? throw new InvalidOperationException(\"Host must implement IReStationHost.\");");
        foreach (GeneratedExeSpec s in specs)
            sb.AppendLine($"        _host.RegisterModule(new {rootNamespace}.{s.FolderName}.{s.ClassName}());");
        if (!string.IsNullOrWhiteSpace(bootExe))
        {
            sb.AppendLine($"        if (!_host.LoadExecutable(\"{Escape(bootExe)}\", 0))");
            sb.AppendLine($"            throw new InvalidOperationException(\"Failed to load boot executable '{Escape(bootExe)}'.\");");
        }
        sb.AppendLine("        return Task.CompletedTask;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    public Task StepFrameAsync(CancellationToken ct = default)");
        sb.AppendLine("    {");
        sb.AppendLine("        ct.ThrowIfCancellationRequested();");
        sb.AppendLine("        if (_host is null) throw new InvalidOperationException(\"Module is not initialized.\");");
        sb.AppendLine("        _host.RunFrame();");
        sb.AppendLine("        return Task.CompletedTask;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    public Task LoadSaveStateAsync(string saveStatePath, CancellationToken ct = default)");
        sb.AppendLine("    {");
        sb.AppendLine("        ArgumentException.ThrowIfNullOrWhiteSpace(saveStatePath);");
        sb.AppendLine("        ct.ThrowIfCancellationRequested();");
        sb.AppendLine("        if (_host is null) throw new InvalidOperationException(\"Module is not initialized.\");");
        sb.AppendLine("        if (_host.Cpu is not PsxCpu psxCpu) throw new NotSupportedException(\"Save-state loading requires PsxCpu.\");");
        sb.AppendLine("        if (!QuickSaveManager.LoadFromFile(psxCpu, saveStatePath).Success)");
        sb.AppendLine("            throw new FileNotFoundException(\"Save-state file not found.\", saveStatePath);");
        sb.AppendLine("        return Task.CompletedTask;");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    private static void EmitProjectFile(string csprojPath, string assemblyName, string rootNamespace)
    {
        string safeAsm = SecurityElement.Escape(assemblyName) ?? assemblyName;
        string safeNs = SecurityElement.Escape(rootNamespace) ?? rootNamespace;
        string xml = $"""
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <LangVersion>12.0</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <Deterministic>true</Deterministic>
    <AssemblyName>{safeAsm}</AssemblyName>
    <RootNamespace>{safeNs}</RootNamespace>
    <ReStationLibDir Condition="'$(ReStationLibDir)' == ''">$(RESTATION_LIB_DIR)</ReStationLibDir>
    <ReStationLibDir Condition="'$(ReStationLibDir)' == ''">$(RESTATION_SHARED_DIR)</ReStationLibDir>
    <ReStationLibDir Condition="'$(ReStationLibDir)' == '' and Exists('$(MSBuildThisFileDirectory)lib\reStation.Contracts.dll')">$(MSBuildThisFileDirectory)lib\</ReStationLibDir>
  </PropertyGroup>

  <PropertyGroup Condition="'$(Configuration)' == 'Release'">
    <PathMap>$([System.IO.Path]::GetFullPath('$(MSBuildThisFileDirectory)'))\=mod\,$(USERPROFILE)\=usr\</PathMap>
    <DebugType>none</DebugType>
    <DebugSymbols>false</DebugSymbols>
  </PropertyGroup>

  <ItemGroup>
    <Reference Include="Foundation.Core">
      <HintPath Condition="'$(ReStationLibDir)' != ''">$([MSBuild]::EnsureTrailingSlash('$(ReStationLibDir)'))Foundation.Core.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="Foundation.Retro">
      <HintPath Condition="'$(ReStationLibDir)' != ''">$([MSBuild]::EnsureTrailingSlash('$(ReStationLibDir)'))Foundation.Retro.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="reStation.Contracts">
      <HintPath Condition="'$(ReStationLibDir)' != ''">$([MSBuild]::EnsureTrailingSlash('$(ReStationLibDir)'))reStation.Contracts.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="reStation.Core">
      <HintPath Condition="'$(ReStationLibDir)' != ''">$([MSBuild]::EnsureTrailingSlash('$(ReStationLibDir)'))reStation.Core.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>

</Project>
""";
        File.WriteAllText(csprojPath, xml, new UTF8Encoding(false));
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
