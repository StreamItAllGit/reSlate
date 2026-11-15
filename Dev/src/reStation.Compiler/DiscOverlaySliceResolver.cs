// @module: reStation.Compiler.DiscOverlaySliceResolver
// @purpose: Orders PS-X EXEs by AotRecipeSpec.Executables and lifts <container>#<sliceLabel> overlay slices via verified SHA-256 search.
// @depends-on: System.Security.Cryptography, reStation.Contracts.AotRecipeSpec, reStation.Core.Aot.PlayerMediaResolver
// @exports: DiscOverlaySliceResolver

using System.Security.Cryptography;
using reStation.Contracts;
using reStation.Core.Aot;

namespace reStation.Compiler;

/// <summary>Deterministically resolves <see cref="DiscoveredExe"/> IDs and <c>&lt;container&gt;#&lt;sliceLabel&gt;</c> overlays against an <see cref="AotRecipeSpec"/>.</summary>
internal static class DiscOverlaySliceResolver
{
    /// <summary>Orders standard PS-X EXEs per <paramref name="recipe"/>, extracts container slices, and appends unmatched EXEs sequentially.</summary>
    internal static List<DiscoveredExe> Resolve(
        AotRecipeSpec recipe,
        List<(string Rel, byte[] Bytes, PsxExeHeader Header)> rawExes,
        Func<string, byte[]?> readContainerFile,
        string source)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(rawExes);
        ArgumentNullException.ThrowIfNull(readContainerFile);

        var usedRaw = new bool[rawExes.Count];
        var rawShas = new string[rawExes.Count];
        for (int k = 0; k < rawExes.Count; k++)
            rawShas[k] = Convert.ToHexString(SHA256.HashData(rawExes[k].Bytes)).ToLowerInvariant();

        var usedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var containerCache = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var result = new List<DiscoveredExe>(Math.Max(recipe.Executables.Count, rawExes.Count));

        for (int i = 0; i < recipe.Executables.Count; i++)
        {
            AotRecipeExecutableSpec spec = recipe.Executables[i];
            int hashIdx = spec.BootExePath.IndexOf('#');
            if (hashIdx < 0)
            {
                int matchIdx = FindMatchingRawExeIndex(rawExes, rawShas, usedRaw, spec);
                if (matchIdx < 0)
                    throw new InvalidDataException($"Recipe executable '{spec.BootExePath}' not found in '{source}'.");

                usedRaw[matchIdx] = true;
                string name = Path.GetFileName(rawExes[matchIdx].Rel);
                (string folder, string cls) = AllocateUniqueNames(Path.GetFileNameWithoutExtension(name), i, usedFolders, preferIndexSuffix: false);
                result.Add(new DiscoveredExe(i, name, rawExes[matchIdx].Rel, folder, cls, rawExes[matchIdx].Bytes, rawExes[matchIdx].Header)
                {
                    IsOverlay = false,
                    ContentSha256Hex = rawShas[matchIdx]
                });
                continue;
            }

            DiscoveredExe overlay = ExtractOverlaySlice(i, spec, hashIdx, readContainerFile, containerCache, usedFolders, source);
            result.Add(overlay);
        }

        int nextId = result.Count;
        for (int k = 0; k < rawExes.Count; k++)
        {
            if (usedRaw[k]) continue;
            string name = Path.GetFileName(rawExes[k].Rel);
            (string folder, string cls) = AllocateUniqueNames(Path.GetFileNameWithoutExtension(name), nextId, usedFolders, preferIndexSuffix: false);
            result.Add(new DiscoveredExe(nextId, name, rawExes[k].Rel, folder, cls, rawExes[k].Bytes, rawExes[k].Header)
            {
                IsOverlay = false,
                ContentSha256Hex = rawShas[k]
            });
            nextId++;
        }

        if (result.Count == 0)
            throw new FileNotFoundException($"No PS-X EXE executables found in '{source}'.");

