// @module: reStation.Player.FixtureRunnerHost
// @purpose: --fixture <spec.json> runner wrapping Foundation.Retro.SaveStateFixtureRunner with PNG/WAV capture and non-zero exit on any miss.
// @depends-on: System.Text.Json, Foundation.Retro, reStation.Contracts, reStation.Core
// @exports: FixtureRunnerHost
// @owner: devv-player-ui (T6) — public surface T1-locked; engine-backed stepFrameAsync wiring lands in T6.

using System.Text.Json;
using Foundation.Retro;
using reStation.Contracts;
using reStation.Core;

namespace reStation.Player;

public static class FixtureRunnerHost
{
    /// <summary>Loads fixture specs from a JSON array, a single spec object, or a module manifest's <c>saveStateFixtures</c>.</summary>
    public static IReadOnlyList<SaveStateFixtureSpec> LoadSpecs(string specPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(specPath);
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.GetFullPath(specPath)));
        JsonElement root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("saveStateFixtures", out JsonElement nested)) root = nested;
        return root.ValueKind switch
        {
            JsonValueKind.Array => root.Deserialize<List<SaveStateFixtureSpec>>(ReStationModuleManifest.JsonOptions) ?? new List<SaveStateFixtureSpec>(),
            JsonValueKind.Object => new[] { root.Deserialize<SaveStateFixtureSpec>(ReStationModuleManifest.JsonOptions)! },
            _ => throw new InvalidDataException($"Fixture spec '{specPath}' must be a JSON object or array.")
        };
    }

    /// <summary>Runs every fixture; returns 0 when all pass, 1 when any fixture fails, 2 when no fixtures were found.</summary>
    public static async Task<int> RunAsync(
        string specPath,
        string artifactOutputDir,
        Func<SaveStateFixtureSpec, int, CancellationToken, Task<SaveStateFixtureFrameOutput>> stepFrameAsync,
        ReStationLogger? logger = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stepFrameAsync);
        IReadOnlyList<SaveStateFixtureSpec> specs = LoadSpecs(specPath);
        if (specs.Count == 0)
        {
            logger?.Error("FIXTURE", $"No fixtures found in '{Path.GetFileName(specPath)}'.");
            return 2;
        }

        IReadOnlyList<SaveStateFixtureResult> results = await SaveStateFixtureRunner
            .RunAllFixturesAsync(specs, stepFrameAsync, artifactOutputDir, ct)
            .ConfigureAwait(false);
        foreach (SaveStateFixtureResult r in results)
        {
            if (r.Passed) logger?.Info("FIXTURE", r.DiagnosticMessage);
            else logger?.Error("FIXTURE", r.DiagnosticMessage);
        }
        return results.All(r => r.Passed) ? 0 : 1;
    }
}
