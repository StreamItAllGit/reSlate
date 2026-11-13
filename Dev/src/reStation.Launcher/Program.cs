// @module: reStation.Launcher.Program
// @purpose: Clean-root reStation.exe launcher forwarding CLI/GUI to app\reStation.Player.exe via embedded Foundation.Launcher RootLauncherHost; synchronous wait for verification/CLI flags.
// @depends-on: System.Diagnostics, System.Reflection, System.Runtime.CompilerServices, System.Runtime.InteropServices, Foundation.Launcher
// @exports: Program
// @owner: devv-pkg-test (T7)

using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Foundation.Launcher;

namespace reStation.Launcher;

internal static class Program
{
    private const int AttachParentProcess = -1;
    private static readonly string PlayerRelativePath = Path.Combine("app", "reStation.Player.exe");

    /// <summary>Flags whose runs must finish before the launcher returns (verification, CLI, and help runs).</summary>
    private static readonly string[] SyncWaitFlags =
    {
        "--visible-test", "--fixture", "--verify", "--test-mode", "--headless", "--version", "--help", "-h", "/?"
    };

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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    [STAThread]
    private static int Main(string[] args) => RunLauncher(args ?? Array.Empty<string>());

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunLauncher(string[] args) =>
        RequiresSyncWait(args)
            ? RunAndWait(args)
            : RootLauncherHost.Run(PlayerRelativePath, "reStation", args);

    /// <summary>SYNC-WAIT FIX: true when any argument asks for a verification/CLI run whose exit code the caller needs.</summary>
    private static bool RequiresSyncWait(string[] args) =>
        args.Any(a => SyncWaitFlags.Contains(a.Trim(), StringComparer.OrdinalIgnoreCase));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunAndWait(string[] args)
    {
        string playerExe = RootLauncherHost.ResolveTargetExecutable(AppContext.BaseDirectory, PlayerRelativePath, Environment.ProcessPath);
        try { AttachConsole(AttachParentProcess); } catch (DllNotFoundException) { }

        if (!File.Exists(playerExe))
        {
            Console.Error.WriteLine($"reStation application binary not found at '{playerExe}'.");
            return 1;
        }

        var psi = new ProcessStartInfo
        {
            FileName = playerExe,
            WorkingDirectory = Path.GetDirectoryName(playerExe) ?? AppContext.BaseDirectory,
            UseShellExecute = false
        };
        foreach (string arg in args) psi.ArgumentList.Add(arg);

        using Process? proc = Process.Start(psi);
        if (proc is null) return 1;
        proc.WaitForExit();
        return proc.ExitCode;
    }
}
