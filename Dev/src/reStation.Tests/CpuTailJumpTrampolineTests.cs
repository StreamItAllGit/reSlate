// @module: reStation.Tests.CpuTailJumpTrampolineTests
// @purpose: Regression coverage for v0.2.5 O(1)-stack tail-jump trampoline, 0xA0 BIOS vector tail dispatch, and F3 HUD strip Keybinds clamp.
// @depends-on: Xunit, reStation.Contracts, reStation.Core, reStation.Core.Cpu, reStation.Core.Gpu, reStation.Player
// @exports: CpuTailJumpTrampolineTests

using reStation.Contracts;
using reStation.Core;
using reStation.Core.Cpu;
using reStation.Core.Gpu;
using reStation.Player;
using Xunit;

namespace reStation.Tests;

public sealed class CpuTailJumpTrampolineTests
{
    private const uint FnCaller = 0x80010000U;
    private const uint FnA = 0x80010100U;
    private const uint FnB = 0x80010200U;
    private const uint FnEnd = 0x80010300U;
    private const uint FnBiosTail = 0x80010400U;

    private static PsxCpu CreateCpu() =>
        new(new RecompSettings(), Path.GetTempPath(), new SoftwareCpuRenderBackend());

    [Fact]
    public void Cpu_MutualTailJumpBounce200k_CompletesInConstantStackAndTriggersOnAfterCall()
    {
        using PsxCpu cpu = CreateCpu();
        var mod = new TailBounceModule(maxBounces: 200_000);
        var hook = new RecordingSubTickHook();
        cpu.RegisterModule(mod);
        cpu.FramePatch = hook;

        const uint expectedRa = 0x80010840U;
        cpu.R[31] = expectedRa;
        cpu.Call(FnCaller);

        Assert.Equal(200_000, mod.Bounces);
        Assert.Equal(0x12345678U, cpu.R[2]);
        Assert.Equal(expectedRa, cpu.R[31]);
        Assert.False(cpu.ShouldUnwind);
        Assert.Equal(0L, cpu.InterpreterFallbackCount);
        Assert.Contains(FnA, hook.AfterCalls);
        Assert.Contains(FnEnd, hook.AfterCalls);
    }

    [Fact]
    public void Cpu_JumpIndirectToBiosVectorA0_DispatchesCleanlyInsideTailChain()
    {
        using PsxCpu cpu = CreateCpu();
        var mod = new TailBounceModule(maxBounces: 0);
        cpu.RegisterModule(mod);

        cpu.R[2] = 0U;
        cpu.R[31] = 0x80010900U;
        cpu.Call(FnBiosTail);

        Assert.NotEqual(0U, cpu.R[2]);
        Assert.Equal(0L, cpu.InterpreterFallbackCount);
        Assert.Equal(0x80010900U, cpu.R[31]);
        Assert.False(cpu.ShouldUnwind);
    }

    [Fact]
    public void UiOverlayPresenter_OpenKeybindsTab_ClampsF3HudStripClearOfDrawer()
    {
        var presenter = new UiOverlayPresenter();
        var drawer = new SlideOverSettingsDrawer { IsOpen = true, ActiveTab = 2 };
        var settings = new RecompSettings { IsFpsOverlayEnabled = true, FrameRateMode = FrameRateMode.Enhanced };
        var manifest = new ReStationModuleManifest
        {
            ModuleId = "meadowvalley-recomp",
            Title = "HM",
            DisplayName = "HM",
            EngineId = ReStationEngineInfo.EngineId,
            EngineVersion = ReStationEngineInfo.Version,
            CompatibleEngineTrack = ReStationEngineInfo.CompatibleEngineTrack
        };
        var telemetry = PsxTelemetrySnapshot.Empty with
        {
            FrameNumber = 42,
            CpuMs = 12.34,
            GpuMs = 5.67,
            CurrentFps = 60.0,
            ResolutionScale = 4,
            BackendId = PsxRenderBackendIds.OpenGl,
            AudioFillRatio = 0.999
        };

        Assert.True(presenter.EnsureRendered(drawer, settings, manifest, "SLUS-01115", loggingActive: true, telemetry));
        Assert.True(presenter.LastHudBoxRightEdge > 240);
        Assert.True(
            presenter.LastHudBoxRightEdge <= 466,
            $"Expected F3 HUD strip right edge <= 466 px clear of 800 px Keybinds drawer (x=480), actual {presenter.LastHudBoxRightEdge} px.");

        drawer.ActiveTab = 0;
        Assert.True(presenter.EnsureRendered(drawer, settings, manifest, "SLUS-01115", loggingActive: true, telemetry));
        Assert.True(presenter.LastHudBoxRightEdge <= 14 + 652);
    }

    private sealed class TailBounceModule(int maxBounces) : IRecompModule
    {
        public int ExeId => 0;
        public string ExeName => "TAIL.EXE";
        public uint TextStart => 0x80010000U;
        public uint TextEnd => 0x80011000U;
        public int Bounces { get; private set; }

        public bool TryExecuteFunction(IReStationCpu cpu, uint pc)
        {
            switch (pc)
            {
                case FnCaller:
                {
                    uint savedRa = cpu.R[31];
                    cpu.Call(FnA);
                    cpu.R[31] = savedRa;
                    return true;
                }
                case FnA:
                    Bounces++;
                    cpu.JumpIndirect(Bounces < maxBounces ? FnB : FnEnd);
                    return true;
                case FnB:
                    Bounces++;
                    cpu.JumpIndirect(Bounces < maxBounces ? FnA : FnEnd);
                    return true;
                case FnEnd:
                    cpu.R[2] = 0x12345678U;
                    return true;
                case FnBiosTail:
                    cpu.R[9] = 0x2FU; // BIOS A(2Fh) rand()
                    cpu.JumpIndirect(0x000000A0U);
                    return true;
                default:
                    return false;
            }
        }
    }

    private sealed class RecordingSubTickHook : ISubTickFrameHook
    {
        public List<uint> AfterCalls { get; } = new();
        public uint SubTickEntryPc => 0U;
        public uint MaxSubTickCycles => 1000U;
        public bool TryInterceptCall(IReStationCpu cpu, uint canonPc) => false;
        public void Reset() { }
        public bool ShouldRunSubTick(uint subFrameIndex, uint totalSubFrames) => false;
        public void OnSubTickBegin(IReStationCpuEx cpu, uint subFrameIndex, uint totalSubFrames) { }
        public void OnSubTickEnd(IReStationCpuEx cpu, uint subFrameIndex, bool completedCleanly) { }
        public void OnAfterCall(IReStationCpuEx cpu, uint canonPc) => AfterCalls.Add(canonPc);
    }
}
