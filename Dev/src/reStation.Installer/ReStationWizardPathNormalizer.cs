// @module: reStation.Installer.ReStationWizardPathNormalizer
// @purpose: Live Setup Wizard folder auto-sorting into engines\reStation\v<Ver> / <parent>\reEngine and suite resolution.
// @depends-on: System, System.Collections.Generic, System.IO, System.Linq, System.Windows.Forms, Foundation.Installer
// @exports: ReStationWizardPathNormalizer

using System.Windows.Forms;
using Foundation.Installer;

namespace reStation.Installer;

/// <summary>
/// Normalizes interactive Setup Wizard directory selections so choosing a generic parent folder
/// (for example <c>&lt;custom-dir&gt;\Misc</c>) sorts <c>reEngine</c> into <c>&lt;parent&gt;\reEngine</c>
/// and <c>reStation</c> into <c>&lt;reEngineDir&gt;\engines\reStation\v&lt;Version&gt;</c> without
/// altering explicit CLI paths.
/// </summary>
internal static class ReStationWizardPathNormalizer
{
    internal static string VersionFolder => $"v{ReStationInstallerTheme.Version}";

    public static string NormalizeWizardReEngineDir(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return string.Empty;
        }

        string normalized = NormalizeTrimmedFullPath(rawPath);
        string leaf = Path.GetFileName(normalized);
        if (string.Equals(leaf, "reEngine", StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        if (HasReEngineMarker(normalized))
        {
            return normalized;
        }

        return Path.Combine(normalized, "reEngine");
    }

    public static string ResolvePreferredSuiteInstallDir(string reEngineInstallDir, string? explicitSuiteDir = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitSuiteDir))
        {
            return NormalizeTrimmedFullPath(explicitSuiteDir);
        }

        string normalized = NormalizeTrimmedFullPath(reEngineInstallDir);
        DirectoryInfo? parent = Directory.GetParent(normalized);
        if (parent is not null &&
            !string.Equals(parent.FullName, Path.GetPathRoot(parent.FullName), StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(parent.Name, "reEngine", StringComparison.OrdinalIgnoreCase) ||
                Directory.Exists(Path.Combine(parent.FullName, "engines")) ||
                Directory.Exists(Path.Combine(parent.FullName, "toolchain")))
            {
                return parent.FullName;
            }
        }

