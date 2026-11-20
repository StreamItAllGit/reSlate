// @module: reStation.Tests.Wave2CoreSeamTests
// @purpose: Wave 2 Slice 1 seam verification: unlimited slots, 0x8019F07C SHA-256 overlays, sub-tick MMIO guard, polygon skip, ExecOverlay reset, Mode2 0x12, RSTX, Std 15 logs.
// @depends-on: Xunit, Foundation.Retro, reStation.Contracts, reStation.Core, reStation.Core.CdRom, reStation.Core.Cpu, reStation.Core.DiscIngestion, reStation.Core.Gpu, reStation.Core.Saves, reStation.Player
// @exports: Wave2CoreSeamTests
// @owner: devv-core

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Foundation.Core;
using Foundation.Retro;
using reStation.Contracts;
using reStation.Core;
using reStation.Core.CdRom;
using reStation.Core.Cpu;
using reStation.Core.DiscIngestion;
using reStation.Core.Gpu;
using reStation.Core.Saves;
using reStation.Player;
using Xunit;

namespace reStation.Tests;

public sealed class Wave2CoreSeamTests
{
    private sealed class StubModule(int id, string exe, uint s, uint e, Action<IReStationCpu> fn) : IRecompModule
    {
        public string ModuleId => "TEST"; public string ExeName => exe; public int ExeId => id;
        public uint TextStart => s; public uint TextEnd => e;
        public bool TryExecuteFunction(IReStationCpu cpu, uint pc) { fn(cpu); return true; }
    }

    private sealed class StubOverlay(int id, string exe, uint s, uint e, uint ram, int sz, string sha, Action<IReStationCpu> fn) : IOverlayRecompModule
    {
        public string ModuleId => "TEST"; public string ExeName => exe; public int ExeId => id;
        public uint TextStart => s; public uint TextEnd => e;
        public uint LoadRamAddress => ram; public int ContentSizeBytes => sz; public string ContentSha256Hex => sha;
        public bool TryExecuteFunction(IReStationCpu cpu, uint pc) { fn(cpu); return true; }
    }

    private sealed class CountingPatch : IGameFramePatch
    {
        public int ResetCount { get; private set; }
        public void ApplyPostFrame(IReStationCpu cpu) { }
        public bool TryInterceptCall(IReStationCpu cpu, uint pc) => false;
        public void Reset() => ResetCount++;
    }

    private static PsxCpu CreateCpu(string? dir = null, ReStationLogger? logger = null) =>
        new(new RecompSettings(), dir ?? Path.GetTempPath(), new SoftwareCpuRenderBackend(), logger);

    [Fact]
    public void UnlimitedSlots_Registers16Modules_DispatchesSlot15()
    {
        using PsxCpu cpu = CreateCpu();
        int ran = -1;
        for (int i = 0; i < 16; i++)
        {
            int id = i; uint b = 0x80010000U + (uint)(i * 0x1000);
            cpu.RegisterModule(new StubModule(id, $"MOD{id}.EXE", b, b + 0x400U, _ => ran = id));
        }
        cpu.Call(0x8001F010U);
        Assert.Equal(15, ran);
        Assert.Equal(1L, cpu.CompiledDispatchCount);
    }

