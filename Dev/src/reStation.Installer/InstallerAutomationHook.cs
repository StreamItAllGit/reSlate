// @module: reStation.Installer.InstallerAutomationHook
// @purpose: Debug/RESTATION_TEST_AUTOMATION-only WinForms naming + AutomationLoopbackServer hook for live visible Setup Wizard testing.
// @depends-on: System, System.Security.Cryptography, Foundation.Automation, Foundation.Core, Foundation.Installer
// @exports: InstallerAutomationHook

#if DEBUG || RESTATION_TEST_AUTOMATION
using System.Security.Cryptography;
using Foundation.Automation;
using Foundation.Core;
using Foundation.Installer;

namespace reStation.Installer;

/// <summary>Attaches deterministic control IDs and a loopback automation server to <see cref="InstallerWizardForm"/> in Debug/Test builds only.</summary>
internal sealed class InstallerAutomationHook : IDisposable
{
    private readonly AutomationLoopbackServer _server;
    private readonly ManualResetEventSlim _started = new(false);
    private bool _disposed;

    private InstallerAutomationHook(AutomationLoopbackServer server) => _server = server;

    public int ListeningPort => _server.AutomationPort;
    public string BearerToken => _server.BearerToken;

    /// <summary>Assigns stable automation control identifiers to interactive <see cref="InstallerWizardForm"/> controls.</summary>
    public static void ApplyControlNames(InstallerWizardForm wizard)
    {
        ArgumentNullException.ThrowIfNull(wizard);
        wizard.Name = "InstallerWizardRoot";
        wizard.PrimaryButton.Name = "Wizard_PrimaryButton";
        wizard.CancelButton.Name = "Wizard_CancelButton";
        wizard.BrowseButton.Name = "Wizard_BrowseButton";
        wizard.DetailsToggleButton.Name = "Wizard_DetailsToggle";
        wizard.InstallDirTextBox.Name = "Wizard_InstallDir";
        wizard.SecondaryDirTextBox.Name = "Wizard_SecondaryDir";
        wizard.LightweightProfileRadioButton.Name = "Wizard_ProfileLightweight";
        wizard.FullProfileRadioButton.Name = "Wizard_ProfileFull";
        wizard.ShortcutsCheckBox.Name = "Wizard_Shortcuts";
        wizard.LaunchAfterCheckBox.Name = "Wizard_LaunchAfter";
        wizard.MigrateLegacyCheckBox.Name = "Wizard_MigrateLegacy";
        wizard.StatusLabel.Name = "Wizard_StatusLabel";
        wizard.LogTextBox.Name = "Wizard_LogTextBox";
    }

    /// <summary>Wires loopback HTTP automation onto <paramref name="wizard"/> on load and tears down cleanly on close.</summary>
    public static InstallerAutomationHook Attach(
        InstallerWizardForm wizard,
        int port,
        string? token,
        InstallerRuntimeContext ctx)
    {
        ArgumentNullException.ThrowIfNull(wizard);
        ArgumentNullException.ThrowIfNull(ctx);

        ApplyControlNames(wizard);
        string effectiveToken = !string.IsNullOrWhiteSpace(token)
            ? token.Trim()
            : (Environment.GetEnvironmentVariable("FOUNDATION_AUTOMATION_TOKEN")?.Trim()
               ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant());

        var opts = new AutomationServerOptions(port, effectiveToken, ReStationInstallerTheme.AppId, AllowedSnapshotRoot: null);
        SandboxResolution effectiveSandbox = ctx.IsSandboxActive ? ctx.Sandbox : SandboxResolution.Inactive;
        var server = new AutomationLoopbackServer(opts, wizard, renderProvider: null, sandbox: effectiveSandbox);
        var hook = new InstallerAutomationHook(server);

        wizard.Load += (_, _) =>
        {
            if (hook._disposed || server.IsListening) return;
            server.Start();
            hook._started.Set();
            ctx.WriteLog("AUTOMATION", $"Wizard loopback listening on 127.0.0.1:{server.AutomationPort}");
            Console.WriteLine($"AUTOMATION: Wizard loopback listening on 127.0.0.1:{server.AutomationPort}");
        };
        wizard.FormClosed += (_, _) => hook.Dispose();
        return hook;
    }

    public bool WaitUntilListening(TimeSpan timeout) => _started.Wait(timeout);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _server.Dispose();
        _started.Dispose();
    }
}
#endif
