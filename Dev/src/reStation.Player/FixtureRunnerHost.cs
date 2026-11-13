// @module: reStation.Player.FixtureRunnerHost
// @purpose: --fixture <spec.json> runner wrapping Foundation.Retro.SaveStateFixtureRunner with engine-backed PNG/WAV capture and non-zero exit on any miss.
// @depends-on: System.Text.Json, Foundation.Core, Foundation.Retro, reStation.Contracts, reStation.Core, reStation.Core.Cpu, reStation.Core.Gpu, reStation.Core.Saves
// @exports: FixtureRunnerHost
// @owner: devv-player-ui (T6)

using System.Text.Json;
using Foundation.Core;
using Foundation.Retro;
using reStation.Contracts;
using reStation.Core;
using reStation.Core.Cpu;
using reStation.Core.Gpu;
using reStation.Core.Saves;

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
        List<SaveStateFixtureSpec> raw = root.ValueKind switch
        {
            JsonValueKind.Array => root.Deserialize<List<SaveStateFixtureSpec>>(ReStationModuleManifest.JsonOptions) ?? new List<SaveStateFixtureSpec>(),
            JsonValueKind.Object => root.Deserialize<SaveStateFixtureSpec>(ReStationModuleManifest.JsonOptions) is { } single ? new List<SaveStateFixtureSpec> { single } : new List<SaveStateFixtureSpec>(),
            _ => throw new InvalidDataException($"Fixture spec '{specPath}' must be a JSON object or array.")
        };
        return raw.Where(static s => s is not null).Select((s, idx) => s with
        {
            FixtureId = string.IsNullOrWhiteSpace(s.FixtureId) ? $"fixture-{idx + 1}" : s.FixtureId,
            SceneName = string.IsNullOrWhiteSpace(s.SceneName) ? $"scene-{idx + 1}" : s.SceneName,
            SaveStatePath = s.SaveStatePath ?? string.Empty,
            FrameCount = s.FrameCount > 0 ? s.FrameCount : 1
        }).ToArray();
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

    /// <summary>
    /// Runs <paramref name="specPath"/> against a deterministic <see cref="SoftwareCpuRenderBackend"/>-backed
    /// <see cref="PsxCpu"/> (and optional loaded <see cref="IRecompGameModule"/>), writing PNG/WAV artifacts under <paramref name="artifactOutputDir"/>.
    /// </summary>
    public static async Task<int> RunWithEngineAsync(
        string specPath,
        string artifactOutputDir,
        IReStationModuleManifest? manifest,
        string? moduleDirectory,
        string dataDirectory,
        RecompSettings settings,
        ReStationLogger logger,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(specPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactOutputDir);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);

        string fullSpecPath = Path.GetFullPath(specPath);
        string specDir = Path.GetDirectoryName(fullSpecPath) ?? Directory.GetCurrentDirectory();
        using var backend = new SoftwareCpuRenderBackend();
        backend.Initialize(HostVideoSurface.NativeWidth, HostVideoSurface.NativeHeight, settings.ResolutionScale);
        var cpu = new PsxCpu(settings, dataDirectory, backend, logger);
        using var pacer = new HighResFramePacer(0);
        IReStationModuleManifest effectiveManifest = manifest ?? new ReStationModuleManifest
        {
            ModuleId = "fixture-host", DisplayName = "Fixture Host", Title = "Fixture Host"
        };

        using var host = new ReStationHost(cpu, effectiveManifest, dataDirectory,
            new RecompHostOptions { TargetFps = 60, ScaleFactor = settings.ResolutionScale }, pacer, logger);
        IRecompGameModule? gameModule = null;
        ModuleAssemblyLoader? alc = null;
        if (manifest is not null && !string.IsNullOrWhiteSpace(moduleDirectory) && !string.IsNullOrWhiteSpace(manifest.EntryAssembly))
        {
            gameModule = ModuleAssemblyLoader.LoadGameModule(manifest, moduleDirectory, out alc);
            await gameModule.InitializeAsync(host, ct).ConfigureAwait(false);
        }

        string? loadedSpecId = null;
        var audioChunk = new short[ReStationHost.SamplesPerFrame * 2];

        try
        {
            return await RunAsync(fullSpecPath, artifactOutputDir, async (spec, frameIndex, token) =>
            {
                if (frameIndex == 0 || !string.Equals(loadedSpecId, spec.FixtureId, StringComparison.Ordinal))
                {
                    loadedSpecId = spec.FixtureId;
                    string? statePath = ResolveSaveStatePath(spec.SaveStatePath, dataDirectory, moduleDirectory, specDir);
                    if (statePath is not null)
                    {
                        if (gameModule is not null) await gameModule.LoadSaveStateAsync(statePath, token).ConfigureAwait(false);
                        else LoadRst1Snapshot(cpu, statePath);
                    }
                }

                if (gameModule is not null) await host.StepModuleFrameAsync(gameModule, token).ConfigureAwait(false);
                else host.RunFrame();

                cpu.Gpu.PresentFrame();
                byte[] rgba = backend.CaptureRgba(out int w, out int h);
                int read = cpu.Spu.Output.Read(audioChunk);
                short[] pcm = read > 0 ? audioChunk.AsSpan(0, read).ToArray() : new short[audioChunk.Length];
                return new SaveStateFixtureFrameOutput(rgba, w, h, pcm, 44_100, 2);
            }, logger, ct).ConfigureAwait(false);
        }
        finally
        {
            _ = alc;
        }
    }

    private static string? ResolveSaveStatePath(string saveStatePath, string dataDir, string? moduleDir, string specDir)
    {
        if (string.IsNullOrWhiteSpace(saveStatePath)) return null;
        if (Path.IsPathRooted(saveStatePath)) return File.Exists(saveStatePath) ? saveStatePath : null;
        foreach (string root in new[] { dataDir, moduleDir, specDir }.Where(static r => !string.IsNullOrWhiteSpace(r))!)
        {
            string full = SandboxEnvironment.AssertPathContained(Path.GetFullPath(Path.Combine(root, saveStatePath)), Path.GetFullPath(root));
            if (File.Exists(full)) return full;
        }
        return null;
    }

    private static void LoadRst1Snapshot(PsxCpu cpu, string statePath)
    {
        using var fs = File.OpenRead(statePath);
        using var hdr = new BinaryReader(fs, System.Text.Encoding.UTF8, leaveOpen: true);
        if (hdr.ReadUInt32() != QuickSaveManager.Magic) throw new InvalidDataException($"Snapshot '{Path.GetFileName(statePath)}' missing RST1 magic.");
        _ = hdr.ReadInt32();
        _ = hdr.ReadString();
        using var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionMode.Decompress);
        using var r = new BinaryReader(gz, System.Text.Encoding.UTF8);
        cpu.ReadSnapshotState(r);
    }
}
