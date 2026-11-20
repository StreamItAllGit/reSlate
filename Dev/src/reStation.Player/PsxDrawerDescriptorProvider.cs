// @module: reStation.Player.PsxDrawerDescriptorProvider
// @purpose: Declarative F1 drawer metadata provider projecting RecompSettings, backend options, and module control options onto reEngine.Host HostDrawerDescriptor.
// @depends-on: reEngine.Host.Contracts, reStation.Contracts, reStation.Core
// @exports: PsxBackendOption, PsxDrawerDescriptorProvider

using System.Globalization;
using reEngine.Host.Contracts;
using reStation.Contracts;
using reStation.Core;

namespace reStation.Player;

internal readonly record struct PsxBackendOption(string BackendId, string Label, string ControlId);

/// <summary>Projects live <see cref="RecompSettings"/> and module metadata into declarative <see cref="HostDrawerDescriptor"/> rows.</summary>
internal static class PsxDrawerDescriptorProvider
{
    public static readonly IReadOnlyList<PsxBackendOption> BackendOptions =
    [
        new(PsxRenderBackendIds.OpenGl, "OpenGL 3.3", "Video_BackendOpenGl"),
        new(PsxRenderBackendIds.Vulkan, "Vulkan", "Video_BackendVulkan"),
        new(PsxRenderBackendIds.ParallelCpu, "Parallel CPU", "Video_BackendParallelCpu"),
        new(PsxRenderBackendIds.Software, "Software CPU", "Video_BackendSoftware")
    ];

    public static readonly IReadOnlyList<double> FastForwardMultipliers = [2.0, 3.0, 4.0];

    public static HostDrawerDescriptor BuildDescriptor(
        RecompSettings settings,
        IReStationModuleManifest? manifest,
        IModuleInputProfileProvider? moduleProfile,
        string discInfo,
        string executionTierLabel = "AOT Primary")
    {
        ArgumentNullException.ThrowIfNull(settings);
        HostAspectRatioMode aspect = ResolveDisplayAspectMode(settings);
        string aspectLabel = HostAspectRatioModeCodec.ToDisplayLabel(aspect);
        string overscanLabel = FormatOverscanLabel(settings.OverscanCropPx);
        string ffLabel = settings.IsFastForwardEnabled
            ? $"ON ({NormalizeFastForwardMultiplier(settings.FastForwardMultiplier):0.#}x)"
            : "OFF";

        var extraRows = new List<HostDrawerRowDescriptor>
        {
            new("Video_ToggleHdPacks", HostDrawerTabId.Video, "HD Texture Packs", settings.IsHdTexturePackEnabled ? "ON" : "OFF", "Clean-room <dataDir>\\hdpacks\\ override store", IsInteractive: true),
            new("Video_AspectRatio", HostDrawerTabId.Video, "Aspect Ratio Mode", aspectLabel, "Native PAR / 4:3 / Square Int / Native Fit / 16:9 Hor+", IsInteractive: true),
            new("Video_ToggleOverscan", HostDrawerTabId.Video, "Overscan Crop", overscanLabel, "Source edge overscan crop in pixels", IsInteractive: true),
            new("System_ToggleFastForward", HostDrawerTabId.System, "Fast-Forward", ffLabel, "F6 toggle (or unbound Tab hold) 2x..4x", IsInteractive: true),
            new("System_SkipCutscene", HostDrawerTabId.System, "Cutscene Skip", "Trigger (F7)", "Invokes ISkipCutsceneHook at safe frame boundary", IsInteractive: true)
        };

        List<HostControlOptionDescriptor>? controlOptions = null;
        if (moduleProfile?.ControlOptions is { Count: > 0 } modOpts)
        {
            controlOptions = new List<HostControlOptionDescriptor>(modOpts.Count);
            foreach (GameControlOptionDescriptor opt in modOpts)
            {
                string[] choices = opt.Choices.Select(c => c.Label).ToArray();
                controlOptions.Add(new HostControlOptionDescriptor(opt.OptionId, opt.Label, choices, settings.GetModuleOption(opt)));
            }
        }

        return new HostDrawerDescriptor(
            ModuleTitle: string.IsNullOrWhiteSpace(manifest?.DisplayName) ? "reSlate" : manifest.DisplayName,
            EngineDisplayName: $"reSlate v{ReStationEngineInfo.Version}",
            ExecutionTierLabel: string.IsNullOrWhiteSpace(executionTierLabel) ? "AOT Primary" : executionTierLabel,
            BackendLabel: FormatBackendDisplayLabel(settings.BackendId),
            ResolutionScale: settings.ResolutionScale,
            ActiveSchema: settings.InputProfileMode.ToString(),
            MediaLabel: string.IsNullOrWhiteSpace(discInfo) ? "Disc/Media Image" : discInfo,
            ExtraRows: extraRows,
            ControlOptions: controlOptions);
    }

    public static string FormatBackendDisplayLabel(string? backendId) => HostVideoSurface.NormalizeBackendId(backendId) switch
    {
        PsxRenderBackendIds.Vulkan => "Vulkan",
        PsxRenderBackendIds.ParallelCpu => "Parallel CPU",
        PsxRenderBackendIds.Software => "Software CPU",
        _ => "OpenGL 3.3"
    };

    public static HostAspectRatioMode ResolveDisplayAspectMode(RecompSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(settings.AspectRatioMode))
            return settings.IsWidescreen16By9 ? HostAspectRatioMode.HorPlus16By9 : HostAspectRatioMode.NativePar;
        return HostAspectRatioModeCodec.Parse(settings.AspectRatioMode);
    }

    public static string FormatOverscanLabel(HostOverscanCrop crop)
    {
        if (crop.Top <= 0 && crop.Bottom <= 0 && crop.Left <= 0 && crop.Right <= 0) return "0px";
        if (crop.Top == crop.Bottom && crop.Top == crop.Left && crop.Top == crop.Right)
            return string.Create(CultureInfo.InvariantCulture, $"{crop.Top}px");
        return string.Create(CultureInfo.InvariantCulture, $"{crop.Top}/{crop.Bottom}/{crop.Left}/{crop.Right}px");
    }

    public static HostOverscanCrop CycleOverscan(HostOverscanCrop current)
    {
        int max = Math.Max(Math.Max(current.Top, current.Bottom), Math.Max(current.Left, current.Right));
        return max switch
        {
            0 => new HostOverscanCrop(8, 8, 8, 8),
            <= 8 => new HostOverscanCrop(16, 16, 16, 16),
            _ => HostOverscanCrop.None
        };
    }

    public static double NormalizeFastForwardMultiplier(double raw) =>
        double.IsFinite(raw) ? Math.Clamp(raw, 2.0, 4.0) : 2.0;
}
