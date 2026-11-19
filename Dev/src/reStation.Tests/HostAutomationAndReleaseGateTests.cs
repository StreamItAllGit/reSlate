// @module: reStation.Tests.HostAutomationAndReleaseGateTests
// @purpose: Validates T6 reEngine.Host.Contracts alignment, ModuleAssemblyLoader SharedAssemblyNames, --require-backend/--doctor CLI gates, pure Release PE zero-Automation purity + ExitCode=2 fail-closed gate, and Debug-only loopback HTTP automation.
// @depends-on: System.Diagnostics, System.Net, System.Net.Http, System.Reflection.Metadata, System.Reflection.PortableExecutable, System.Text.Json, Foundation.Core, reEngine.Host.Contracts, reStation.Contracts, reStation.Core, reStation.Player, Xunit
// @exports: HostAutomationAndReleaseGateTests
// @owner: devv-player-host (T5/T6)

using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Foundation.Core;
using Foundation.Installer;
using reEngine.Host.Contracts;
using reEngine.Host.Input;
using reStation.Contracts;
using reStation.Core;
using reStation.Core.Cpu;
using reStation.Player;
using Xunit;

#if DEBUG || RESTATION_TEST_AUTOMATION
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Foundation.Automation;
using Foundation.Retro;
#endif

namespace reStation.Tests;

public sealed class HostAutomationAndReleaseGateTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"reslate-host-gate-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (!Directory.Exists(_tempRoot)) return;
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void ModuleAssemblyLoader_IncludesReEngineHost_InSharedAssemblyNames()
    {
        Assert.Contains("reEngine.Host", ModuleAssemblyLoader.SharedAssemblyNames);
        Assert.Contains("reEngine.Core", ModuleAssemblyLoader.SharedAssemblyNames);
        Assert.Contains("reStation.Contracts", ModuleAssemblyLoader.SharedAssemblyNames);
        Assert.Contains("Foundation.Core", ModuleAssemblyLoader.SharedAssemblyNames);
    }

    [Fact]
    public void HostContractBridge_ProjectsSettingsTelemetryHitBoxes_AndSyntheticInput()
    {
        var drawer = new SlideOverSettingsDrawer { IsOpen = false, ActiveTab = 1 };
        var overlay = new UiOverlayPresenter();
        var settings = new RecompSettings { BackendId = PsxRenderBackendIds.OpenGl, IsFpsOverlayEnabled = true, ResolutionScale = 2 };
        var snap = new PsxTelemetrySnapshot(42, 1.5, 2.5, 11.2, 60.0, 16.7, 2, PsxRenderBackendIds.OpenGl, 12.0, 4.0, 5, 0, 0.55, 0);

        Assert.Equal(HostRenderBackendKind.OpenGl, HostContractBridge.MapBackendKind(PsxRenderBackendIds.OpenGl));
        Assert.Equal(HostRenderBackendKind.Software, HostContractBridge.MapBackendKind(PsxRenderBackendIds.Software));
        Assert.Equal(HostRenderBackendKind.Vulkan, HostContractBridge.MapBackendKind(PsxRenderBackendIds.Vulkan));
        Assert.Equal(HostRenderBackendKind.ParallelCpu, HostContractBridge.MapBackendKind(PsxRenderBackendIds.ParallelCpu));
        Assert.Equal(HostDrawerTabId.Controls, HostContractBridge.MapDrawerTab(1));

        _ = overlay.EnsureRendered(drawer, settings, new ReStationModuleManifest { ModuleId = "m1", DisplayName = "M1" }, "SLUS-01115", false, snap);

        var bridge = new HostContractBridge();
        bridge.SyncWidgets(drawer, settings, overlay, snap, ReStationExecutionTierModes.Default);

        Assert.Equal(HostRenderBackendKind.OpenGl, bridge.ActiveBackendKind);
        Assert.Equal(HostDrawerTabId.Controls, bridge.ActiveDrawerTab);
        Assert.NotNull(bridge.LastHudTelemetry);
        Assert.Equal(60.0, bridge.LastHudTelemetry!.CurrentFps);
        Assert.Contains(bridge.GetOverlayWidgets(), w => w.ControlId == "Drawer_TogglePill");
        Assert.Contains(bridge.GetOverlayWidgets(), w => w.ControlId == "F3Hud");

        int clickedX = -1, clickedY = -1, pressedScancode = -1;
        bridge.EnqueuePointerClick(1190, 24, "Drawer_TogglePill");
        bridge.EnqueueKeyPress("F3");
        bridge.DrainCommands((x, y) => { clickedX = x; clickedY = y; }, sc => pressedScancode = sc);

        Assert.Equal(1190, clickedX);
        Assert.Equal(24, clickedY);
        Assert.Equal(Sdl3Native.ScF3, pressedScancode);
        Assert.True((bridge.LastSyntheticInputFrame.HotkeyFlags & (uint)HostHotkeyFlags.ToggleHud) != 0U);
    }

    [Theory]
    [InlineData("vulkan")]
    [InlineData("parallelCpu")]
    public void RunMain_RequireDeferredBackend_ExitsWithCode6(string backend)
    {
        int code = Program.RunMain(["--require-backend", backend, "--tray-only", "--frames", "1"]);
        Assert.Equal(6, code);
    }

    [Fact]
    public void RunMain_RequireOpenGlWithTrayOnlyFallback_ExitsWithCode6_AndDoctorPassesWithCode0()
    {
        Directory.CreateDirectory(_tempRoot);
        string dataDir = Path.Combine(_tempRoot, "cli-data");
        string logDir = Path.Combine(_tempRoot, "cli-logs");
        string cfgPath = Path.Combine(_tempRoot, "settings.json");

        int glOnTrayCode = Program.RunMain(
        [
            "--require-backend", "opengl",
            "--tray-only", "--frames", "1",
            "--data-dir", dataDir, "--config", cfgPath, "--log-dir", logDir
        ]);
        Assert.Equal(6, glOnTrayCode);

        int softTrayCode = Program.RunMain(
        [
            "--require-backend", "software",
            "--tray-only", "--frames", "1",
            "--data-dir", dataDir, "--config", cfgPath, "--log-dir", logDir
        ]);
        Assert.Equal(0, softTrayCode);

        int doctorCode = Program.RunMain(
        [
            "--doctor", "--enable-logs",
            "--data-dir", dataDir, "--config", cfgPath, "--log-dir", logDir
        ]);
        Assert.Equal(0, doctorCode);

        string[] logFiles = Directory.GetFiles(logDir, "*.log");
        Assert.NotEmpty(logFiles);
        string logBody = string.Join('\n', logFiles.Select(File.ReadAllText));
        Assert.Contains("reSlate Doctor v0.3.0", logBody, StringComparison.Ordinal);
        Assert.Contains("DeferredBackends (v0.3.1): vulkan, parallelCpu", logBody, StringComparison.Ordinal);
    }

