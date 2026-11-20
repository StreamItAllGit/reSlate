// @module: reStation.Installer.ReStationInstallerTheme
// @purpose: Canonical v0.3.0 reSlate installer metadata loaded via InstallerManifestStore, Suite path options, Lightweight/Full profiles, and zero-daemon AllowAutoStart = false descriptor.
// @depends-on: System, System.Collections.Generic, System.IO, Foundation.Installer, reEngine.Core.Bootstrap
// @exports: ReStationInstallerTheme

using Foundation.Installer;
using reEngine.Core.Bootstrap;

namespace reStation.Installer;

internal static class ReStationInstallerTheme
{
    public const string ProductName = "reSlate";
    public const string EngineId = "reSlate";
    public const string LegacyEngineId = "reStation";
    public const string AppId = "reslate";
    public const string Version = "0.3.0-hf.3";
    public const string Publisher = "StreamItAll";
    public const string StudioName = "StreamItAll";
    public const string SuiteName = "reEngine";
    public const string UmbrellaSubpath = "StreamItAll\\reEngine\\engines\\reSlate\\v0.3.0-hf.3";
    public const string DefaultInstallProfile = "lightweight";
    public const string Subtitle = "SlateOne Static Recompilation Engine & .decomp Provider for reEngine";
    public const string BrandAccentHex = "#38BDF8";
    public const string MainExecutable = "reSlate.exe";
    public const string PayloadResourceName = "reSlate.Installer.Embedded.payload.zip";
    public const bool AllowAutoStart = false;
    public const string InstallDirEnvironmentVariable = "RESLATE_INSTALL_DIR";
    public const string LegacyInstallDirEnvironmentVariable = "RESTATION_INSTALL_DIR";

    public static InstallerMarkupManifest? LoadManifest() =>
        InstallerManifestStore.TryLoadFromDirectoryOrAssembly(
            AppContext.BaseDirectory,
            typeof(ReStationInstallerTheme).Assembly);

    public static string ResolveDefaultVersionInstallDir() =>
        ReEngineBootstrapHelper.BuildEngineVersionInstallDir(
            ReEngineBootstrapHelper.ResolveDefaultReEngineInstallDir(),
            EngineId,
            Version);

    public static IReadOnlyList<InstallerProfileOption> CreateAvailableProfiles() =>
    [
        new InstallerProfileOption(
            Id: "lightweight",
            Kind: InstallerProfileKind.Lightweight,
            Label: "Lightweight",
            Description: "Core SlateOne recomp engine, player, compiler & .decomp provider (~108 MB).",
            IsDefault: true),
        new InstallerProfileOption(
            Id: "full",
            Kind: InstallerProfileKind.Full,
            Label: "Full (Offline Toolchain Suite)",
            Description: "Registers suite affinity for full offline Ghidra + OpenJDK companion runs.",
            IsDefault: false)
    ];

    public static SecondaryPathPickerOptions CreateSecondaryPathPicker() => new(
        FlagName: "--reengine-dir",
        Label: "Companion reEngine Directory:",
        DefaultPath: ReEngineBootstrapHelper.ResolveDefaultReEngineInstallDir(),
        Placeholder: "%LOCALAPPDATA%\\Programs\\StreamItAll\\reEngine\\reEngine",
        Tooltip: "Optional companion reEngine router directory hosting peer engines and toolchain (--reengine-dir).");

    public static InstallerThemeOptions CreateThemeOptions()
    {
        string defaultInstallDir = ResolveDefaultVersionInstallDir();
        InstallerMarkupManifest? manifest = LoadManifest();
        if (manifest is not null)
        {
            InstallerThemeOptions loaded = InstallerManifestStore.ToThemeOptions(manifest, defaultInstallDir);
            SecondaryPathPickerOptions? picker = loaded.SecondaryPathPicker is { } rawPicker
                ? rawPicker with
                {
                    DefaultPath = string.IsNullOrWhiteSpace(rawPicker.DefaultPath)
                        ? ReEngineBootstrapHelper.ResolveDefaultReEngineInstallDir()
                        : Environment.ExpandEnvironmentVariables(rawPicker.DefaultPath)
                }
                : CreateSecondaryPathPicker();

            return loaded with
            {
                AllowAutoStart = false,
                BootstrapSharedSuiteDaemon = false,
                ExecutableFileName = !string.IsNullOrWhiteSpace(manifest.MainExecutableRelativePath)
                    ? Path.GetFileName(manifest.MainExecutableRelativePath)
                    : MainExecutable,
                AvailableProfiles = CreateAvailableProfiles(),
                SecondaryPathPicker = picker
            };
        }

        return new InstallerThemeOptions
        {
            AppId = AppId,
            DisplayName = ProductName,
            Version = Version,
            Subtitle = Subtitle,
            BrandAccentHex = BrandAccentHex,
            DefaultInstallDir = defaultInstallDir,
            ExecutableFileName = MainExecutable,
            AllowAutoStart = AllowAutoStart,
            BootstrapSharedSuiteDaemon = false,
            StudioName = StudioName,
            SuiteName = SuiteName,
            UmbrellaSubpath = UmbrellaSubpath,
            AvailableProfiles = CreateAvailableProfiles(),
            DefaultProfileId = DefaultInstallProfile,
            SecondaryPathPicker = CreateSecondaryPathPicker(),
            AllowLegacyMigration = true
        };
    }

    public static InstallerAppDescriptor CreateDescriptor() => CreateDescriptor(CreateThemeOptions());

    internal static InstallerAppDescriptor CreateDescriptor(InstallerThemeOptions theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        var baseDescriptor = new InstallerAppDescriptor(
            ProductName: ProductName,
            DisplayName: theme.DisplayName,
            Publisher: theme.StudioName,
            Version: theme.Version,
            MainExecutableRelativePath: !string.IsNullOrWhiteSpace(theme.ExecutableFileName) ? theme.ExecutableFileName! : MainExecutable,
            Subtitle: theme.Subtitle,
            EmbeddedPayloadResourceName: PayloadResourceName,
            BrandAccentHex: theme.BrandAccentHex,
            AllowAutoStart: false,
            StudioName: theme.StudioName,
            SuiteName: theme.SuiteName,
            UmbrellaSubpath: theme.UmbrellaSubpath,
            DefaultProfile: theme.DefaultProfileId,
            AllowLegacyMigration: theme.AllowLegacyMigration);

        InstallerMarkupManifest? manifest = LoadManifest();
        return manifest is not null
            ? InstallerManifestStore.ApplyToDescriptor(baseDescriptor, manifest) with
              {
                  AllowAutoStart = false,
                  EmbeddedPayloadResourceName = PayloadResourceName
              }
            : baseDescriptor;
    }
}
