// @module: reStation.Installer.Program
// @purpose: 4-pillar zero-PII single-EXE installer entry: static AssemblyResolve (.dll.gz), NoInlining core, AllowAutoStart=false, StandardInstallerHost.
// @depends-on: System.IO.Compression, System.Reflection, System.Runtime.CompilerServices, Foundation.Installer
// @exports: Program
// @owner: devv-pkg-test (T7) — T1 skeleton; reEngine auto-bootstrap + side-by-side engines\reStation\v0.1.0 resolution lands in T7.

using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using Foundation.Installer;

namespace reStation.Installer;

internal static class Program
{
    public const string ProductName = "reStation";
    public const string Version = "0.1.0";
    public const string BrandAccentHex = "#38BDF8";
    public const string PayloadResourceName = "reStation.Installer.Embedded.payload.zip";
    public const string ReEngineBootstrapResourceName = "reStation.Installer.Embedded.reEngine-Setup.exe";
    public const bool AllowAutoStart = false;

    private static readonly string[] EmbeddedAssemblies = { "Foundation.Core", "Foundation.Installer", "reEngine.Core" };

    static Program()
    {
        AppDomain.CurrentDomain.AssemblyResolve += static (_, e) =>
        {
            string? name = new AssemblyName(e.Name).Name;
            if (name is null || !EmbeddedAssemblies.Contains(name, StringComparer.OrdinalIgnoreCase)) return null;
            using Stream? gz = typeof(Program).Assembly.GetManifestResourceStream(name + ".dll.gz");
            if (gz is null) return null;
            using var inflate = new GZipStream(gz, CompressionMode.Decompress);
            using var ms = new MemoryStream();
            inflate.CopyTo(ms);
            return Assembly.Load(ms.ToArray());
        };
    }

    [STAThread]
    private static int Main(string[] args) => RunInstallerCore(args ?? Array.Empty<string>());

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunInstallerCore(string[] args) =>
        StandardInstallerHost.Run(CreateDescriptor(), SanitizeArgs(args), typeof(Program).Assembly);

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static InstallerAppDescriptor CreateDescriptor() => new(
        ProductName: ProductName,
        DisplayName: ProductName,
        Publisher: "StreamItAll",
        Version: Version,
        MainExecutableRelativePath: "reStation.exe",
        Subtitle: "PS1 Static Recompilation Engine for reEngine",
        EmbeddedPayloadResourceName: PayloadResourceName,
        BrandAccentHex: BrandAccentHex,
        AllowAutoStart: AllowAutoStart);

    /// <summary>Strips --autostart (zero Startup registration invariant).</summary>
    internal static string[] SanitizeArgs(string[]? args) =>
        args is null
            ? Array.Empty<string>()
            : args.Where(a => !string.Equals(a?.Trim(), "--autostart", StringComparison.OrdinalIgnoreCase)).ToArray();
}
