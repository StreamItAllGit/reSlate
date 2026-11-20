// @module: reStation.Player.Program.FrameLoop.QoL
// @purpose: Fast-Forward guest-slice pacing + Tab unbound guard (Std 33), audio-safe SPU decimation (xaDrops == 0), Cutscene Skip, and HD Pack hot-wiring.
// @depends-on: Foundation.Core, reStation.Contracts, reStation.Core, reStation.Core.Audio, reStation.Core.Cpu, reStation.Core.Enhancement, reStation.Core.Gpu, reStation.Core.Input
// @exports: Program (FrameLoop.QoL partial)

using Foundation.Core;
using Foundation.Retro;
using reStation.Contracts;
using reStation.Core;
using reStation.Core.Audio;
using reStation.Core.Cpu;
using reStation.Core.Enhancement;
using reStation.Core.Gpu;
using reStation.Core.Input;

namespace reStation.Player;

internal static unsafe partial class Program
{
    /// <summary>
    /// Auxiliary <c>Tab</c> (<c>ScTab = 43</c>) Fast-Forward is permitted ONLY when <c>Tab</c> is unbound in BOTH
    /// <see cref="RecompSettings.ModernActionBindings"/> (or profile actions) AND the active 1:1 <see cref="KeyBindingsConfig"/>.
    /// </summary>
    internal static bool IsTabAuxiliaryFastForwardAllowed(
        RecompSettings settings,
        KeyBindingsConfig? globalBindings,
        IModuleInputProfileProvider? profileProvider)
    {
        ArgumentNullException.ThrowIfNull(settings);
        KeyBindingsConfig? active1To1 = settings.InputProfileMode == InputProfileMode.GlobalController
            ? (globalBindings ?? settings.KeyBindings)
            : settings.KeyBindings;
        if (active1To1?.Bindings is not null)
        {
            foreach (int bound in active1To1.Bindings.Values)
            {
                if (bound == Sdl3Native.ScTab) return false;
            }
        }

        if (profileProvider is not null &&
            settings.IsBindingUsedByAnyModernAction(Sdl3Native.ScTab, profileProvider.ModernActions))
        {
            return false;
        }

        if (settings.ModernActionBindings is not null)
        {
            foreach (int[] codes in settings.ModernActionBindings.Values)
            {
                if (codes is null) continue;
                for (int i = 0; i < codes.Length; i++)
                {
                    if (codes[i] == Sdl3Native.ScTab) return false;
                }
            }
        }

        return true;
    }

    internal static int ComputeFastForwardGuestSlices(bool fastForwardActive, double multiplier, ref double tickBudget)
    {
        if (!fastForwardActive)
        {
            tickBudget = 0.0;
            return 1;
        }

        double clamped = double.IsFinite(multiplier) ? Math.Clamp(multiplier, 2.0, 4.0) : 2.0;
        tickBudget += clamped;
        int slices = Math.Clamp((int)Math.Floor(tickBudget + 1e-6), 1, 4);
        tickBudget = Math.Max(0.0, tickBudget - slices);
        return slices;
    }

    private static bool ResolveEffectiveFastForward(
        HostVideoSurface video,
        SlideOverSettingsDrawer drawer,
        RecompSettings settings,
        KeyBindingsConfig globalBindings,
        IModuleInputProfileProvider? profileProvider,
        LoopState st,
        HostContractBridge bridge)
    {
        if (settings.IsFastForwardEnabled) return true;
        if (drawer.IsOpen || !IsTabAuxiliaryFastForwardAllowed(settings, globalBindings, profileProvider)) return false;

        if (st.LatchedKeys[Sdl3Native.ScTab] != 0) return true;
        if (video.SdlAvailable)
        {
            byte* ks = Sdl3Native.SDL_GetKeyboardState(out int numKeys);
            if (ks != null && Sdl3Native.ScTab < numKeys && ks[Sdl3Native.ScTab] != 0) return true;
        }
#if DEBUG || RESTATION_TEST_AUTOMATION
        if (bridge.Diagnostics?.IsVirtualKeyHeld(Sdl3Native.ScTab) == true) return true;
#else
        _ = bridge;
#endif
        return false;
    }

    private static void StepGuestSlicesWithQoL(
        ReStationHost host,
        PsxCpu cpu,
        IRecompGameModule? gameModule,
        LoopState st,
        bool fastForwardActive,
        double multiplier,
        short[] audioScratch)
    {
        int slices = ComputeFastForwardGuestSlices(fastForwardActive, multiplier, ref st.FastForwardTickBudget);
        st.LastGuestSlices = slices;
        for (int i = 0; i < slices && !st.QuitRequested && !host.HasFinished; i++)
        {
            if (i > 0 && cpu.Spu.Output.BufferedSamples > (PsxSpu.RingCapacitySamples / 2))
            {
                TrimSpuRingHead(cpu.Spu.Output, audioScratch, PsxSpu.RingCapacitySamples / 4);
            }
            if (gameModule is not null)
                host.StepModuleFrameAsync(gameModule, CancellationToken.None).GetAwaiter().GetResult();
            else
                host.RunFrame();
        }
    }

    private static void TrimSpuRingHead(AudioRingBuffer output, short[] scratch, int targetMaxSamples)
    {
        while (output.BufferedSamples > targetMaxSamples)
        {
            int excess = (output.BufferedSamples - targetMaxSamples) & ~1;
            int step = Math.Min(scratch.Length & ~1, excess);
            if (step <= 0 || output.Read(scratch.AsSpan(0, step)) <= 0) break;
        }
    }

