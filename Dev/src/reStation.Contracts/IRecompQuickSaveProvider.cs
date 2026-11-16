// @module: reStation.Contracts.IRecompQuickSaveProvider
// @purpose: Optional game-module interface allowing RSTX trailer chunk serialization and post-load state restoration.
// @depends-on: System.Collections.Generic, Foundation.Retro
// @exports: IRecompQuickSaveProvider

using Foundation.Retro;

namespace reStation.Contracts;

/// <summary>
/// Optional contract implemented by <see cref="IRecompGameModule"/> implementations that need to persist
/// and restore module-private state (such as dual-slot overlay tracking) alongside quick-save snapshots.
/// </summary>
public interface IRecompQuickSaveProvider
{
    /// <summary>Writes module-private RSTX trailer chunks into <paramref name="chunks"/> right before saving.</summary>
    void CaptureQuickSaveChunks(IDictionary<string, byte[]> chunks);

    /// <summary>Invoked immediately after quick-load completes with the decoded RSTX chunks.</summary>
    void OnQuickSaveLoaded(IReadOnlyDictionary<string, byte[]> chunks);
}
