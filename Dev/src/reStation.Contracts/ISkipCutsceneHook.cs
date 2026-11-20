// @module: reStation.Contracts.ISkipCutsceneHook
// @purpose: Opt-in game module hook for clean-room cutscene/FMV fast-skip QoL integration (Standard 24).
// @depends-on: reStation.Contracts.IReStationCpu, reStation.Contracts.IRecompModule
// @exports: ISkipCutsceneHook

namespace reStation.Contracts;

/// <summary>
/// Optional opt-in module hook invoked by the host when the player triggers Cutscene Skip (<c>F7</c> / F1 drawer).
/// </summary>
public interface ISkipCutsceneHook
{
    /// <summary>
    /// Attempts to safely skip an active in-game cutscene or FMV sequence via game-aware clean-room state transition.
    /// Returns <see langword="true"/> when a skip was performed; <see langword="false"/> outside skippable scenes.
    /// </summary>
    bool TrySkipCutscene(IReStationCpu cpu, IReStationHost host);
}