    internal static int DrainSpuAudioToStream(
        AudioRingBuffer output,
        IntPtr audioStream,
        short[] chunkBuf
#if DEBUG || RESTATION_TEST_AUTOMATION
        , DebugAutomationDiagnostics? diagnostics = null
#endif
        )
    {
        const int maxQueueBytes = (PsxSpu.SampleRate * 2 * sizeof(short)) / 10;
        int totalSamples = 0;
        if (audioStream != IntPtr.Zero && Sdl3Native.SDL_GetAudioStreamQueued(audioStream) > maxQueueBytes * 2)
            _ = Sdl3Native.SDL_ClearAudioStream(audioStream);

        int maxRingSamples = (PsxSpu.SampleRate * 2) / 10;
        if (output.BufferedSamples > maxRingSamples * 2)
        {
            int trim = (output.BufferedSamples - maxRingSamples) & ~1;
            while (trim > 0)
            {
                int step = Math.Min(chunkBuf.Length & ~1, trim);
                int dropped = output.Read(chunkBuf.AsSpan(0, step));
                if (dropped <= 0) break;
                trim -= dropped;
            }
        }

        while (true)
        {
            int availEven = output.BufferedSamples & ~1;
            if (availEven <= 0) break;
            int toRead = Math.Min(chunkBuf.Length & ~1, availEven);
            if (toRead <= 0) break;
            int got = output.Read(chunkBuf.AsSpan(0, toRead));
            if (got <= 0) break;
            totalSamples += got;
#if DEBUG || RESTATION_TEST_AUTOMATION
            diagnostics?.RecordAudioSamples(chunkBuf.AsSpan(0, got), got);
#endif
            if (audioStream != IntPtr.Zero)
            {
                fixed (short* ap = chunkBuf) _ = Sdl3Native.SDL_PutAudioStreamData(audioStream, ap, got * sizeof(short));
            }
        }
        return totalSamples;
    }

    internal static int DrainAudioWithFastForwardDecimation(
        AudioRingBuffer output,
        IntPtr audioStream,
        short[] chunkBuf,
        int guestSlices
#if DEBUG || RESTATION_TEST_AUTOMATION
        , DebugAutomationDiagnostics? diagnostics = null
#endif
        )
    {
        if (guestSlices <= 1)
        {
#if DEBUG || RESTATION_TEST_AUTOMATION
            return DrainSpuAudioToStream(output, audioStream, chunkBuf, diagnostics);
#else
            return DrainSpuAudioToStream(output, audioStream, chunkBuf);
#endif
        }

        const int maxQueueBytes = (PsxSpu.SampleRate * 2 * sizeof(short)) / 10;
        if (audioStream != IntPtr.Zero && Sdl3Native.SDL_GetAudioStreamQueued(audioStream) > maxQueueBytes * 2)
            _ = Sdl3Native.SDL_ClearAudioStream(audioStream);

        int stride = Math.Clamp(guestSlices, 2, 4);
        int totalKept = 0;
        while (true)
        {
            int availEven = output.BufferedSamples & ~1;
            if (availEven <= 0) break;
            int toRead = Math.Min(chunkBuf.Length & ~1, availEven);
            if (toRead <= 0) break;
            int got = output.Read(chunkBuf.AsSpan(0, toRead));
            if (got <= 0) break;

            int kept = 0;
            for (int src = 0; src + 1 < got; src += stride * 2)
            {
                chunkBuf[kept++] = chunkBuf[src];
                chunkBuf[kept++] = chunkBuf[src + 1];
            }
            if (kept <= 0) continue;
            totalKept += kept;
#if DEBUG || RESTATION_TEST_AUTOMATION
            diagnostics?.RecordAudioSamples(chunkBuf.AsSpan(0, kept), kept);
#endif
            if (audioStream != IntPtr.Zero)
            {
                fixed (short* ap = chunkBuf) _ = Sdl3Native.SDL_PutAudioStreamData(audioStream, ap, kept * sizeof(short));
            }
        }
        return totalKept;
    }

    private static void ApplyHdTexturePackSetting(
        PsxCpu cpu,
        AppEnvironment env,
        SlideOverSettingsDrawer drawer,
        RecompSettings settings,
        ReStationLogger logger)
    {
        if (!settings.IsHdTexturePackEnabled)
        {
            (cpu.Gpu.Backend as IPsxGpuBackendHooks)?.AttachHdTexturePack(null);
            drawer.VideoWarningBanner = null;
            logger.Info("VIDEO", "HD texture packs disabled.");
            return;
        }

        try
        {
            var store = HdTexturePackStore.FromDataDirectory(env.DataDirectory, isEnabled: true);
            (cpu.Gpu.Backend as IPsxGpuBackendHooks)?.AttachHdTexturePack(store);
            drawer.VideoWarningBanner = null;
            logger.Info("VIDEO", $"HD texture packs enabled (entries={store.IndexedCount}).");
        }
        catch (InvalidOperationException ex)
        {
            settings.IsHdTexturePackEnabled = false;
            settings.Save(env.ConfigFilePath);
            (cpu.Gpu.Backend as IPsxGpuBackendHooks)?.AttachHdTexturePack(null);
            drawer.VideoWarningBanner = $"HD pack rejected: {ex.Message}";
            logger.Warn("CONTAINMENT", $"Rejected runtime HD texture pack: {ex.Message}");
        }
    }

    private static void SyncBackendHdTexturePack(
        PsxCpu cpu,
        AppEnvironment env,
        SlideOverSettingsDrawer drawer,
        RecompSettings settings,
        ReStationLogger logger)
    {
        if (settings.IsHdTexturePackEnabled)
            ApplyHdTexturePackSetting(cpu, env, drawer, settings, logger);
        else
            (cpu.Gpu.Backend as IPsxGpuBackendHooks)?.AttachHdTexturePack(null);
    }
}
