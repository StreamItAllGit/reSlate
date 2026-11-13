// @module: reStation.Compiler.DiscExeLoader
// @purpose: Loads PS-X EXE binaries from --exe-path, --gamedata-dir, or --disc (.iso/.bin+.cue/.chd/.7z/.zip) and parses PS-X EXE headers + SYSTEM.CNF.
// @depends-on: System.Buffers.Binary, System.Text, Foundation.Retro
// @exports: PsxExeHeader, DiscoveredExe, DiscExeLoader
// @owner: devv-compiler (T5)

using System.Buffers.Binary;
using System.Text;
using Foundation.Retro;

namespace reStation.Compiler;

/// <summary>Parsed 2 KB PlayStation 1 PS-X EXE header.</summary>
public readonly record struct PsxExeHeader(
    uint Pc0,
    uint Gp0,
    uint TextAddr,
    uint TextSize,
    uint StackAddr,
    uint StackSize)
{
    public uint TextEnd => TextAddr + TextSize;
}

/// <summary>One PS-X executable discovered for lifting.</summary>
public sealed record DiscoveredExe(
    int ExeId,
    string ExeName,
    string RelativePath,
    string FolderName,
    string ClassName,
    byte[] Bytes,
    PsxExeHeader Header);

/// <summary>Discovers and validates PS-X executables from single EXE files, gamedata folders, or optical disc containers.</summary>
public static class DiscExeLoader
{
    private const int PsxHeaderSize = 0x800;
    private const uint MaxCandidateExeBytes = 16U * 1024U * 1024U;
    private static readonly byte[] PsxMagic = "PS-X EXE"u8.ToArray();
    private static readonly byte[] SyncPattern = { 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00 };

    /// <summary>Parses and validates a PS-X EXE header; returns false when magic or text bounds are invalid.</summary>
    public static bool TryParseHeader(ReadOnlySpan<byte> exeBytes, out PsxExeHeader header)
    {
        header = default;
        if (exeBytes.Length < PsxHeaderSize || !exeBytes[..8].SequenceEqual(PsxMagic)) return false;

        uint pc0 = CanonicalRam(BinaryPrimitives.ReadUInt32LittleEndian(exeBytes.Slice(0x10, 4)));
        uint gp0 = BinaryPrimitives.ReadUInt32LittleEndian(exeBytes.Slice(0x14, 4));
        uint tAddr = CanonicalRam(BinaryPrimitives.ReadUInt32LittleEndian(exeBytes.Slice(0x18, 4)));
        uint tSize = BinaryPrimitives.ReadUInt32LittleEndian(exeBytes.Slice(0x1C, 4));
        uint sAddr = BinaryPrimitives.ReadUInt32LittleEndian(exeBytes.Slice(0x30, 4));
        uint sSize = BinaryPrimitives.ReadUInt32LittleEndian(exeBytes.Slice(0x34, 4));

        uint avail = (uint)Math.Max(0, exeBytes.Length - PsxHeaderSize);
        uint effectiveSize = tSize == 0 ? avail : Math.Min(tSize, avail);
        if (effectiveSize < 4U) return false;
        effectiveSize &= ~3U;

        header = new PsxExeHeader(pc0, gp0, tAddr, effectiveSize, sAddr, sSize);
        return true;
    }