#if !DEBUG && !RESTATION_TEST_AUTOMATION
    [Fact]
    public void ReleaseBinary_HasZeroAutomationOrWinFormsReferences_AndFailsClosedOnAutomationPort()
    {
        Assembly playerAsm = typeof(Program).Assembly;
        string[] runtimeRefs = playerAsm.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty).ToArray();
        Assert.DoesNotContain(runtimeRefs, r => r.Equals("Foundation.Automation", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(runtimeRefs, r => r.Contains("Windows.Forms", StringComparison.OrdinalIgnoreCase));

        string playerPePath = playerAsm.Location;
        Assert.True(File.Exists(playerPePath), $"Expected Release PE at '{playerPePath}'.");
        using (var fs = File.OpenRead(playerPePath))
        using (var pe = new PEReader(fs))
        {
            MetadataReader md = pe.GetMetadataReader();
            List<string> peRefs = [];
            foreach (AssemblyReferenceHandle handle in md.AssemblyReferences)
                peRefs.Add(md.GetString(md.GetAssemblyReference(handle).Name));

            Assert.DoesNotContain(peRefs, r => r.Equals("Foundation.Automation", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(peRefs, r => r.Contains("Windows.Forms", StringComparison.OrdinalIgnoreCase));
        }

        Directory.CreateDirectory(_tempRoot);
        string dataDir = Path.Combine(_tempRoot, "rel-data");
        string logDir = Path.Combine(_tempRoot, "rel-logs");
        string cfgPath = Path.Combine(_tempRoot, "rel-settings.json");

        int inProcCode = Program.RunMain(
        [
            "--automation-port", "19999",
            "--tray-only", "--frames", "1",
            "--data-dir", dataDir, "--config", cfgPath, "--log-dir", logDir
        ]);
        Assert.Equal(2, inProcCode);

        string exePath = Path.Combine(Path.GetDirectoryName(playerPePath)!, "reSlate.Player.exe");
        if (File.Exists(exePath))
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"--automation-port 19999 --tray-only --frames 1 --data-dir \"{dataDir}\" --config \"{cfgPath}\" --log-dir \"{logDir}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                Environment = { ["APP_ISOLATED_TEST"] = "1" }
            })!;
            Assert.True(proc.WaitForExit(15_000));
            Assert.Equal(2, proc.ExitCode);
        }
    }

    [Fact]
    public void ReleaseInstaller_HasZeroAutomationReferences_AndFailsClosedOnAutomationPort()
    {
        Assembly installerAsm = typeof(reStation.Installer.Program).Assembly;
        string[] runtimeRefs = installerAsm.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty).ToArray();
        Assert.DoesNotContain(runtimeRefs, r => r.Equals("Foundation.Automation", StringComparison.OrdinalIgnoreCase));
        string[] resources = installerAsm.GetManifestResourceNames();
        Assert.DoesNotContain(resources, r => r.Contains("Foundation.Automation", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(resources, r => r.Contains("Foundation.UI", StringComparison.OrdinalIgnoreCase));

        string installerPePath = installerAsm.Location;
        Assert.True(File.Exists(installerPePath), $"Expected Release installer PE at '{installerPePath}'.");
        using (var fs = File.OpenRead(installerPePath))
        using (var pe = new PEReader(fs))
        {
            MetadataReader md = pe.GetMetadataReader();
            List<string> peRefs = [];
            foreach (AssemblyReferenceHandle handle in md.AssemblyReferences)
                peRefs.Add(md.GetString(md.GetAssemblyReference(handle).Name));
            Assert.DoesNotContain(peRefs, r => r.Equals("Foundation.Automation", StringComparison.OrdinalIgnoreCase));
        }

        int inProcCode = reStation.Installer.Program.RunInstallerCore(["--automation-port", "5199"]);
        Assert.Equal(2, inProcCode);

        string exePath = Path.Combine(Path.GetDirectoryName(installerPePath)!, "reSlate-Setup-v0.3.0-win-x64.exe");
        if (File.Exists(exePath))
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "--automation-port 5199",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                Environment = { ["APP_ISOLATED_TEST"] = "1" }
            })!;
            Assert.True(proc.WaitForExit(15_000));
            Assert.Equal(2, proc.ExitCode);
        }
    }
