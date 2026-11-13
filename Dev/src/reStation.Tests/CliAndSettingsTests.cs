// @module: reStation.Tests.CliAndSettingsTests
// @purpose: Validates CliOptions fail-closed parsing, RecompSettings clamping/self-heal, ExtensionData preservation, telemetry schema, and logger containment.
// @depends-on: Xunit, System.Text.Json, Foundation.Core, reStation.Contracts, reStation.Core
// @exports: CliAndSettingsTests
// @owner: devv-pkg-test (T8)

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
            "--open-settings", "--enable-logs", "--show-fps", "--help"
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
        Assert.True(parsed.OpenSettings && parsed.EnableLogs && parsed.ShowFps && parsed.ShowHelp);
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
        var settings = new RecompSettings
        {
            SchemaVersion = 999,
            AppId = "rogue",
            ResolutionScale = 5,
            BackendId = "DirectX",
            MouseSensitivity = double.NaN,
            MasterVolume = 4.2,
            TargetFps = 999
        };

        settings.Clamp();

        Assert.Equal(RecompSettings.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.Equal(ReStationEngineInfo.AppId, settings.AppId);
        Assert.Equal(8, settings.ResolutionScale);
        Assert.Equal(PsxRenderBackendIds.OpenGl, settings.BackendId);
        Assert.Equal(1.0, settings.MouseSensitivity);
        Assert.Equal(1.0, settings.MasterVolume);
        Assert.Equal(500, settings.TargetFps);
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
}
