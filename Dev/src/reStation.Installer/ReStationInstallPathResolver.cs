// @module: reStation.Installer
// @purpose: Resolve initial reSlate and reEngine install paths from CLI overrides, HKCU history, or sandbox defaults.
// @depends-on: Foundation.Core, reEngine.Core
// @exports: ResolvedWizardInstallPaths, ReStationInstallPathResolver

using System.Text.Json;
using System.Text.RegularExpressions;
using Foundation.Core;
using reEngine.Core.Bootstrap;

namespace reStation.Installer;

/// <summary>
/// Resolved wizard installation paths and initial profile before displaying the setup UI.
/// </summary>
internal sealed record ResolvedWizardInstallPaths(
    string ReEngineInstallDir,
    string InstallDir,
    string ProfileId);

/// <summary>
/// Resolves prefilled reSlate and reEngine installation directories from explicit overrides,
/// existing HKCU Uninstall entries, shared StreamItAll install history, or canonical defaults.
/// </summary>
internal static partial class ReStationInstallPathResolver
{
    private const string ReSlateUninstallSubKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\reSlate";
    private const string ReEngineUninstallSubKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\reEngine";
    private const string ReStationUninstallSubKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\reStation";
    private const string StreamItAllSubKey = @"Software\StreamItAll";
    private const string InstallLocationValueName = "InstallLocation";
    private const string LastInstallBaseDirValueName = "LastInstallBaseDir";

