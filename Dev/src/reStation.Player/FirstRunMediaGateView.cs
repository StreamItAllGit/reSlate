// @module: reStation.Player.FirstRunMediaGateView
// @purpose: Dark #0E0E10 first-run BYO media gate rendered on PixelOverlayCanvas with disc/module picker and drag-drop ingestion.
// @depends-on: Foundation.Core, reStation.Contracts, reStation.Core, reStation.Core.DiscIngestion
// @exports: FirstRunMediaGateView
// @owner: devv-player-ui (T6)

using Foundation.Core;
using reStation.Contracts;
using reStation.Core;
using reStation.Core.DiscIngestion;

namespace reStation.Player;

/// <summary>First-run gate prompting for a user-supplied disc image (or .recomp module) when data\rom\ preload is empty.</summary>
internal sealed class FirstRunMediaGateView
{
    public const int CanvasWidth = 1280, CanvasHeight = 720;

    private static readonly UiRect DropZone = new(240, 260, 800, 140);
    private static readonly UiRect BrowseDiscBtn = new(330, 428, 296, 48);
    private static readonly UiRect BrowseModuleBtn = new(654, 428, 296, 48);
    private static readonly UiRect CenteredBrowseDiscBtn = new(470, 428, 340, 48);

    private readonly PixelOverlayCanvas _canvas = new(CanvasWidth, CanvasHeight);

    public string StatusMessage { get; set; } = "Provide a PS1 disc image (.cue / .bin / .iso / .chd / .7z / .zip) or place it in data\\rom\\.";
    public bool StatusIsError { get; set; }
    public bool HoverDisc { get; set; }
    public bool HoverModule { get; set; }

    public byte[] Pixels => _canvas.Buffer;

    public bool HitTestBrowseDisc(int x, int y, bool hasModule) =>
        hasModule ? CenteredBrowseDiscBtn.Contains(x, y) : BrowseDiscBtn.Contains(x, y);

    public bool HitTestBrowseModule(int x, int y, bool hasModule) =>
        !hasModule && BrowseModuleBtn.Contains(x, y);

    public void Render(IReStationModuleManifest? manifest)
    {
        _canvas.Clear(UiPaint.Background);
        _canvas.FillRect(0, 0, CanvasWidth, 68, UiPaint.Panel);
        _canvas.FillRect(0, 66, CanvasWidth, 2, UiPaint.Accent);

        UiPaint.Text(_canvas, 36, 20, $"reStation v{ReStationEngineInfo.Version}", UiPaint.AccentText, 3);
        string sub = manifest is null ? "PS1 Static Recompilation Engine" : $"{manifest.DisplayName} ({manifest.SerialNumber})";
        UiPaint.Text(_canvas, CanvasWidth - UiPaint.TextWidth(sub) - 36, 26, sub, UiPaint.Muted);

        UiPaint.BorderRect(_canvas, new UiRect(160, 110, 960, 520), new RgbaColor(0x13, 0x16, 0x1E), UiPaint.Border);
        UiPaint.Text(_canvas, 210, 144, "First-Run Media Verification", UiPaint.TextPrimary, 3);
        UiPaint.Text(_canvas, 210, 188, "Zero copyrighted game assets are bundled with reStation.", UiPaint.Muted);
        UiPaint.Text(_canvas, 210, 214, manifest is null
            ? "Select a .recomp game module and your verified PS1 disc image to continue."
            : $"Provide your verified disc image for {manifest.DisplayName} (or preload in {manifest.PreloadRomDir}).", UiPaint.Muted);

        UiPaint.BorderRect(_canvas, DropZone, new RgbaColor(0x10, 0x12, 0x18), UiPaint.Border);
        UiPaint.TextCentered(_canvas, new UiRect(DropZone.X, DropZone.Y + 34, DropZone.W, 28), "Drag & Drop Disc Image or .recomp Module Here", UiPaint.TextPrimary);
        UiPaint.TextCentered(_canvas, new UiRect(DropZone.X, DropZone.Y + 74, DropZone.W, 28), "Supports .cue / .bin / .iso / .chd / .7z / .zip", UiPaint.Muted);

        if (manifest is not null)
        {
            UiPaint.Pill(_canvas, CenteredBrowseDiscBtn, "Select Disc Image...", active: false, hover: HoverDisc);
        }
        else
        {
            UiPaint.Pill(_canvas, BrowseDiscBtn, "Select Disc Image...", active: false, hover: HoverDisc);
            UiPaint.Pill(_canvas, BrowseModuleBtn, "Select .recomp Module...", active: false, hover: HoverModule);
        }

        RgbaColor stFill = StatusIsError ? new RgbaColor(0x2E, 0x17, 0x1B) : new RgbaColor(0x17, 0x22, 0x2E);
        RgbaColor stBorder = StatusIsError ? new RgbaColor(0xCC, 0x55, 0x55) : new RgbaColor(0x39, 0x50, 0x6B);
        RgbaColor stText = StatusIsError ? new RgbaColor(0xFF, 0xB8, 0xB8) : UiPaint.AccentText;
        UiPaint.BorderRect(_canvas, new UiRect(210, 520, 860, 52), stFill, stBorder);
        UiPaint.Text(_canvas, 228, 539, Truncate(StatusMessage, 68), stText);
    }

    public async Task<DiscIngestResult> TryIngestAsync(
        string discPath, string dataDir, IReStationModuleManifest manifest, ReStationLogger? logger, CancellationToken ct)
    {
        StatusMessage = $"Verifying {Path.GetFileName(discPath)}...";
        StatusIsError = false;
        DiscIngestResult res = await DiscVerifierAndExtractor
            .IngestAsync(discPath, dataDir, manifest, logger, AppContext.BaseDirectory, ct)
            .ConfigureAwait(false);
        StatusMessage = res.Message;
        StatusIsError = !res.Success;
        return res;
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? string.Empty : s.Length <= max ? s : s[..(max - 3)] + "...";
}
