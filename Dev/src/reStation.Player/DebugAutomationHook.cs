// @module: reStation.Player.DebugAutomationHook
// @purpose: Debug/RESTATION_TEST_AUTOMATION-only STA WinForms proxy Control + Foundation.Automation loopback server forwarding commands cleanly to HostContractBridge.
// @depends-on: System.Drawing, System.Security.Cryptography, System.Windows.Forms, Foundation.Automation, reEngine.Host.Contracts
// @exports: DebugAutomationHook

#if DEBUG || RESTATION_TEST_AUTOMATION
using System.Diagnostics;
using System.Drawing;
using System.Security.Cryptography;
using System.Windows.Forms;
using Foundation.Automation;
using reEngine.Host.Contracts;
using reStation.Contracts;

namespace reStation.Player;

/// <summary>Debug/test-only loopback automation proxy stripped completely from pure Release builds.</summary>
internal sealed class DebugAutomationHook : IDisposable, IRenderSurfaceSnapshotProvider
{
    private readonly IHostAutomationTarget _target;
    private readonly AutomationServerOptions _serverOptions;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly int _width;
    private readonly int _height;
    private Thread? _staThread;
    private ProxyHostForm? _proxyForm;
    private AutomationLoopbackServer? _server;
    private string _lastWidgetSignature = string.Empty;
    private string _lastTelemetrySummary = string.Empty;
    private long _lastTelemetryPushTicks;
    private volatile bool _disposing;

