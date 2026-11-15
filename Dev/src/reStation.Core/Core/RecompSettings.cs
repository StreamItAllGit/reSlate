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
    [JsonPropertyName("isWidescreen16By9")] public bool IsWidescreen16By9 { get; set; } = false;
    [JsonPropertyName("isPgxpEnabled")] public bool IsPgxpEnabled { get; set; } = true;
    [JsonPropertyName("isFullscreen")] public bool IsFullscreen { get; set; }
    [JsonPropertyName("controlMode")] public ControlMode ControlMode { get; set; } = ControlMode.ModernWasdMouse;
    [JsonPropertyName("controllerDpadMode")] public string ControllerDpadMode { get; set; } = NativeInputModeIds.ControllerDpadQuickActions;
    [JsonPropertyName("isometricMoveMode")] public string IsometricMoveMode { get; set; } = NativeInputModeIds.IsometricMoveCropGrid;
    [JsonPropertyName("executionTierMode")] public string ExecutionTierMode { get; set; } = ReStationExecutionTierModes.Default;
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
    [JsonPropertyName("inputProfileMode")] public InputProfileMode InputProfileMode { get; set; } = InputProfileMode.PerGameModern;
    [JsonPropertyName("modernActionBindings"), JsonConverter(typeof(ModernBindingsJsonConverter))] public Dictionary<string, int[]>? ModernActionBindings { get; set; }
    [JsonPropertyName("modernControllerBindings"), JsonConverter(typeof(ModernBindingsJsonConverter))] public Dictionary<string, int[]>? ModernControllerBindings { get; set; }
    /// <summary>Preserves forward/unknown settings keys across load-save cycles (Standard 19 §2.3).</summary>
    [JsonExtensionData] public IDictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>Normalizes every field into its legal range (called after load and before save).</summary>
    public void Clamp()
    {
        SchemaVersion = SchemaVersion >= CurrentSchemaVersion ? SchemaVersion : CurrentSchemaVersion;
        AppId = string.IsNullOrWhiteSpace(AppId) ? ReStationEngineInfo.AppId : AppId.Trim();
        ResolutionScale = ResolutionScale <= 1 ? 1 : ResolutionScale <= 2 ? 2 : ResolutionScale <= 4 ? 4 : 8;
        BackendId = string.Equals(BackendId, PsxRenderBackendIds.Software, StringComparison.OrdinalIgnoreCase)
            ? PsxRenderBackendIds.Software
            : PsxRenderBackendIds.OpenGl;
        ControllerDpadMode = NativeInputModeIds.NormalizeControllerDpadMode(ControllerDpadMode);
        IsometricMoveMode = NativeInputModeIds.NormalizeIsometricMoveMode(IsometricMoveMode);
        ExecutionTierMode = ReStationExecutionTierModes.Normalize(ExecutionTierMode);
        MouseSensitivity = double.IsFinite(MouseSensitivity) ? Math.Clamp(MouseSensitivity, 0.1, 10.0) : 1.0;
        MasterVolume = double.IsFinite(MasterVolume) ? Math.Clamp(MasterVolume, 0.0, 1.0) : 0.85;
        TargetFps = TargetFps <= 0 ? 0 : Math.Clamp(TargetFps, 30, 500);
        if (!Enum.IsDefined(InputProfileMode)) InputProfileMode = InputProfileMode.PerGameModern;
        KeyBindings ??= new KeyBindingsConfig();
        KeyBindings.Clamp(ControlMode);
        ModernActionBindings = NormalizeBindingsMap(ModernActionBindings, isController: false);
        ModernControllerBindings = NormalizeBindingsMap(ModernControllerBindings, isController: true);
    }

    public IReadOnlyList<int> GetModernActionBindings(GameActionDescriptor action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (ModernActionBindings is not null && ModernActionBindings.TryGetValue(action.ActionId, out int[]? bound))
            return bound;
        return action.DefaultBindingCodes;
    }

    public IReadOnlyList<int> GetModernControllerBindings(GameActionDescriptor action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (ModernControllerBindings is not null && ModernControllerBindings.TryGetValue(action.ActionId, out int[]? bound))
            return bound;
        return action.DefaultControllerCodes;
    }

    public int GetModernActionBinding(GameActionDescriptor action)
    {
        IReadOnlyList<int> list = GetModernActionBindings(action);
        return list.Count > 0 ? list[0] : 0;
    }

    public void SetModernActionBinding(string actionId, int bindingCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);
        int norm = KeyBindingsConfig.NormalizeBindingCode(bindingCode, 0);
        ModernActionBindings ??= new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
        ModernActionBindings[actionId] = norm > 0 ? new[] { norm } : Array.Empty<int>();
    }

    public void SetModernActionSlot(GameActionDescriptor action, int slotIndex, int bindingCode)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (slotIndex is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(slotIndex));
        if (slotIndex == 3) { SetModernControllerSlot(action, bindingCode); return; }
        int norm = KeyBindingsConfig.NormalizeBindingCode(bindingCode, 0);
        if (norm <= 0) { ClearModernActionSlot(action, slotIndex); return; }
        var list = new List<int>(GetModernActionBindings(action));
        list.RemoveAll(c => c == norm);
        if (slotIndex < list.Count) list[slotIndex] = norm;
        else list.Add(norm);
        if (list.Count > GameActionDescriptor.MaxBindingsPerDevice)
            list.RemoveRange(GameActionDescriptor.MaxBindingsPerDevice, list.Count - GameActionDescriptor.MaxBindingsPerDevice);
        ModernActionBindings ??= new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
        ModernActionBindings[action.ActionId] = list.ToArray();
    }

    public void SetModernControllerSlot(GameActionDescriptor action, int controllerCode)
    {
        ArgumentNullException.ThrowIfNull(action);
        int norm = KeyBindingsConfig.NormalizeControllerCode(controllerCode, 0);
        ModernControllerBindings ??= new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
        if (norm <= 0) { ModernControllerBindings[action.ActionId] = Array.Empty<int>(); return; }
        var list = new List<int>(GetModernControllerBindings(action));
        list.RemoveAll(c => c == norm);
        if (list.Count == 0) list.Add(norm);
        else list[0] = norm;
        if (list.Count > GameActionDescriptor.MaxBindingsPerDevice)
            list.RemoveRange(GameActionDescriptor.MaxBindingsPerDevice, list.Count - GameActionDescriptor.MaxBindingsPerDevice);
        ModernControllerBindings[action.ActionId] = list.ToArray();
    }

    public void ClearModernActionSlot(GameActionDescriptor action, int slotIndex)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (slotIndex is < 1 or > 3) return;
        if (slotIndex == 3)
        {
            ModernControllerBindings ??= new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
            ModernControllerBindings[action.ActionId] = Array.Empty<int>();
            return;
        }
        var list = new List<int>(GetModernActionBindings(action));
        if (slotIndex < list.Count) list.RemoveAt(slotIndex);
        ModernActionBindings ??= new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
        ModernActionBindings[action.ActionId] = list.ToArray();
    }

    public void ResetModernActionBindings()
    {
        ModernActionBindings = null;
        ModernControllerBindings = null;
    }

    private static Dictionary<string, int[]>? NormalizeBindingsMap(Dictionary<string, int[]>? rawMap, bool isController)
    {
        if (rawMap is null) return null;
        var normalized = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, int[]? values) in rawMap)
        {
            if (string.IsNullOrWhiteSpace(key)) continue;
            var slots = new List<int>(GameActionDescriptor.MaxBindingsPerDevice);
            if (values is not null)
            {
                foreach (int raw in values)
                {
                    int clean = isController
                        ? KeyBindingsConfig.NormalizeControllerCode(raw, 0)
                        : KeyBindingsConfig.NormalizeBindingCode(raw, 0);
                    if (clean > 0 && !slots.Contains(clean))
                    {
                        slots.Add(clean);
                        if (slots.Count >= GameActionDescriptor.MaxBindingsPerDevice) break;
                    }
                }
            }
            normalized[key] = slots.Count == 0 ? Array.Empty<int>() : slots.ToArray();
        }
        return normalized;
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

