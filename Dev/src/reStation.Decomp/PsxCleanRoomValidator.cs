// @module: reStation.Decomp.PsxCleanRoomValidator
// @purpose: Enforces zero retail media, save states, GPLv3 ghidra_psx_ldr, Sony SDK headers, or *.g.cs/*.Generated.dll leakage.
// @depends-on: System, System.Collections.Generic, System.IO, System.IO.Compression, System.Text, Foundation.Core, reEngine.Core.Manifests
// @exports: PsxCleanRoomValidator
// SPDX-License-Identifier: MPL-2.0

using System.IO.Compression;
using System.Text;
using Foundation.Core;
using reEngine.Core.Manifests;

namespace reStation.Decomp;

/// <summary>
/// Validates clean-room output directories and <c>.decomp</c>/<c>.recomp</c> archives against copyrighted media,
/// <c>GPLv3</c> loader leaks, Sony SDK type headers, and pre-built <c>*.g.cs</c>/<c>*.Generated.dll</c> artifacts.
/// </summary>
internal static class PsxCleanRoomValidator
{
    private static readonly byte[] PsxExeMagic = "PS-X EXE"u8.ToArray();
    private static readonly byte[] SaveMagic = "RST1"u8.ToArray();

    private static readonly HashSet<string> ForbiddenExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bin", ".iso", ".cue", ".chd", ".img", ".mdf", ".mds", ".pbp", ".ecm",
        ".rom", ".n64", ".z64", ".v64", ".nes", ".sfc", ".smc", ".gb", ".gbc", ".gba", ".gcm", ".rvz",
        ".sav", ".srm", ".state", ".mcr", ".mcd",
        ".tim", ".vab", ".seq", ".vag", ".xa", ".str",
        ".gdt", ".til", ".exe"
    };

    private static readonly string[] ForbiddenPathTokens =
    [
        "ghidra_psx_ldr", "libgpu.h", "libgte.h", "libgs.h", "libcd.h",
        "libspu.h", "libsn.h", "libsnd.h", "libetc.h", "libapi.h"
    ];

    public static DecompCleanRoomValidationResult ValidateDirectory(string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        string fullOut = Path.GetFullPath(outputDirectory);
        if (!Directory.Exists(fullOut))
            return new DecompCleanRoomValidationResult(true, Array.Empty<string>(), "Output directory does not exist yet.");

        var violations = new List<string>();
        foreach (string file in Directory.EnumerateFiles(fullOut, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            string rel = Path.GetRelativePath(fullOut, file).Replace('\\', '/');
            if (HasForbiddenPathOrExtension(rel) || HasForbiddenFileMagic(file) || HasForbiddenTextLeak(file))
                violations.Add(rel);
        }
        return FormatResult(violations, fullOut);
    }

    public static DecompCleanRoomValidationResult ValidatePackage(string packageOrDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageOrDirectory);
        string full = Path.GetFullPath(packageOrDirectory);
        if (Directory.Exists(full))
            return ValidateDirectory(full);
        if (!File.Exists(full))
            return new DecompCleanRoomValidationResult(false, [full], $"Package or directory not found: '{full}'.");

        if (!IsZipFile(full))
        {
            string name = Path.GetFileName(full);
            bool bad = HasForbiddenPathOrExtension(name) || HasForbiddenFileMagic(full) || HasForbiddenTextLeak(full);
            return bad
                ? new DecompCleanRoomValidationResult(false, [name], $"Clean-room violation detected in '{name}'.")
                : new DecompCleanRoomValidationResult(true, Array.Empty<string>(), "Clean-room package manifest verified.");
        }

        var violations = new List<string>();
        string virtualRoot = Path.Combine(Path.GetTempPath(), "restation_cleanroom_zip");
        using ZipArchive zip = ZipFile.OpenRead(full);
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            string rel = entry.FullName.Replace('\\', '/');
            _ = AppEnvironment.AssertPathContained(Path.GetFullPath(Path.Combine(virtualRoot, rel.Replace('/', Path.DirectorySeparatorChar))), virtualRoot);
            if (string.IsNullOrEmpty(entry.Name))
                continue;

            if (HasForbiddenPathOrExtension(rel) || HasForbiddenZipEntryContent(entry))
                violations.Add(rel);
        }
        return FormatResult(violations, full);
    }

    private static bool HasForbiddenPathOrExtension(string relPath)
    {
        string name = Path.GetFileName(relPath);
        string ext = Path.GetExtension(name);
        if (ForbiddenExtensions.Contains(ext) || name.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".Generated.dll", StringComparison.OrdinalIgnoreCase))
            return true;
        return ForbiddenPathTokens.Any(tok => relPath.Contains(tok, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasForbiddenFileMagic(string filePath)
    {
        Span<byte> buf = stackalloc byte[8];
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        int read = fs.Read(buf);
        if (read >= 8 && buf.SequenceEqual(PsxExeMagic)) return true;
        if (read >= 4 && buf[..4].SequenceEqual(SaveMagic)) return true;
        if (fs.Length >= 0x8006)
        {
            Span<byte> pvd = stackalloc byte[6];
            fs.Seek(0x8000, SeekOrigin.Begin);
            if (fs.Read(pvd) == 6 && pvd[0] == 1 && Encoding.ASCII.GetString(pvd[1..]) == "CD001") return true;
        }
        return false;
    }

    private static bool HasForbiddenTextLeak(string filePath)
    {
        string ext = Path.GetExtension(filePath);
        if (ext is not (".c" or ".h" or ".py" or ".java" or ".slaspec" or ".json" or ".xml") || new FileInfo(filePath).Length > 1024 * 1024)
            return false;
        return ContainsForbiddenText(File.ReadAllText(filePath, Encoding.UTF8));
    }

    private static bool HasForbiddenZipEntryContent(ZipArchiveEntry entry)
    {
        using Stream s = entry.Open();
        int cap = (int)Math.Min(entry.Length, 16 * 1024);
        byte[] head = new byte[cap];
        int totalRead = 0;
        while (totalRead < cap)
        {
            int n = s.Read(head, totalRead, cap - totalRead);
            if (n <= 0) break;
            totalRead += n;
        }
        if (totalRead >= 8 && head.AsSpan(0, 8).SequenceEqual(PsxExeMagic)) return true;
        if (totalRead >= 4 && head.AsSpan(0, 4).SequenceEqual(SaveMagic)) return true;
        return ContainsForbiddenText(Encoding.UTF8.GetString(head, 0, totalRead));
    }

    private static bool ContainsForbiddenText(string text) =>
        text.Contains("ghidra_psx_ldr", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("GNU General Public License", StringComparison.OrdinalIgnoreCase);

    private static bool IsZipFile(string path)
    {
        Span<byte> h = stackalloc byte[2];
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return fs.Read(h) == 2 && h[0] == 'P' && h[1] == 'K';
    }

    private static DecompCleanRoomValidationResult FormatResult(List<string> violations, string target) =>
        violations.Count == 0
            ? new DecompCleanRoomValidationResult(true, Array.Empty<string>(), $"Clean-room verification passed for '{target}'.")
            : new DecompCleanRoomValidationResult(false, violations, $"Clean-room violation ({violations.Count} item(s)): {string.Join("; ", violations)}");
}
