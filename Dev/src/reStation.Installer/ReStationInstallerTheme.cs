// @module: reStation.Installer.ReStationInstallerTheme
// @purpose: Zero-PII reStation installer identity (names, version, accent, embedded resource names) and the InstallerAppDescriptor factory (AllowAutoStart = false).
// @depends-on: Foundation.Installer
// @exports: ReStationInstallerTheme
// @owner: devv-pkg (Wave 2 Slice 3)

using Foundation.Installer;

namespace reStation.Installer;

/// <summary>Single source of truth for reStation installer branding and packaging constants (no host paths).</summary>
internal static class ReStationInstallerTheme
{
    public const string ProductName = "reStation";
    public const string EngineId = "reStation";
    public const string Version = "0.1.1";
    public const string Publisher = "StreamItAll";
    public const string Subtitle = "PS1 Static Recompilation Engine for reEngine";
    public const string BrandAccentHex = "#38BDF8";
    public const string MainExecutable = "reStation.exe";
    public const string PayloadResourceName = "reStation.Installer.Embedded.payload.zip";
    public const string ReEngineBootstrapResourceName = "reStation.Installer.Embedded.reEngine-Setup.exe";
    /// <summary>Zero-daemon invariant: reStation never registers a Windows Startup entry.</summary>
    public const bool AllowAutoStart = false;
    /// <summary>Process-local variable read by Foundation.Installer to seed the default install directory (<c>{ENVPREFIX}_INSTALL_DIR</c>).</summary>
    public const string InstallDirEnvironmentVariable = "RESTATION_INSTALL_DIR";

    /// <summary>Builds the installer descriptor handed to <see cref="StandardInstallerHost"/>.</summary>
    public static InstallerAppDescriptor CreateDescriptor() => new(
        ProductName: ProductName,
        DisplayName: ProductName,
        Publisher: Publisher,
        Version: Version,
        MainExecutableRelativePath: MainExecutable,
        Subtitle: Subtitle,
        EmbeddedPayloadResourceName: PayloadResourceName,
        BrandAccentHex: BrandAccentHex,
        AllowAutoStart: AllowAutoStart);
}
