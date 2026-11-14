// @module: reStation.Tests.PsxBiosPadAndThreeTierInputTests
// @purpose: Verifies PsyQ PAD_init/PAD_dr byte-swapping + Port-2 active-low polarity, 3-tier rebindable input + mouse support, settings.controls.json, and --input-script.
// @depends-on: Xunit, System.Text.Json, Foundation.Core, reStation.Contracts, reStation.Core, reStation.Core.Cpu, reStation.Core.Gpu, reStation.Core.Input, reStation.Launcher
// @exports: PsxBiosPadAndThreeTierInputTests
// @owner: devv-tests

using System.Text.Json;
using Foundation.Core;
using reStation.Contracts;
using reStation.Core;
using reStation.Core.Cpu;
using reStation.Core.Gpu;
using reStation.Core.Input;
using reStation.Launcher;
using Xunit;

namespace reStation.Tests;

public sealed class PsxBiosPadAndThreeTierInputTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"restation-input-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true);
    }

    private sealed class StubModernProfile : IModuleInputProfileProvider
    {
        public bool GameplayActive { get; set; } = true;
        public string ProfileDisplayName => "Stub Modern PC";

        public IReadOnlyList<GameActionDescriptor> ModernActions { get; } =
        [
            new("moveForward", "Move Forward", 26, (ushort)PsxPadButton.Up, (ushort)PsxPadButton.Up),
            new("attack", "Attack", KeyBindingsConfig.MouseLeft, (ushort)PsxPadButton.Square, (ushort)PsxPadButton.Cross),
            new("castMagic", "Cast Magic", KeyBindingsConfig.MouseRight, (ushort)PsxPadButton.Triangle, (ushort)PsxPadButton.Circle),
            new("interact", "Interact", 44, (ushort)PsxPadButton.Cross, (ushort)PsxPadButton.Cross),
            new("quickSave", "Quick Save", 62, 0, 0, IsQuickSave: true),
            new("quickLoad", "Quick Load", 66, 0, 0, IsQuickLoad: true)
        ];

        public bool IsGameplayContextActive(IReStationCpu cpu) => GameplayActive;
    }

    private PsxCpu CreateCpu()
    {
        string dataDir = Path.Combine(_tempRoot, "cpu-data");
        Directory.CreateDirectory(dataDir);
        return new PsxCpu(new RecompSettings(), dataDir, new SoftwareCpuRenderBackend());
    }

    [Fact]
    public void PsxBiosHle_PadInitAndPadDr_SwapsBytesPreservesPort2HighBitsAndLeavesB012Raw()
    {
        using PsxCpu cpu = CreateCpu();
        const uint padBuf = 0x80020000U;

        // Idle state: PAD_init(0x20000001, padBuf) -> returns 2, writes 0xFFFFFFFF.
        cpu.SetControllerState(0, 0, 0x80, 0x80, 0x80, 0x80);
        cpu.R[9] = 0x15;
        cpu.R[4] = 0x20000001U;
        cpu.R[5] = padBuf;
        cpu.R[6] = 8;
        cpu.Bios.HandleVectorCall(0xB0U);
        Assert.Equal(2U, cpu.R[2]);
        Assert.Equal(0xFFFFFFFFU, cpu.Bus.Read32(padBuf));

        // Cross (0x4000) swaps to 0x0040 -> active-low ~0x0040 = 0xFFBF; Port 2 stays 0xFFFF -> 0xFFFFFFBF.
        cpu.SetControllerState(0, (ushort)PsxPadButton.Cross, 0x80, 0x80, 0x80, 0x80);
        cpu.R[9] = 0x16;
        cpu.Bios.HandleVectorCall(0xB0U);
        Assert.Equal(0xFFFFFFBFU, cpu.R[2]);
        Assert.Equal(0xFFFFFFBFU, cpu.Bus.Read32(padBuf));

        // D-Pad Down (0x0040) swaps to 0x4000 -> active-low 0xFFFFBFFF (never collides with Cross).
        cpu.SetControllerState(0, (ushort)PsxPadButton.Down, 0x80, 0x80, 0x80, 0x80);
        cpu.R[9] = 0x16;
        cpu.Bios.HandleVectorCall(0xB0U);
        Assert.Equal(0xFFFFBFFFU, cpu.R[2]);
        Assert.NotEqual(0xFFFFFFBFU, cpu.R[2]);

        // D-Pad Up (0x0010) swaps to 0x1000 -> active-low 0xFFFFEFFF.
        cpu.SetControllerState(0, (ushort)PsxPadButton.Up, 0x80, 0x80, 0x80, 0x80);
        cpu.R[9] = 0x16;
        cpu.Bios.HandleVectorCall(0xB0U);
        Assert.Equal(0xFFFFEFFFU, cpu.R[2]);

        // Raw B0:0x12 InitPAD writes 0x4100 + unswapped (ushort)~Cross (0xBFFF) at +2.
        const uint rawBuf = 0x80020100U;
        cpu.SetControllerState(0, (ushort)PsxPadButton.Cross, 0x80, 0x80, 0x80, 0x80);
        cpu.R[9] = 0x12;
        cpu.R[4] = 0x20000000U;
        cpu.R[5] = rawBuf;
        cpu.R[6] = 8;
        cpu.Bios.HandleVectorCall(0xB0U);
        Assert.Equal(1U, cpu.R[2]);
        Assert.Equal(0x4100, cpu.Bus.Read16(rawBuf));
        Assert.Equal(0xBFFF, cpu.Bus.Read16(rawBuf + 2));
    }

    [Fact]
    public void ThreeTierProfiles_RouteGlobalPerGameAndModernContextWithMouseAndArrowFallback()
    {
        using PsxCpu cpu = CreateCpu();
        var profile = new StubModernProfile();
        var settings = new RecompSettings { InputProfileMode = InputProfileMode.GlobalController };
        var globalKb = new KeyBindingsConfig();
        globalKb.SetScancode(BindableAction.Cross, KeyBindingsConfig.MouseLeft);

        byte[] keys = new byte[KeyBindingsConfig.MaxScancode + 1];
        var st = new HostInputState();

        // Tier 1: GlobalController reads globalBindings (LClick -> Cross).
        ModernInputController.ApplyProfileBindings(keys, 1u, cpu, settings, globalKb, profile, ref st);
        Assert.True(st.Buttons.HasFlag(PsxPadButton.Cross));

        // Tier 2: PerGameController reads settings.KeyBindings (LClick -> Start).
        settings.InputProfileMode = InputProfileMode.PerGameController;
        settings.KeyBindings.SetScancode(BindableAction.Start, KeyBindingsConfig.MouseRight);
        st = default;
        ModernInputController.ApplyProfileBindings(keys, 4u, cpu, settings, globalKb, profile, ref st);
        Assert.True(st.Buttons.HasFlag(PsxPadButton.Start));
        Assert.False(st.Buttons.HasFlag(PsxPadButton.Cross));

        // Tier 3 Gameplay: LClick -> Square, RClick -> Triangle, Space -> Cross, F5 -> QuickSave, Up Arrow fallback -> DPadUp.
        settings.InputProfileMode = InputProfileMode.PerGameModern;
        profile.GameplayActive = true;
        keys[44] = 1; // Space
        keys[62] = 1; // F5
        keys[82] = 1; // Arrow Up (unclaimed)
        st = default;
        ModernInputController.ApplyProfileBindings(keys, 1u | 4u, cpu, settings, globalKb, profile, ref st);
        Assert.True(st.Buttons.HasFlag(PsxPadButton.Square | PsxPadButton.Triangle | PsxPadButton.Cross | PsxPadButton.Up));
        Assert.True(st.QuickSave);

        // Tier 3 Menu: RClick -> Circle, LClick -> Cross; custom rebind Interact -> Enter (40).
        profile.GameplayActive = false;
        settings.SetModernActionBinding("interact", 40);
        Array.Clear(keys);
        keys[40] = 1;
        st = default;
        ModernInputController.ApplyProfileBindings(keys, 4u, cpu, settings, globalKb, profile, ref st);
        Assert.True(st.Buttons.HasFlag(PsxPadButton.Cross | PsxPadButton.Circle));
        Assert.False(st.Buttons.HasFlag(PsxPadButton.Triangle));
    }

    [Fact]
    public void GlobalControllerProfile_StaysContainedInSandboxAndRoundTripsUnknownJson()
    {
        string sbxData = Path.Combine(_tempRoot, "Sandbox", "data");
        string sbxLogs = Path.Combine(_tempRoot, "Sandbox", "logs");
        Directory.CreateDirectory(sbxData);
        Directory.CreateDirectory(sbxLogs);
        SandboxEnvironment sbx = CliOptions.Parse(["--test-mode", "--data-dir", sbxData, "--log-dir", sbxLogs]).CreateSandbox();

        string resolved = GlobalControllerProfile.ResolvePath(sbx);
        Assert.Equal(Path.GetFullPath(Path.Combine(sbxData, GlobalControllerProfile.FileName)), Path.GetFullPath(resolved));

        GlobalControllerProfile prof = GlobalControllerProfile.LoadOrDefault(resolved);
        prof.KeyBindings.SetScancode(BindableAction.Square, KeyBindingsConfig.MouseLeft);
        prof.ExtensionData = new Dictionary<string, JsonElement>
        {
            ["futureField"] = JsonDocument.Parse("\"keep-me\"").RootElement.Clone()
        };
        prof.Save(resolved);

        GlobalControllerProfile reloaded = GlobalControllerProfile.LoadOrDefault(resolved);
        Assert.Equal(KeyBindingsConfig.MouseLeft, reloaded.KeyBindings.GetScancode(BindableAction.Square));
        Assert.Equal("controls", reloaded.Domain);
        Assert.True(reloaded.ExtensionData?.ContainsKey("futureField"));
    }

    [Fact]
    public void InputScriptRunner_AndCliForwarding_EnforceContainmentAndEvaluateRamAssertions()
    {
        string sbxData = Path.Combine(_tempRoot, "Sandbox", "data");
        string sbxLogs = Path.Combine(_tempRoot, "Sandbox", "logs");
        Directory.CreateDirectory(sbxData);
        Directory.CreateDirectory(sbxLogs);
        string scriptPath = Path.Combine(sbxLogs, "script.json");
        string shotPath = Path.Combine(sbxLogs, "cap.png");
        File.WriteAllText(scriptPath, JsonSerializer.Serialize(new InputScriptSpec
        {
            Description = "Smoke hold & assert",
            HoldFrames = [new InputHoldStep { StartFrame = 1, EndFrame = 3, Keys = ["Space"], MouseButtons = ["lclick"], OpenDrawer = true, DrawerTab = 2 }],
            Captures = [new InputCaptureStep { Frame = 2, ScreenshotPath = shotPath }],
            RamAssertions =
            [
                new InputRamAssertion { Frame = 2, AddressHex = "0x80199554", MaskHex = "0xFFFF", ExpectedEqualsHex = "0x0040", Label = "CrossDown" },
                new InputRamAssertion { Frame = 3, AddressHex = "0x80199554", MaskHex = "0xFFFF", ExpectedEqualsHex = "0xFFFF", Label = "IntentionalFail" }
            ]
        }));

        string boundary = Path.GetFullPath(Path.Combine(_tempRoot, "Sandbox"));
        string outsideScript = Path.Combine(_tempRoot, "outside.json");
        File.WriteAllText(outsideScript, "{}");
        Assert.Throws<InvalidOperationException>(() => InputScriptRunner.Load(outsideScript, boundary));

        InputScriptRunner runner = InputScriptRunner.Load(scriptPath, boundary);
        byte[] keys = new byte[KeyBindingsConfig.MaxScancode + 1];
        runner.ApplyFrame(2, keys, out uint mouseMask, out DrawerScriptCommand drawerCmd, out string? capOut);
        Assert.Equal(1, keys[44]); // Space scancode
        Assert.Equal(1u, mouseMask);
        Assert.True(drawerCmd.Open);
        Assert.Equal(2, drawerCmd.Tab);
        Assert.Equal(Path.GetFullPath(shotPath), Path.GetFullPath(capOut!));

        using PsxCpu cpu = CreateCpu();
        using var logger = new ReStationLogger(sbxLogs, enabled: true);
        cpu.Bus.Write16(0x80199554U, 0x0040);
        runner.EvaluateAssertions(2, cpu, logger);
        Assert.Equal(1, runner.AssertionsPassed);
        Assert.False(runner.HasFailures);

        runner.EvaluateAssertions(3, cpu, logger);
        Assert.True(runner.HasFailures);

        CliOptions parsed = CliOptions.Parse(["--input-script", scriptPath]);
        Assert.Equal(scriptPath, parsed.InputScriptPath);
        Assert.True(LauncherArgsHelper.RequiresSyncWait(["--input-script", "a.json"]));
        string[] resolved = LauncherArgsHelper.ResolveRelativePathArgs(["--input-script", "sub\\s.json"], _tempRoot);
        Assert.Equal(Path.GetFullPath(Path.Combine(_tempRoot, "sub", "s.json")), resolved[1]);
    }
}