/// <summary>Deserializes legacy scalar <c>int</c> or multi-bind <c>int[]</c> modern binding entries into normalized <c>int[]</c> slots.</summary>
public sealed class ModernBindingsJsonConverter : JsonConverter<Dictionary<string, int[]>?>
{
    public override Dictionary<string, int[]>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Expected JSON object for modern action bindings.");
        var map = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) return map;
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            string key = reader.GetString() ?? string.Empty;
            if (!reader.Read()) break;
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int scalar))
            {
                if (!string.IsNullOrWhiteSpace(key)) map[key] = scalar > 0 ? new[] { scalar } : Array.Empty<int>();
            }
            else if (reader.TokenType == JsonTokenType.StartArray)
            {
                var list = new List<int>(GameActionDescriptor.MaxBindingsPerDevice);
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int item) && item > 0 && !list.Contains(item) && list.Count < GameActionDescriptor.MaxBindingsPerDevice)
                        list.Add(item);
                }
                if (!string.IsNullOrWhiteSpace(key)) map[key] = list.Count == 0 ? Array.Empty<int>() : list.ToArray();
            }
            else if (reader.TokenType == JsonTokenType.Null)
            {
                if (!string.IsNullOrWhiteSpace(key)) map[key] = Array.Empty<int>();
            }
            else
            {
                reader.Skip();
            }
        }
        return map;
    }

    public override void Write(Utf8JsonWriter writer, Dictionary<string, int[]>? value, JsonSerializerOptions options)
    {
        if (value is null) { writer.WriteNullValue(); return; }
        writer.WriteStartObject();
        foreach (KeyValuePair<string, int[]> kv in value)
        {
            writer.WritePropertyName(kv.Key);
            writer.WriteStartArray();
            if (kv.Value is not null)
            {
                int count = 0;
                foreach (int code in kv.Value)
                {
                    if (code <= 0) continue;
                    writer.WriteNumberValue(code);
                    if (++count >= GameActionDescriptor.MaxBindingsPerDevice) break;
                }
            }
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }
}
