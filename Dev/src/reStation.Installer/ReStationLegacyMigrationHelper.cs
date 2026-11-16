// @module: reStation.Installer.ReStationLegacyMigrationHelper
// @purpose: Read-only v0.1.1 artifact detector, opt-in --migrate-legacy migrator (preserving data\rom\), and Standard 18 settings persistence.
// @depends-on: System, System.Collections.Generic, System.IO, System.Linq, System.Text.Json, System.Text.Json.Nodes, System.Windows.Forms, Foundation.Core
// @exports: LegacyV011DetectionResult, LegacyMigrationResult, ReStationLegacyMigrationHelper

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using Foundation.Core;

namespace reStation.Installer;

internal sealed record LegacyV011DetectionResult(
    IReadOnlyList<string> CandidateRoots,
    IReadOnlyList<string> StrandedDataDirs,
    IReadOnlyList<string> StrandedCrashFiles,
    int StrandedFileCount,
    int PreservedRomCount,
    string? SettingsFilePath,
    string? RomDirPath)
{
    public bool HasStrandedArtifacts => StrandedFileCount > 0 || StrandedCrashFiles.Count > 0;
}

internal sealed record LegacyMigrationResult(
    bool MigratedSettings,
    int PreservedRomFiles,
    IReadOnlyList<string> StrandedArtifactsFound,
    bool StrandedArtifactsCleaned,
    string Summary);

