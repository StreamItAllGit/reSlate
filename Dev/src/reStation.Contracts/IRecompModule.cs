// @module: reStation.Contracts.IRecompModule
// @purpose: Locked per-EXE lifted module ABI and reStation-specific IRecompHost surface handed to IRecompGameModule entrypoints.
// @depends-on: Foundation.Retro, reStation.Contracts.IReStationCpu, reStation.Contracts.IGameFramePatch
// @exports: IRecompModule, IReStationHost

using Foundation.Retro;

namespace reStation.Contracts;

/// <summary>
/// Statically lifted PS-X executable (e.g. boot, intro, game, ending EXEs). A module assembly's top-level
/// <see cref="IRecompGameModule"/> entrypoint (<see cref="IReStationModuleManifest.ModuleTypeName"/>) owns and registers these.
/// </summary>
public interface IRecompModule
{
    /// <summary>Overlay slot index (0-11); slot 0 is the resident boot executable.</summary>
    int ExeId { get; }
    /// <summary>Executable file name inside gamedata (e.g. the boot EXE).</summary>
    string ExeName { get; }
    /// <summary>Inclusive start of the lifted text segment (canonical KSEG0 address).</summary>
    uint TextStart { get; }
    /// <summary>Exclusive end of the lifted text segment.</summary>
    uint TextEnd { get; }
    /// <summary>Executes the compiled function at <paramref name="pc"/>; returns false when no compiled entry exists.</summary>
    bool TryExecuteFunction(IReStationCpu cpu, uint pc);
}

/// <summary>
/// reStation host passed to <see cref="IRecompGameModule.InitializeAsync"/>. Game modules cast the
/// <see cref="IRecompHost"/> argument to this interface to reach the PS1 machine.
/// </summary>
public interface IReStationHost : IRecompHost
{
    /// <summary>The emulated PS1 CPU/machine.</summary>
    IReStationCpu Cpu { get; }
    /// <summary>Manifest of the loaded module.</summary>
    IReStationModuleManifest ModuleManifest { get; }
    /// <summary>Directory containing verified, extracted disc files (<c>&lt;dataDir&gt;\gamedata</c>).</summary>
    string GameDataDirectory { get; }
    /// <summary>Per-module save directory (memory cards and quick saves).</summary>
    string SaveDirectory { get; }
    /// <summary>Registers a lifted per-EXE module with the CPU dispatcher.</summary>
    void RegisterModule(IRecompModule module);
    /// <summary>Installs (or clears with null) the active game-specific frame patch.</summary>
    void SetFramePatch(IGameFramePatch? patch);
    /// <summary>Loads a PS-X EXE from gamedata into RAM, sets PC/GP/SP, and activates <paramref name="exeId"/>.</summary>
    bool LoadExecutable(string exeRelativePath, int exeId);
    /// <summary>Advances the emulated machine by exactly one VBlank frame (cooperatively resuming lifted execution).</summary>
    void RunFrame();
    /// <summary>Latest engine telemetry snapshot.</summary>
    PsxTelemetrySnapshot GetTelemetry();
}