#endif

#if DEBUG || RESTATION_TEST_AUTOMATION
    [Fact]
    public async Task DebugAutomationHook_RoundTripsLoopbackHealthTreePointerKeyboardAndPngSnapshot()
    {
        Directory.CreateDirectory(_tempRoot);
        string dataDir = Path.Combine(_tempRoot, "auto-data");
        string logDir = Path.Combine(_tempRoot, "auto-logs");
        string cfgPath = Path.Combine(dataDir, "settings.json");
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(logDir);

        var env = new AppEnvironment("reslate", ["--data-dir", dataDir, "--config", cfgPath, "--log-dir", logDir, "--tray-only"]);
        var settings = new RecompSettings { BackendId = PsxRenderBackendIds.Software, IsFpsOverlayEnabled = true };
        using var logger = new ReStationLogger(logDir, false);
        using var video = HostVideoSurface.CreateOffscreen(settings, logger, "automation-test");
        using var cpu = new PsxCpu(settings, dataDir, video.Backend, logger);
        using var pacer = new HighResFramePacer(0.0);
        using var host = new ReStationHost(cpu, new ReStationModuleManifest { ModuleId = "auto", DisplayName = "Auto" }, dataDir, new RecompHostOptions(), pacer, logger);
        var drawer = new SlideOverSettingsDrawer { IsOpen = false };
        var overlay = new UiOverlayPresenter();
        var st = new Program.LoopState { Gamepad = IntPtr.Zero, IsOffscreen = true };

        var bridge = new HostContractBridge();
        byte[] rgba = new byte[UiOverlayPresenter.OverlayWidth * UiOverlayPresenter.OverlayHeight * 4];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = 0x20; rgba[i + 1] = 0x55; rgba[i + 2] = 0x99; rgba[i + 3] = 0xFF;
        }
        bridge.PublishFramebufferRgba(UiOverlayPresenter.OverlayWidth, UiOverlayPresenter.OverlayHeight, rgba);
        PsxTelemetrySnapshot snap = host.GetTelemetry();
        _ = overlay.EnsureRendered(drawer, settings, host.ModuleManifest, "SLUS-01115", false, snap);
        bridge.SyncWidgets(drawer, settings, overlay, snap, ReStationExecutionTierModes.Default);

        const string token = "debug-loopback-token-030";
        using var hook = new DebugAutomationHook(bridge, port: 0, bearerToken: token, allowedSnapshotRoot: _tempRoot);
        hook.Start();

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{hook.ListeningPort}") };

        // Unauthorized & malicious Origin requests must fail closed.
        using HttpResponseMessage unauth = await http.GetAsync("/v1/health");
        Assert.Equal(HttpStatusCode.Unauthorized, unauth.StatusCode);

        using (var badOrigin = new HttpRequestMessage(HttpMethod.Get, "/v1/health"))
        {
            badOrigin.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            badOrigin.Headers.Add("Origin", "https://evil.example");
            using HttpResponseMessage badOriginResp = await http.SendAsync(badOrigin);
            Assert.Equal(HttpStatusCode.Forbidden, badOriginResp.StatusCode);
        }

        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var health = await http.GetFromJsonAsync<AutomationResponse<AutomationHealthPayload>>("/v1/health");
        Assert.NotNull(health);
        Assert.True(health!.IsPassed);
        Assert.Equal(ReStationEngineInfo.AppId, health.Data!.AppId);

        var tree = await http.GetFromJsonAsync<AutomationResponse<AutomationControlNode>>("/v1/tree");
        Assert.NotNull(tree?.Data);
        Assert.True(ContainsControl(tree!.Data!, "Drawer_TogglePill"));

        // Pointer click by targetId -> drain onto player drawer -> verify drawer opens & tree updates.
        using HttpResponseMessage ptrResp = await PostJsonWithContentLengthAsync(
            http, "/v1/pointer", new AutomationPointerRequest(Action: "click", TargetId: "Drawer_TogglePill"));
        ptrResp.EnsureSuccessStatusCode();
        bridge.DrainCommands(
            (ox, oy) => Program.HandleOverlayPointerDown(ox, oy, Sdl3Native.MouseBtnLeft, video, host, cpu, drawer, overlay, settings, env, pacer, logger, st),
            sc => Program.HandleKeyDownScancode(sc, video, host, drawer, overlay, settings, env, st));
        Assert.True(drawer.IsOpen);

        _ = overlay.EnsureRendered(drawer, settings, host.ModuleManifest, "SLUS-01115", false, snap);
        bridge.SyncWidgets(drawer, settings, overlay, snap, ReStationExecutionTierModes.Default);
        hook.SyncWidgets(bridge.GetOverlayWidgets());

        bool hasControlsTab = false;
        for (int attempt = 0; attempt < 25 && !hasControlsTab; attempt++)
        {
            await Task.Delay(20);
            var openTree = await http.GetFromJsonAsync<AutomationResponse<AutomationControlNode>>("/v1/tree");
            hasControlsTab = openTree?.Data is not null && ContainsControl(openTree.Data, "Tab_Controls");
        }
        Assert.True(hasControlsTab);

        // Keyboard F3 press via reEngine.Host InputMapper toggles FPS HUD.
        using HttpResponseMessage keyResp = await PostJsonWithContentLengthAsync(
            http, "/v1/keyboard", new AutomationKeyboardRequest(Action: "press", Key: "F3"));
        keyResp.EnsureSuccessStatusCode();
        bridge.DrainCommands(
            (ox, oy) => Program.HandleOverlayPointerDown(ox, oy, Sdl3Native.MouseBtnLeft, video, host, cpu, drawer, overlay, settings, env, pacer, logger, st),
            sc => Program.HandleKeyDownScancode(sc, video, host, drawer, overlay, settings, env, st));
        Assert.False(settings.IsFpsOverlayEnabled);

        // Snapshot writes real PNG inside allowed _tempRoot.
        string snapPath = Path.Combine(_tempRoot, "capture.png");
        using HttpResponseMessage snapResp = await PostJsonWithContentLengthAsync(
            http, "/v1/snapshot", new AutomationSnapshotRequest(FilePath: snapPath));
        snapResp.EnsureSuccessStatusCode();
        Assert.True(File.Exists(snapPath));
        byte[] png = await File.ReadAllBytesAsync(snapPath);
        Assert.True(png.Length > 16);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png[..4]);

        // Ephemeral --automation-port 0 headless smoke run passes cleanly in Debug.
        int cliSmoke = Program.RunMain(
        [
            "--automation-port", "0", "--automation-token", "smoke-tok",
            "--tray-only", "--frames", "1",
            "--data-dir", Path.Combine(_tempRoot, "cli-auto-data"),
            "--config", Path.Combine(_tempRoot, "cli-auto-settings.json"),
            "--log-dir", Path.Combine(_tempRoot, "cli-auto-logs")
        ]);
        Assert.Equal(0, cliSmoke);
    }

    [Fact]
    public void DebugInstallerHook_AssignsDeterministicWizardControlNames_AndExtractsAutomationArgs()
    {
        string[] stripped = reStation.Installer.Program.ExtractAutomationArgs(
            ["--automation-port", "0", "--automation-token=wiz-tok", "--install-dir", _tempRoot],
            out int? port, out string? token);
        Assert.Equal(0, port);
        Assert.Equal("wiz-tok", token);
        Assert.Equal(["--install-dir", _tempRoot], stripped);

        Assert.Throws<ArgumentException>(() =>
            reStation.Installer.Program.ExtractAutomationArgs(["--automation-port", "70000"], out _, out _));

        Directory.CreateDirectory(_tempRoot);
        string dataDir = Path.Combine(_tempRoot, "wiz-data");
        string cfgPath = Path.Combine(dataDir, "settings.json");
        string logDir = Path.Combine(_tempRoot, "wiz-logs");
        InstallerThemeOptions theme = reStation.Installer.ReStationInstallerTheme.CreateThemeOptions();
        InstallerAppDescriptor descriptor = reStation.Installer.ReStationInstallerTheme.CreateDescriptor(theme);
        var ctx = new InstallerRuntimeContext(
            cliDataDir: dataDir, cliConfigPath: cfgPath, cliLogDir: logDir,
            dryRun: true, envPrefix: descriptor.EnvPrefix, sandboxOverride: SandboxResolution.Inactive);
        var engine = new InstallerEngine(descriptor, ctx, typeof(reStation.Installer.Program).Assembly);

        using var wizard = new InstallerWizardForm(
            engine: engine, ctx: ctx, initialInstallDir: Path.Combine(_tempRoot, "install"),
            initialSecondaryDir: Path.Combine(_tempRoot, "reEngine"), secondaryFlagName: "--reengine-dir");
        using var hook = reStation.Installer.InstallerAutomationHook.Attach(wizard, 0, "wiz-loop-tok", ctx);

        Assert.Equal("InstallerWizardRoot", wizard.Name);
        Assert.Equal("Wizard_PrimaryButton", wizard.PrimaryButton.Name);
        Assert.Equal("Wizard_DetailsToggle", wizard.DetailsToggleButton.Name);
        Assert.Equal("Wizard_ProfileLightweight", wizard.LightweightProfileRadioButton.Name);
        Assert.Equal("Wizard_ProfileFull", wizard.FullProfileRadioButton.Name);
        Assert.Equal("Wizard_InstallDir", wizard.InstallDirTextBox.Name);
        Assert.Equal("Wizard_SecondaryDir", wizard.SecondaryDirTextBox.Name);
        Assert.Equal("Wizard_StatusLabel", wizard.StatusLabel.Name);
        Assert.Equal("Wizard_LogTextBox", wizard.LogTextBox.Name);
        Assert.Equal("wiz-loop-tok", hook.BearerToken);
    }

    private static Task<HttpResponseMessage> PostJsonWithContentLengthAsync<T>(HttpClient http, string path, T payload)
    {
        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        return http.PostAsync(path, content);
    }

    private static bool ContainsControl(AutomationControlNode node, string id)
    {
        if (string.Equals(node.Id, id, StringComparison.Ordinal)) return true;
        foreach (AutomationControlNode child in node.Children)
            if (ContainsControl(child, id)) return true;
        return false;
    }
#endif
}
