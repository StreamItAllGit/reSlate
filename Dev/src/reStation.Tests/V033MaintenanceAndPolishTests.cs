// @module: reStation.Tests.V033MaintenanceAndPolishTests
// @purpose: Verifies v0.3.3 T1 cold-archive UI-pumped ingest + pre-1.0 UI version visibility and T2 MemCardMcrStore safe orphan auto-backup/reclaim.
// @depends-on: Xunit, System.Buffers.Binary, System.Diagnostics, System.IO.Compression, System.Text, Foundation.Core, reStation.Contracts, reStation.Core, reStation.Core.Bios, reStation.Core.Cpu, reStation.Core.DiscIngestion, reStation.Core.Gpu, reStation.Player
// @exports: V033MaintenanceAndPolishTests

using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Foundation.Core;
using reStation.Contracts;
using reStation.Core;
using reStation.Core.Bios;
using reStation.Core.Cpu;
using reStation.Core.DiscIngestion;
using reStation.Core.Gpu;
using reStation.Player;
using Xunit;
using PlayerProgram = reStation.Player.Program;

namespace reStation.Tests;

public sealed class V033MaintenanceAndPolishTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"reslate-v033-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (!Directory.Exists(_tempRoot)) return;
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"Temp cleanup skipped: {ex.Message}");
        }
    }

    [Fact]
    public void MemCardMcrStore_AutoReclaimsOrphanedNamelessChain_CreatesAtomicBackup_WritesXor_LogsWarn_AndAllows4BlockSave()
    {
        string dataDir = Path.Combine(_tempRoot, "mc-orphan");
        string memcardsDir = Path.Combine(dataDir, "memcards");
        Directory.CreateDirectory(memcardsDir);

        byte[] card = CreateFormattedCardBytes();
        WriteDirChain(card, startBlock: 1, blockCount: 11, name: "BASLUS-01115", writeScMagic: true);
        WriteDirChain(card, startBlock: 12, blockCount: 4, name: string.Empty, writeScMagic: false);
        string slot1Path = Path.Combine(memcardsDir, "memcard_mod-orphan_slot1.mcr");
        File.WriteAllBytes(slot1Path, card);

        var warnings = new List<string>();
        var store = new MemCardMcrStore(dataDir, "mod-orphan", warnLogger: warnings.Add);

        Assert.Single(warnings);
        Assert.Contains("[MEMCARD] Reclaimed 4 orphaned nameless block(s)", warnings[0], StringComparison.Ordinal);

        MemCardSlotSummary summary = store.GetSlotBlockSummary(0);
        Assert.Equal(MemCardMcrStore.UserBlockCount, summary.TotalBlocks);
        Assert.Equal(11, summary.OccupiedBlocks);
        Assert.Equal(4, summary.FreeBlocks);
        Assert.Equal(0, summary.OrphanedBlocks);
        Assert.Equal(["BASLUS-01115"], summary.NamedEntries);

        string[] backups = Directory.GetFiles(memcardsDir, "memcard_mod-orphan_slot1.mcr.bak-*");
        Assert.Single(backups);
        Assert.Equal(card, File.ReadAllBytes(backups[0]));

        byte[] diskAfter = File.ReadAllBytes(slot1Path);
        for (int b = 12; b <= 15; b++)
        {
            int dir = b * MemCardMcrStore.SectorSize;
            Assert.Equal(0x000000A0U, BinaryPrimitives.ReadUInt32LittleEndian(diskAfter.AsSpan(dir, 4)));
            Assert.Equal((ushort)0xFFFF, BinaryPrimitives.ReadUInt16LittleEndian(diskAfter.AsSpan(dir + 8, 2)));
            Assert.Equal(ComputeXor127(diskAfter.AsSpan(dir, 128)), diskAfter[dir + 127]);
        }

        Assert.NotNull(store.ReadFile("BASLUS-01115"));
        byte[] newSave = Enumerable.Range(0, 4 * MemCardMcrStore.BlockSize).Select(i => (byte)(i & 0xFF)).ToArray();
        newSave[0] = (byte)'S';
        newSave[1] = (byte)'C';
        Assert.True(store.WriteFile("BASLUS-09999", newSave));
        Assert.Equal(newSave, store.ReadFile("BASLUS-09999"));

        // Re-mounting is idempotent (no duplicate backup, no warning).
        var remountWarnings = new List<string>();
        _ = new MemCardMcrStore(dataDir, "mod-orphan", warnLogger: remountWarnings.Add);
        Assert.Empty(remountWarnings);
        Assert.Single(Directory.GetFiles(memcardsDir, "memcard_mod-orphan_slot1.mcr.bak-*"));
    }

    [Fact]
    public void MemCardMcrStore_ZeroFalsePositives_PreservesNamed_ScMagic_AndNamedZeroMagic()
    {
        string dataDir = Path.Combine(_tempRoot, "mc-safe");
        string memcardsDir = Path.Combine(dataDir, "memcards");
        Directory.CreateDirectory(memcardsDir);

        byte[] card = CreateFormattedCardBytes();
        WriteDirChain(card, startBlock: 1, blockCount: 1, name: "BASLUS-01115", writeScMagic: true);
        WriteDirChain(card, startBlock: 2, blockCount: 1, name: string.Empty, writeScMagic: true);
        WriteDirChain(card, startBlock: 3, blockCount: 1, name: "BASCUS-94100", writeScMagic: false);
        WriteDirChain(card, startBlock: 4, blockCount: 2, name: "BASLUS-CHAIN", writeScMagic: false);
        File.WriteAllBytes(Path.Combine(memcardsDir, "memcard_mod-safe_slot1.mcr"), card);

        var warnings = new List<string>();
        var store = new MemCardMcrStore(dataDir, "mod-safe", warnLogger: warnings.Add);

        Assert.Empty(warnings);
        Assert.Empty(Directory.GetFiles(memcardsDir, "*.bak-*"));
        MemCardSlotSummary summary = store.GetSlotBlockSummary(0);
        Assert.Equal(5, summary.OccupiedBlocks);
        Assert.Equal(10, summary.FreeBlocks);
        Assert.Equal(0, summary.OrphanedBlocks);
        Assert.Equal(0, store.ReclaimOrphanedNamelessBlocks(0).ReclaimedBlockCount);
    }

    [Fact]
    public void MemCardMcrStore_Slot2Bu10_ReclaimsOrphanedChainAndUnreachableTail_AndLogsViaPsxCpu()
    {
        string dataDir = Path.Combine(_tempRoot, "mc-slot2");
        string logDir = Path.Combine(_tempRoot, "mc-slot2-logs");
        string memcardsDir = Path.Combine(dataDir, "memcards");
        Directory.CreateDirectory(memcardsDir);

        byte[] card2 = CreateFormattedCardBytes();
        // Unreachable nameless zero-magic tail at block 4 + orphaned nameless chain at blocks 6..7.
        WriteSingleDirBlock(card2, blk: 4, alloc: 0x00000053U, nextRaw: 0xFFFF, name: string.Empty, writeScMagic: false);
        WriteDirChain(card2, startBlock: 6, blockCount: 2, name: string.Empty, writeScMagic: false);
        File.WriteAllBytes(Path.Combine(memcardsDir, "memcard_mod-s2_slot2.mcr"), card2);

        using var logger = new ReStationLogger(logDir, enabled: true);
        using var cpu = new PsxCpu(new RecompSettings(), dataDir, new SoftwareCpuRenderBackend(), logger);
        cpu.Bios.SetCardIdentity("mod-s2");

        MemCardSlotSummary slot2Summary = cpu.Bios.MemCard.GetSlotBlockSummary(1);
        Assert.Equal(15, slot2Summary.FreeBlocks);
        Assert.Equal(0, slot2Summary.OrphanedBlocks);
        Assert.Single(Directory.GetFiles(memcardsDir, "memcard_mod-s2_slot2.mcr.bak-*"));
        Assert.Contains(
            logger.GetRecentLines(),
            line => line.Contains("[WARN] [MEMCARD] Reclaimed 3 orphaned nameless block(s)", StringComparison.Ordinal));
    }

    [Fact]
    public void Program_MissingMediaExits4WithoutWindow_And_IngestMediaWithUiPumpHandlesSuccessCancellationAndContainment()
    {
        Directory.CreateDirectory(_tempRoot);
        string modDir = Path.Combine(_tempRoot, "mod");
        Directory.CreateDirectory(modDir);
        string modPath = Path.Combine(modDir, "module.recomp.json");
        var manifest = new ReStationModuleManifest
        {
            ModuleId = "v033-ingest",
            Title = "V033 Ingest",
            DisplayName = "V033 Ingest",
            SerialNumber = "SLUS-09999"
        };
        manifest.SaveToFile(modPath);

        string dataDir = Path.Combine(_tempRoot, "data");
        string cfgPath = Path.Combine(_tempRoot, "settings.json");
        string logDir = Path.Combine(_tempRoot, "logs");
        string missingIso = Path.Combine(_tempRoot, "missing.iso");

        int missingExit = PlayerProgram.RunMain(
        [
            "--module", modPath,
            "--disc", missingIso,
            "--data-dir", dataDir,
            "--config", cfgPath,
            "--log-dir", logDir
        ]);
        Assert.Equal(4, missingExit);

        var optsWithDisc = CliOptions.Parse(["--disc", "override.iso", "--preload-media", "preload.iso"]);
        Assert.Equal("override.iso", PlayerProgram.ResolveMediaCandidate(optsWithDisc, manifest, modDir, null));

        // Direct IngestMediaWithUiPump test on a valid zipped synthetic ISO-9660 image.
        string zipPath = Path.Combine(_tempRoot, "game.zip");
        byte[] isoBytes = BuildMinimalIsoBytes();
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            ZipArchiveEntry entry = zip.CreateEntry("game.iso");
            using Stream s = entry.Open();
            s.Write(isoBytes, 0, isoBytes.Length);
        }

        var settings = new RecompSettings { BackendId = PsxRenderBackendIds.Software };
        var env = new AppEnvironment("reslate", ["--data-dir", dataDir, "--config", cfgPath, "--log-dir", logDir, "--tray-only"]);
        using var logger = new ReStationLogger(logDir, enabled: false);
        using var video = HostVideoSurface.CreateOffscreen(settings, logger, "v033-ingest-test");
        var bridge = new HostContractBridge();

        int okExit = PlayerProgram.IngestMediaWithUiPump(
            zipPath, video, env, settings, manifest, logger, out DiscManifest? ingested, out bool cancelled, bridge);
        Assert.Equal(0, okExit);
        Assert.False(cancelled);
        Assert.NotNull(ingested);
        Assert.Equal("game.zip", settings.LastDiscPath);
        Assert.Equal("game.zip", RecompSettings.Load(cfgPath).Value.LastDiscPath);
        Assert.True(bridge.TryCaptureFramebufferPng(out int fbW, out int fbH, out byte[] fbRgba));
        Assert.Equal(FirstRunMediaGateView.CanvasWidth, fbW);
        Assert.Equal(FirstRunMediaGateView.CanvasHeight, fbH);
        Assert.Equal(fbW * fbH * 4, fbRgba.Length);

        // Esc cancellation via HostContractBridge exits cleanly with 0 and cancelled=true.
        string cancelDataDir = Path.Combine(_tempRoot, "data-cancel");
        var cancelEnv = new AppEnvironment("reslate", ["--data-dir", cancelDataDir, "--config", cfgPath, "--log-dir", logDir, "--tray-only"]);
        var cancelBridge = new HostContractBridge();
        cancelBridge.EnqueueKeyPress("Escape");
        int cancelExit = PlayerProgram.IngestMediaWithUiPump(
            zipPath, video, cancelEnv, settings, manifest, logger, out DiscManifest? cancelDisc, out bool wasCancelled, cancelBridge);
        Assert.Equal(0, cancelExit);
        Assert.True(wasCancelled);
        Assert.Null(cancelDisc);

        // Zip-Slip path traversal inside archive fails closed with ExitCode=3.
        string evilZip = Path.Combine(_tempRoot, "evil.zip");
        using (var zip = ZipFile.Open(evilZip, ZipArchiveMode.Create))
        {
            ZipArchiveEntry entry = zip.CreateEntry("../../escaped.txt");
            using var w = new StreamWriter(entry.Open());
            w.Write("evil");
        }
        int containmentExit = PlayerProgram.IngestMediaWithUiPump(
            evilZip, video, cancelEnv, settings, manifest, logger, out _, out bool evilCancelled, bridge);
        Assert.Equal(3, containmentExit);
        Assert.False(evilCancelled);
        Assert.Equal(4, PlayerProgram.IngestMediaWithUiPump(missingIso, video, cancelEnv, settings, manifest, logger, out _, out _, bridge));
    }

    [Fact]
    public void SlideOverSettingsDrawer_And_UiOverlayPresenter_RenderPre10VersionInCollapsedOpenAndCompactHudStates()
    {
        var drawer = new SlideOverSettingsDrawer { IsOpen = false, ActiveTab = 0 };
        var settings = new RecompSettings { IsFpsOverlayEnabled = true, FrameRateMode = FrameRateMode.Enhanced };
        var canvas = new PixelOverlayCanvas(UiOverlayPresenter.OverlayWidth, UiOverlayPresenter.OverlayHeight);
        var manifest = new ReStationModuleManifest { ModuleId = "m-ver", DisplayName = "MVer", SerialNumber = "SLUS-01115" };

        drawer.RenderOverlay(canvas, settings, manifest, "SLUS-01115", loggingActive: false);
        DrawerHitBox togglePill = Assert.Single(drawer.HitBoxes, hb => hb.ControlId == "Drawer_TogglePill");
        Assert.True(togglePill.Rect.Contains(1190, 24));
        Assert.True(HasPixelColorInRegion(canvas, 1170, 50, 1268, 75, UiPaint.Muted));

        drawer.IsOpen = true;
        canvas.Clear(UiPaint.Transparent);
        drawer.RenderOverlay(canvas, settings, manifest, "SLUS-01115", loggingActive: false);
        Assert.True(HasPixelColorInRegion(canvas, 845, 20, 960, 42, UiPaint.Muted));

        drawer.IsOpen = false;
        var presenter = new UiOverlayPresenter();
        var telemetry = PsxTelemetrySnapshot.Empty with { FrameNumber = 5, CurrentFps = 60.0, BackendId = PsxRenderBackendIds.Software, ResolutionScale = 2 };
        Assert.True(presenter.EnsureRendered(drawer, settings, manifest, "SLUS-01115", loggingActive: false, telemetry));
        Assert.True(presenter.IsCompactHudStripActive);
        Assert.Contains($"v{ReStationEngineInfo.Version}", UiOverlayPresenter.FormatHudText(settings, telemetry), StringComparison.Ordinal);

        ReadOnlySpan<byte> strip = presenter.ActiveOverlayPixels;
        bool hasAccentText = false;
        for (int y = 8; y < 26 && !hasAccentText; y++)
        {
            for (int x = 10; x < 100; x++)
            {
                int idx = ((y * presenter.ActiveOverlayWidth) + x) * 4;
                if (strip[idx] == UiPaint.AccentText.R && strip[idx + 1] == UiPaint.AccentText.G && strip[idx + 2] == UiPaint.AccentText.B)
                {
                    hasAccentText = true;
                    break;
                }
            }
        }
        Assert.True(hasAccentText);
    }

    private static bool HasPixelColorInRegion(PixelOverlayCanvas canvas, int x0, int y0, int x1, int y1, RgbaColor target)
    {
        byte[] buf = canvas.Buffer;
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                int idx = ((y * canvas.Width) + x) * 4;
                if (buf[idx] == target.R && buf[idx + 1] == target.G && buf[idx + 2] == target.B)
                    return true;
            }
        }
        return false;
    }

    private static byte[] CreateFormattedCardBytes()
    {
        byte[] card = new byte[MemCardMcrStore.CardSize];
        card[0] = (byte)'M';
        card[1] = (byte)'C';
        card[127] = ComputeXor127(card.AsSpan(0, 128));
        for (int b = 1; b <= MemCardMcrStore.UserBlockCount; b++)
            WriteSingleDirBlock(card, b, 0x000000A0U, 0xFFFF, string.Empty, writeScMagic: false);
        return card;
    }

    private static void WriteDirChain(byte[] card, int startBlock, int blockCount, string name, bool writeScMagic)
    {
        for (int i = 0; i < blockCount; i++)
        {
            int blk = startBlock + i;
            uint alloc = i == 0 ? 0x00000051U : (i == blockCount - 1 ? 0x00000053U : 0x00000052U);
            ushort nextRaw = i + 1 < blockCount ? (ushort)(blk) : (ushort)0xFFFF;
            uint size = i == 0 ? (uint)(blockCount * MemCardMcrStore.BlockSize) : 0U;
            WriteSingleDirBlock(card, blk, alloc, nextRaw, i == 0 ? name : string.Empty, i == 0 && writeScMagic, size);
        }
    }

    private static void WriteSingleDirBlock(
        byte[] card, int blk, uint alloc, ushort nextRaw, string name, bool writeScMagic, uint sizeBytes = 8192U)
    {
        int dir = blk * MemCardMcrStore.SectorSize;
        Array.Fill(card, (byte)0, dir, MemCardMcrStore.SectorSize);
        BinaryPrimitives.WriteUInt32LittleEndian(card.AsSpan(dir, 4), alloc);
        BinaryPrimitives.WriteUInt32LittleEndian(card.AsSpan(dir + 4, 4), sizeBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(card.AsSpan(dir + 8, 2), nextRaw);
        if (!string.IsNullOrEmpty(name))
        {
            byte[] ascii = Encoding.ASCII.GetBytes(name);
            ascii.AsSpan(0, Math.Min(ascii.Length, 20)).CopyTo(card.AsSpan(dir + 10));
        }
        card[dir + 127] = ComputeXor127(card.AsSpan(dir, 128));
        if (writeScMagic)
        {
            int dataOff = blk * MemCardMcrStore.BlockSize;
            card[dataOff] = (byte)'S';
            card[dataOff + 1] = (byte)'C';
        }
    }

    private static byte ComputeXor127(ReadOnlySpan<byte> frame128)
    {
        byte x = 0;
        for (int i = 0; i < 127; i++) x ^= frame128[i];
        return x;
    }

    private static byte[] BuildMinimalIsoBytes()
    {
        byte[] img = new byte[20 * Iso9660RawReader.UserSectorSize];
        int pvd = 16 * Iso9660RawReader.UserSectorSize;
        img[pvd] = 1;
        Encoding.ASCII.GetBytes("CD001").CopyTo(img, pvd + 1);
        img[pvd + 6] = 1;
        Encoding.ASCII.GetBytes("RESTATION").CopyTo(img, pvd + 40);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(pvd + 158, 4), 18U);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(pvd + 166, 4), 2048U);

        int dir = 18 * Iso9660RawReader.UserSectorSize;
        WriteIsoDirRecord(img, dir, lba: 18, length: 2048, isDir: true, "\0");
        WriteIsoDirRecord(img, dir + 34, lba: 18, length: 2048, isDir: true, "\u0001");
        byte[] sysCnf = Encoding.ASCII.GetBytes("BOOT = cdrom:\\SLUS_099.99;1\r\n");
        sysCnf.CopyTo(img, 19 * Iso9660RawReader.UserSectorSize);
        WriteIsoDirRecord(img, dir + 68, lba: 19, length: (uint)sysCnf.Length, isDir: false, "SYSTEM.CNF;1");
        return img;
    }

    private static void WriteIsoDirRecord(byte[] img, int offset, uint lba, uint length, bool isDir, string name)
    {
        byte[] nameBytes = Encoding.ASCII.GetBytes(name);
        byte recordLen = (byte)(33 + nameBytes.Length + ((33 + nameBytes.Length) & 1));
        img[offset] = recordLen;
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(offset + 2, 4), lba);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(offset + 10, 4), length);
        img[offset + 25] = isDir ? (byte)0x02 : (byte)0x00;
        img[offset + 32] = (byte)nameBytes.Length;
        nameBytes.CopyTo(img, offset + 33);
    }
}