    [GeneratedRegex(@"^v\d+\.\d+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionFolderRegex();

    internal static ResolvedWizardInstallPaths ResolveInitialPaths(
        IPlatformRegistry registry,
        SandboxResolution? activeSandbox,
        bool suppressHostMutations,
        bool isIsolatedTestProcess,
        string? isolatedDataDir,
        string defaultInstallDir,
        string defaultProfileId,
        string? explicitInstallDir = null,
        string? explicitReEngineDir = null,
        string? explicitSuiteDir = null,
        string? explicitProfileId = null)
    {
        if (isIsolatedTestProcess)
        {
            string isoEngine = ResolveExplicitReEngineDir(explicitReEngineDir, explicitSuiteDir)
                ?? ReEngineBootstrapHelper.ResolveDefaultReEngineInstallDir(null, isolatedDataDir, isolatedTest: true);
            string isoInstall = !string.IsNullOrWhiteSpace(explicitInstallDir)
                ? Path.GetFullPath(explicitInstallDir)
                : ReStationInstallerBootstrap.ComputeSideBySideDir(isoEngine);
            string isoProfile = ResolveEffectiveProfile(explicitProfileId, null, defaultProfileId);
            return new ResolvedWizardInstallPaths(isoEngine, isoInstall, isoProfile);
        }

        DiscoveredInstall? discovered = suppressHostMutations
            ? null
            : DiscoverExistingInstall(registry, activeSandbox);

        string? explicitEngine = ResolveExplicitReEngineDir(explicitReEngineDir, explicitSuiteDir);
        string? explicitInstall = !string.IsNullOrWhiteSpace(explicitInstallDir)
            ? Path.GetFullPath(explicitInstallDir)
            : null;

        string? extractedFromExplicitInstall = null;
        if (explicitEngine is null && explicitInstall is not null)
            TryExtractReEngineRoot(explicitInstall, out extractedFromExplicitInstall);

        string effectiveReEngineDir = explicitEngine
            ?? extractedFromExplicitInstall
            ?? discovered?.ReEngineInstallDir
            ?? ResolveCanonicalReEngineDefault(activeSandbox);

        string effectiveInstallDir = explicitInstall
            ?? (explicitEngine is not null
                ? ResolveFromExistingEngineInstall(explicitEngine)
                : discovered?.PreferredInstallDir
                  ?? (discovered?.ReEngineInstallDir is not null
                      ? ResolveFromExistingEngineInstall(discovered.ReEngineInstallDir)
                      : ResolveCanonicalInstallDefault(defaultInstallDir, activeSandbox)));

        string? diskProfile = TryReadSavedInstallProfile(effectiveInstallDir);
        string effectiveProfile = ResolveEffectiveProfile(
            explicitProfileId,
            discovered?.SavedProfileId ?? diskProfile,
            defaultProfileId);

        return new ResolvedWizardInstallPaths(effectiveReEngineDir, effectiveInstallDir, effectiveProfile);
    }

    internal static string ResolveSilentReEngineDir(
        IPlatformRegistry registry,
        SandboxResolution? activeSandbox,
        string? explicitReEngineDir = null,
        string? explicitSuiteDir = null,
        string? isolatedRoot = null,
        bool isolatedTest = false,
        bool suppressHostMutations = false)
    {
        string? explicitEngine = ResolveExplicitReEngineDir(explicitReEngineDir, explicitSuiteDir);
        if (explicitEngine is not null)
            return explicitEngine;

        if (isolatedTest || suppressHostMutations)
            return ReEngineBootstrapHelper.ResolveDefaultReEngineInstallDir(null, isolatedRoot, isolatedTest);

        DiscoveredInstall? discovered = DiscoverExistingInstall(registry, activeSandbox);
        return discovered?.ReEngineInstallDir ?? ResolveCanonicalReEngineDefault(activeSandbox);
    }

    internal static bool TryExtractReEngineRoot(string? rawInstallPath, out string reEngineRoot)
    {
        reEngineRoot = string.Empty;
        if (string.IsNullOrWhiteSpace(rawInstallPath))
            return false;

        string normalized = TrimDirectorySeparators(Path.GetFullPath(rawInstallPath.Trim()));
        DirectoryInfo? current = new(normalized);
        while (current is not null)
        {
            if (string.Equals(current.Name, "engines", StringComparison.OrdinalIgnoreCase) && current.Parent is not null)
            {
                reEngineRoot = TrimDirectorySeparators(current.Parent.FullName);
                return true;
            }

            if (string.Equals(current.Name, ReStationInstallerTheme.SuiteName, StringComparison.OrdinalIgnoreCase))
            {
                reEngineRoot = TrimDirectorySeparators(current.FullName);
                return true;
            }

            current = current.Parent;
        }

        return false;
    }

    internal static string ResolveFromExistingEngineInstall(string reEngineInstallDir)
    {
        return ReStationWizardPathNormalizer.NormalizeWizardStationInstallDir(reEngineInstallDir);
    }

    internal static string? TryReadSavedInstallProfile(string? installDir)
    {
        if (string.IsNullOrWhiteSpace(installDir))
            return null;

        string[] candidates =
        [
            Path.Combine(installDir, "data", "config", "settings.json"),
            Path.Combine(installDir, "data", "settings.json")
        ];

        foreach (string candidate in candidates)
        {
            try
            {
                if (!File.Exists(candidate))
                    continue;

                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(candidate));
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    continue;

                foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
                {
                    if (!string.Equals(prop.Name, "installProfile", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        string? raw = prop.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(raw))
                            return ReStationLegacyMigrationHelper.NormalizeProfileId(raw);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // Corrupt or unreadable legacy config should never crash wizard startup.
            }
        }

        return null;
    }

    private static DiscoveredInstall? DiscoverExistingInstall(
        IPlatformRegistry registry,
        SandboxResolution? activeSandbox)
    {
        string? existingSlateDir = ReadValidatedRegistryPath(registry, ReSlateUninstallSubKey, InstallLocationValueName, activeSandbox);
        string? existingEngineDir = ReadValidatedRegistryPath(registry, ReEngineUninstallSubKey, InstallLocationValueName, activeSandbox);
        string? existingStationDir = ReadValidatedRegistryPath(registry, ReStationUninstallSubKey, InstallLocationValueName, activeSandbox);
        string? lastBaseDir = ReadValidatedRegistryPath(registry, StreamItAllSubKey, LastInstallBaseDirValueName, activeSandbox);

        string? resolvedEngineDir = null;
        string? preferredInstallDir = null;

        if (existingSlateDir is not null)
        {
            if (TryExtractReEngineRoot(existingSlateDir, out string fromSlate))
            {
                resolvedEngineDir = fromSlate;
                preferredInstallDir = ResolveFromExistingEngineInstall(fromSlate);
            }
            else
            {
                string leaf = Path.GetFileName(existingSlateDir);
                preferredInstallDir = VersionFolderRegex().IsMatch(leaf) && Path.GetDirectoryName(existingSlateDir) is string parent
                    ? Path.Combine(parent, $"v{ReStationInstallerTheme.Version}")
                    : existingSlateDir;
            }
        }

        if (resolvedEngineDir is null && existingEngineDir is not null)
        {
            resolvedEngineDir = TryExtractReEngineRoot(existingEngineDir, out string normalizedEngine)
                ? normalizedEngine
                : existingEngineDir;
        }

        if (resolvedEngineDir is null && existingStationDir is not null &&
            TryExtractReEngineRoot(existingStationDir, out string fromStation))
        {
            resolvedEngineDir = fromStation;
        }

        if (resolvedEngineDir is null && lastBaseDir is not null)
            resolvedEngineDir = ResolveReEngineFromBaseDir(lastBaseDir);

        if (resolvedEngineDir is null && preferredInstallDir is null)
            return null;

        preferredInstallDir ??= ResolveFromExistingEngineInstall(resolvedEngineDir!);
        string? savedProfile = TryReadSavedInstallProfile(existingSlateDir)
            ?? TryReadSavedInstallProfile(existingStationDir)
            ?? TryReadSavedInstallProfile(preferredInstallDir);

        return new DiscoveredInstall(resolvedEngineDir, preferredInstallDir, savedProfile);
    }

    private static string ResolveReEngineFromBaseDir(string baseDir)
    {
        string cleanBase = TrimDirectorySeparators(baseDir);
        string leaf = Path.GetFileName(cleanBase);
        if (string.Equals(leaf, ReStationInstallerTheme.SuiteName, StringComparison.OrdinalIgnoreCase))
            return cleanBase;

        string studioRoot = string.Equals(leaf, ReStationInstallerTheme.StudioName, StringComparison.OrdinalIgnoreCase)
            ? cleanBase
            : Path.Combine(cleanBase, ReStationInstallerTheme.StudioName);
        return Path.Combine(studioRoot, ReStationInstallerTheme.SuiteName);
    }

    private static string? ResolveExplicitReEngineDir(string? explicitReEngineDir, string? explicitSuiteDir)
    {
        if (!string.IsNullOrWhiteSpace(explicitReEngineDir))
            return TrimDirectorySeparators(Path.GetFullPath(explicitReEngineDir.Trim()));

        if (!string.IsNullOrWhiteSpace(explicitSuiteDir))
        {
            return ReStationWizardPathNormalizer.NormalizeWizardReEngineDir(
                TrimDirectorySeparators(Path.GetFullPath(explicitSuiteDir.Trim())));
        }

        return null;
    }

    private static string ResolveCanonicalReEngineDefault(SandboxResolution? activeSandbox)
    {
        string canonical = TrimDirectorySeparators(ReEngineBootstrapHelper.ResolveDefaultReEngineInstallDir());
        return activeSandbox is { IsSandboxActive: true }
            ? KnownFolderPaths.MapHostPathToSandbox(canonical, activeSandbox)
            : canonical;
    }

    private static string ResolveCanonicalInstallDefault(string defaultInstallDir, SandboxResolution? activeSandbox)
    {
        string canonical = TrimDirectorySeparators(Path.GetFullPath(defaultInstallDir));
        return activeSandbox is { IsSandboxActive: true }
            ? KnownFolderPaths.MapHostPathToSandbox(canonical, activeSandbox)
            : canonical;
    }

    private static string ResolveEffectiveProfile(string? explicitProfileId, string? savedProfileId, string defaultProfileId)
    {
        string raw = !string.IsNullOrWhiteSpace(explicitProfileId)
            ? explicitProfileId
            : !string.IsNullOrWhiteSpace(savedProfileId)
                ? savedProfileId
                : defaultProfileId;
        return ReStationLegacyMigrationHelper.NormalizeProfileId(raw);
    }

    private static string? ReadValidatedRegistryPath(
        IPlatformRegistry registry,
        string subKeyPath,
        string valueName,
        SandboxResolution? activeSandbox)
    {
        string? raw = registry.GetValue(PlatformRegistryHive.CurrentUser, subKeyPath, valueName)?.AsString();
        if (string.IsNullOrWhiteSpace(raw) || raw.IndexOf('\0') >= 0)
            return null;

        try
        {
            string trimmed = raw.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(trimmed))
                return null;

            string full = TrimDirectorySeparators(Path.GetFullPath(trimmed));
            if (activeSandbox is { IsSandboxActive: true } && !SandboxEnvironmentSeam.IsPathWithinSandbox(full, activeSandbox))
                return null;

            return full;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string TrimDirectorySeparators(string path)
    {
        string full = Path.GetFullPath(path);
        string root = Path.GetPathRoot(full) ?? string.Empty;
        return full.Length > root.Length
            ? Path.TrimEndingDirectorySeparator(full)
            : full;
    }

    private sealed record DiscoveredInstall(
        string? ReEngineInstallDir,
        string PreferredInstallDir,
        string? SavedProfileId);
}
