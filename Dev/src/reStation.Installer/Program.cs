// @module: reStation.Installer.Program
// @purpose: 4-pillar zero-PII single-EXE installer entry: static AssemblyResolve (.dll.gz), NoInlining core, AllowAutoStart=false, reEngine auto-bootstrap, StandardInstallerHost.
// @depends-on: System.IO.Compression, System.Reflection, System.Runtime.CompilerServices, Foundation.Installer, reStation.Installer.ReStationInstallerBootstrap
// @exports: Program
// @owner: devv-pkg-test (T7)

using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using Foundation.Installer;

namespace reStation.Installer;

internal static class Program
{
    public const string ProductName = ReStationInstallerTheme.ProductName;
    public const string Version = ReStationInstallerTheme.Version;
    public const string BrandAccentHex = ReStationInstallerTheme.BrandAccentHex;
    public const string PayloadResourceName = ReStationInstallerTheme.PayloadResourceName;
    public const string ReEngineBootstrapResourceName = ReStationInstallerTheme.ReEngineBootstrapResourceName;
    public const bool AllowAutoStart = ReStationInstallerTheme.AllowAutoStart;

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

    /// <summary>Kept NoInlining so the JIT never resolves Foundation/reEngine assemblies before the AssemblyResolve handler is attached.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunInstallerCore(string[] args)
    {
        InstallerAppDescriptor descriptor = CreateDescriptor();
        string[] cleanArgs = SanitizeArgs(args);
        int prepared = ReStationInstallerBootstrap.Prepare(cleanArgs, typeof(Program).Assembly, descriptor.UninstallerExeName, out string[] effectiveArgs);
        return prepared != 0
            ? prepared
            : StandardInstallerHost.Run(descriptor, effectiveArgs, typeof(Program).Assembly);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static InstallerAppDescriptor CreateDescriptor() => ReStationInstallerTheme.CreateDescriptor();

    /// <summary>Strips every <c>--autostart*</c> / <c>--auto-start*</c> argument (zero Startup registration invariant).</summary>
    internal static string[] SanitizeArgs(string[]? args) =>
        args is null
            ? Array.Empty<string>()
            : args.Where(a =>
                a is not null &&
                !a.Trim().StartsWith("--autostart", StringComparison.OrdinalIgnoreCase) &&
                !a.Trim().StartsWith("--auto-start", StringComparison.OrdinalIgnoreCase)).ToArray();
}
