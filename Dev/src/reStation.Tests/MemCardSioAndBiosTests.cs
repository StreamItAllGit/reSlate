// @module: reStation.Tests.MemCardSioAndBiosTests
// @purpose: Verifies SIO0 0x81 Memory Card (0x52/0x57/0x53/0x58..0x5F NACK), BIOS B0:0x4E/0x4F/0x50 + A0:0xAB/0xAC, memcards\ migration, MCD2 RSTX, and single-VBlank event dedup.
// @depends-on: Xunit, reStation.Contracts, reStation.Core, reStation.Core.Bios, reStation.Core.Cpu, reStation.Core.Gpu, reStation.Core.Hardware, reStation.Core.Saves
// @exports: MemCardSioAndBiosTests

using reStation.Contracts;
using reStation.Core;
using reStation.Core.Bios;
using reStation.Core.Cpu;
using reStation.Core.Gpu;
using reStation.Core.Hardware;
using reStation.Core.Saves;
using Xunit;

namespace reStation.Tests;

public sealed class MemCardSioAndBiosTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"restation-memcard-{Guid.NewGuid():N}");

    public MemCardSioAndBiosTests() => Directory.CreateDirectory(_tempRoot);

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true);
    }

    private PsxCpu CreateCpu(string? subDir = null)
    {
        string dir = subDir is null ? _tempRoot : Path.Combine(_tempRoot, subDir);
        Directory.CreateDirectory(dir);
        return new PsxCpu(new RecompSettings(), dir, new SoftwareCpuRenderBackend());
    }

    private static (byte Rx, bool IrqRaised) ExchangeByte(PsxCpu cpu, byte tx)
    {
        cpu.TimersAndDma.InterruptStatus = 0;
        cpu.Joypad.Write16(0x1F801040U, tx);
        if (cpu.Joypad.NextWakeCycle != long.MaxValue)
        {
            cpu.TotalCycles = cpu.Joypad.NextWakeCycle + 1;
            cpu.Joypad.TickTo(cpu.TotalCycles);
        }
        bool irq = (cpu.TimersAndDma.InterruptStatus & (1U << PsxIrq.Controller)) != 0;
        cpu.Joypad.Write16(0x1F80104AU, (ushort)((cpu.Joypad.Read16(0x1F80104AU) & 0x2000) | 0x1013));
        return ((byte)cpu.Joypad.Read16(0x1F801040U), irq);
    }

    private static (byte Flag, byte[] Data, byte Checksum, byte End) SioReadSector(PsxCpu cpu, bool port2, int sector)
    {
        ushort ctrl = (ushort)(port2 ? 0x3003 : 0x1003);
        cpu.Joypad.Write16(0x1F80104AU, ctrl);
        Assert.Equal((0xFF, true), ExchangeByte(cpu, 0x81));
        (byte flag, bool ack1) = ExchangeByte(cpu, 0x52);
        Assert.True(ack1);
        Assert.Equal((0x5A, true), ExchangeByte(cpu, 0x00));
        Assert.Equal((0x5D, true), ExchangeByte(cpu, 0x00));
        byte msb = (byte)(sector >> 8), lsb = (byte)(sector & 0xFF);
        Assert.Equal((0x00, true), ExchangeByte(cpu, msb));
        Assert.Equal((0x00, true), ExchangeByte(cpu, lsb));
        Assert.Equal((0x5C, true), ExchangeByte(cpu, 0x00));
        Assert.Equal((0x5D, true), ExchangeByte(cpu, 0x00));
        Assert.Equal((msb, true), ExchangeByte(cpu, 0x00));
        Assert.Equal((lsb, true), ExchangeByte(cpu, 0x00));
        byte[] data = new byte[128];
        for (int i = 0; i < 128; i++)
        {
            (byte d, bool ackD) = ExchangeByte(cpu, 0x00);
            Assert.True(ackD);
            data[i] = d;
        }
        (byte chk, bool ackChk) = ExchangeByte(cpu, 0x00);
        Assert.True(ackChk);
        (byte end, bool ackEnd) = ExchangeByte(cpu, 0x00);
        Assert.False(ackEnd);
        cpu.Joypad.Write16(0x1F80104AU, (ushort)(port2 ? 0x2000 : 0x0000));
        return (flag, data, chk, end);
    }

    private static (byte Flag, byte End) SioWriteSector(PsxCpu cpu, bool port2, int sector, byte[] data, byte? xorOverride = null)
    {
        ushort ctrl = (ushort)(port2 ? 0x3003 : 0x1003);
        cpu.Joypad.Write16(0x1F80104AU, ctrl);
        Assert.Equal((0xFF, true), ExchangeByte(cpu, 0x81));
        (byte flag, bool ack1) = ExchangeByte(cpu, 0x57);
        Assert.True(ack1);
        Assert.Equal((0x5A, true), ExchangeByte(cpu, 0x00));
        Assert.Equal((0x5D, true), ExchangeByte(cpu, 0x00));
        byte msb = (byte)(sector >> 8), lsb = (byte)(sector & 0xFF);
        Assert.Equal((0x00, true), ExchangeByte(cpu, msb));
        Assert.Equal((0x00, true), ExchangeByte(cpu, lsb));
        byte xor = (byte)(msb ^ lsb);
        for (int i = 0; i < 128; i++)
        {
            xor ^= data[i];
            Assert.Equal((0x00, true), ExchangeByte(cpu, data[i]));
        }
        Assert.Equal((0x00, true), ExchangeByte(cpu, xorOverride ?? xor));
        Assert.Equal((0x5C, true), ExchangeByte(cpu, 0x00));
        Assert.Equal((0x5D, true), ExchangeByte(cpu, 0x00));
        (byte end, bool ackEnd) = ExchangeByte(cpu, 0x00);
        Assert.False(ackEnd);
        cpu.Joypad.Write16(0x1F80104AU, (ushort)(port2 ? 0x2000 : 0x0000));
        return (flag, end);
    }

    [Fact]
    public void SioMemCard_0x81_GetIdReadWriteXorAndFlag08_WorkAcrossSlot1AndSlot2()
    {
        using PsxCpu cpu = CreateCpu();
        cpu.TimersAndDma.InterruptMask = 1U << PsxIrq.Controller;
        cpu.Bios.SetCardIdentity("slot-dual-test");

        // 0x53 Get ID on Port 1 sees FLAG 0x08 without clearing it.
        cpu.Joypad.Write16(0x1F80104AU, 0x1003);
        Assert.Equal((0xFF, true), ExchangeByte(cpu, 0x81));
        Assert.Equal((MemCardMcrStore.FlagNewCard, true), ExchangeByte(cpu, 0x53));
        byte[] expectedId = [0x5A, 0x5D, 0x5C, 0x5D, 0x04, 0x00, 0x00, 0x80];
        for (int i = 0; i < expectedId.Length; i++)
            Assert.Equal((expectedId[i], i < expectedId.Length - 1), ExchangeByte(cpu, 0x00));
        Assert.True(cpu.Bios.MemCard.IsSlotNewCard(0));

        // 0x52 Read sector 0 returns formatted "MC" header, valid XOR, 'G', and keeps FLAG 0x08 set.
        var (readFlag, hdr, hdrChk, hdrEnd) = SioReadSector(cpu, port2: false, sector: 0);
        Assert.Equal(MemCardMcrStore.FlagNewCard, readFlag);
        Assert.Equal((byte)'M', hdr[0]);
        Assert.Equal((byte)'C', hdr[1]);
        Assert.Equal(0x47, hdrEnd);
        byte calcXor = 0;
        foreach (byte b in hdr) calcXor ^= b;
        Assert.Equal(calcXor, hdrChk);
        Assert.True(cpu.Bios.MemCard.IsSlotNewCard(0));

        // 0x57 Write with bad XOR returns 'N' (0x4E), preserves FLAG 0x08, and does not dirty slot.
        byte[] payload1 = Enumerable.Range(0, 128).Select(i => (byte)(i ^ 0xA5)).ToArray();
        var (_, badEnd) = SioWriteSector(cpu, port2: false, sector: 12, payload1, xorOverride: 0x00);
        Assert.Equal(SioMemCardProtocol.EndBadChecksum, badEnd);
        Assert.True(cpu.Bios.MemCard.IsSlotNewCard(0));
        Assert.False(cpu.Bios.MemCard.IsDirty(0));

        // Valid 0x57 write on Port 1 (sector 12) and Port 2 (sector 15) clears FLAG 0x08 per slot & defers disk flush until VBlank.
        int flushesBefore = cpu.Bios.MemCard.DiskFlushCount;
        var (wFlag1, goodEnd1) = SioWriteSector(cpu, port2: false, sector: 12, payload1);
        Assert.Equal(MemCardMcrStore.FlagNewCard, wFlag1);
        Assert.Equal(SioMemCardProtocol.EndGood, goodEnd1);
        Assert.False(cpu.Bios.MemCard.IsSlotNewCard(0));
        Assert.True(cpu.Bios.MemCard.IsSlotNewCard(1));

        byte[] payload2 = Enumerable.Range(0, 128).Select(i => (byte)(0x5A + i)).ToArray();
        var (wFlag2, goodEnd2) = SioWriteSector(cpu, port2: true, sector: 15, payload2);
        Assert.Equal(MemCardMcrStore.FlagNewCard, wFlag2);
        Assert.Equal(SioMemCardProtocol.EndGood, goodEnd2);
        Assert.False(cpu.Bios.MemCard.IsSlotNewCard(1));
        Assert.Equal(flushesBefore, cpu.Bios.MemCard.DiskFlushCount);

        // One VBlank flushes both dirty slots atomically to canonical memcards\.
        cpu.TotalCycles += PsxCpu.CyclesPerFrame;
        cpu.PollEvents();
        Assert.Equal(flushesBefore + 2, cpu.Bios.MemCard.DiskFlushCount);
        Assert.False(cpu.Bios.MemCard.HasDirtySlots);

        var (afterFlag1, got1, _, _) = SioReadSector(cpu, port2: false, sector: 12);
        var (afterFlag2, got2, _, _) = SioReadSector(cpu, port2: true, sector: 15);
        Assert.Equal((byte)0x00, afterFlag1);
        Assert.Equal((byte)0x00, afterFlag2);
        Assert.Equal(payload1, got1);
        Assert.Equal(payload2, got2);
    }

    [Fact]
    public void SioMemCard_0x58To0x5FMiniHandheldCommands_NackImmediatelyWithoutIrq7()
    {
        using PsxCpu cpu = CreateCpu();
        cpu.TimersAndDma.InterruptMask = 1U << PsxIrq.Controller;

        for (byte cmd = 0x58; cmd <= 0x5F; cmd++)
        {
            cpu.Joypad.Write16(0x1F80104AU, 0x1003);
            Assert.Equal((0xFF, true), ExchangeByte(cpu, 0x81));

            cpu.TimersAndDma.InterruptStatus = 0;
            cpu.Joypad.Write16(0x1F801040U, cmd);
            Assert.Equal((ushort)0xFF, cpu.Joypad.Read16(0x1F801040U));
            Assert.Equal(long.MaxValue, cpu.Joypad.NextWakeCycle);
            cpu.Joypad.TickTo(cpu.TotalCycles + 100_000);
            Assert.Equal(0U, cpu.TimersAndDma.InterruptStatus & (1U << PsxIrq.Controller));
            Assert.Equal(0, cpu.Joypad.Read16(0x1F801044U) & 0x0080);
            cpu.Joypad.Write16(0x1F80104AU, 0x0000);
        }

        // Standard 0x53 ID still succeeds right after 0x58..0x5F probes.
        cpu.Joypad.Write16(0x1F80104AU, 0x1003);
        Assert.Equal((0xFF, true), ExchangeByte(cpu, 0x81));
        Assert.Equal((MemCardMcrStore.FlagNewCard, true), ExchangeByte(cpu, 0x53));
    }

    [Fact]
    public void BiosMemCard_B04E4F50_AndA0ABAC_ShareSlotsNotifyRamAndDebounceFlush()
    {
        using PsxCpu cpu = CreateCpu();
        var ramNotifies = new List<(uint Start, uint Len)>();
        cpu.Bus.OnRamWriteRange += (start, len) => ramNotifies.Add((start, len));

        // Open SwCARD EvSpNEW (0x2000) and EvSpIOE (0x0004) events.
        cpu.R[4] = 0xF4000001U; cpu.R[5] = 0x2000U; cpu.R[6] = 0x2000U; cpu.R[7] = 0;
        cpu.R[9] = 0x08; cpu.Bios.HandleVectorCall(0xB0U);
        uint evNew = cpu.R[2];
        cpu.R[4] = evNew; cpu.R[9] = 0x0C; cpu.Bios.HandleVectorCall(0xB0U);

        cpu.R[4] = 0xF4000001U; cpu.R[5] = 0x0004U; cpu.R[6] = 0x2000U; cpu.R[7] = 0;
        cpu.R[9] = 0x08; cpu.Bios.HandleVectorCall(0xB0U);
        uint evIoe = cpu.R[2];
        cpu.R[4] = evIoe; cpu.R[9] = 0x0C; cpu.Bios.HandleVectorCall(0xB0U);

        // A0:0xAB _card_info before _new_card delivers EvSpNEW.
        cpu.R[4] = 0x00U; cpu.R[9] = 0xAB; cpu.Bios.HandleVectorCall(0xA0U);
        Assert.Equal(1U, cpu.R[2]);
        cpu.R[4] = evNew; cpu.R[9] = 0x0B; cpu.Bios.HandleVectorCall(0xB0U);
        Assert.Equal(1U, cpu.R[2]);

        // B0:0x50 _new_card clears FLAG 0x08 across both slots; subsequent A0:0xAB & A0:0xAC deliver EvSpIOE.
        cpu.R[9] = 0x50; cpu.Bios.HandleVectorCall(0xB0U);
        Assert.Equal(1U, cpu.R[2]);
        Assert.False(cpu.Bios.MemCard.IsSlotNewCard(0));
        Assert.False(cpu.Bios.MemCard.IsSlotNewCard(1));

        cpu.R[4] = 0x00U; cpu.R[9] = 0xAC; cpu.Bios.HandleVectorCall(0xA0U);
        Assert.Equal(1U, cpu.R[2]);
        cpu.R[4] = evIoe; cpu.R[9] = 0x0B; cpu.Bios.HandleVectorCall(0xB0U);
        Assert.Equal(1U, cpu.R[2]);

        // B0:0x4E _card_write writes 128B to Slot 1 sector 20 and Slot 2 sector 21 without immediate disk flush.
        const uint src1 = 0x80020000U, src2 = 0x80020100U, dst1 = 0x80021000U, dst2 = 0x80021100U;
        for (uint i = 0; i < 128; i++)
        {
            cpu.Bus.Write8(src1 + i, (byte)(i + 1));
            cpu.Bus.Write8(src2 + i, (byte)(0xFF - i));
        }
        int flushesBefore = cpu.Bios.MemCard.DiskFlushCount;
        cpu.R[4] = 0x00U; cpu.R[5] = 20U; cpu.R[6] = src1; cpu.R[9] = 0x4E; cpu.Bios.HandleVectorCall(0xB0U);
        Assert.Equal(1U, cpu.R[2]);
        cpu.R[4] = 0x10U; cpu.R[5] = 21U; cpu.R[6] = src2; cpu.R[9] = 0x4E; cpu.Bios.HandleVectorCall(0xB0U);
        Assert.Equal(1U, cpu.R[2]);
        Assert.Equal(flushesBefore, cpu.Bios.MemCard.DiskFlushCount);

        // Second write in same VBlank still coalesces into one flush per slot at VBlank boundary.
        cpu.R[4] = 0x00U; cpu.R[5] = 22U; cpu.R[6] = src1; cpu.R[9] = 0x4E; cpu.Bios.HandleVectorCall(0xB0U);
        cpu.TotalCycles += PsxCpu.CyclesPerFrame;
        cpu.PollEvents();
        Assert.Equal(flushesBefore + 2, cpu.Bios.MemCard.DiskFlushCount);
        cpu.Bios.OnVBlank(); // same VBlankSequence -> no extra disk write
        Assert.Equal(flushesBefore + 2, cpu.Bios.MemCard.DiskFlushCount);

        // B0:0x4F _card_read transfers 128B to guest RAM and fires NotifyRamWriteRange(addr, 128).
        ramNotifies.Clear();
        cpu.R[4] = 0x00U; cpu.R[5] = 20U; cpu.R[6] = dst1; cpu.R[9] = 0x4F; cpu.Bios.HandleVectorCall(0xB0U);
        Assert.Equal(1U, cpu.R[2]);
        cpu.R[4] = 0x10U; cpu.R[5] = 21U; cpu.R[6] = dst2; cpu.R[9] = 0x4F; cpu.Bios.HandleVectorCall(0xB0U);
        Assert.Equal(1U, cpu.R[2]);
        Assert.Contains((dst1 & 0x1FFFFFU, 128U), ramNotifies);
        Assert.Contains((dst2 & 0x1FFFFFU, 128U), ramNotifies);
        for (uint i = 0; i < 128; i++)
        {
            Assert.Equal((byte)(i + 1), cpu.Bus.Read8(dst1 + i));
            Assert.Equal((byte)(0xFF - i), cpu.Bus.Read8(dst2 + i));
        }

        // Multitap sub-channel 0x01 fails cleanly without shadowing Slot 1.
        cpu.R[4] = 0x01U; cpu.R[5] = 0U; cpu.R[6] = dst1; cpu.R[9] = 0x4F; cpu.Bios.HandleVectorCall(0xB0U);
        Assert.Equal(0U, cpu.R[2]);
    }

    [Fact]
    public void MemCardMcrStore_CanonicalMemcardsDir_ModuleIdPriorityAndNonDestructiveSavesMigration()
    {
        string savesDir = Path.Combine(_tempRoot, "saves");
        string memcardsDir = Path.Combine(_tempRoot, "memcards");
        Directory.CreateDirectory(savesDir);
        Directory.CreateDirectory(memcardsDir);

        byte[] legacy1 = new byte[MemCardMcrStore.CardSize];
        legacy1[0] = (byte)'M'; legacy1[1] = (byte)'C'; legacy1[5 * 128] = 0x77;
        string legacySrc1 = Path.Combine(savesDir, "memcard_mod-a_slot1.mcr");
        File.WriteAllBytes(legacySrc1, legacy1);

        byte[] existingDst = new byte[MemCardMcrStore.CardSize];
        existingDst[0] = (byte)'M'; existingDst[1] = (byte)'C'; existingDst[5 * 128] = 0x99;
        string existingPath = Path.Combine(memcardsDir, "memcard_mod-b_slot1.mcr");
        File.WriteAllBytes(existingPath, existingDst);
        byte[] oldOverwriteAttempt = new byte[MemCardMcrStore.CardSize];
        oldOverwriteAttempt[5 * 128] = 0x11;
        File.WriteAllBytes(Path.Combine(savesDir, "memcard_mod-b_slot1.mcr"), oldOverwriteAttempt);

        var store = new MemCardMcrStore(_tempRoot, "mod-a", "SLUS-01115");
        Assert.Equal(Path.GetFullPath(Path.Combine(memcardsDir, "memcard_mod-a_slot1.mcr")), Path.GetFullPath(store.Slot1Path));
        Assert.Equal(Path.GetFullPath(Path.Combine(memcardsDir, "memcard_mod-a_slot2.mcr")), Path.GetFullPath(store.Slot2Path));
        Assert.Equal((byte)0x77, store.GetSlotData(0)[5 * 128]);
        Assert.True(File.Exists(legacySrc1)); // non-destructive copy keeps saves\ intact

        // Existing file in memcards\ is never overwritten (overwrite: false).
        store.SetCardIdentity("mod-b", "SLUS-99999");
        Assert.Equal((byte)0x99, store.GetSlotData(0)[5 * 128]);

        // Blank moduleId falls back cleanly to serialFallback.
        store.SetCardIdentity("   ", "SLUS-00158");
        Assert.Equal(Path.GetFullPath(Path.Combine(memcardsDir, "memcard_SLUS-00158_slot1.mcr")), Path.GetFullPath(store.Slot1Path));
        Assert.Equal(Path.GetFullPath(Path.Combine(memcardsDir, "memcard_SLUS-00158_slot2.mcr")), Path.GetFullPath(store.Slot2Path));
    }

    [Fact]
    public void QuickSave_Mcd2Trailer_RoundTripsDirtySlotState_AndLoadsLegacyWithoutMcd2()
    {
        using PsxCpu src = CreateCpu("qs-src");
        src.Bios.SetCardIdentity("mcd2-test", "SLUS-00001");

        // Write dirty sector 7 on Slot 1 via SIO0 and dirty sector 8 on Slot 2 via BIOS without flushing to disk.
        byte[] s1 = Enumerable.Range(0, 128).Select(i => (byte)(0xC0 ^ i)).ToArray();
        var (_, end1) = SioWriteSector(src, port2: false, sector: 7, s1);
        Assert.Equal(SioMemCardProtocol.EndGood, end1);
        for (uint i = 0; i < 128; i++) src.Bus.Write8(0x80030000U + i, (byte)(0x30 + i));
        src.R[4] = 0x10U; src.R[5] = 8U; src.R[6] = 0x80030000U; src.R[9] = 0x4E;
        src.Bios.HandleVectorCall(0xB0U);

        QuickSaveManager.Save(src, src.DataDirectory, "mcd2-test", 0);
        string slotPath = QuickSaveManager.GetSlotPath(src.DataDirectory, "mcd2-test", 0);

        using PsxCpu dst = CreateCpu("qs-dst");
        QuickSaveLoadResult loaded = QuickSaveManager.LoadFromFile(dst, slotPath);
        Assert.True(loaded.Success);
        Assert.True(loaded.RstxChunks!.ContainsKey(QuickSaveTrailer.ChunkMemCard));
        Assert.True(dst.Bios.MemCard.IsDirty(0));
        Assert.True(dst.Bios.MemCard.IsDirty(1));
        Assert.False(dst.Bios.MemCard.IsSlotNewCard(0));
        Assert.False(dst.Bios.MemCard.IsSlotNewCard(1));

        byte[] readBack1 = new byte[128], readBack2 = new byte[128];
        Assert.True(dst.Bios.MemCard.ReadSector(0, 7, readBack1));
        Assert.True(dst.Bios.MemCard.ReadSector(1, 8, readBack2));
        Assert.Equal(s1, readBack1);
        for (int i = 0; i < 128; i++) Assert.Equal((byte)(0x30 + i), readBack2[i]);

        // Legacy RSTX without "MCD2" and bare RST1 still load cleanly.
        string legacyRstx = Path.Combine(_tempRoot, "legacy_nomcd2.rst");
        var legacyChunks = new Dictionary<string, byte[]> { [QuickSaveTrailer.ChunkBios] = src.Bios.CaptureSnapshot() };
        QuickSaveManager.SaveToFileWithTrailer(src, legacyRstx, "mcd2-test", legacyChunks);
        using PsxCpu dstLegacy = CreateCpu("qs-legacy");
        QuickSaveLoadResult legacyRes = QuickSaveManager.LoadFromFile(dstLegacy, legacyRstx);
        Assert.True(legacyRes.Success);
        Assert.False(legacyRes.RstxChunks!.ContainsKey(QuickSaveTrailer.ChunkMemCard));
    }

    private sealed class EventCounterModule(uint kernelCbPc, uint intRpCheckPc, uint intRpFunc2Pc, uint nonVblankCheckPc, uint nonVblankFunc2Pc) : IRecompModule
    {
        public int ExeId => 0;
        public string ExeName => "VBLANK.EXE";
        public uint TextStart => 0x80050000U;
        public uint TextEnd => 0x80051000U;

        public bool TryExecuteFunction(IReStationCpu cpu, uint pc)
        {
            if (pc == kernelCbPc) { cpu.Write32(0x80040000U, cpu.Read32(0x80040000U) + 1U); return true; }
            if (pc == intRpCheckPc) { cpu.R[2] = (cpu.Read32(0x1F801070U) & (1U << PsxIrq.VBlank)) != 0 ? 1U : 0U; return true; }
            if (pc == intRpFunc2Pc) { cpu.Write32(0x80040004U, cpu.Read32(0x80040004U) + 1U); return true; }
            if (pc == nonVblankCheckPc) { cpu.R[2] = (cpu.Read32(0x1F801070U) & (1U << PsxIrq.Controller)) != 0 ? 1U : 0U; return true; }
            if (pc == nonVblankFunc2Pc) { cpu.Write32(0x80040008U, cpu.Read32(0x80040008U) + 1U); return true; }
            return false;
        }
    }

    [Fact]
    public void BiosEvents_SingleVBlank_DeduplicatesKernelEventAndIntRpDoubleAdvance()
    {
        using PsxCpu cpu = CreateCpu();
        const uint kernelCb = 0x80050000U, intRpCheck = 0x80050080U, intRpCb = 0x80050100U, padCheck = 0x80050180U, padIrqCb = 0x80050200U;
        cpu.RegisterModule(new EventCounterModule(kernelCb, intRpCheck, intRpCb, padCheck, padIrqCb));

        // Open and enable VBlank KernelEvent (0xF0000001, spec 0x0002, callback mode 0x1000).
        cpu.R[4] = 0xF0000001U; cpu.R[5] = 0x0002U; cpu.R[6] = 0x1000U; cpu.R[7] = kernelCb;
        cpu.R[9] = 0x08; cpu.Bios.HandleVectorCall(0xB0U);
        cpu.R[4] = cpu.R[2]; cpu.R[9] = 0x0C; cpu.Bios.HandleVectorCall(0xB0U);

        // Enqueue IntRP VBlank node checking I_STAT bit 0.
        const uint nodeVblank = 0x80022000U;
        cpu.Bus.Write32(nodeVblank + 4U, intRpCb);
        cpu.Bus.Write32(nodeVblank + 8U, intRpCheck);
        cpu.R[4] = 0U; cpu.R[5] = nodeVblank; cpu.R[9] = 0x02; cpu.Bios.HandleVectorCall(0xC0U);

        // Repeated calls within the same VBlankSequence must execute at most once.
        cpu.TimersAndDma.InterruptMask = (1U << PsxIrq.VBlank) | (1U << PsxIrq.Controller);
        cpu.Bios.OnVBlank();
        cpu.Bios.OnVBlank();
        cpu.TimersAndDma.InterruptStatus = 1U << PsxIrq.VBlank;
        cpu.Bios.TryRunInterruptHook();
        cpu.TimersAndDma.InterruptStatus = 1U << PsxIrq.VBlank;
        cpu.Bios.TryRunInterruptHook();
        Assert.Equal(1U, cpu.Bus.Read32(0x80040000U));
        Assert.Equal(1U, cpu.Bus.Read32(0x80040004U));

        // Non-VBlank Controller IRQ7 still dispatches during the same VBlank without re-triggering VBlank IntRP.
        const uint nodeCtrl = 0x80022020U;
        cpu.Bus.Write32(nodeCtrl + 4U, padIrqCb);
        cpu.Bus.Write32(nodeCtrl + 8U, padCheck);
        cpu.R[4] = 1U; cpu.R[5] = nodeCtrl; cpu.R[9] = 0x02; cpu.Bios.HandleVectorCall(0xC0U);
        cpu.TimersAndDma.InterruptStatus = (1U << PsxIrq.VBlank) | (1U << PsxIrq.Controller);
        cpu.Bios.TryRunInterruptHook();
        Assert.Equal(1U, cpu.Bus.Read32(0x80040004U));
        Assert.Equal(1U, cpu.Bus.Read32(0x80040008U));

        // Across 3 real VBlanks via PollEvents, both KernelEvent and VBlank IntRP advance exactly once per VBlank (1 -> 4).
        for (int f = 0; f < 3; f++)
        {
            cpu.TotalCycles += PsxCpu.CyclesPerFrame;
            cpu.PollEvents();
        }
        Assert.Equal(4U, cpu.Bus.Read32(0x80040000U));
        Assert.Equal(4U, cpu.Bus.Read32(0x80040004U));
        Assert.Equal(3L, cpu.VBlankSequence);
    }
}
