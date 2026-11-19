// @module: reStation.Core.SettingsMigrationHelper
// @purpose: Atomic non-destructive migration of legacy <dataDir>\settings.json (+ .bak) into <dataDir>\config\settings.json.
// @depends-on: System, System.IO
// @exports: SettingsMigrationResult, SettingsMigrationHelper

namespace reStation.Core;

/// <summary>Outcome of checking or migrating legacy <c>&lt;dataDir&gt;\settings.json</c> to <c>config\settings.json</c>.</summary>
public sealed record SettingsMigrationResult(
    bool Migrated,
    bool MigratedBackup,
    string LegacyPath,
    string CanonicalPath);

/// <summary>Migrates legacy v0.1.x/v0.2.x settings files non-destructively into <c>&lt;dataDir&gt;\config\settings.json</c>.</summary>
public static class SettingsMigrationHelper
{
    public const string CanonicalRelativePath = "config/settings.json";
    public const string LegacyFileName = "settings.json";

    /// <summary>Returns the canonical <c>&lt;dataDir&gt;\config\settings.json</c> path.</summary>
    public static string ResolveCanonicalSettingsPath(string dataDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDir);
        return Path.GetFullPath(Path.Combine(dataDir, "config", LegacyFileName));
    }

    /// <summary>
    /// Atomically copies <c>&lt;dataDir&gt;\settings.json</c> (and <c>.bak</c> if present) to <c>&lt;dataDir&gt;\config\settings.json</c>
    /// when the canonical file does not yet exist, leaving the legacy file intact.
    /// </summary>
    public static SettingsMigrationResult MigrateLegacySettingsIfNeeded(string dataDir, string? canonicalConfigPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDir);
        string fullData = Path.GetFullPath(dataDir);
        string legacyPath = Path.Combine(fullData, LegacyFileName);
        string targetPath = string.IsNullOrWhiteSpace(canonicalConfigPath)
            ? ResolveCanonicalSettingsPath(fullData)
            : Path.GetFullPath(canonicalConfigPath);

        if (string.Equals(legacyPath, targetPath, StringComparison.OrdinalIgnoreCase))
            return new SettingsMigrationResult(false, false, legacyPath, targetPath);

        bool migratedPrimary = TryAtomicCopyIfMissing(legacyPath, targetPath);
        bool migratedBak = TryAtomicCopyIfMissing(legacyPath + ".bak", targetPath + ".bak");
        return new SettingsMigrationResult(migratedPrimary, migratedBak, legacyPath, targetPath);
    }

    /// <summary>
    /// Atomically migrates legacy <c>&lt;dataDir&gt;\settings.json</c> when <paramref name="fullConfigPath"/> targets <c>config\settings.json</c>.
    /// </summary>
    public static SettingsMigrationResult MigrateForConfigPathIfNeeded(string fullConfigPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullConfigPath);
        string full = Path.GetFullPath(fullConfigPath);
        if (!string.Equals(Path.GetFileName(full), LegacyFileName, StringComparison.OrdinalIgnoreCase))
            return new SettingsMigrationResult(false, false, full, full);

        string? configDir = Path.GetDirectoryName(full);
        if (string.IsNullOrWhiteSpace(configDir) ||
            !string.Equals(Path.GetFileName(configDir), "config", StringComparison.OrdinalIgnoreCase))
        {
            return new SettingsMigrationResult(false, false, full, full);
        }

        string? dataDir = Path.GetDirectoryName(configDir);
        if (string.IsNullOrWhiteSpace(dataDir))
            return new SettingsMigrationResult(false, false, full, full);

        return MigrateLegacySettingsIfNeeded(dataDir, full);
    }

    private static bool TryAtomicCopyIfMissing(string sourcePath, string targetPath)
    {
        if (!File.Exists(sourcePath) || File.Exists(targetPath))
            return false;

        string? targetDir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(targetDir))
            Directory.CreateDirectory(targetDir);

        string tmpPath = $"{targetPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.Copy(sourcePath, tmpPath, overwrite: true);
            if (File.Exists(targetPath))
                return false;
            File.Move(tmpPath, targetPath, overwrite: false);
            return true;
        }
        catch (IOException) when (File.Exists(targetPath))
        {
            return false;
        }
        finally
        {
            if (File.Exists(tmpPath))
            {
                try { File.Delete(tmpPath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