internal static class ReStationLegacyMigrationHelper
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    public static string NormalizeProfileId(string? rawProfile)
    {
        if (string.IsNullOrWhiteSpace(rawProfile))
        {
            return ReStationInstallerTheme.DefaultInstallProfile;
        }

        return rawProfile.Trim().ToLowerInvariant() switch
        {
            "lightweight" => "lightweight",
            "full" => "full",
            _ => throw new ArgumentException($"Invalid --profile '{rawProfile}'. Expected 'Lightweight' or 'Full'.")
        };
    }

    public static IReadOnlyList<string> ResolveLegacyV011EngineRoots(bool isolated, string? isolatedDataDir)
    {
        if (isolated)
        {
            if (string.IsNullOrWhiteSpace(isolatedDataDir))
            {
                return Array.Empty<string>();
            }

            string fullIsolated = Path.GetFullPath(isolatedDataDir);
            string[] isolatedCandidates =
            [
                AppEnvironment.AssertPathContained(
                    Path.Combine(fullIsolated, "legacy-programs", ReStationInstallerTheme.SuiteName, "engines", ReStationInstallerTheme.EngineId, "v0.1.1"),
                    fullIsolated),
                AppEnvironment.AssertPathContained(
                    Path.Combine(fullIsolated, "legacy-programs", ReStationInstallerTheme.EngineId, "v0.1.1"),
                    fullIsolated)
            ];
            return isolatedCandidates.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        var roots = new List<string>();
        string? envLegacy = Environment.GetEnvironmentVariable("RESTATION_LEGACY_DIR");
        if (!string.IsNullOrWhiteSpace(envLegacy))
        {
            string fullEnv = Path.GetFullPath(envLegacy.Trim());
            if (Directory.Exists(fullEnv)) roots.Add(fullEnv);
            return roots;
        }

        string driveRoot = $"{(char)68}:{Path.DirectorySeparatorChar}";
        string programsDrive = Path.Combine(driveRoot, "Programs");
        string localPrograms = Path.Combine(ResolveLocalAppData(), "Programs");
        string[] candidates =
        [
            Path.Combine(programsDrive, ReStationInstallerTheme.SuiteName, "engines", ReStationInstallerTheme.EngineId, "v0.1.1"),
            Path.Combine(programsDrive, ReStationInstallerTheme.EngineId, "v0.1.1"),
            Path.Combine(localPrograms, ReStationInstallerTheme.SuiteName, "engines", ReStationInstallerTheme.EngineId, "v0.1.1"),
            Path.Combine(localPrograms, ReStationInstallerTheme.StudioName, ReStationInstallerTheme.SuiteName, "engines", ReStationInstallerTheme.EngineId, "v0.1.1")
        ];

        foreach (string c in candidates)
        {
            string full = Path.GetFullPath(c);
            if (Directory.Exists(full) && !roots.Contains(full, StringComparer.OrdinalIgnoreCase))
            {
                roots.Add(full);
            }
        }
        return roots;
    }

    /// <summary>Strictly read-only scan detecting stranded v0.1.1 data files and crash JSONs without modifying disk state.</summary>
    public static LegacyV011DetectionResult DetectLegacyV011HostArtifacts(bool isolated, string? isolatedDataDir)
    {
        IReadOnlyList<string> roots = ResolveLegacyV011EngineRoots(isolated, isolatedDataDir);
        var dataDirs = new List<string>();
        var crashFiles = new List<string>();
        int strandedFileCount = 0;
        int romCount = 0;
        string? settingsPath = null;
        string? romDirPath = null;

        foreach (string root in roots)
        {
            foreach (string sub in new[] { Path.Combine(root, "app"), root })
            {
                string dataDir = Path.Combine(sub, "data");
                if (Directory.Exists(dataDir) && !dataDirs.Contains(dataDir, StringComparer.OrdinalIgnoreCase))
                {
                    dataDirs.Add(dataDir);
                    string romDir = Path.Combine(dataDir, "rom");
                    foreach (string file in Directory.EnumerateFiles(dataDir, "*", SearchOption.AllDirectories))
                    {
                        if (IsUnderDirectory(file, romDir))
                        {
                            romCount++;
                            romDirPath ??= romDir;
                        }
                        else
                        {
                            strandedFileCount++;
                        }
                    }

                    string candSettings = Path.Combine(dataDir, "settings.json");
                    if (settingsPath is null && File.Exists(candSettings)) settingsPath = candSettings;
                }

                string crashDir = Path.Combine(sub, "logs", "crashes");
                if (Directory.Exists(crashDir))
                {
                    foreach (string crash in Directory.EnumerateFiles(crashDir, "crash_*.json", SearchOption.TopDirectoryOnly))
                    {
                        if (!crashFiles.Contains(crash, StringComparer.OrdinalIgnoreCase)) crashFiles.Add(crash);
                    }
                }
            }
        }

        return new LegacyV011DetectionResult(roots, dataDirs, crashFiles, strandedFileCount, romCount, settingsPath, romDirPath);
    }

    public static void PersistInstallProfileSettings(
        string installDir,
        string profileId,
        string? suiteDir,
        string? reEngineDir)
    {
        string dataDir = Path.Combine(Path.GetFullPath(installDir), "data");
        Directory.CreateDirectory(dataDir);
        string settingsPath = Path.Combine(dataDir, "settings.json");

        JsonObject root = File.Exists(settingsPath)
            ? JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject ?? new JsonObject()
            : new JsonObject();

        root["schemaVersion"] = 1;
        root["appId"] = ReStationInstallerTheme.AppId;
        root["installProfile"] = NormalizeProfileId(profileId);
        if (!string.IsNullOrWhiteSpace(reEngineDir)) root["reEngineInstallDir"] = Path.GetFullPath(reEngineDir);
        if (!string.IsNullOrWhiteSpace(suiteDir)) root["suiteInstallDir"] = Path.GetFullPath(suiteDir);
        root["umbrellaSubpath"] = ReStationInstallerTheme.UmbrellaSubpath;
        root["decompSchemaVersion"] ??= "1.0.0";
        root["goalDecompTier"] ??= "D3";
        root["updatedAtUtc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

        string tmpPath = settingsPath + ".tmp";
        File.WriteAllText(tmpPath, root.ToJsonString(IndentedJson));
        File.Move(tmpPath, settingsPath, overwrite: true);
    }

    public static LegacyMigrationResult TryMigrateLegacyHostArtifacts(
        string targetInstallDir,
        bool isolated,
        string? isolatedDataDir,
        bool silent,
        bool dryRun,
        bool confirmCleanupFlag)
    {
        LegacyV011DetectionResult detected = DetectLegacyV011HostArtifacts(isolated, isolatedDataDir);
        if (detected.CandidateRoots.Count == 0)
        {
            return new LegacyMigrationResult(false, 0, Array.Empty<string>(), false, "No legacy v0.1.1 host installation found.");
        }

        bool migratedSettings = false;
        int preservedRoms = 0;
        string targetDataDir = Path.Combine(Path.GetFullPath(targetInstallDir), "data");

        if (!dryRun)
        {
            Directory.CreateDirectory(targetDataDir);
            string targetSettings = Path.Combine(targetDataDir, "settings.json");
            if (!string.IsNullOrWhiteSpace(detected.SettingsFilePath) && File.Exists(detected.SettingsFilePath) && !File.Exists(targetSettings))
            {
                File.Copy(detected.SettingsFilePath, targetSettings, overwrite: false);
                migratedSettings = true;
            }

            if (!string.IsNullOrWhiteSpace(detected.RomDirPath) && Directory.Exists(detected.RomDirPath))
            {
                string targetRomDir = Path.Combine(targetDataDir, "rom");
                foreach (string srcFile in Directory.EnumerateFiles(detected.RomDirPath, "*", SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(detected.RomDirPath, srcFile);
                    string dstFile = Path.Combine(targetRomDir, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dstFile)!);
                    if (!File.Exists(dstFile))
                    {
                        File.Copy(srcFile, dstFile, overwrite: false);
                        preservedRoms++;
                    }
                }
            }
        }

        var strandedPaths = detected.StrandedDataDirs.Concat(detected.StrandedCrashFiles).ToList();
        bool cleaned = false;

        if (!dryRun && detected.HasStrandedArtifacts && ShouldAllowCleanup(detected, silent, isolated, confirmCleanupFlag))
        {
            foreach (string dataDir in detected.StrandedDataDirs)
            {
                CleanNonRomContents(dataDir);
            }
            foreach (string crash in detected.StrandedCrashFiles)
            {
                if (File.Exists(crash)) File.Delete(crash);
            }
            cleaned = true;
        }

        string summary = $"Legacy v0.1.1 migration: settings={(migratedSettings ? "migrated" : "unchanged")}, " +
                         $"romsPreserved={preservedRoms}, strandedFiles={detected.StrandedFileCount}, crashFiles={detected.StrandedCrashFiles.Count}, cleaned={cleaned}.";
        return new LegacyMigrationResult(migratedSettings, preservedRoms, strandedPaths, cleaned, summary);
    }

    private static bool ShouldAllowCleanup(
        LegacyV011DetectionResult detected,
        bool silent,
        bool isolated,
        bool confirmCleanupFlag)
    {
        if (!silent && !isolated && OperatingSystem.IsWindows())
        {
            DialogResult choice = MessageBox.Show(
                $"Found {detected.StrandedFileCount} stranded v0.1.1 data file(s) and {detected.StrandedCrashFiles.Count} crash JSON(s).\n\n" +
                $"Preserve your data\\rom\\ files into the v{ReStationInstallerTheme.Version} install and remove the stranded v0.1.1 artifacts?",
                "reStation — Human-Gated Legacy v0.1.1 Cleanup",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            return choice == DialogResult.Yes;
        }
        return confirmCleanupFlag;
    }

    private static void CleanNonRomContents(string dataDir)
    {
        if (!Directory.Exists(dataDir)) return;
        string romDir = Path.Combine(dataDir, "rom");
        foreach (string file in Directory.EnumerateFiles(dataDir, "*", SearchOption.AllDirectories))
        {
            if (!IsUnderDirectory(file, romDir)) File.Delete(file);
        }
        foreach (string sub in Directory.EnumerateDirectories(dataDir, "*", SearchOption.TopDirectoryOnly))
        {
            if (!string.Equals(Path.GetFullPath(sub).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(romDir).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) &&
                !Directory.EnumerateFileSystemEntries(sub).Any())
            {
                Directory.Delete(sub, recursive: true);
            }
        }
    }

    private static bool IsUnderDirectory(string candidateFile, string parentDir)
    {
        string fullParent = Path.GetFullPath(parentDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(candidateFile).StartsWith(fullParent, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveLocalAppData()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(local)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local")
            : local;
    }
}