    /// <summary>Loads all PS-X executables matching the compiler options.</summary>
    internal static List<DiscoveredExe> Discover(RecompCompilerOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ExePath))
        {
            string exePath = ResolveExePath(options.ExePath, options.GameDataDir);
            byte[] bytes = File.ReadAllBytes(exePath);
            if (!TryParseHeader(bytes, out PsxExeHeader hdr))
                throw new InvalidDataException($"'{exePath}' is not a valid PS-X EXE.");
            string rel = options.GameDataDir is not null && Directory.Exists(options.GameDataDir)
                ? Path.GetRelativePath(Path.GetFullPath(options.GameDataDir), exePath).Replace('\\', '/')
                : Path.GetFileName(exePath);
            string stem = ToPascalIdentifier(Path.GetFileNameWithoutExtension(exePath));
            return new List<DiscoveredExe> { new(0, Path.GetFileName(exePath), rel, $"{stem}Exe", $"{stem}Module", bytes, hdr) };
        }

        if (!string.IsNullOrWhiteSpace(options.GameDataDir))
            return DiscoverFromDirectory(Path.GetFullPath(options.GameDataDir), options.SerialNumber);

        if (!string.IsNullOrWhiteSpace(options.DiscPath))
            return DiscoverFromDisc(Path.GetFullPath(options.DiscPath), options.SerialNumber);

        throw new InvalidOperationException("No input media (--disc, --gamedata-dir, or --exe-path) specified.");
    }

    internal static string ToPascalIdentifier(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        bool capNext = true;
        foreach (char c in raw)
        {
            if (!char.IsLetterOrDigit(c)) { capNext = true; continue; }
            if (sb.Length == 0 && char.IsDigit(c)) sb.Append('M');
            sb.Append(capNext ? char.ToUpperInvariant(c) : c);
            capNext = false;
        }
        return sb.Length > 0 ? sb.ToString() : "ModuleExe";
    }

    private static string ResolveExePath(string exePath, string? gameDataDir)
    {
        if (File.Exists(exePath)) return Path.GetFullPath(exePath);
        if (!string.IsNullOrWhiteSpace(gameDataDir))
        {
            string combined = Path.GetFullPath(Path.Combine(gameDataDir, exePath));
            if (File.Exists(combined)) return combined;
        }
        throw new FileNotFoundException($"PS-X executable not found: '{exePath}'.", exePath);
    }

    private static List<DiscoveredExe> DiscoverFromDirectory(string rootDir, string serial)
    {
        string? bootRel = null;
        string sysCnf = Directory.EnumerateFiles(rootDir, "SYSTEM.CNF", SearchOption.AllDirectories).FirstOrDefault() ?? string.Empty;
        if (File.Exists(sysCnf)) bootRel = ParseSystemCnfBoot(File.ReadAllText(sysCnf, Encoding.ASCII));

        var raw = new List<(string Rel, byte[] Bytes, PsxExeHeader Header)>();
        foreach (string file in Directory.EnumerateFiles(rootDir, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            long len = new FileInfo(file).Length;
            if (len < PsxHeaderSize + 4 || len > MaxCandidateExeBytes) continue;
            if (!HasPsxMagic(file)) continue;
            byte[] bytes = File.ReadAllBytes(file);
            if (TryParseHeader(bytes, out PsxExeHeader hdr))
                raw.Add((Path.GetRelativePath(rootDir, file).Replace('\\', '/'), bytes, hdr));
        }
        return OrderAndBuild(raw, bootRel, serial, rootDir);
    }

    private static List<DiscoveredExe> DiscoverFromDisc(string discPath, string serial)
    {
        string ext = Path.GetExtension(discPath);
        if (ext.Equals(".iso", StringComparison.OrdinalIgnoreCase) || ext.Equals(".bin", StringComparison.OrdinalIgnoreCase))
            return ReadIsoExecutables(discPath, serial);

        string tempDir = Path.Combine(Path.GetTempPath(), $"restation-comp-{Guid.NewGuid():N}");
        try
        {
            var opts = new MediaExtractionOptions(ToolsBaseDirectory: AppContext.BaseDirectory, CopyDirectMediaToDestination: false, ComputeSha256: false);
            MediaExtractionResult res = MediaContainerExtractor.ExtractOrVerifyAsync(discPath, tempDir, opts).GetAwaiter().GetResult();
            if (!res.Success || string.IsNullOrWhiteSpace(res.PrimaryMediaPath))
                throw new InvalidDataException($"Failed to inspect disc '{discPath}': {res.DiagnosticMessage}");
            return ReadIsoExecutables(res.PrimaryMediaPath, serial);
        }
        finally
        {
            if (Directory.Exists(tempDir)) try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static List<DiscoveredExe> ReadIsoExecutables(string imagePath, string serial)
    {
        using var fs = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> head = stackalloc byte[32];
        fs.ReadExactly(head);
        bool isRaw = head[..12].SequenceEqual(SyncPattern);
        int stride = isRaw ? 2352 : 2048;
        int userOff = isRaw ? (head[15] == 2 ? 24 : 16) : 0;

        byte[] pvd = new byte[2048];
        ReadSector(fs, stride, userOff, 16, pvd);
        if (pvd[0] != 1 || Encoding.ASCII.GetString(pvd, 1, 5) != "CD001")
            throw new InvalidDataException($"'{Path.GetFileName(imagePath)}' is not a valid ISO-9660 image.");

        uint rootLba = BinaryPrimitives.ReadUInt32LittleEndian(pvd.AsSpan(158, 4));
        uint rootLen = BinaryPrimitives.ReadUInt32LittleEndian(pvd.AsSpan(166, 4));
        var files = new List<(string Rel, uint Lba, uint Len)>();
        WalkIsoDir(fs, stride, userOff, string.Empty, rootLba, rootLen, files);

        string? bootRel = null;
        var cnf = files.FirstOrDefault(f => string.Equals(f.Rel, "SYSTEM.CNF", StringComparison.OrdinalIgnoreCase));
        if (cnf.Len is > 0 and <= 4096)
            bootRel = ParseSystemCnfBoot(Encoding.ASCII.GetString(ReadIsoBytes(fs, stride, userOff, cnf.Lba, cnf.Len)));

        var raw = new List<(string Rel, byte[] Bytes, PsxExeHeader Header)>();
        foreach (var f in files.OrderBy(x => x.Rel, StringComparer.OrdinalIgnoreCase))
        {
            if (f.Len < PsxHeaderSize + 4 || f.Len > MaxCandidateExeBytes) continue;
            byte[] peek = ReadIsoBytes(fs, stride, userOff, f.Lba, 8);
            if (!peek.AsSpan().SequenceEqual(PsxMagic)) continue;
            byte[] bytes = ReadIsoBytes(fs, stride, userOff, f.Lba, f.Len);
            if (TryParseHeader(bytes, out PsxExeHeader hdr)) raw.Add((f.Rel, bytes, hdr));
        }
        return OrderAndBuild(raw, bootRel, serial, imagePath);
    }

    private static List<DiscoveredExe> OrderAndBuild(List<(string Rel, byte[] Bytes, PsxExeHeader Header)> raw, string? bootRel, string serial, string source)
    {
        if (raw.Count == 0) throw new FileNotFoundException($"No PS-X EXE executables found in '{source}'.");

        int bootIdx = raw.FindIndex(x => bootRel is not null && string.Equals(x.Rel, bootRel, StringComparison.OrdinalIgnoreCase));
        if (bootIdx < 0 && !string.IsNullOrWhiteSpace(serial))
        {
            string normSerial = serial.Replace("-", "_").Replace(".", string.Empty);
            bootIdx = raw.FindIndex(x => x.Rel.Replace(".", string.Empty).Contains(normSerial, StringComparison.OrdinalIgnoreCase));
        }
        if (bootIdx > 0)
        {
            var first = raw[bootIdx];
            raw.RemoveAt(bootIdx);
            raw.Insert(0, first);
        }

        var usedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<DiscoveredExe>(Math.Min(12, raw.Count));
        for (int i = 0; i < raw.Count && i < 12; i++)
        {
            string name = Path.GetFileName(raw[i].Rel);
            string baseStem = ToPascalIdentifier(Path.GetFileNameWithoutExtension(name));
            string folder = $"{baseStem}Exe";
            if (!usedFolders.Add(folder)) folder = $"{baseStem}_{i}Exe";
            string cls = folder[..^3] + "Module";
            result.Add(new DiscoveredExe(i, name, raw[i].Rel, folder, cls, raw[i].Bytes, raw[i].Header));
        }
        return result;
    }

    private static void WalkIsoDir(FileStream fs, int stride, int userOff, string prefix, uint lba, uint len, List<(string Rel, uint Lba, uint Len)> files)
    {
        byte[] data = ReadIsoBytes(fs, stride, userOff, lba, Math.Max(2048U, ((len + 2047U) / 2048U) * 2048U));
        int pos = 0;
        while (pos < (int)len)
        {
            byte recLen = data[pos];
            if (recLen == 0)
            {
                int next = ((pos / 2048) + 1) * 2048;
                if (next >= (int)len) break;
                pos = next;
                continue;
            }
            uint childLba = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 2, 4));
            uint childLen = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 10, 4));
            byte flags = data[pos + 25];
            byte nameLen = data[pos + 32];
            string rawName = Encoding.ASCII.GetString(data, pos + 33, nameLen);
            pos += recLen;
            if (nameLen == 1 && (rawName[0] == '\0' || rawName[0] == '\x01')) continue;
            int semi = rawName.IndexOf(';');
            string clean = semi >= 0 ? rawName[..semi] : rawName;
            string full = string.IsNullOrEmpty(prefix) ? clean : $"{prefix}/{clean}";
            if ((flags & 0x02) != 0) WalkIsoDir(fs, stride, userOff, full, childLba, childLen, files);
            else files.Add((full, childLba, childLen));
        }
    }

    private static byte[] ReadIsoBytes(FileStream fs, int stride, int userOff, uint lba, uint len)
    {
        byte[] buf = new byte[len];
        Span<byte> sec = stackalloc byte[2048];
        uint off = 0, cur = lba;
        while (off < len)
        {
            ReadSector(fs, stride, userOff, cur++, sec);
            int take = (int)Math.Min(2048U, len - off);
            sec[..take].CopyTo(buf.AsSpan((int)off, take));
            off += (uint)take;
        }
        return buf;
    }

    private static void ReadSector(FileStream fs, int stride, int userOff, uint lba, Span<byte> dest)
    {
        fs.Seek((long)lba * stride + userOff, SeekOrigin.Begin);
        fs.ReadExactly(dest);
    }

    private static string? ParseSystemCnfBoot(string cnf)
    {
        foreach (string line in cnf.Split('\n'))
        {
            int idx = line.IndexOf("BOOT", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            int eq = line.IndexOf('=', idx);
            if (eq < 0) continue;
            string rhs = line[(eq + 1)..].Trim().TrimEnd(';');
            int semi = rhs.IndexOf(';');
            if (semi >= 0) rhs = rhs[..semi];
            int colon = rhs.IndexOf(':');
            if (colon >= 0) rhs = rhs[(colon + 1)..];
            return rhs.Replace('\\', '/').TrimStart('/');
        }
        return null;
    }

    private static bool HasPsxMagic(string path)
    {
        Span<byte> head = stackalloc byte[8];
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return fs.Read(head) == 8 && head.SequenceEqual(PsxMagic);
    }

    private static uint CanonicalRam(uint addr) => 0x80000000U | (addr & 0x1FFFFFFFU);
}