    [Fact]
    public void SharedOverlaySlot_RamSha256AndBulkNotify_SwitchesAndCaches()
    {
        using PsxCpu cpu = CreateCpu();
        const uint ovAddr = 0x8019F07CU;
        byte[] imgA = Enumerable.Range(0, 64).Select(i => (byte)(i + 1)).ToArray();
        byte[] imgB = Enumerable.Range(0, 64).Select(i => (byte)(200 - i)).ToArray();
        string shaA = Convert.ToHexString(SHA256.HashData(imgA)).ToLowerInvariant();
        string shaB = Convert.ToHexString(SHA256.HashData(imgB)).ToLowerInvariant();
        string picked = string.Empty;
        cpu.RegisterModule(new StubOverlay(1, "OV_A.EXE", ovAddr, ovAddr + 0x100, ovAddr, 64, shaA, _ => picked = "A"));
        cpu.RegisterModule(new StubOverlay(1, "OV_B.EXE", ovAddr, ovAddr + 0x100, ovAddr, 64, shaB, _ => picked = "B"));

        Assert.True(cpu.Bus.TryGetRamSpan(ovAddr, 64, out Span<byte> spanA));
        imgA.CopyTo(spanA); cpu.Bus.NotifyRamWriteRange(ovAddr, 64); cpu.Call(ovAddr);
        Assert.Equal("A", picked);
        long firstHashes = cpu.OverlayHashComputations;

        cpu.Bus.Write32(0x80010000U, 0x12345678U); // unrelated RAM write must not bust overlay cache
        cpu.Call(ovAddr);
        Assert.Equal("A", picked); Assert.Equal(firstHashes, cpu.OverlayHashComputations);

        Assert.True(cpu.Bus.TryGetRamSpan(ovAddr, 64, out Span<byte> spanB));
        imgB.CopyTo(spanB); cpu.Bus.NotifyRamWriteRange(ovAddr, 64); cpu.Call(ovAddr);
        Assert.Equal("B", picked); Assert.True(cpu.OverlayHashComputations > firstHashes);

        cpu.R[4] = ovAddr; cpu.R[5] = 64; cpu.R[9] = 0x28; cpu.Bios.HandleVectorCall(0xA0); // ZeroRam invalidation
        Assert.False(cpu.HasCompiledEntry(ovAddr));
    }

    [Fact]
    public void SubTickRender_MmioGuardAndWatchdog_BlocksUnsafeWritesOnly()
    {
        using PsxCpu cpu = CreateCpu();
        cpu.TimersAndDma.WriteDmaReg(0x1F8010F0U, 0x07654321U);
        bool nestedRan = false;
        cpu.RegisterModule(new StubModule(0, "SUB.EXE", 0x80020000U, 0x80020200U, c =>
        {
            var pc = (PsxCpu)c;
            pc.Bus.Write8(0x1F801800U, 0x03); pc.Bus.Write8(0x1F801040U, 0xFF);
            pc.Bus.Write16(0x1F801C00U, 0xABCD); pc.Bus.Write16(0x1F801100U, 0x7777);
            pc.Bus.Write32(0x1FFE0130U, 0xDEADBEEFU); pc.Bus.Write32(0x1F801074U, 0x7FFU);
            pc.Bus.Write32(0x1F8010F0U, 0x00000800U); // DPCR: only ch2 bits [11:8] permitted
            pc.Bus.Write32(0x80010100U, 0xCAFEBABEU); pc.Bus.Write32(0x1F800020U, 0x11223344U);
            pc.Bus.Write32(0x1F801814U, 0x03000000U); // GP1 DisplayEnable
            pc.RunSlice(0x80020100U, inSubTickRender: true); // nested rejected
            pc.TotalCycles = 500; pc.PollEvents(); // trips SubTickDeadlineCycle = 400
        }));
        cpu.RegisterModule(new StubModule(1, "NEST.EXE", 0x80020100U, 0x80020180U, _ => nestedRan = true));

        cpu.TotalCycles = 100; cpu.SubTickDeadlineCycle = 400; cpu.RunSlice(0x80020000U, inSubTickRender: true);
        Assert.False(nestedRan); Assert.False(cpu.InSubTickRender); Assert.Equal(0L, cpu.SubTickDeadlineCycle);
        Assert.Equal(0x00000800U, cpu.Bus.CacheControl); Assert.Equal(0U, cpu.TimersAndDma.InterruptMask);
        uint t0 = cpu.TimersAndDma.ReadTimerReg(0x1F801100U);
        Assert.Equal(500U, t0);
        Assert.NotEqual(0x7777U, t0);
        Assert.Equal(0x07654821U, cpu.TimersAndDma.ReadDmaReg(0x1F8010F0U));
        Assert.Equal(0xCAFEBABEU, cpu.Bus.Read32(0x80010100U)); Assert.Equal(0x11223344U, cpu.Bus.Read32(0x1F800020U));
        Assert.False(cpu.Gpu.DisplayDisabled);
    }

