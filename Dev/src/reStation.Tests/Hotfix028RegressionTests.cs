// @module: reStation.Tests.Hotfix028RegressionTests
// @purpose: Regression coverage for v0.2.8 per-game ControlOptions + metadataJson, unstretched Hor+ 16:9 wings/24-bit 4:3, latched inputs, BIOS CloseEvent/ExecOverlay IntRP isolation, and bounded SPU audio drain.
// @depends-on: Xunit, Foundation.Core, Foundation.Retro, reStation.Contracts, reStation.Core, reStation.Player
// @exports: Hotfix028RegressionTests

using Foundation.Core;
using Foundation.Retro;
using reStation.Contracts;
using reStation.Core;
using reStation.Core.Audio;
using reStation.Core.Cpu;
using reStation.Core.Gpu;
using reStation.Core.Gte;
using reStation.Core.Hardware;
using reStation.Core.Input;
using reStation.Player;
using Xunit;

namespace reStation.Tests;

public sealed class Hotfix028RegressionTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"restation-v028-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    private sealed class CustomOptionsProfile : IModuleInputProfileProvider, INativeGameActionHook
    {
        public string ProfileDisplayName => "Custom Module Modern PC";
        public IReadOnlyList<GameActionDescriptor> ModernActions { get; } =
        [
            new("attack", "Attack", 1001, (ushort)PsxPadButton.Cross, (ushort)PsxPadButton.Cross),
            new("pause", "Pause", 41, (ushort)PsxPadButton.Start, (ushort)PsxPadButton.Start)
        ];
        public IReadOnlyList<GameControlOptionDescriptor> ControlOptions { get; } =
        [
            new("invertLookY", "Look Y Axis", "off",
                [new GameControlOptionChoice("off", "Normal"), new GameControlOptionChoice("on", "Inverted")]),
            new("sprintMode", "Controller Sprint", "outerTilt",
                [new GameControlOptionChoice("outerTilt", "Outer Stick"), new GameControlOptionChoice("hold", "Hold L2")])
        ];

        public NativeGameActionContext LastContext { get; private set; }
        public List<string> LastPressed { get; } = new();
        public bool IsGameplayContextActive(IReStationCpu cpu) => true;

        public void ApplyNativeActions(
            IReStationCpuEx cpu, ReadOnlySpan<string> activeActions, ReadOnlySpan<string> pressedActions,
            in NativeGameActionContext context, bool gameplayActive, out bool releaseMouseCapture)
        {
            LastContext = context;
            LastPressed.Clear();
            foreach (string s in pressedActions) LastPressed.Add(s);
            releaseMouseCapture = false;
        }

        public void ApplyNativeActions(
            IReStationCpuEx cpu, ReadOnlySpan<string> activeActions, ReadOnlySpan<string> pressedActions,
            (float MoveX, float MoveY, float LookX, float LookY) axes, bool gameplayActive, out bool releaseMouseCapture)
            => releaseMouseCapture = false;
    }

    [Fact]
    public void PerGameControlOptions_MetadataJsonAndEscBinding_RoundTripAndUpdateUiDigest()
    {
        Directory.CreateDirectory(_tempRoot);
        var provider = new CustomOptionsProfile();
        var settings = new RecompSettings { InputProfileMode = InputProfileMode.PerGameModern };
        Assert.True(settings.IsBindingUsedByAnyModernAction(41, provider.ModernActions));
        Assert.False(settings.IsBindingUsedByAnyModernAction(99, provider.ModernActions));

        settings.SetModuleOption("invertLookY", "on");
        string cfg = Path.Combine(_tempRoot, "settings.json");
        settings.Save(cfg);
        RecompSettings reloaded = RecompSettings.Load(cfg).Value;
        Assert.Equal("on", reloaded.GetModuleOption(provider.ControlOptions[0]));
        Assert.Equal("outerTilt", reloaded.GetModuleOption(provider.ControlOptions[1]));

        string scalarCfg = Path.Combine(_tempRoot, "scalar-meta.json");
        File.WriteAllText(scalarCfg, """{"metadataJson":{"invertLookY":true,"repeatCount":2}}""");
        RecompSettings scalarLoaded = RecompSettings.Load(scalarCfg).Value;
        Assert.Equal("true", scalarLoaded.GetModuleOption("invertLookY"));
        Assert.Equal("2", scalarLoaded.GetModuleOption("repeatCount"));

        var drawer = new SlideOverSettingsDrawer { IsOpen = true, ActiveTab = 1 };
        drawer.AttachInputProfiles(new GlobalControllerProfile(), string.Empty, provider);
        var presenter = new UiOverlayPresenter();
        Assert.True(presenter.EnsureRendered(drawer, reloaded, null, "SLUS", false, PsxTelemetrySnapshot.Empty));

        reloaded.SetModuleOption("invertLookY", "off");
        Assert.True(presenter.EnsureRendered(drawer, reloaded, null, "SLUS", false, PsxTelemetrySnapshot.Empty));

        var hookProfileCpu = new PsxCpu(reloaded, _tempRoot, new SoftwareCpuRenderBackend());
        var prev = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var actBuf = new List<string>();
        var prsBuf = new List<string>();
        Span<byte> keys = stackalloc byte[KeyBindingsConfig.MaxScancode + 1];
        keys[41] = 1;
        ModernInputController.ApplyNativeGameActions(
            keys, 1u, default, hookProfileCpu, reloaded, provider, provider,
            prev, actBuf, prsBuf, out bool releaseMouse);
        Assert.False(releaseMouse);
        Assert.Equal("off", provider.LastContext.GetOption("invertLookY", "on"));
        Assert.Contains("attack", provider.LastPressed);
        Assert.Contains("pause", provider.LastPressed);
    }

    [Fact]
    public void TrueHorPlus16By9_Preserves1To1PixelAspectRendersWingsAndKeeps24BitAt4By3()
    {
        var settings = new RecompSettings { IsPgxpEnabled = false, IsWidescreen16By9 = true };
        var gte = new PsxGte(settings, new PgxpTracker());
        gte.WriteControl(0, 0x1000U); gte.WriteControl(2, 0x00001000U); gte.WriteControl(4, 0x1000U);
        gte.WriteControl(24, 160U << 16); gte.WriteControl(25, 120U << 16); gte.WriteControl(26, 256U);
        gte.WriteData(0, 0x00000010U); gte.WriteData(1, 256U);
        gte.Execute(0x00080001U); // RTPS: 1:1 unstretched even in 16:9
        Assert.Equal((short)176, (short)(gte.ReadData(14) & 0xFFFFU));

        var sw = new SoftwareCpuRenderBackend();
        sw.Initialize(320, 240, 1);
        // Clear + draw untextured wing triangle extending left into [-40..-10] + centered 20x20 sprite at x=100
        sw.FillDisplayRect(0, 0, 320, 240, 0x000000U);
        uint[] wingTri =
        [
            0x20FF0000U,
            unchecked((uint)(100 << 16 | (ushort)(-40))),
            unchecked((uint)(100 << 16 | (ushort)(-10))),
            unchecked((uint)(140 << 16 | (ushort)(-20)))
        ];
        sw.SubmitPrimitive(0x20U, wingTri, 0, 0, 0, 0, 0, 319, 239);
        uint[] squareRect =
        [
            0x6000FF00U,
            (80U << 16) | 100U,
            (20U << 16) | 20U
        ];
        sw.SubmitPrimitive(0x60U, squareRect, 0, 0, 0, 0, 0, 319, 239);
        sw.EndFrameAndPresent(0, 0, 320, 240, is24Bit: false, widescreen16By9: true);
        byte[] rgba = sw.CaptureRgba(out int w, out int h);
        Assert.Equal(428, w);
        Assert.Equal(240, h);
        // Wing triangle pixel at sx=-25 -> wideX = 54 - 25 = 29, y = 115 must be blue (BGR 0xFF0000 => R=0, G=0, B=255)
        int wingIdx = ((115 * w) + 29) * 4;
        Assert.Equal(255, rgba[wingIdx + 2]);
        // Center square must remain 20px wide (x=54+100..54+119), NOT stretched to 27px!
        int insideSq = ((85 * w) + (54 + 119)) * 4;
        int justRightSq = ((85 * w) + (54 + 120)) * 4;
        Assert.Equal(255, rgba[insideSq + 1]);
        Assert.Equal(0, rgba[justRightSq + 1]);

        var gpu = new PsxGpu(settings, new PgxpTracker(), sw);
        Assert.True(gpu.EffectiveWidescreen);
        gpu.WriteGp1(0x08000011U); // Enable 24-bit MDEC display mode
        Assert.False(gpu.EffectiveWidescreen);
    }

    [Fact]
    public void HostInputMapper_MergesLatchedKeyAndMouseDownAcrossTicks()
    {
        var provider = new CustomOptionsProfile();
        var settings = new RecompSettings { InputProfileMode = InputProfileMode.PerGameModern };
        var cpu = new PsxCpu(settings, _tempRoot, new SoftwareCpuRenderBackend());
        byte[] latched = new byte[KeyBindingsConfig.MaxScancode + 1];
        latched[41] = 1; // Esc = Pause
        float dx = 0f, dy = 0f;
        var prev = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var active = new List<string>();
        var pressed = new List<string>();

        _ = HostInputMapper.BuildLiveInput(
            cpu, drawerOpen: false, sdlAvailable: false, settings, null, provider, IntPtr.Zero,
            ref dx, ref dy, wheelUp: false, wheelDown: false, latched, latchedMouseMask: 1u,
            provider, prev, active, pressed, out bool release);
        Assert.False(release);
        Assert.Contains("pause", provider.LastPressed);
        Assert.Contains("attack", provider.LastPressed);
    }

    [Fact]
    public void BiosCloseEvent_ReuseAndDrainSpu_AvoidEventLeaksAndAudioUnderruns()
    {
        var cpu = new PsxCpu(new RecompSettings(), _tempRoot, new SoftwareCpuRenderBackend());
        // Open event slot 0 -> CloseEvent -> Reopen reuses handle 0xF1000000 and stops old fires
        cpu.R[4] = 0xF0000001U; cpu.R[5] = 0x0002U; cpu.R[6] = 0x2000U; cpu.R[7] = 0U;
        cpu.R[9] = 0x08U; cpu.Bios.HandleVectorCall(0xB0U);
        uint h0 = cpu.R[2];
        Assert.Equal(0xF1000000U, h0);
        cpu.R[4] = h0;
        cpu.R[9] = 0x0CU; cpu.Bios.HandleVectorCall(0xB0U); // EnableEvent
        cpu.Bios.OnVBlank();
        cpu.R[4] = h0;
        cpu.R[9] = 0x0BU; cpu.Bios.HandleVectorCall(0xB0U); // TestEvent -> 1
        Assert.Equal(1U, cpu.R[2]);

        cpu.R[4] = h0;
        cpu.R[9] = 0x09U; cpu.Bios.HandleVectorCall(0xB0U); // CloseEvent -> 1
        Assert.Equal(1U, cpu.R[2]);
        cpu.Bios.OnVBlank();
        cpu.R[4] = h0;
        cpu.R[9] = 0x0BU; cpu.Bios.HandleVectorCall(0xB0U); // Closed handle returns 0
        Assert.Equal(0U, cpu.R[2]);

        cpu.R[4] = 0xF4000001U; cpu.R[5] = 0x0004U; cpu.R[6] = 0x2000U; cpu.R[7] = 0U;
        cpu.R[9] = 0x08U; cpu.Bios.HandleVectorCall(0xB0U);
        Assert.Equal(0xF1000000U, cpu.R[2]); // Reuses closed slot

        // Duplicate active callback (Mode=0x1000) across respawn re-init deduplicates instead of double-firing
        cpu.R[4] = 0xF0000001U; cpu.R[5] = 0x0002U; cpu.R[6] = 0x1000U; cpu.R[7] = 0x80012340U;
        cpu.R[9] = 0x08U; cpu.Bios.HandleVectorCall(0xB0U);
        uint cbHandle1 = cpu.R[2];
        cpu.R[4] = 0xF0000001U; cpu.R[5] = 0x0002U; cpu.R[6] = 0x1000U; cpu.R[7] = 0x80012340U;
        cpu.R[9] = 0x08U; cpu.Bios.HandleVectorCall(0xB0U);
        Assert.Equal(cbHandle1, cpu.R[2]);

        // DrainSpuAudioToStream drains multi-frame accumulation with zero underruns
        var ring = new AudioRingBuffer(PsxSpu.SampleRate * 2);
        short[] feed = new short[2940]; // 33.3 ms stereo
        feed[0] = 1234;
        ring.Write(feed);
        short[] scratch = new short[8820];
        int drained = Program.DrainSpuAudioToStream(ring, IntPtr.Zero, scratch);
        Assert.Equal(2940, drained);
        Assert.Equal(0, ring.BufferedSamples);
        Assert.Equal(0L, ring.UnderrunCount);
    }
}