    public DebugAutomationHook(
        IHostAutomationTarget target,
        int port,
        string? bearerToken = null,
        string? allowedSnapshotRoot = null,
        int width = UiOverlayPresenter.OverlayWidth,
        int height = UiOverlayPresenter.OverlayHeight)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        string token = !string.IsNullOrWhiteSpace(bearerToken)
            ? bearerToken.Trim()
            : (Environment.GetEnvironmentVariable("FOUNDATION_AUTOMATION_TOKEN")?.Trim()
               ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant());
        _serverOptions = new AutomationServerOptions(port, token, ReStationEngineInfo.AppId, allowedSnapshotRoot);
        _width = Math.Max(320, width);
        _height = Math.Max(240, height);
        if (_target is HostContractBridge hcb)
            hcb.Diagnostics ??= new DebugAutomationDiagnostics();
    }

    public int ListeningPort => _server?.AutomationPort ?? _serverOptions.AutomationPort;
    public string BearerToken => _server?.BearerToken ?? _serverOptions.BearerToken;

    public void Start()
    {
        if (_staThread is not null) return;
        _staThread = new Thread(RunStaLoop) { IsBackground = true, Name = "reSlate.AutomationProxy" };
        _staThread.SetApartmentState(ApartmentState.STA);
        _staThread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(30)))
            throw new TimeoutException("Timed out waiting for DebugAutomationHook STA loopback server to bind.");
    }

    public void SyncWidgets(IReadOnlyList<HostWidgetBounds> widgets)
    {
        if (_disposing || _proxyForm is null || _proxyForm.IsDisposed || !_proxyForm.IsHandleCreated) return;
        string sig = BuildWidgetSignature(widgets);
        bool widgetsChanged = !string.Equals(sig, _lastWidgetSignature, StringComparison.Ordinal);

        string? summary = (_target as HostContractBridge)?.Diagnostics?.TelemetrySummary;
        long now = Stopwatch.GetTimestamp();
        bool telemetryChanged = summary is not null
            && !string.Equals(summary, _lastTelemetrySummary, StringComparison.Ordinal)
            && ((now - _lastTelemetryPushTicks) >= (Stopwatch.Frequency / 15L) || _lastTelemetryPushTicks == 0);
        if (!widgetsChanged && !telemetryChanged) return;

        if (widgetsChanged) _lastWidgetSignature = sig;
        if (telemetryChanged && summary is not null)
        {
            _lastTelemetrySummary = summary;
            _lastTelemetryPushTicks = now;
        }

        try
        {
            IReadOnlyList<HostWidgetBounds>? widgetUpdate = widgetsChanged ? widgets : null;
            string? summaryUpdate = telemetryChanged ? summary : null;
            _ = _proxyForm.BeginInvoke(() => _proxyForm.ApplyWidgetsAndTelemetry(widgetUpdate, summaryUpdate));
        }
        catch (InvalidOperationException ex)
        {
            Trace.WriteLine($"Automation widget sync skipped during teardown: {ex.Message}");
        }
    }

    private static string BuildWidgetSignature(IReadOnlyList<HostWidgetBounds> widgets) =>
        string.Join('|', widgets.Select(w => $"{w.ControlId}:{w.Bounds.X},{w.Bounds.Y},{w.Bounds.Width},{w.Bounds.Height}:{w.Label}"));

    public bool TryCaptureRgba(out int width, out int height, out byte[] rgbaBytes) =>
        _target.TryCaptureFramebufferPng(out width, out height, out rgbaBytes);

    private void RunStaLoop()
    {
        try { Application.EnableVisualStyles(); }
        catch (InvalidOperationException ex) { Trace.WriteLine($"EnableVisualStyles skipped: {ex.Message}"); }

        using var form = new ProxyHostForm(_target, _width, _height);
        _proxyForm = form;
        form.Load += (_, _) =>
        {
            IReadOnlyList<HostWidgetBounds> initial = _target.GetOverlayWidgets();
            if (initial.Count > 0)
            {
                _lastWidgetSignature = BuildWidgetSignature(initial);
                form.ApplyWidgets(initial);
            }
            _server = new AutomationLoopbackServer(_serverOptions, form, this);
            _server.Start();
            _ready.Set();
        };
        Application.Run(form);
    }

    public void Dispose()
    {
        if (_disposing) return;
        _disposing = true;
        _server?.Dispose();
        _server = null;

        if (_proxyForm is { IsDisposed: false, IsHandleCreated: true } pf)
        {
            try { _ = pf.BeginInvoke(() => pf.Close()); }
            catch (InvalidOperationException ex) { Trace.WriteLine($"Proxy form close skipped: {ex.Message}"); }
        }
        _staThread?.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }

    private sealed class ProxyHostForm : Form
    {
        private const int WmKeyDown = 0x0100;
        private readonly IHostAutomationTarget _target;
        private readonly DiagTextBox _diagCommandInput;
        private readonly Label _diagTelemetry;
        private bool _clearingDiagInput;

        public ProxyHostForm(IHostAutomationTarget target, int width, int height)
        {
            _target = target;
            Name = "SdlHostSurface";
            Text = "reSlate SDL3 Host Surface";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-32000, -32000);
            ClientSize = new Size(width, height);
            Opacity = 0.0;

            _diagCommandInput = new DiagTextBox(this)
            {
                Name = "Diag_CommandInput",
                Bounds = new Rectangle(0, 0, 120, 20),
                TabStop = false,
                Visible = true,
                Enabled = true
            };
            _diagCommandInput.TextChanged += (_, _) =>
            {
                if (_clearingDiagInput || string.IsNullOrWhiteSpace(_diagCommandInput.Text)) return;
                string raw = _diagCommandInput.Text;
                if (_target is HostContractBridge hcb)
                    hcb.Diagnostics?.EnqueueRawCommands(raw);
                _clearingDiagInput = true;
                try { _diagCommandInput.Text = string.Empty; }
                finally { _clearingDiagInput = false; }
            };
            _diagTelemetry = new Label
            {
                Name = "Diag_Telemetry",
                Text = string.Empty,
                Bounds = new Rectangle(0, 20, 240, 20),
                Visible = true,
                Enabled = true
            };
            Controls.Add(_diagCommandInput);
            Controls.Add(_diagTelemetry);
        }

        public void ApplyWidgetsAndTelemetry(IReadOnlyList<HostWidgetBounds>? widgets, string? telemetrySummary)
        {
            if (telemetrySummary is not null) _diagTelemetry.Text = telemetrySummary;
            if (widgets is not null) ApplyWidgets(widgets);
        }

        public void ApplyWidgets(IReadOnlyList<HostWidgetBounds> widgets)
        {
            foreach (Control existing in Controls.OfType<KeyForwardButton>().ToArray())
            {
                Controls.Remove(existing);
                existing.Dispose();
            }
            foreach (HostWidgetBounds w in widgets)
            {
                if (string.Equals(w.ControlId, "HostViewport", StringComparison.Ordinal)) continue;
                var btn = new KeyForwardButton(this)
                {
                    Name = w.ControlId,
                    Text = w.Label,
                    Bounds = w.Bounds,
                    Visible = w.IsVisible,
                    Enabled = true
                };
                string id = w.ControlId;
                int cx = w.Bounds.X + (w.Bounds.Width / 2);
                int cy = w.Bounds.Y + (w.Bounds.Height / 2);
                btn.Click += (_, _) => _target.EnqueuePointerClick(cx, cy, id);
                btn.MouseUp += (_, me) =>
                {
                    if (me.Button == MouseButtons.Right && _target is HostContractBridge hcb)
                        hcb.EnqueuePointerRightClick(cx, cy, id);
                };
                Controls.Add(btn);
            }
        }

        internal void ForwardKeyMessage(int wParam)
        {
            if (MapVkToName(wParam) is { } keyName) _target.EnqueueKeyPress(keyName);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmKeyDown) ForwardKeyMessage((int)m.WParam);
            base.WndProc(ref m);
        }

        private sealed class DiagTextBox(ProxyHostForm parent) : TextBox
        {
            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WmKeyDown) parent.ForwardKeyMessage((int)m.WParam);
                base.WndProc(ref m);
            }
        }

        private sealed class KeyForwardButton(ProxyHostForm parent) : Button
        {
            private const int WmLButtonDown = 0x0201;
            private const int WmLButtonUp = 0x0202;
            private bool _inLeftButtonUp;
            private bool _clickedInLeftButtonUp;

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WmKeyDown)
                {
                    parent.ForwardKeyMessage((int)m.WParam);
                }
                else if (m.Msg == WmLButtonDown)
                {
                    _clickedInLeftButtonUp = false;
                }
                else if (m.Msg == WmLButtonUp)
                {
                    _inLeftButtonUp = true;
                    _clickedInLeftButtonUp = false;
                    try { base.WndProc(ref m); }
                    finally { _inLeftButtonUp = false; }
                    return;
                }
                base.WndProc(ref m);
            }

            protected override void OnClick(EventArgs e)
            {
                if (_inLeftButtonUp)
                {
                    _clickedInLeftButtonUp = true;
                    base.OnClick(e);
                    return;
                }
                // Suppress redundant Win32VirtualInputDriver.PerformClick() immediately following a WM_LBUTTONUP click.
                if (_clickedInLeftButtonUp)
                {
                    _clickedInLeftButtonUp = false;
                    return;
                }
                base.OnClick(e);
            }
        }

        private static string? MapVkToName(int vk) => vk switch
        {
            0x70 => "F1", 0x72 => "F3", 0x74 => "F5", 0x78 => "F9", 0x7B => "F12",
            0x1B => "ESC", 0x08 => "BACKSPACE", 0x09 => "TAB", 0x0D => "ENTER", 0x20 => "SPACE",
            0x21 => "PGUP", 0x22 => "PGDN", 0x25 => "LEFT", 0x26 => "UP", 0x27 => "RIGHT", 0x28 => "DOWN", 0x2E => "DELETE",
            >= 0x41 and <= 0x5A => ((char)vk).ToString(),
            >= 0x30 and <= 0x39 => ((char)vk).ToString(),
            _ => null
        };
    }
}
#endif
