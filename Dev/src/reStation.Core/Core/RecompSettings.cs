// @module: reStation.Core.RecompSettings
// @purpose: Standard 18 camelCase settings.json model with clamp rules and Foundation GranularSettingsManager atomic load/self-heal/save.
// @depends-on: System.Text.Json, Foundation.Core, reStation.Contracts, reStation.Core.Input
// @exports: ControlMode, FrameRateMode, RecompSettings
// @owner: devv-cpu-cd (T2) — public property names/types are T1-locked; extend additively only.

using System.Text.Json;
using System.Text.Json.Serialization;
using Foundation.Core;
using reStation.Contracts;
using reStation.Core.Input;

namespace reStation.Core;

/// <summary>Host control scheme (<c>controlMode</c>: modernWasdMouse | classicTank).</summary>
public enum ControlMode { ModernWasdMouse, ClassicTank }

/// <summary>Presentation cadence (<c>frameRateMode</c>: original | enhanced).</summary>
public enum FrameRateMode { Original, Enhanced }

/// <summary>Persisted reStation runtime settings (settings.json, schemaVersion 1).</summary>
public sealed class RecompSettings
{
    public const int CurrentSchemaVersion = 1;
    public static readonly IReadOnlyList<int> AllowedResolutionScales = new[] { 1, 2, 4, 8 };

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    [JsonPropertyName("appId")] public string AppId { get; set; } = ReStationEngineInfo.AppId;
    [JsonPropertyName("updatedAtUtc")] public string? UpdatedAtUtc { get; set; }
    [JsonPropertyName("useGlobalFormatRules")] public bool UseGlobalFormatRules { get; set; } = true;
    [JsonPropertyName("resolutionScale")] public int ResolutionScale { get; set; } = 4;
    [JsonPropertyName("backendId")] public string BackendId { get; set; } = PsxRenderBackendIds.OpenGl;
    [JsonPropertyName("isWidescreen16By9")] public bool IsWidescreen16By9 { get; set; } = true;
    [JsonPropertyName("isPgxpEnabled")] public bool IsPgxpEnabled { get; set; } = true;
    [JsonPropertyName("isFullscreen")] public bool IsFullscreen { get; set; }
    [JsonPropertyName("controlMode")] public ControlMode ControlMode { get; set; } = ControlMode.ModernWasdMouse;
    [JsonPropertyName("mouseSensitivity")] public double MouseSensitivity { get; set; } = 1.0;
    [JsonPropertyName("shouldFastLoadData")] public bool ShouldFastLoadData { get; set; } = true;
    [JsonPropertyName("masterVolume")] public double MasterVolume { get; set; } = 0.85;
    [JsonPropertyName("lastDiscPath")] public string? LastDiscPath { get; set; }
    [JsonPropertyName("lastModulePath")] public string? LastModulePath { get; set; }
    [JsonPropertyName("isFpsOverlayEnabled")] public bool IsFpsOverlayEnabled { get; set; }
    [JsonPropertyName("shouldEnableRuntimeLogs")] public bool ShouldEnableRuntimeLogs { get; set; }
    [JsonPropertyName("frameRateMode")] public FrameRateMode FrameRateMode { get; set; } = FrameRateMode.Enhanced;
    /// <summary>Target presentation FPS; 0 = unlocked.</summary>
    [JsonPropertyName("targetFps")] public int TargetFps { get; set; } = 60;
    [JsonPropertyName("isVsyncEnabled")] public bool IsVsyncEnabled { get; set; }
    [JsonPropertyName("keyBindings")] public KeyBindingsConfig KeyBindings { get; set; } = new();
    /// <summary>Preserves forward/unknown settings keys across load-save cycles (Standard 19 §2.3).</summary>
    [JsonExtensionData] public IDictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>Normalizes every field into its legal range (called after load and before save).</summary>
    public void Clamp()
    {
        SchemaVersion = CurrentSchemaVersion;
        AppId = ReStationEngineInfo.AppId;
        ResolutionScale = ResolutionScale <= 1 ? 1 : ResolutionScale <= 2 ? 2 : ResolutionScale <= 4 ? 4 : 8;
        BackendId = string.Equals(BackendId, PsxRenderBackendIds.Software, StringComparison.OrdinalIgnoreCase)
            ? PsxRenderBackendIds.Software
            : PsxRenderBackendIds.OpenGl;
        MouseSensitivity = double.IsFinite(MouseSensitivity) ? Math.Clamp(MouseSensitivity, 0.1, 10.0) : 1.0;
        MasterVolume = double.IsFinite(MasterVolume) ? Math.Clamp(MasterVolume, 0.0, 1.0) : 0.85;
        TargetFps = TargetFps <= 0 ? 0 : Math.Clamp(TargetFps, 30, 500);
        KeyBindings ??= new KeyBindingsConfig();
        KeyBindings.Clamp(ControlMode);
    }

    /// <summary>Loads settings atomically (self-healing from .bak or defaults) and clamps the result.</summary>
    public static SettingsLoadOutcome<RecompSettings> Load(string configFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configFilePath);
        string full = Path.GetFullPath(configFilePath);
        var manager = new GranularSettingsManager(Path.GetDirectoryName(full)!);
        SettingsLoadOutcome<RecompSettings> outcome = manager.LoadOrSelfHeal(full, static () => new RecompSettings(), JsonOptions);
        outcome.Value.Clamp();
        return outcome;
    }

    /// <summary>Clamps, stamps <see cref="UpdatedAtUtc"/>, and saves atomically with config_history snapshot rotation.</summary>
    public void Save(string configFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configFilePath);
        string full = Path.GetFullPath(configFilePath);
        Clamp();
        UpdatedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);
        new GranularSettingsManager(Path.GetDirectoryName(full)!).SaveAtomically(full, this, JsonOptions);
    }
}