    [Fact]
    public void SkipPolygonRasterization_SuppressesGp0AndPolylines_BeginSubTickResets()
    {
        using PsxCpu cpu = CreateCpu();
        cpu.SkipPolygonRasterization = true;
        uint[] poly = [0x20FF0000U, 0x00100010U, 0x00200010U, 0x00100020U];
        foreach (uint w in poly) cpu.Gpu.WriteGp0(w);
        foreach (uint w in new uint[] { 0x48FFFFFFU, 0x00050005U, 0x00150015U, 0x50005000U }) cpu.Gpu.WriteGp0(w);
        Assert.Equal(0L, cpu.Gpu.PrimitiveCount);

        cpu.Gpu.WriteGp0(0x20FF0000U); // partial GP0 packet cleared by BeginSubTickFrame
        cpu.BeginSubTickFrame(); cpu.SkipPolygonRasterization = false;
        foreach (uint w in poly) cpu.Gpu.WriteGp0(w);
        Assert.Equal(1L, cpu.Gpu.PrimitiveCount);
    }

    [Fact]
    public void ExecOverlay_ResetsFramePatchOnSwitchAndCleanReturn()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"reStation_ov_{Guid.NewGuid():N}");
        Directory.CreateDirectory(DiscManifest.GetGameDataDir(tmp));
        try { byte[] exe = new byte[0x1000]; Encoding.ASCII.GetBytes("PS-X EXE").CopyTo(exe, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(exe.AsSpan(0x10), 0x80030000U); BinaryPrimitives.WriteUInt32LittleEndian(exe.AsSpan(0x18), 0x80030000U); BinaryPrimitives.WriteUInt32LittleEndian(exe.AsSpan(0x1C), 0x800U);
            File.WriteAllBytes(Path.Combine(DiscManifest.GetGameDataDir(tmp), "OPEN.EXE"), exe);

            using PsxCpu cpu = CreateCpu(tmp);
            var patch = new CountingPatch(); cpu.FramePatch = patch;
            cpu.RegisterModule(new StubModule(0, "MAIN.EXE", 0x80010000U, 0x80010400U, _ => { }));
            cpu.RegisterModule(new StubModule(1, "OPEN.EXE", 0x80030000U, 0x80030800U, c => ((PsxCpu)c).ShouldUnwind = true));

            byte[] pathBytes = Encoding.ASCII.GetBytes("cdrom:\\OPEN.EXE;1\0");
            for (int i = 0; i < pathBytes.Length; i++) cpu.Bus.Write8(0x80012000U + (uint)i, pathBytes[i]);
            cpu.R[4] = 0x80012000U; cpu.R[5] = 0x80012100U; cpu.R[9] = 0x42; cpu.Bios.HandleVectorCall(0xA0);
            Assert.Equal(1U, cpu.R[2]);
            cpu.R[4] = 0x80012100U; cpu.R[5] = 0; cpu.R[6] = 0; cpu.R[9] = 0x43; cpu.Bios.HandleVectorCall(0xA0);
            Assert.Equal(2, patch.ResetCount); Assert.Equal(0, cpu.ActiveExeId);
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public void Mode2Subheader0x12_DetectedAsStreamingAcrossCdRomAndIsoReader()
    {
        Assert.True(PsxCdRomTiming.HasStreamingSubmode(0x12));
        Assert.True(PsxCdRomTiming.HasStreamingSubmode(0x40));
        Assert.False(PsxCdRomTiming.HasStreamingSubmode(0x08));
        Assert.False(PsxCdRomTiming.HasStreamingSubmode(0x48));

        byte[] raw = new byte[2352 * 20];
        Array.Copy(new byte[] { 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0, 2, 0, 2 }, raw, 16);
        int pvd = (16 * 2352) + 24; raw[pvd] = 1; Encoding.ASCII.GetBytes("CD001").CopyTo(raw, pvd + 1);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(pvd + 158), 18U);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(pvd + 166), 2048U);
        int root = (18 * 2352) + 24;
        int PutEntry(int off, uint lba, string name)
        {
            byte len = (byte)(33 + name.Length + ((name.Length & 1) == 0 ? 1 : 0));
            raw[off] = len; raw[off + 32] = (byte)name.Length;
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(off + 2), lba);
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(off + 10), 2048U);
            Encoding.ASCII.GetBytes(name).CopyTo(raw, off + 33);
            return off + len;
        }
        int nextOff = PutEntry(root, 19U, "STR012.BIN"); PutEntry(nextOff, 17U, "DAT008.BIN");
        raw[(19 * 2352) + 18] = 0x12; raw[(17 * 2352) + 18] = 0x08;
        string isoPath = Path.Combine(Path.GetTempPath(), $"reStation_m2_{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(isoPath, raw);
        using var reader = Iso9660RawReader.Open(isoPath);
        try { Assert.True(reader.FindEntry("STR012.BIN")!.IsForm2Stream); Assert.False(reader.FindEntry("DAT008.BIN")!.IsForm2Stream); }
        finally { reader.Dispose(); File.Delete(isoPath); }
    }

    [Fact]
    public void QuickSave_Rst1LegacyAndRstxTrailer_RoundtripsAndRejectsCorruptTail()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"reStation_qs_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            using var logger = new ReStationLogger(tmp, enabled: true);
            using PsxCpu src = CreateCpu(tmp, logger);
            src.Bus.Write32(0x80010040U, 0x13572468U);
            src.R[4] = 0xCAFEU; src.R[9] = 0x30; src.Bios.HandleVectorCall(0xA0);

            string rst1Path = Path.Combine(tmp, "legacy.rst");
            QuickSaveManager.SaveToFileWithTrailer(src, rst1Path, "SLUS-00158", rstxChunks: null, logger);
            using (PsxCpu d1 = CreateCpu(tmp, logger))
            {
                QuickSaveLoadResult r1 = QuickSaveManager.LoadFromFile(d1, rst1Path, logger);
                Assert.True(r1.Success); Assert.Equal("RST1", r1.Format); Assert.Equal(0x13572468U, d1.Bus.Read32(0x80010040U));
            }

            string rstxPath = Path.Combine(tmp, "ext.rst");
            var chunks = new Dictionary<string, byte[]> { ["BIOS"] = src.Bios.CaptureSnapshot(), ["OVLY"] = [9, 8, 7, 6] };
            QuickSaveManager.SaveToFileWithTrailer(src, rstxPath, "SLUS-00158", chunks, logger);
            using (PsxCpu d2 = CreateCpu(tmp, logger))
            {
                QuickSaveLoadResult r2 = QuickSaveManager.LoadFromFile(d2, rstxPath, logger);
                Assert.Equal("RST1+RSTX", r2.Format); Assert.Equal([9, 8, 7, 6], r2.RstxChunks!["OVLY"]);
                d2.R[9] = 0x2F; d2.Bios.HandleVectorCall(0xA0); uint nextD2 = d2.R[2];
                src.R[9] = 0x2F; src.Bios.HandleVectorCall(0xA0); Assert.Equal(src.R[2], nextD2);
            }

            string corrupt = Path.Combine(tmp, "corrupt.rst"); File.Copy(rst1Path, corrupt);
            using (var fs = new FileStream(corrupt, FileMode.Append, FileAccess.Write))
                fs.Write(new byte[] { 0xFF, 0xFF, 0x0F, 0x00, (byte)'R', (byte)'S', (byte)'T', (byte)'X' });
            using (PsxCpu d3 = CreateCpu(tmp))
            {
                Assert.Throws<InvalidDataException>(() => QuickSaveManager.LoadFromFile(d3, corrupt));
                File.Copy(corrupt, QuickSaveManager.GetSlotPath(tmp, "SLUS-00158", 0), overwrite: true);
                Assert.False(QuickSaveManager.TryLoad(d3, tmp, "SLUS-00158", 0));
            }
            Assert.Contains(logger.GetRecentLines(), l => l.Contains("[INFO] [SAVE]"));
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public void ReStationLogger_Standard15Format_SingleHeartbeatAndPositiveDenominator()
    {
        using var logger = new ReStationLogger(Path.GetTempPath(), enabled: false);
        Assert.True(logger.TryHeartbeat(PsxTelemetrySnapshot.Empty with { FrameNumber = 0, BackendId = "software" }, force: true));
        string line = Assert.Single(logger.GetRecentLines());
        Assert.Matches($@"^\d{{4}}-\d{{2}}-\d{{2}}T\d{{2}}:\d{{2}}:\d{{2}}\.\d{{3}}Z \[v{Regex.Escape(ReStationEngineInfo.Version)}\] \[PID:\d+/T:\d{{2,}}\] \[INFO\] \[HEARTBEAT\] ", line);
        Assert.Single(Regex.Matches(line, @"\[HEARTBEAT\]"));
        Assert.DoesNotContain("1/0", line);
        Assert.Contains("resolutionScale=", line); Assert.Contains("sectorsPerSec=", line);
    }

    private sealed class SeamSubTickHook : ISubTickFrameHook
    {
        public uint SubTickEntryPc => 0x80010040U;
        public uint MaxSubTickCycles => 4096U;
        public int MainInterceptCount { get; private set; }
        public int SubInterceptCount { get; private set; }
        public List<uint> AfterCallPcs { get; } = [];
        public bool ShouldRunSubTick(uint subFrameIndex, uint totalSubFrames) => true;
        public void ApplyPostFrame(IReStationCpu cpu) { }
        public void OnSubTickBegin(IReStationCpuEx cpu, uint subFrameIndex, uint totalSubFrames) { }
        public void OnSubTickEnd(IReStationCpuEx cpu, uint subFrameIndex, bool completedCleanly) { }
        public void Reset() { }
        public bool TryInterceptCall(IReStationCpu cpu, uint pc)
        {
            if (pc != 0x800100C0U) return false;
            MainInterceptCount++;
            return true;
        }
        public bool TryInterceptSubTickCall(IReStationCpuEx cpu, uint canonPc)
        {
            if (canonPc != 0x80010080U) return false;
            SubInterceptCount++;
            return true;
        }
        public void OnAfterCall(IReStationCpuEx cpu, uint canonPc) => AfterCallPcs.Add(canonPc);
    }

    private sealed class MinimalDefaultSubTickHook : ISubTickFrameHook
    {
        public uint SubTickEntryPc => 0;
        public uint MaxSubTickCycles => 0;
        public bool ShouldRunSubTick(uint subFrameIndex, uint totalSubFrames) => false;
        public void ApplyPostFrame(IReStationCpu cpu) { }
        public bool TryInterceptCall(IReStationCpu cpu, uint pc) => false;
        public void OnSubTickBegin(IReStationCpuEx cpu, uint subFrameIndex, uint totalSubFrames) { }
        public void OnSubTickEnd(IReStationCpuEx cpu, uint subFrameIndex, bool completedCleanly) { }
        public void Reset() { }
    }

    [Fact]
    public void SubTickHook_TryInterceptSubTickCall_And_OnAfterCall_EnforceModeAndFinallyCleanup()
    {
        using PsxCpu cpu = CreateCpu();
        ISubTickFrameHook defaults = new MinimalDefaultSubTickHook();
        Assert.False(defaults.TryInterceptSubTickCall(cpu, 0x80010040U));
        defaults.OnAfterCall(cpu, 0x80010040U);

        int calls = 0;
        cpu.RegisterModule(new StubModule(0, "M.EXE", 0x80010000U, 0x80011000U, c =>
        {
            calls++;
            if (c.Pc == 0x800100F0U) throw new InvalidOperationException("unwind");
        }));
        var hook = new SeamSubTickHook();
        cpu.FramePatch = hook;

        cpu.Call(0x80010040U); // normal main call -> module + OnAfterCall
        cpu.Call(0x800100C0U); // main intercepted -> neither module nor OnAfterCall
        Assert.Throws<InvalidOperationException>(() => cpu.Call(0x800100F0U)); // throws -> finally still calls OnAfterCall

        cpu.InSubTickRender = true;
        cpu.Call(0x80010080U); // sub-tick intercepted -> module skipped, OnAfterCall skipped
        cpu.Call(0x80010040U); // sub-tick non-intercepted -> module runs, OnAfterCall still skipped
        cpu.InSubTickRender = false;

        Assert.Equal(3, calls);
        Assert.Equal(1, hook.MainInterceptCount);
        Assert.Equal(1, hook.SubInterceptCount);
        Assert.Equal([0x80010040U, 0x800100F0U], hook.AfterCallPcs);
    }

    [Fact]
    public void PsxCpu_TryAdvanceToNextVBlank_RespectsMaskAndGuards_AndRaisesVBlank()
    {
        using PsxCpu cpu = CreateCpu();
        int vblanks = 0;
        cpu.OnVBlankFrame = _ => vblanks++;

        Assert.False(cpu.TryAdvanceToNextVBlank()); // I_MASK bit 0 masked initially
        cpu.Bus.Write32(0x1F801074U, 1U); // enable VBlank IRQ mask
        cpu.InSubTickRender = true;
        Assert.False(cpu.TryAdvanceToNextVBlank());
        cpu.InSubTickRender = false;
        cpu.ShouldUnwind = true;
        Assert.False(cpu.TryAdvanceToNextVBlank());
        cpu.ShouldUnwind = false;

        Assert.True(cpu.TryAdvanceToNextVBlank());
        Assert.Equal(1L, cpu.FrameCount);
        Assert.Equal(1, vblanks);
        Assert.True(cpu.TotalCycles >= PsxCpu.CyclesPerFrame);
    }

    [Fact]
    public void ReStationHost_IsEnhancedFrameRateEnabled_TracksSettingsAndRuntimeContext()
    {
        using var backend = new SoftwareCpuRenderBackend();
        using var logger = new ReStationLogger(Path.GetTempPath(), enabled: false);
        using var pacer = new HighResFramePacer(60);
        var manifest = new ReStationModuleManifest { ModuleId = "seam-test", Title = "Seam", DisplayName = "Seam" };

        using var origCpu = new PsxCpu(new RecompSettings { FrameRateMode = FrameRateMode.Original }, Path.GetTempPath(), backend, logger);
        using var origHost = new ReStationHost(origCpu, manifest, Path.GetTempPath(), new RecompHostOptions { TargetFps = 60 }, pacer, logger);
        Assert.False(((IReStationHostEx)origHost).IsEnhancedFrameRateEnabled);

        using var enhCpu = new PsxCpu(new RecompSettings { FrameRateMode = FrameRateMode.Enhanced }, Path.GetTempPath(), backend, logger);
        using var enhHost = new ReStationHost(enhCpu, manifest, Path.GetTempPath(), new RecompHostOptions { TargetFps = 120 }, pacer, logger);
        Assert.True(((IReStationHostEx)enhHost).IsEnhancedFrameRateEnabled);

        bool dynamicEnhanced = false;
        enhHost.AttachRuntimeContext(new HostRuntimeContext(IsOffscreen: true, IsEnhancedFrameRateEnabled: () => dynamicEnhanced));
        Assert.False(enhHost.IsEnhancedFrameRateEnabled);
        dynamicEnhanced = true;
        Assert.True(enhHost.IsEnhancedFrameRateEnabled);
    }
}
