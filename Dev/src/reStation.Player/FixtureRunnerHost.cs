// @module: reStation.Player.FixtureRunnerHost
// @purpose: --fixture <spec.json> runner wrapping Foundation.Retro.SaveStateFixtureRunner with engine-backed PNG/WAV capture and non-zero exit on any miss.
// @depends-on: System.Text.Json, Foundation.Core, Foundation.Retro, reStation.Contracts, reStation.Core, reStation.Core.Cpu, reStation.Core.Gpu, reStation.Core.Saves
// @exports: FixtureRunnerHost
// @owner: devv-player-ui (T6)

using System.Diagnostics;
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

    /// <summary>Executes each fixture in <paramref name="specs"/>, isolating corrupt/invalid failures per fixture.</summary>
    public static async Task<IReadOnlyList<SaveStateFixtureResult>> RunFixturesAsync(
        IReadOnlyList<SaveStateFixtureSpec> specs,
        string artifactOutputDir,
        Func<SaveStateFixtureSpec, int, CancellationToken, Task<SaveStateFixtureFrameOutput>> stepFrameAsync,
        ReStationLogger? logger = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(specs);
        ArgumentNullException.ThrowIfNull(stepFrameAsync);
        var results = new List<SaveStateFixtureResult>(specs.Count);
        foreach (SaveStateFixtureSpec spec in specs)
        {
            ct.ThrowIfCancellationRequested();
            SaveStateFixtureResult result;
            try
            {
                result = await SaveStateFixtureRunner
                    .RunFixtureAsync(spec, stepFrameAsync, artifactOutputDir, referenceRgbaPixels: null, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                result = new SaveStateFixtureResult(
                    spec.FixtureId,
                    spec.SceneName,
                    Passed: false,
                    FramesExecuted: 0,
                    ActualFramebufferSha256: null,
                    ActualAudioWavSha256: null,
                    MaxChannelDelta: int.MaxValue,
                    ScreenshotPngPath: null,
                    AudioWavPath: null,
                    DiagnosticMessage: $"Fixture '{spec.FixtureId}' failed ({ex.GetType().Name}): {ex.Message}");
            }

            results.Add(result);
            if (result.Passed) logger?.Info("FIXTURE", result.DiagnosticMessage);
            else logger?.Error("FIXTURE", result.DiagnosticMessage);
        }
        return results;
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

        IReadOnlyList<SaveStateFixtureResult> results = await RunFixturesAsync(specs, artifactOutputDir, stepFrameAsync, logger, ct).ConfigureAwait(false);
        return results.All(r => r.Passed) ? 0 : 1;
    }

    /// <summary>
    /// Runs <paramref name="specPath"/> against a deterministic <see cref="IPsxRenderBackend"/>-backed
    /// <see cref="PsxCpu"/> (and optional loaded <see cref="IRecompGameModule"/>), writing PNG/WAV artifacts under <paramref name="artifactOutputDir"/>.
    /// </summary>
    public static Task<int> RunWithEngineAsync(
        string specPath,
        string artifactOutputDir,
        IReStationModuleManifest? manifest,
        string? moduleDirectory,
        string dataDirectory,
        RecompSettings settings,
        ReStationLogger logger,
        CancellationToken ct = default) =>
        RunWithEngineAsync(specPath, artifactOutputDir, manifest, moduleDirectory, dataDirectory, settings, logger, headless: false, ct);

    public static async Task<int> RunWithEngineAsync(
        string specPath,
        string artifactOutputDir,
        IReStationModuleManifest? manifest,
        string? moduleDirectory,
        string dataDirectory,
        RecompSettings settings,
        ReStationLogger logger,
        bool headless,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(specPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactOutputDir);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);

        string fullSpecPath = Path.GetFullPath(specPath);
        string specDir = Path.GetDirectoryName(fullSpecPath) ?? Directory.GetCurrentDirectory();
        IPsxRenderBackend backend = CreateFixtureBackend(settings, headless, logger, out HostVideoSurface? videoSurface, out SoftwareCpuRenderBackend? ownedSoftware);
        logger.Info("FIXTURE", $"Fixture engine initialized backend={backend.BackendId} requested={settings.BackendId} scale={settings.ResolutionScale}x headless={headless}.");

        try
        {
            var cpu = new PsxCpu(settings, dataDirectory, backend, logger);
            using var pacer = new HighResFramePacer(0);
            IReStationModuleManifest effectiveManifest = manifest ?? new ReStationModuleManifest
            {
                ModuleId = "fixture-host", DisplayName = "Fixture Host", Title = "Fixture Host"
            };

            using var host = new ReStationHost(cpu, effectiveManifest, dataDirectory,
                new RecompHostOptions { TargetFps = 60, ScaleFactor = settings.ResolutionScale }, pacer, logger);
            host.AttachRuntimeContext(new HostRuntimeContext(IsHeadless: headless));

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
                        if (!string.IsNullOrWhiteSpace(spec.SaveStatePath))
                        {
                            string? statePath = ResolveSaveStatePath(spec.SaveStatePath, dataDirectory, moduleDirectory, specDir)
                                ?? throw new FileNotFoundException($"Fixture save-state file not found: '{spec.SaveStatePath}'.", spec.SaveStatePath);
                            if (gameModule is not null) await gameModule.LoadSaveStateAsync(statePath, token).ConfigureAwait(false);
                            else _ = QuickSaveManager.LoadFromFile(cpu, statePath, logger);
                        }
                    }

                    long t0 = Stopwatch.GetTimestamp();
                    if (gameModule is not null) await host.StepModuleFrameAsync(gameModule, token).ConfigureAwait(false);
                    else host.RunFrame();
                    long tCpu = Stopwatch.GetTimestamp();

                    cpu.Gpu.PresentFrame();
                    byte[] rgba = backend.CaptureRgba(out int w, out int h);
                    int read = cpu.Spu.Output.Read(audioChunk);
                    short[] pcm = read > 0 ? audioChunk.AsSpan(0, read).ToArray() : new short[audioChunk.Length];
                    long tGpu = Stopwatch.GetTimestamp();

                    double ticksToMs = 1000.0 / Stopwatch.Frequency;
                    host.RecordTimings((tCpu - t0) * ticksToMs, (tGpu - tCpu) * ticksToMs, 0.0);
                    PsxTelemetrySnapshot snap = host.GetTelemetry();
                    _ = logger.TryHeartbeat(frameIndex + 1, Math.Max(1, spec.FrameCount), snap.ToHeartbeatMetrics(), force: frameIndex == 0);
                    return new SaveStateFixtureFrameOutput(rgba, w, h, pcm, 44_100, 2);
                }, logger, ct).ConfigureAwait(false);
            }
            finally
            {
                _ = alc;
            }
        }
        finally
        {
            videoSurface?.Dispose();
            ownedSoftware?.Dispose();
        }
    }

    private static IPsxRenderBackend CreateFixtureBackend(RecompSettings settings, bool headless, ReStationLogger logger,
        out HostVideoSurface? videoSurface, out SoftwareCpuRenderBackend? ownedSoftware)
    {
        bool requestedGl = string.Equals(settings.BackendId, PsxRenderBackendIds.OpenGl, StringComparison.OrdinalIgnoreCase)
            || string.Equals(settings.BackendId, "gl", StringComparison.OrdinalIgnoreCase);
        if (!headless && requestedGl)
        {
            videoSurface = HostVideoSurface.Create("reStation Fixture Host", settings, headless: false, foreground: false, logger);
            ownedSoftware = null;
            return videoSurface.Backend;
        }

        if (headless && requestedGl)
            logger.Warn("GL_FALLBACK", "reason=headless forced=software");
        videoSurface = null;
        ownedSoftware = new SoftwareCpuRenderBackend();
        ownedSoftware.Initialize(HostVideoSurface.NativeWidth, HostVideoSurface.NativeHeight, settings.ResolutionScale);
        return ownedSoftware;
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
}