        return result;
    }

    private static int FindMatchingRawExeIndex(
        List<(string Rel, byte[] Bytes, PsxExeHeader Header)> rawExes,
        string[] rawShas,
        bool[] usedRaw,
        AotRecipeExecutableSpec spec)
    {
        if (!string.IsNullOrWhiteSpace(spec.Sha256))
        {
            for (int k = 0; k < rawExes.Count; k++)
            {
                if (!usedRaw[k] && string.Equals(rawShas[k], spec.Sha256, StringComparison.OrdinalIgnoreCase))
                    return k;
            }
        }

        string normBoot = spec.BootExePath.Replace('\\', '/').TrimStart('/');
        for (int k = 0; k < rawExes.Count; k++)
        {
            if (!usedRaw[k] && string.Equals(rawExes[k].Rel, normBoot, StringComparison.OrdinalIgnoreCase))
                return k;
        }

        string fileName = Path.GetFileName(normBoot);
        for (int k = 0; k < rawExes.Count; k++)
        {
            if (!usedRaw[k] && string.Equals(Path.GetFileName(rawExes[k].Rel), fileName, StringComparison.OrdinalIgnoreCase))
                return k;
        }
        return -1;
    }

    private static DiscoveredExe ExtractOverlaySlice(
        int exeId,
        AotRecipeExecutableSpec spec,
        int hashIdx,
        Func<string, byte[]?> readContainerFile,
        Dictionary<string, byte[]> containerCache,
        HashSet<string> usedFolders,
        string source)
    {
        if (hashIdx == 0 || hashIdx + 1 >= spec.BootExePath.Length)
            throw new InvalidDataException($"Malformed container slice specifier '{spec.BootExePath}'.");

        string containerRel = spec.BootExePath[..hashIdx].Replace('\\', '/').TrimStart('/');
        string sliceLabel = spec.BootExePath[(hashIdx + 1)..];
        if (!containerCache.TryGetValue(containerRel, out byte[]? containerBytes))
        {
            containerBytes = readContainerFile(containerRel)
                ?? throw new InvalidDataException($"Overlay container '{containerRel}' for '{spec.BootExePath}' not found in '{source}'.");
            containerCache[containerRel] = containerBytes;
        }

        if (spec.TextSizeBytes <= 0)
            throw new InvalidDataException($"Overlay recipe entry '{spec.BootExePath}' requires positive textSizeBytes ({spec.TextSizeBytes}).");
        if (string.IsNullOrWhiteSpace(spec.Sha256) || spec.Sha256.Length != 64 || !spec.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException($"Overlay recipe entry '{spec.BootExePath}' requires a valid 64-character SHA-256 digest.");
        if (!PlayerMediaResolver.TryFindContainerSliceOffset(containerBytes, spec.TextSizeBytes, spec.Sha256, out int offset))
            throw new InvalidDataException($"Overlay slice '{spec.BootExePath}' ({spec.TextSizeBytes} bytes, sha256={spec.Sha256}) not found in '{containerRel}'.");

        byte[] slice = containerBytes.AsSpan(offset, spec.TextSizeBytes).ToArray();
        uint loadAddr = DiscExeLoader.CanonicalRam(spec.LoadAddress);
        uint pc0 = DiscExeLoader.CanonicalRam(spec.EntryAddress != 0 ? spec.EntryAddress : spec.LoadAddress);
        var hdr = new PsxExeHeader(pc0, spec.GlobalPointer, loadAddr, (uint)slice.Length, 0U, 0U);
        (string folder, string cls) = AllocateUniqueNames(sliceLabel, exeId, usedFolders, preferIndexSuffix: true);

        return new DiscoveredExe(exeId, spec.BootExePath, spec.BootExePath, folder, cls, slice, hdr)
        {
            IsOverlay = true,
            ContentSha256Hex = spec.Sha256.ToLowerInvariant()
        };
    }

    internal static (string Folder, string ClassName) AllocateUniqueNames(
        string rawStem,
        int index,
        HashSet<string> usedFolders,
        bool preferIndexSuffix)
    {
        string baseStem = DiscExeLoader.ToPascalIdentifier(rawStem);
        string folder = preferIndexSuffix ? $"{baseStem}_{index}Exe" : $"{baseStem}Exe";
        if (!usedFolders.Add(folder))
        {
            folder = $"{baseStem}_{index}Exe";
            int suffix = 1;
            while (!usedFolders.Add(folder))
                folder = $"{baseStem}_{index}_{suffix++}Exe";
        }
        string cls = folder[..^3] + "Module";
        return (folder, cls);
    }
}
