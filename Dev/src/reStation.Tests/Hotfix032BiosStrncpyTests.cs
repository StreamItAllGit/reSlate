// @module: reStation.Tests.Hotfix032BiosStrncpyTests
// @purpose: Verifies v0.3.2 BIOS A0:0x1A (strncpy) ANSI C zero-padding/truncation semantics and end-to-end PS1 memory card create/open/write persistence for 4-block saves (SLUS-01115 diary bug).
// @depends-on: Xunit, reStation.Contracts, reStation.Core, reStation.Core.Bios, reStation.Core.Cpu, reStation.Core.Gpu
// @exports: Hotfix032BiosStrncpyTests

using System.Buffers.Binary;
using System.Text;
using reStation.Contracts;
using reStation.Core;
using reStation.Core.Bios;
using reStation.Core.Cpu;
using reStation.Core.Gpu;
using Xunit;

namespace reStation.Tests;

public sealed class Hotfix032BiosStrncpyTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "reslate-v032-strncpy-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private PsxCpu CreateCpu()
    {
        Directory.CreateDirectory(_tempRoot);
        return new PsxCpu(new RecompSettings(), _tempRoot, new SoftwareCpuRenderBackend());
    }

    private static void WriteAsciiCString(PsxCpu cpu, uint addr, string text)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(text);
        for (int i = 0; i < bytes.Length; i++)
        {
            cpu.Bus.Write8(addr + (uint)i, bytes[i]);
        }

        cpu.Bus.Write8(addr + (uint)bytes.Length, 0);
    }

    [Fact]
    public void BiosA0_0x1A_Strncpy_ZeroPadsShortStringAndRespectsBounds()
    {
        using PsxCpu cpu = CreateCpu();
        const uint src = 0x80021000U;
        const uint dst = 0x80022000U;
        WriteAsciiCString(cpu, src, "BASLUS-01115-FARM0"); // 18 chars + NUL

        for (uint i = 0; i < 20; i++)
        {
            cpu.Bus.Write8(dst + i, 0xAA);
        }
        cpu.Bus.Write8(dst + 20, 0xBB); // guard sentinel right past n=20

        var notified = new List<(uint addr, uint len)>();
        cpu.Bus.OnRamWriteRange += (a, l) => notified.Add((a, l));

        cpu.R[4] = dst;
        cpu.R[5] = src;
        cpu.R[6] = 20;
        cpu.R[9] = 0x1A;
        cpu.Bios.HandleVectorCall(0xA0U);

        Assert.Equal(dst, cpu.R[2]);
        byte[] expectedPrefix = Encoding.ASCII.GetBytes("BASLUS-01115-FARM0");
        for (int i = 0; i < expectedPrefix.Length; i++)
        {
            Assert.Equal(expectedPrefix[i], cpu.Bus.Read8(dst + (uint)i));
        }
        Assert.Equal((byte)0, cpu.Bus.Read8(dst + 18));
        Assert.Equal((byte)0, cpu.Bus.Read8(dst + 19));
        Assert.Equal((byte)0xBB, cpu.Bus.Read8(dst + 20));
        Assert.Contains((dst & 0x001FFFFFU, 20U), notified);
    }

    [Fact]
    public void BiosA0_0x1A_Strncpy_TruncatesLongStringWithoutImplicitNulAndIgnoresNonPositiveLen()
    {
        using PsxCpu cpu = CreateCpu();
        const uint src = 0x80023000U;
        const uint dst = 0x80024000U;
        WriteAsciiCString(cpu, src, "ABCDEFGHIJKLMNOP");

        for (uint i = 0; i < 12; i++)
        {
            cpu.Bus.Write8(dst + i, 0xEE);
        }

        cpu.R[4] = dst;
        cpu.R[5] = src;
        cpu.R[6] = 8;
        cpu.R[9] = 0x1A;
        cpu.Bios.HandleVectorCall(0xA0U);

        Assert.Equal(dst, cpu.R[2]);
        byte[] expected = Encoding.ASCII.GetBytes("ABCDEFGH");
        for (int i = 0; i < 8; i++)
        {
            Assert.Equal(expected[i], cpu.Bus.Read8(dst + (uint)i));
        }
        Assert.Equal((byte)0xEE, cpu.Bus.Read8(dst + 8));

        // Zero and negative lengths leave destination intact and return dst.
        cpu.Bus.Write8(dst, 0x77);
        cpu.R[4] = dst;
        cpu.R[5] = src;
        cpu.R[6] = 0;
        cpu.R[9] = 0x1A;
        cpu.Bios.HandleVectorCall(0xA0U);
        Assert.Equal(dst, cpu.R[2]);
        Assert.Equal((byte)0x77, cpu.Bus.Read8(dst));

        cpu.R[4] = dst;
        cpu.R[5] = src;
        cpu.R[6] = unchecked((uint)-5);
        cpu.R[9] = 0x1A;
        cpu.Bios.HandleVectorCall(0xA0U);
        Assert.Equal(dst, cpu.R[2]);
        Assert.Equal((byte)0x77, cpu.Bus.Read8(dst));
    }

    [Fact]
    public void MemCardCreateViaBiosStrncpy_AllowsOpenWriteAndPersistsFourBlockSave()
    {
        using PsxCpu cpu = CreateCpu();
        cpu.Bios.MemCard.Format(0);

        const uint nameSrc = 0x80025000U;
        const string saveName = "BASLUS-01115-FARM0";
        WriteAsciiCString(cpu, nameSrc, saveName);

        // Emulate libmcrd _card_create2 building a 4-block directory chain (blocks 1..4) using A0:0x1A for the filename.
        const uint dirBuf = 0x80030000U;
        for (int blk = 1; blk <= 4; blk++)
        {
            uint frame = dirBuf;
            for (uint i = 0; i < MemCardMcrStore.SectorSize; i++)
            {
                cpu.Bus.Write8(frame + i, 0);
            }

            uint alloc = blk switch { 1 => 0x51U, 4 => 0x53U, _ => 0x52U };
            uint nextBlk = blk < 4 ? (uint)blk : 0xFFFFU;
            cpu.Bus.Write32(frame + 0x00U, alloc);
            cpu.Bus.Write32(frame + 0x04U, blk == 1 ? 0x8000U : 0U);
            cpu.Bus.Write16(frame + 0x08U, (ushort)nextBlk);

            if (blk == 1)
            {
                cpu.R[4] = frame + 0x0AU;
                cpu.R[5] = nameSrc;
                cpu.R[6] = 20;
                cpu.R[9] = 0x1A;
                cpu.Bios.HandleVectorCall(0xA0U);
                Assert.Equal(frame + 0x0AU, cpu.R[2]);
            }

            byte xor = 0;
            for (uint i = 0; i < 127; i++)
            {
                xor ^= cpu.Bus.Read8(frame + i);
            }
            cpu.Bus.Write8(frame + 127U, xor);

            cpu.R[4] = 0x00U;
            cpu.R[5] = (uint)blk;
            cpu.R[6] = frame;
            cpu.R[9] = 0x4E; // _card_write
            cpu.Bios.HandleVectorCall(0xB0U);
        }

        List<(string Name, int SizeBytes, int FirstBlock)> listed = cpu.Bios.MemCard.ListFiles("bu00:*");
        (string Name, int SizeBytes, int FirstBlock) created = Assert.Single(listed);
        Assert.Equal(saveName, created.Name);
        Assert.Equal(0x8000, created.SizeBytes);
        Assert.Equal(1, created.FirstBlock);

        // Open existing created file in write mode (0x8001) as MemCardWriteFile / LIBMCRD_OBJ_1170 does.
        const uint pathAddr = 0x80026000U;
        WriteAsciiCString(cpu, pathAddr, "bu00:" + saveName);
        cpu.R[4] = pathAddr;
        cpu.R[5] = 0x8001U;
        cpu.R[9] = 0x32; // open
        cpu.Bios.HandleVectorCall(0xB0U);
        uint fd = cpu.R[2];
        Assert.NotEqual(0xFFFFFFFFU, fd);

        // Write 32 KB (0x8000) save payload and close.
        const uint payloadAddr = 0x80040000U;
        byte[] expectedPayload = new byte[0x8000];
        for (int i = 0; i < expectedPayload.Length; i++)
        {
            byte v = (byte)((i * 37 + 13) & 0xFF);
            expectedPayload[i] = v;
            cpu.Bus.Write8(payloadAddr + (uint)i, v);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(expectedPayload, 0x4E544248U);
        cpu.Bus.Write32(payloadAddr, 0x4E544248U);

        cpu.R[4] = fd;
        cpu.R[5] = payloadAddr;
        cpu.R[6] = 0x8000U;
        cpu.R[9] = 0x35; // write (async flags 0x8001 -> delivers EvSpIOE and returns 0)
        cpu.Bios.HandleVectorCall(0xB0U);
        Assert.Equal(0U, cpu.R[2]);

        cpu.R[4] = fd;
        cpu.R[9] = 0x36; // close
        cpu.Bios.HandleVectorCall(0xB0U);
        Assert.Equal(fd, cpu.R[2]);
        cpu.Bios.MemCard.Flush();

        byte[]? readBack = cpu.Bios.MemCard.ReadFile("bu00:" + saveName);
        Assert.NotNull(readBack);
        Assert.Equal(expectedPayload, readBack);

        // Verify disk .mcr persistence on reload.
        var reloaded = new MemCardMcrStore(_tempRoot);
        byte[]? diskBytes = reloaded.ReadFile("bu00:" + saveName);
        Assert.NotNull(diskBytes);
        Assert.Equal(expectedPayload, diskBytes);
    }
}
