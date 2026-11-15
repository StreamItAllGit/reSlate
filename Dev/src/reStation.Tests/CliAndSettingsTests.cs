// @module: reStation.Tests.CliAndSettingsTests
// @purpose: Validates CliOptions fail-closed parsing, RecompSettings clamping/self-heal, ExtensionData preservation, telemetry schema, and logger containment.
// @depends-on: Xunit, System.Text.Json, Foundation.Core, reStation.Contracts, reStation.Core
// @exports: CliAndSettingsTests
// @owner: devv-pkg-test (T8)

using System.Diagnostics;
using System.Text.Json;
using Foundation.Core;
using reStation.Contracts;
using reStation.Core;
using Xunit;

namespace reStation.Tests;

public sealed class CliAndSettingsTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"restation-cli-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true);
    }

    [Fact]
    public void CliOptions_Parse_FullFlagSuite_PopulatesAllFields()
    {
        CliOptions parsed = CliOptions.Parse(new[]
        {
            "--module", "game.recomp.json",
            "--disc", "disc1.cue",
            "--backend", "software",
            "--scale", "8",
            "--fps-mode", "original",
            "--target-fps", "120",
            "--vsync", "on",
            "--frames", "180",
            "--fixture", "boot.json",
            "--dump-screenshot", "frame.png",
            "--data-dir", _tempRoot,
            "--config", Path.Combine(_tempRoot, "custom.json"),
            "--log-dir", Path.Combine(_tempRoot, "logs"),
            "--test-mode", "--headless", "--visible-test", "--verify",
            "--open-settings", "--enable-logs", "--show-fps", "--version", "--help"
        });

        Assert.Equal("game.recomp.json", parsed.ModulePath);
        Assert.Equal("disc1.cue", parsed.DiscPath);
        Assert.Equal(PsxRenderBackendIds.Software, parsed.BackendId);
        Assert.Equal(8, parsed.ResolutionScale);
        Assert.Equal(FrameRateMode.Original, parsed.FpsMode);
        Assert.Equal(120, parsed.TargetFps);
        Assert.True(parsed.Vsync);
        Assert.Equal(180, parsed.MaxFrames);
        Assert.Equal("boot.json", parsed.FixturePath);
        Assert.Equal("frame.png", parsed.DumpScreenshotPath);
        Assert.Equal(_tempRoot, parsed.DataDir);
        Assert.True(parsed.TestMode && parsed.Headless && parsed.VisibleTest && parsed.VerifyOnly);
        Assert.True(parsed.OpenSettings && parsed.EnableLogs && parsed.ShowFps && parsed.ShowVersion && parsed.ShowHelp);
        Assert.Contains("--version", CliOptions.HelpText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--unknown-flag")]
    [InlineData("--backend", "vulkan")]
    [InlineData("--scale", "3")]
    [InlineData("--target-fps", "15")]
    [InlineData("--module")]
    public void CliOptions_Parse_InvalidOrIncompleteArgs_ThrowsArgumentException(params string[] args)
    {
        Assert.Throws<ArgumentException>(() => CliOptions.Parse(args));
    }

    [Fact]
    public void RecompSettings_Clamp_NormalizesOutOfRangeAndNonFiniteValues()
    {
        var invalidIdentity = new RecompSettings
        {
            SchemaVersion = 0,
            AppId = "   ",
            ResolutionScale = 5,
            BackendId = "DirectX",
            MouseSensitivity = double.NaN,
            MasterVolume = 4.2,
            TargetFps = 999
        };

        invalidIdentity.Clamp();

        Assert.Equal(RecompSettings.CurrentSchemaVersion, invalidIdentity.SchemaVersion);
        Assert.Equal(ReStationEngineInfo.AppId, invalidIdentity.AppId);
        Assert.Equal(8, invalidIdentity.ResolutionScale);
        Assert.Equal(PsxRenderBackendIds.OpenGl, invalidIdentity.BackendId);
        Assert.Equal(1.0, invalidIdentity.MouseSensitivity);
        Assert.Equal(1.0, invalidIdentity.MasterVolume);
        Assert.Equal(500, invalidIdentity.TargetFps);

        var consumerSettings = new RecompSettings
        {
            SchemaVersion = 2,
            AppId = "  crowndepths-recomp  "
        };
        consumerSettings.Clamp();
        Assert.Equal(2, consumerSettings.SchemaVersion);
        Assert.Equal("crowndepths-recomp", consumerSettings.AppId);
    }

    [Fact]
    public void RecompSettings_UnknownJsonKeys_PreservedAcrossLoadSaveRoundTrip()
    {
        Directory.CreateDirectory(_tempRoot);
        string settingsPath = Path.Combine(_tempRoot, "settings.json");
        File.WriteAllText(settingsPath, """
        {
          "schemaVersion": 1,
          "resolutionScale": 4,
          "futureExperimentalFlag": 42,
          "futurePluginSettings": { "enabled": true, "profile": "sharp" }
        }
        """);

        SettingsLoadOutcome<RecompSettings> loaded = RecompSettings.Load(settingsPath);
        Assert.NotNull(loaded.Value.ExtensionData);
        Assert.Equal(42, loaded.Value.ExtensionData!["futureExperimentalFlag"].GetInt32());

        loaded.Value.ResolutionScale = 8;
        loaded.Value.Save(settingsPath);

        SettingsLoadOutcome<RecompSettings> reloaded = RecompSettings.Load(settingsPath);
        Assert.Equal(8, reloaded.Value.ResolutionScale);
        Assert.NotNull(reloaded.Value.ExtensionData);
        Assert.Equal(42, reloaded.Value.ExtensionData!["futureExperimentalFlag"].GetInt32());
        Assert.True(reloaded.Value.ExtensionData["futurePluginSettings"].GetProperty("enabled").GetBoolean());
        Assert.Equal("sharp", reloaded.Value.ExtensionData["futurePluginSettings"].GetProperty("profile").GetString());
    }

    [Fact]
    public void RecompSettings_Load_SelfHealsCorruptJsonFromBackupOrDefaults()
    {
        Directory.CreateDirectory(_tempRoot);
        string settingsPath = Path.Combine(_tempRoot, "settings.json");
        var valid = new RecompSettings { ResolutionScale = 2, TargetFps = 120 };
        valid.Save(settingsPath);
        valid.TargetFps = 144;
        valid.Save(settingsPath);

        File.WriteAllText(settingsPath, "{ corrupt-json!!!");
        SettingsLoadOutcome<RecompSettings> healed = RecompSettings.Load(settingsPath);

        Assert.True(healed.HealedFromBackup || healed.CreatedDefault);
        Assert.Equal(RecompSettings.CurrentSchemaVersion, healed.Value.SchemaVersion);
        Assert.Equal(2, healed.Value.ResolutionScale);
    }

    [Fact]
    public void PsxTelemetrySnapshot_JsonSerialization_ExposesCanonicalHeartbeatKeys()
    {
        var snap = new PsxTelemetrySnapshot(
            FrameNumber: 1, CpuMs: 3.2, GpuMs: 4.1, WaitMs: 1.0, CurrentFps: 60.0, P99FrameMs: 16.6,
            ResolutionScale: 4, BackendId: PsxRenderBackendIds.OpenGl, CdSectorsPerSec: 150.0,
            MdecMacroblocksPerSec: 19.2, ActiveSpuVoices: 8, XaDrops: 0, AudioFillRatio: 0.87, AudioUnderruns: 0);
        string json = JsonSerializer.Serialize(snap);

        foreach (string key in new[]
        {
            "\"FrameNumber\":", "\"CpuMs\":", "\"GpuMs\":", "\"CurrentFps\":", "\"P99FrameMs\":",
            "\"ResolutionScale\":", "\"BackendId\":", "\"CdSectorsPerSec\":", "\"MdecMacroblocksPerSec\":",
            "\"XaDrops\":", "\"AudioFillRatio\":", "\"AudioUnderruns\":"
        })
        {
            Assert.Contains(key, json, StringComparison.Ordinal);
        }
        Assert.Contains("sectorsPerSec=150.0", snap.ToHeartbeatLine(), StringComparison.Ordinal);
    }

    [Fact]
    public void CliOptions_TestModeAndLogger_EnforceSandboxContainmentAndWriteLogs()
    {
        string dataRoot = Path.Combine(_tempRoot, "sandbox_data");
        string escapedLogDir = Path.Combine(_tempRoot, "outside_logs");
        Directory.CreateDirectory(dataRoot);
        Directory.CreateDirectory(escapedLogDir);

        Assert.ThrowsAny<Exception>(() =>
            CliOptions.Parse(new[] { "--test-mode", "--data-dir", dataRoot, "--log-dir", escapedLogDir }).CreateSandbox());
        Assert.ThrowsAny<Exception>(() =>
            CliOptions.Parse(new[] { "--test-mode" }).CreateSandbox());

        string containedLogs = Path.Combine(dataRoot, "logs");
        SandboxEnvironment sbx = CliOptions.Parse(new[] { "--test-mode", "--data-dir", dataRoot, "--log-dir", containedLogs }).CreateSandbox();
        Assert.Equal(Path.GetFullPath(containedLogs), Path.GetFullPath(sbx.LogDirectory));

        using var validLogger = new ReStationLogger(sbx.LogDirectory, enabled: true);
        validLogger.Info("TEST", "Heartbeat ok");
        Assert.True(File.Exists(validLogger.LogFilePath));
    }

    [Fact]
    public void CliOptions_TestMode_AllowsSiblingSandboxDataAndLogs_WhileRejectingEscape()
    {
        string sbxRoot = Path.Combine(_tempRoot, "Sandbox");
        string sbxData = Path.Combine(sbxRoot, "data");
        string sbxLogs = Path.Combine(sbxRoot, "logs");
        string escapedLogs = Path.Combine(_tempRoot, "outside_logs");
        string escapedCfg = Path.Combine(_tempRoot, "outside_settings.json");

        SandboxEnvironment sbx = CliOptions.Parse(new[]
        {
            "--test-mode", "--data-dir", sbxData, "--log-dir", sbxLogs
        }).CreateSandbox();
        Assert.Equal(Path.GetFullPath(sbxData), Path.GetFullPath(sbx.DataDirectory));
        Assert.Equal(Path.GetFullPath(sbxLogs), Path.GetFullPath(sbx.LogDirectory));
        Assert.Equal(Path.GetFullPath(Path.Combine(sbxData, "settings.json")), Path.GetFullPath(sbx.ConfigFilePath));

        string runData = Path.Combine(_tempRoot, "run1", "data");
        string runLogs = Path.Combine(_tempRoot, "run1", "logs");
        SandboxEnvironment runSbx = CliOptions.Parse(new[]
        {
            "--test-mode", "--data-dir", runData, "--log-dir", runLogs
        }).CreateSandbox();
        Assert.Equal(Path.GetFullPath(runLogs), Path.GetFullPath(runSbx.LogDirectory));

        Assert.Throws<InvalidOperationException>(() =>
            CliOptions.Parse(new[] { "--test-mode", "--data-dir", sbxData, "--log-dir", escapedLogs }).CreateSandbox());
        Assert.Throws<InvalidOperationException>(() =>
            CliOptions.Parse(new[] { "--test-mode", "--data-dir", sbxData, "--config", escapedCfg }).CreateSandbox());
    }

    [Fact]
    public void PlayerProcess_RelativeSandboxArgs_PreservesWorkingDirAndCapturesRedirectedStderr()
    {
        string playerExe = Path.Combine(AppContext.BaseDirectory, "reStation.Player.exe");
        Assert.True(File.Exists(playerExe), $"Missing test target: {playerExe}");
        Directory.CreateDirectory(_tempRoot);

        var okPsi = new ProcessStartInfo
        {
            FileName = playerExe,
            WorkingDirectory = _tempRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string a in new[] { "--test-mode", "--headless", "--frames", "1", "--data-dir", "Sandbox\\data", "--log-dir", "Sandbox\\logs" })
            okPsi.ArgumentList.Add(a);

        using (Process okProc = Process.Start(okPsi)!)
        {
            string okOut = okProc.StandardOutput.ReadToEnd();
            string okErr = okProc.StandardError.ReadToEnd();
            okProc.WaitForExit();
            Assert.True(okProc.ExitCode == 0, $"Expected exit 0, got {okProc.ExitCode}. out='{okOut}' err='{okErr}'");
        }
        Assert.True(File.Exists(Path.Combine(_tempRoot, "Sandbox", "logs", "runtime.log")));

        var badPsi = new ProcessStartInfo
        {
            FileName = playerExe,
            WorkingDirectory = _tempRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string a in new[] { "--test-mode", "--data-dir", "Sandbox\\data", "--log-dir", "outside_logs" })
            badPsi.ArgumentList.Add(a);

        using Process badProc = Process.Start(badPsi)!;
        _ = badProc.StandardOutput.ReadToEnd();
        string badErr = badProc.StandardError.ReadToEnd();
        badProc.WaitForExit();
        Assert.Equal(3, badProc.ExitCode);
        Assert.Contains("Fail-Closed Sandbox Violation", badErr, StringComparison.Ordinal);
    }

    [Fact]
    public void CliOptions_HelpTextAndEngineInfo_Reflect020Track()
    {
        Assert.Equal("0.2.5", ReStationEngineInfo.Version);
        Assert.Equal("0.2.x", ReStationEngineInfo.CompatibleEngineTrack);
        Assert.Contains("reStation v0.2.5", CliOptions.HelpText, StringComparison.Ordinal);
    }

    [Fact]
    public void PsxTelemetrySnapshot_ToHeartbeatMetrics_OmitsPrefixAndMatchesLineKeys()
    {
        var snap = new PsxTelemetrySnapshot(
            FrameNumber: 12, CpuMs: 2.5, GpuMs: 3.5, WaitMs: 1.5, CurrentFps: 60.0, P99FrameMs: 16.67,
            ResolutionScale: 4, BackendId: PsxRenderBackendIds.OpenGl, CdSectorsPerSec: 150.0,
            MdecMacroblocksPerSec: 24.0, ActiveSpuVoices: 6, XaDrops: 0, AudioFillRatio: 0.75, AudioUnderruns: 0);

        string metrics = snap.ToHeartbeatMetrics();
        Assert.DoesNotContain("[HEARTBEAT]", metrics, StringComparison.Ordinal);
        Assert.Contains("frame=12 ", metrics, StringComparison.Ordinal);
        Assert.Contains("resolutionScale=4 ", metrics, StringComparison.Ordinal);
        Assert.Contains("backend=opengl ", metrics, StringComparison.Ordinal);
        Assert.Equal("[HEARTBEAT] " + metrics, snap.ToHeartbeatLine());
    }

    [Fact]
    public void CliOptions_ApplyOverrides_PropagatesScaleAndBackendToSettings()
    {
        CliOptions parsed = CliOptions.Parse(new[] { "--verify", "--scale", "4", "--backend", "software" });
        var settings = new RecompSettings();

        parsed.ApplyOverrides(settings);

        Assert.Equal(4, settings.ResolutionScale);
        Assert.Equal(PsxRenderBackendIds.Software, settings.BackendId);
    }

    [Fact]
    public void ReStationLogger_TryHeartbeatProgress_EnforcesPositiveTotalAndEmits()
    {
        Directory.CreateDirectory(_tempRoot);
        using var logger = new ReStationLogger(_tempRoot, enabled: true);

        bool emitted = logger.TryHeartbeat(completed: 1, total: 0, item: "verify", force: true);

        Assert.True(emitted);
        Assert.Equal(1, logger.HeartbeatCount);
        string logText = File.ReadAllText(logger.LogFilePath);
        Assert.Contains("[HEARTBEAT]", logText, StringComparison.Ordinal);
    }

    [Fact]
    public void ReStationLogger_DevLogsRoot_CreatesDevLogsCleanlyWhenEnabled()
    {
        string? devLogsDir = ReStationLogger.TryResolveDevLogsDirectory();
        Assert.False(string.IsNullOrWhiteSpace(devLogsDir));

        using var devLogger = new ReStationLogger(devLogsDir!, enabled: true, fileName: "dev-verification.log");
        devLogger.Info("DEV", "RS-06 dev-mode logging verified cleanly under Dev\\logs.");

        Assert.True(Directory.Exists(devLogsDir));
        Assert.True(File.Exists(devLogger.LogFilePath));
        Assert.Contains("RS-06 dev-mode logging verified", File.ReadAllText(devLogger.LogFilePath), StringComparison.Ordinal);
    }

    [Fact]
    public void RecompSettings_DefaultsAndSchemaB_Enforce4By3AspectAndModernInputModes()
    {
        var defaults = new RecompSettings();
        Assert.False(defaults.IsWidescreen16By9);
        Assert.Equal(NativeInputModeIds.ControllerDpadQuickActions, defaults.ControllerDpadMode);
        Assert.Equal(NativeInputModeIds.IsometricMoveCropGrid, defaults.IsometricMoveMode);

        defaults.ControllerDpadMode = "DPADWALKSTICKRUN";
        defaults.IsometricMoveMode = "SCREENSPACE";
        defaults.Clamp();
        Assert.Equal(NativeInputModeIds.ControllerDpadWalkStickRun, defaults.ControllerDpadMode);
        Assert.Equal(NativeInputModeIds.IsometricMoveScreenSpace, defaults.IsometricMoveMode);

        Directory.CreateDirectory(_tempRoot);
        string cfgPath = Path.Combine(_tempRoot, "schema-b-settings.json");
        defaults.Save(cfgPath);
        RecompSettings reloaded = RecompSettings.Load(cfgPath).Value;
        Assert.False(reloaded.IsWidescreen16By9);
        Assert.Equal(NativeInputModeIds.ControllerDpadWalkStickRun, reloaded.ControllerDpadMode);
        Assert.Equal(NativeInputModeIds.IsometricMoveScreenSpace, reloaded.IsometricMoveMode);

        reloaded.ControllerDpadMode = "invalid-mode";
        reloaded.IsometricMoveMode = "invalid-iso";
        reloaded.Clamp();
        Assert.Equal(NativeInputModeIds.ControllerDpadQuickActions, reloaded.ControllerDpadMode);
        Assert.Equal(NativeInputModeIds.IsometricMoveCropGrid, reloaded.IsometricMoveMode);
    }
}
