// @module: reStation.Tests.Wave3Patch022RegressionTests
// @purpose: Wave 3.2 v0.2.2 regression tests for DEFECT-04 (companion alias ALC, recipe ExeId ordering, container#slice overlay lifting) and DEFECT-05 (numpad/nav labels & wide Keybinds grid).
// @depends-on: System.Buffers.Binary, System.Diagnostics, System.IO.Compression, System.Reflection, System.Runtime.CompilerServices, System.Runtime.Loader, System.Security.Cryptography, System.Text.Json, Foundation.Core, Foundation.Retro, reStation.Compiler, reStation.Contracts, reStation.Core, reStation.Player, Xunit
// @exports: Wave3Patch022RegressionTests
// @owner: devv-tests (Wave 3.2)

using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using Foundation.Core;
using Foundation.Retro;
using reStation.Compiler;
using reStation.Contracts;
using reStation.Core;
using reStation.Core.Aot;
using reStation.Core.Cpu;
using reStation.Core.Gpu;
using reStation.Core.Input;
using reStation.Player;

namespace reStation.Tests;

public sealed class Wave3Patch022RegressionTests : IDisposable
{
    private readonly string _tempRoot;

    public Wave3Patch022RegressionTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"reStation-wave3-022-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        TestTempCleanup.TryDeleteDirectory(_tempRoot, nameof(Wave3Patch022RegressionTests));
    }

    [Fact]
    public void StreamLoadedRecomp_ResolvesCompanionAliasAndAttachesRecipeSpec_WithoutUnloadLeaks()
    {
        string romDir = Path.Combine(_tempRoot, "t1-rom");
        string modDir = Path.Combine(_tempRoot, "t1-mod");
        string dataDir = Path.Combine(_tempRoot, "t1-data");
        Directory.CreateDirectory(romDir);
        Directory.CreateDirectory(modDir);

        byte[] exeBytes = BuildPsxExeBytes(0x80010000U, 0x24020007U, 0x03E00008U, 0x00000000U);
        File.WriteAllBytes(Path.Combine(romDir, "SLUS_888.01"), exeBytes);
        string exeSha = Convert.ToHexString(SHA256.HashData(exeBytes)).ToLowerInvariant();

        var recipe = new AotRecipeSpec
        {
            ModuleId = "aotmod-thin",
            DiscSerial = "SLUS-88801",
            PrimaryBootExecutable = "SLUS_888.01",
            GeneratedCacheAssemblyName = "custom-alias.Generated.dll",
            EntryAddresses = [0x80010000U],
            Executables =
            [
                new AotRecipeExecutableSpec { BootExePath = "SLUS_888.01", Sha256 = exeSha, LoadAddress = 0x80010000U, EntryAddress = 0x80010000U, TextSizeBytes = 12 }
            ]
        };

        (string thinDll, string thinDeps) = BuildSyntheticThinShellAssembly(Path.Combine(_tempRoot, "t1-shell"), "thintest.recomp", "aotmod-thin", "custom-alias.Generated");
        string pkgPath = Path.Combine(modDir, "thintest.recomp");
        string manifestJson = """{"schemaVersion":1,"moduleId":"aotmod-thin","title":"Thin Shell Test","displayName":"Thin Shell Test","platformId":"ps1","version":"0.2.2","engineId":"reStation","engineVersion":"0.2.2","compatibleEngineTrack":"0.2.x","executionTierMode":"aotWithJitFallback","aotRecipe":"aot-recipe.json","serialNumber":"SLUS-88801","entryAssembly":"thintest.recomp.dll","defaultBootExecutable":"SLUS_888.01","moduleTypeName":"ThinTest.ThinShellGameModule"}""";
        using (ZipArchive zip = ZipFile.Open(pkgPath, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(zip.CreateEntry("module.recomp.json").Open())) w.Write(manifestJson);
            using (var w = new StreamWriter(zip.CreateEntry("aot-recipe.json").Open())) w.Write(JsonSerializer.Serialize(recipe, ReStationModuleManifest.JsonOptions));
            zip.CreateEntryFromFile(thinDll, "thintest.recomp.dll");
            zip.CreateEntryFromFile(thinDeps, "thintest.recomp.deps.json");
        }

        ReStationModuleManifest manifest = ReStationModuleManifest.LoadFromFile(pkgPath);
        WeakReference weakAlc = ExerciseStreamThinShell(manifest, modDir, pkgPath, dataDir, romDir);
        Assert.NotNull(manifest.AotRecipeSpec);
        Assert.Equal("custom-alias.Generated.dll", manifest.AotRecipeSpec!.GeneratedCacheAssemblyName);
        Assert.True(ModuleAssemblyLoader.WaitForUnload(weakAlc), "Collectible ModuleAssemblyLoader should unload with zero reference leaks.");
    }

    [Fact]
    public void DiscOverlaySliceResolver_OrdersByRecipeExeId_AndLiftsContainerSliceAsIOverlayModule()
    {
        string romDir = Path.Combine(_tempRoot, "t2-rom");
        string outDir = Path.Combine(_tempRoot, "t2-out");
        Directory.CreateDirectory(romDir);

        byte[] zBoot = BuildPsxExeBytes(0x80010000U, 0x24020001U, 0x03E00008U, 0x00000000U);
        byte[] bOvl = BuildPsxExeBytes(0x80020000U, 0x24020002U, 0x03E00008U, 0x00000000U);
        byte[] aLast = BuildPsxExeBytes(0x80030000U, 0x24020003U, 0x03E00008U, 0x00000000U);
        File.WriteAllBytes(Path.Combine(romDir, "Z_BOOT.EXE"), zBoot);
        File.WriteAllBytes(Path.Combine(romDir, "B_OVL.EXE"), bOvl);
        File.WriteAllBytes(Path.Combine(romDir, "A_LAST.EXE"), aLast);

        byte[] container = new byte[4096];
        Array.Fill(container, (byte)0xAA, 0, 2048);
        BinaryPrimitives.WriteUInt32LittleEndian(container.AsSpan(2048, 4), 0x03E00008U); // jr ra
        BinaryPrimitives.WriteUInt32LittleEndian(container.AsSpan(2052, 4), 0x00000000U); // nop
        File.WriteAllBytes(Path.Combine(romDir, "DATA.T"), container);
        string sliceSha = Convert.ToHexString(SHA256.HashData(container.AsSpan(2048, 2048))).ToLowerInvariant();

        var recipe = new AotRecipeSpec
        {
            ModuleId = "ovltest",
            DiscSerial = "SLUS-99988",
            PrimaryBootExecutable = "Z_BOOT.EXE",
            EntryAddresses = [0x80010000U, 0x80020000U, 0x80030000U, 0x8019F07CU],
            Executables =
            [
                new AotRecipeExecutableSpec { BootExePath = "Z_BOOT.EXE", Sha256 = HexSha(zBoot), LoadAddress = 0x80010000U, EntryAddress = 0x80010000U, TextSizeBytes = 12 },
                new AotRecipeExecutableSpec { BootExePath = "B_OVL.EXE", Sha256 = HexSha(bOvl), LoadAddress = 0x80020000U, EntryAddress = 0x80020000U, TextSizeBytes = 12 },
                new AotRecipeExecutableSpec { BootExePath = "A_LAST.EXE", Sha256 = HexSha(aLast), LoadAddress = 0x80030000U, EntryAddress = 0x80030000U, TextSizeBytes = 12 },
                new AotRecipeExecutableSpec { BootExePath = "DATA.T#Map0", Sha256 = sliceSha, LoadAddress = 0x8019F07CU, EntryAddress = 0x8019F07CU, TextSizeBytes = 2048 }
            ]
        };

        string recipePath = Path.Combine(_tempRoot, "ovl-recipe.json");
        File.WriteAllText(recipePath, JsonSerializer.Serialize(recipe, ReStationModuleManifest.JsonOptions));
        ResolvedPlayerMedia verified = PlayerMediaResolver.ResolveAndVerifyMedia(recipe, _tempRoot, new LocalAotLoadOptions(PreloadRomDir: romDir));
        Assert.Equal(64, verified.RomSha256Hex.Length);

        var opts = new RecompCompilerOptions(null, romDir, string.Empty, outDir, "ovltest", "SLUS-99988", "Overlay Order Test")
        {
            AotRecipePath = recipePath,
            Version = "0.2.2",
            EmitCleanRoomOnly = false,
            CompileGeneratedDll = true
        };
        RecompCompileResult result = new RecompCompilerPipeline(opts).Run();
        Assert.True(result.Success, result.Message);

        WeakReference weakAlc = VerifyOverlayGeneratedModules(Path.Combine(outDir, "ovltest.Generated.dll"), sliceSha);
        Assert.True(ModuleAssemblyLoader.WaitForUnload(weakAlc));
    }

    [Fact]
    public void KeyBindingsAndDrawer_SupportNumpadNavLabels_WideActionColumn_AndHitTesting()
    {
        Assert.Equal("Num8", KeyBindingsConfig.FormatBindingLabel(96));
        Assert.Equal("Num2", KeyBindingsConfig.FormatBindingLabel(90));
        Assert.Equal("Num4", KeyBindingsConfig.FormatBindingLabel(92));
        Assert.Equal("Num6", KeyBindingsConfig.FormatBindingLabel(94));
        Assert.Equal("NumEnt", KeyBindingsConfig.FormatBindingLabel(88));
        Assert.Equal("Ins", KeyBindingsConfig.FormatBindingLabel(73));
        Assert.Equal("Home", KeyBindingsConfig.FormatBindingLabel(74));
        Assert.Equal("End", KeyBindingsConfig.FormatBindingLabel(77));
        Assert.Equal("RAlt", KeyBindingsConfig.FormatBindingLabel(230));

        var actions = new List<GameActionDescriptor>
        {
            new("moveBack", "Move Backward", 90, 0x0010, 0x0010),
            new("strafeRight", "Strafe Right", 94, 0x0020, 0x0020),
            new("quickSlot", "Action 1-11/21", 73, 0x0040, 0x0040)
        };
        var provider = new WideLabelStubProfile(actions);
        var settings = new RecompSettings { InputProfileMode = InputProfileMode.PerGameModern };
        var drawer = new SlideOverSettingsDrawer { IsOpen = true, ActiveTab = 2 };
        string cfgPath = Path.Combine(_tempRoot, "settings.json");
        drawer.AttachInputProfiles(new GlobalControllerProfile(), Path.Combine(_tempRoot, "global.json"), provider);

        var canvas = new PixelOverlayCanvas(1280, 720);
        drawer.RenderOverlay(canvas, settings, null, "No disc", false);
        Assert.Contains(canvas.Buffer, static b => b != 0);

        // dw=800 -> dx=480 -> x=498, rw=764, actW=304, slotsStartX=807, slot3 sx=1110, row0 ry=214 -> pill (1110,216,96,31)
        Assert.Equal(DrawerAction.MarkDirty, drawer.HandleClick(1158, 231, settings, cfgPath));
        Assert.Equal("moveBack", drawer.KeybindsTab.ListeningModernActionId);
        Assert.Equal(3, drawer.KeybindsTab.ListeningSlotIndex);

        Assert.True(drawer.TryHandleGamepadCapture(GamepadBindingCode.East, settings, cfgPath));
        Assert.Equal(GamepadBindingCode.East, settings.GetModernControllerBindings(actions[0])[0]);

        drawer.RenderOverlay(canvas, settings, null, "No disc", false);
        Assert.Equal(DrawerAction.MarkDirty, drawer.HandleClick(1158, 231, settings, cfgPath, isRightClick: true));
        Assert.Empty(settings.GetModernControllerBindings(actions[0]));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ExerciseStreamThinShell(ReStationModuleManifest manifest, string modDir, string pkgPath, string dataDir, string romDir)
    {
        var opts = new LocalAotLoadOptions(DataDir: dataDir, PreloadRomDir: romDir);
        IRecompGameModule mod = ModuleAssemblyLoader.LoadGameModule(manifest, modDir, pkgPath, opts, out ModuleAssemblyLoader alc);
        Assert.True(alc.LoadedFromLocalAotCache);
        using (var cpu = new PsxCpu(new RecompSettings(), dataDir, new SoftwareCpuRenderBackend(), new ReStationLogger(dataDir, false)))
        using (var pacer = new HighResFramePacer(60.0))
        using (var host = new ReStationHost(cpu, manifest, dataDir, new RecompHostOptions(), pacer, new ReStationLogger(dataDir, false)))
        {
            mod.InitializeAsync(host).GetAwaiter().GetResult();
            Assert.True(cpu.HasCompiledEntry(0x80010000U));
        }
        Assembly a1 = alc.LoadFromAssemblyName(new AssemblyName("custom-alias.Generated"));
        Assert.Same(a1, alc.CompanionAssembly);
        Assert.Contains(alc.Assemblies, a => string.Equals(a.GetName().Name, "custom-alias.Generated", StringComparison.OrdinalIgnoreCase));
        Assert.Same(alc, AssemblyLoadContext.GetLoadContext(a1));
        var weak = new WeakReference(alc, trackResurrection: false);
        alc.Unload();
        return weak;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference VerifyOverlayGeneratedModules(string generatedDllPath, string expectedSliceSha)
    {
        var alc = new ModuleAssemblyLoader(generatedDllPath);
        Assembly asm = alc.LoadFromAssemblyPath(generatedDllPath);
        List<IRecompModule> modules = asm.GetTypes()
            .Where(t => !t.IsAbstract && typeof(IRecompModule).IsAssignableFrom(t))
            .Select(t => (IRecompModule)Activator.CreateInstance(t)!)
            .OrderBy(m => m.ExeId)
            .ToList();
        Assert.Equal(4, modules.Count);
        Assert.Equal(new[] { 0, 1, 2, 3 }, modules.Select(m => m.ExeId).ToArray());
        Assert.Equal(new[] { "Z_BOOT.EXE", "B_OVL.EXE", "A_LAST.EXE", "DATA.T#Map0" }, modules.Select(m => m.ExeName).ToArray());
        var overlay = Assert.IsAssignableFrom<IOverlayRecompModule>(modules[3]);
        Assert.Equal(0x8019F07CU, overlay.LoadRamAddress);
        Assert.Equal(2048, overlay.ContentSizeBytes);
        Assert.Equal(expectedSliceSha, overlay.ContentSha256Hex);
        var weak = new WeakReference(alc, trackResurrection: false);
        alc.Unload();
        return weak;
    }

    private static (string DllPath, string DepsPath) BuildSyntheticThinShellAssembly(string dir, string asmName, string modId, string aliasName)
    {
        Directory.CreateDirectory(dir);
        string libDir = Path.GetDirectoryName(typeof(IReStationCpu).Assembly.Location)!;
        string csproj = $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><TargetFramework>net8.0</TargetFramework><LangVersion>12.0</LangVersion><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><AssemblyName>{asmName}</AssemblyName></PropertyGroup>
          <ItemGroup>
            <Reference Include="Foundation.Core"><HintPath>{Path.Combine(libDir, "Foundation.Core.dll")}</HintPath><Private>false</Private></Reference>
            <Reference Include="Foundation.Retro"><HintPath>{Path.Combine(libDir, "Foundation.Retro.dll")}</HintPath><Private>false</Private></Reference>
            <Reference Include="reStation.Contracts"><HintPath>{Path.Combine(libDir, "reStation.Contracts.dll")}</HintPath><Private>false</Private></Reference>
          </ItemGroup>
        </Project>
        """;
        string cs = $$"""
        using System.Reflection;
        using Foundation.Retro;
        using reStation.Contracts;
        namespace ThinTest;
        public sealed class ThinShellGameModule : IRecompGameModule
        {
            private IReStationHost? _host;
            private readonly ReStationModuleManifest _fb = new() { ModuleId = "{{modId}}", EntryAssembly = "{{asmName}}.dll", ModuleTypeName = "ThinTest.ThinShellGameModule" };
            public IRecompModuleManifest Manifest => _host?.ModuleManifest ?? _fb;
            public IReadOnlyList<RecompPatchDescriptor> TimingPatches { get; } = [];
            public Task InitializeAsync(IRecompHost host, CancellationToken ct = default)
            {
                _host = (IReStationHost)host;
                Assembly companion = Assembly.Load(new AssemblyName("{{aliasName}}"));
                foreach (Type t in companion.GetTypes().Where(t => !t.IsAbstract && typeof(IRecompModule).IsAssignableFrom(t)))
                    _host.RegisterModule((IRecompModule)Activator.CreateInstance(t)!);
                return Task.CompletedTask;
            }
            public Task StepFrameAsync(CancellationToken ct = default) => Task.CompletedTask;
            public Task LoadSaveStateAsync(string saveStatePath, CancellationToken ct = default) => Task.CompletedTask;
        }
        """;
        string csprojPath = Path.Combine(dir, "thinshell.csproj");
        File.WriteAllText(csprojPath, csproj);
        File.WriteAllText(Path.Combine(dir, "ThinShellGameModule.cs"), cs);
        string outDir = Path.Combine(dir, "out");
        var psi = new ProcessStartInfo("dotnet", $"build \"{csprojPath}\" -c Release --nologo -o \"{outDir}\"") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        using Process proc = Process.Start(psi)!;
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        Assert.True(proc.ExitCode == 0, $"Thin shell build failed: {stderr} {stdout}");
        return (Path.Combine(outDir, $"{asmName}.dll"), Path.Combine(outDir, $"{asmName}.deps.json"));
    }

    private static byte[] BuildPsxExeBytes(uint baseAddr, params uint[] words)
    {
        byte[] exe = new byte[0x800 + (words.Length * 4)];
        "PS-X EXE"u8.CopyTo(exe.AsSpan(0, 8));
        BinaryPrimitives.WriteUInt32LittleEndian(exe.AsSpan(0x10, 4), baseAddr);
        BinaryPrimitives.WriteUInt32LittleEndian(exe.AsSpan(0x18, 4), baseAddr);
        BinaryPrimitives.WriteUInt32LittleEndian(exe.AsSpan(0x1C, 4), (uint)(words.Length * 4));
        for (int i = 0; i < words.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(exe.AsSpan(0x800 + (i * 4), 4), words[i]);
        return exe;
    }

    private static string HexSha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private sealed class WideLabelStubProfile(IReadOnlyList<GameActionDescriptor> actions) : IModuleInputProfileProvider
    {
        public string ProfileDisplayName => "Wide Label Stub";
        public IReadOnlyList<GameActionDescriptor> ModernActions => actions;
        public bool IsGameplayContextActive(IReStationCpu cpu) => true;
    }
}