        return normalized;
    }

    public static string ResolvePreferredEnginesRootDir(string reEngineInstallDir, string? explicitSuiteDir = null)
    {
        string suiteRoot = ResolvePreferredSuiteInstallDir(reEngineInstallDir, explicitSuiteDir);
        return Path.Combine(suiteRoot, "engines");
    }

    public static string NormalizeWizardStationInstallDir(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return string.Empty;
        }

        string normalized = NormalizeTrimmedFullPath(rawPath);
        string leaf = Path.GetFileName(normalized);
        DirectoryInfo? parent = Directory.GetParent(normalized);
        string parentName = parent?.Name ?? string.Empty;

        if (string.Equals(leaf, VersionFolder, StringComparison.OrdinalIgnoreCase) ||
            (string.Equals(parentName, ReStationInstallerTheme.EngineId, StringComparison.OrdinalIgnoreCase) &&
             leaf.StartsWith("v", StringComparison.OrdinalIgnoreCase)) ||
            ReStationInstallerBootstrap.HasDirectInstallation(normalized))
        {
            return normalized;
        }

        if (string.Equals(leaf, ReStationInstallerTheme.EngineId, StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(normalized, VersionFolder);
        }

        if (string.Equals(leaf, "engines", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(normalized, ReStationInstallerTheme.EngineId, VersionFolder);
        }

        if (string.Equals(leaf, "reEngine", StringComparison.OrdinalIgnoreCase) || HasReEngineMarker(normalized))
        {
            string enginesRoot = ResolvePreferredEnginesRootDir(normalized);
            return Path.Combine(enginesRoot, ReStationInstallerTheme.EngineId, VersionFolder);
        }

        string preferredReEngine = Path.Combine(normalized, "reEngine");
        string preferredEnginesRoot = ResolvePreferredEnginesRootDir(preferredReEngine);
        return Path.Combine(preferredEnginesRoot, ReStationInstallerTheme.EngineId, VersionFolder);
    }

    public static void WireWizardAutoSortHandlers(InstallerWizardForm wizard)
    {
        bool updating = false;
        bool userCustomizedPrimary = false;
        bool userCustomizedSecondary = false;
        string lastSyncedPrimary = NormalizeTrimmedFullPath(wizard.InstallDirTextBox.Text);
        string lastSyncedSecondary = NormalizeTrimmedFullPath(wizard.SecondaryDirTextBox.Text);

        void ApplyPrimaryAutoSort()
        {
            if (updating) return;

            string current = wizard.InstallDirTextBox.Text;
            string sorted = NormalizeWizardStationInstallDir(current);
            if (string.IsNullOrWhiteSpace(sorted)) return;

            updating = true;
            try
            {
                if (!string.Equals(current.Trim(), sorted, StringComparison.Ordinal))
                {
                    wizard.InstallDirTextBox.Text = sorted;
                    wizard.InstallDirTextBox.SelectionStart = sorted.Length;
                }

                if (!string.Equals(sorted, lastSyncedPrimary, StringComparison.OrdinalIgnoreCase))
                {
                    userCustomizedPrimary = true;
                    lastSyncedPrimary = sorted;
                }

                string? inferredReEngine = TryInferExistingReEngineRoot(sorted);
                string currentSecondary = NormalizeTrimmedFullPath(wizard.SecondaryDirTextBox.Text);
                if (!userCustomizedSecondary &&
                    !string.IsNullOrWhiteSpace(inferredReEngine) &&
                    !string.Equals(currentSecondary, inferredReEngine, StringComparison.Ordinal))
                {
                    wizard.SecondaryDirTextBox.Text = inferredReEngine;
                    wizard.SecondaryDirTextBox.SelectionStart = inferredReEngine.Length;
                    lastSyncedSecondary = inferredReEngine;
                }
            }
            finally
            {
                updating = false;
            }
        }

        void ApplySecondaryAutoSort()
        {
            if (updating) return;

            string current = wizard.SecondaryDirTextBox.Text;
            string sorted = NormalizeWizardReEngineDir(current);
            if (string.IsNullOrWhiteSpace(sorted)) return;

            updating = true;
            try
            {
                if (!string.Equals(current.Trim(), sorted, StringComparison.Ordinal))
                {
                    wizard.SecondaryDirTextBox.Text = sorted;
                    wizard.SecondaryDirTextBox.SelectionStart = sorted.Length;
                }

                if (!string.Equals(sorted, lastSyncedSecondary, StringComparison.OrdinalIgnoreCase))
                {
                    userCustomizedSecondary = true;
                    lastSyncedSecondary = sorted;
                }

                string currentPrimary = NormalizeTrimmedFullPath(wizard.InstallDirTextBox.Text);
                if (!userCustomizedPrimary ||
                    string.IsNullOrWhiteSpace(currentPrimary) ||
                    string.Equals(currentPrimary, lastSyncedPrimary, StringComparison.OrdinalIgnoreCase))
                {
                    string syncedPrimary = NormalizeWizardStationInstallDir(sorted);
                    if (!string.Equals(currentPrimary, syncedPrimary, StringComparison.Ordinal))
                    {
                        wizard.InstallDirTextBox.Text = syncedPrimary;
                        wizard.InstallDirTextBox.SelectionStart = syncedPrimary.Length;
                    }

                    lastSyncedPrimary = syncedPrimary;
                }
            }
            finally
            {
                updating = false;
            }
        }

        wizard.BrowseButton.Click += (_, _) => ApplyPrimaryAutoSort();
        wizard.InstallDirTextBox.Leave += (_, _) => ApplyPrimaryAutoSort();
        wizard.InstallDirTextBox.Validating += (_, _) => ApplyPrimaryAutoSort();
        wizard.InstallDirTextBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) ApplyPrimaryAutoSort();
        };

        Button? secondaryBrowse = EnumerateButtons(wizard)
            .FirstOrDefault(b => b != wizard.BrowseButton && b.Text.StartsWith("Browse", StringComparison.OrdinalIgnoreCase));
        if (secondaryBrowse is not null)
        {
            secondaryBrowse.Click += (_, _) => ApplySecondaryAutoSort();
        }

        wizard.SecondaryDirTextBox.Leave += (_, _) => ApplySecondaryAutoSort();
        wizard.SecondaryDirTextBox.Validating += (_, _) => ApplySecondaryAutoSort();
        wizard.SecondaryDirTextBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) ApplySecondaryAutoSort();
        };

        wizard.PrimaryButton.MouseDown += (_, _) =>
        {
            ApplySecondaryAutoSort();
            ApplyPrimaryAutoSort();
        };
        wizard.PrimaryButton.Enter += (_, _) =>
        {
            ApplySecondaryAutoSort();
            ApplyPrimaryAutoSort();
        };
    }

    private static bool HasReEngineMarker(string dir) =>
        File.Exists(Path.Combine(dir, "reEngine.exe")) ||
        File.Exists(Path.Combine(dir, "app", "reEngine.exe")) ||
        File.Exists(Path.Combine(dir, "reEngine.Core.dll")) ||
        Directory.Exists(Path.Combine(dir, "engines"));

    private static string? TryInferExistingReEngineRoot(string stationInstallDir)
    {
        DirectoryInfo? cursor = Directory.GetParent(NormalizeTrimmedFullPath(stationInstallDir));
        for (int depth = 0; depth < 4 && cursor is not null; depth++)
        {
            if (File.Exists(Path.Combine(cursor.FullName, "reEngine.exe")) ||
                File.Exists(Path.Combine(cursor.FullName, "app", "reEngine.exe")) ||
                File.Exists(Path.Combine(cursor.FullName, "reEngine.Core.dll")))
            {
                return cursor.FullName;
            }

            string nestedReEngine = Path.Combine(cursor.FullName, "reEngine");
            if (File.Exists(Path.Combine(nestedReEngine, "reEngine.exe")) ||
                File.Exists(Path.Combine(nestedReEngine, "app", "reEngine.exe")) ||
                File.Exists(Path.Combine(nestedReEngine, "reEngine.Core.dll")))
            {
                return nestedReEngine;
            }

            cursor = cursor.Parent;
        }

        return null;
    }

    private static IEnumerable<Button> EnumerateButtons(Control root)
    {
        foreach (Control child in root.Controls)
        {
            if (child is Button button) yield return button;
            foreach (Button nested in EnumerateButtons(child)) yield return nested;
        }
    }

    private static string NormalizeTrimmedFullPath(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) return string.Empty;
        string trimmed = rawPath.Trim();
        try
        {
            string full = Path.GetFullPath(trimmed);
            string? root = Path.GetPathRoot(full);
            if (!string.IsNullOrEmpty(root) && string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
            {
                return full;
            }

            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return trimmed.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }
}
