// @module: reStation.Installer.ReStationInstallerTheme
// @purpose: Canonical v0.2.7 installer metadata, Suite path options, Lightweight/Full profiles, and zero-daemon AllowAutoStart = false descriptor.
// @depends-on: System, System.Collections.Generic, Foundation.Installer, reEngine.Core.Bootstrap
// @exports: ReStationInstallerTheme

using Foundation.Installer;
using reEngine.Core.Bootstrap;

namespace reStation.Installer;

internal static class ReStationInstallerTheme
{
    public const string ProductName = "reStation";
    public const string EngineId = "reStation";
    public const string AppId = "restation";
    public const string Version = "0.2.7";
    public const string Publisher = "StreamItAll";
    public const string StudioName = "StreamItAll";
    public const string SuiteName = "reEngine";
    public const string UmbrellaSubpath = "StreamItAll\\reEngine\\engines\\reStation\\v0.2.7";
    public const string DefaultInstallProfile = "lightweight";
    public const string Subtitle = "PS1 Static Recompilation Engine & .decomp Provider for reEngine";
    public const string BrandAccentHex = "#38BDF8";
    public const string MainExecutable = "reStation.exe";
    public const string PayloadResourceName = "reStation.Installer.Embedded.payload.zip";
    public const bool AllowAutoStart = false;
    public const string InstallDirEnvironmentVariable = "RESTATION_INSTALL_DIR";

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
            Description: "Core PS1 recomp engine, player, compiler & .decomp provider (~108 MB).",
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

    public static InstallerThemeOptions CreateThemeOptions() => new()
    {
        AppId = AppId,
        DisplayName = ProductName,
        Version = Version,
        Subtitle = Subtitle,
        BrandAccentHex = BrandAccentHex,
        DefaultInstallDir = ResolveDefaultVersionInstallDir(),
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

    public static InstallerAppDescriptor CreateDescriptor() => CreateDescriptor(CreateThemeOptions());

    internal static InstallerAppDescriptor CreateDescriptor(InstallerThemeOptions theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        return new InstallerAppDescriptor(
            ProductName: ProductName,
            DisplayName: theme.DisplayName,
            Publisher: theme.StudioName,
            Version: theme.Version,
            MainExecutableRelativePath: MainExecutable,
            Subtitle: theme.Subtitle,
            EmbeddedPayloadResourceName: PayloadResourceName,
            BrandAccentHex: theme.BrandAccentHex,
            AllowAutoStart: theme.AllowAutoStart,
            StudioName: theme.StudioName,
            SuiteName: theme.SuiteName,
            UmbrellaSubpath: theme.UmbrellaSubpath,
            DefaultProfile: theme.DefaultProfileId);
    }
}
