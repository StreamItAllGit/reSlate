// @module: reStation.Launcher.Program
// @purpose: Clean-root reStation.exe launcher forwarding CLI/GUI to app\reStation.Player.exe via embedded Foundation.Launcher RootLauncherHost.
// @depends-on: System.Reflection, System.Runtime.CompilerServices, Foundation.Launcher
// @exports: Program
// @owner: devv-pkg-test (T7)

using System.Reflection;
using System.Runtime.CompilerServices;
using Foundation.Launcher;

namespace reStation.Launcher;

internal static class Program
{
    static Program()
    {
        AppDomain.CurrentDomain.AssemblyResolve += static (_, e) =>
        {
            if (!string.Equals(new AssemblyName(e.Name).Name, "Foundation.Launcher", StringComparison.OrdinalIgnoreCase)) return null;
            using Stream? stream = typeof(Program).Assembly.GetManifestResourceStream("Foundation.Launcher.dll");
            if (stream is null) return null;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return Assembly.Load(ms.ToArray());
        };
    }

    [STAThread]
    private static int Main(string[] args) => RunLauncher(args ?? Array.Empty<string>());

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunLauncher(string[] args) =>
        RootLauncherHost.Run(Path.Combine("app", "reStation.Player.exe"), "reStation", args);
}
