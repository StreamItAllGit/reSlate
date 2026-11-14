// @module: reStation.Core.Input.GlobalControllerProfile
// @purpose: Standard 19 domain settings envelope (settings.controls.json) for Tier 1 engine-wide 1:1 PS1 controller bindings.
// @depends-on: System.Globalization, System.Text.Json, Foundation.Core, reStation.Contracts, reStation.Core
// @exports: GlobalControllerProfile

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Foundation.Core;
using reStation.Contracts;

namespace reStation.Core.Input;

/// <summary>Tier 1 engine-wide 1:1 PS1 controller profile persisted atomically in <c>settings.controls.json</c>.</summary>
public sealed class GlobalControllerProfile
{
    public const int CurrentSchemaVersion = 1;
    public const string FileName = "settings.controls.json";

    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    [JsonPropertyName("appId")] public string AppId { get; set; } = ReStationEngineInfo.AppId;
    [JsonPropertyName("domain")] public string Domain { get; set; } = "controls";
    [JsonPropertyName("updatedAtUtc")] public string? UpdatedAtUtc { get; set; }
    [JsonPropertyName("keyBindings")] public KeyBindingsConfig KeyBindings { get; set; } = new();
    /// <summary>Preserves forward/unknown keys across load-save cycles (Standard 19 §2.3).</summary>
    [JsonExtensionData] public IDictionary<string, JsonElement>? ExtensionData { get; set; }

    public void Clamp()
    {
        SchemaVersion = CurrentSchemaVersion;
        AppId = ReStationEngineInfo.AppId;
        Domain = "controls";
        KeyBindings ??= new KeyBindingsConfig();
        KeyBindings.Clamp(ControlMode.ModernWasdMouse);
    }

    /// <summary>
    /// Resolves <c>settings.controls.json</c>; fails closed inside the sandbox boundary under <c>--test-mode</c> (zero host/AppData writes).
    /// </summary>
    public static string ResolvePath(SandboxEnvironment sandbox, string? appBaseDirOverride = null)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        if (sandbox.TestMode)
        {
            string boundary = CliOptions.ResolveSandboxBoundary(sandbox);
            string dataRoot = Path.Combine(boundary, "data");
            string candidate = string.Equals(
                Path.GetFullPath(boundary),
                Path.GetFullPath(sandbox.DataDirectory),
                StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(sandbox.DataDirectory, FileName)
                : Path.Combine(dataRoot, FileName);
            return SandboxEnvironment.AssertPathContained(candidate, boundary);
        }

        string? engineRoot = TryResolveReEngineRoot(appBaseDirOverride ?? AppContext.BaseDirectory);
        if (!string.IsNullOrWhiteSpace(engineRoot))
        {
            string engineCandidate = Path.Combine(engineRoot, "data", FileName);
            if (IsDirectoryWritable(Path.GetDirectoryName(engineCandidate)!))
                return engineCandidate;
        }

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(appData, "reStation", FileName);
    }

    /// <summary>Loads or self-heals <c>settings.controls.json</c> and clamps all binding codes.</summary>
    public static GlobalControllerProfile LoadOrDefault(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        string full = Path.GetFullPath(filePath);
        string dir = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(dir);
        SettingsLoadOutcome<GlobalControllerProfile> outcome = new GranularSettingsManager(dir)
            .LoadOrSelfHeal(full, static () => new GlobalControllerProfile(), RecompSettings.JsonOptions);
        outcome.Value.Clamp();
        return outcome.Value;
    }

    /// <summary>Clamps, stamps UTC timestamp, and writes atomically via <see cref="GranularSettingsManager"/>.</summary>
    public void Save(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        string full = Path.GetFullPath(filePath);
        string dir = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(dir);
        Clamp();
        UpdatedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        new GranularSettingsManager(dir).SaveAtomically(full, this, RecompSettings.JsonOptions);
    }

    private static string? TryResolveReEngineRoot(string? baseDir)
    {
        if (string.IsNullOrWhiteSpace(baseDir)) return null;
        var dir = new DirectoryInfo(Path.GetFullPath(baseDir));
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "reEngine.exe"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    private static bool IsDirectoryWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            string probe = Path.Combine(dir, $".write-probe-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
