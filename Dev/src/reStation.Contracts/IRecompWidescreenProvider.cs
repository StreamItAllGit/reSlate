// @module: reStation.Contracts.IRecompWidescreenProvider
// @purpose: Optional module capability indicating whether dynamic true 16:9 widescreen projection patching is active for the current frame/scene.
// @depends-on: reStation.Contracts.IReStationCpu
// @exports: IRecompWidescreenProvider

namespace reStation.Contracts;

/// <summary>
/// Optional interface implemented by a game module to declare whether true 16:9 widescreen projection
/// patching is actively valid for the current in-game scene (allowing FMV/title overlays to stay 4:3).
/// </summary>
public interface IRecompWidescreenProvider
{
    /// <summary>
    /// Returns <see langword="true"/> when true 16:9 widescreen projection hooks are actively supported
    /// in the current game state; returns <see langword="false"/> to constrain viewport presentation to 4:3.
    /// </summary>
    bool IsActiveTrueWidescreenSupported(IReStationCpu cpu);
}
