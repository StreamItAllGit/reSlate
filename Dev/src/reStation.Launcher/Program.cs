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

    private static readonly HashSet<string> PathValueFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "--module", "--disc", "--fixture", "--dump-screenshot", "--data-dir", "--config", "--log-dir"
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
            : RootLauncherHost.Run(PlayerRelativePath, "reStation", ResolveRelativePathArgs(args, Environment.CurrentDirectory));

    /// <summary>SYNC-WAIT FIX: true when any argument asks for a verification/CLI run whose exit code the caller needs.</summary>
    private static bool RequiresSyncWait(string[] args) =>
        args.Any(a => SyncWaitFlags.Contains(a.Trim(), StringComparer.OrdinalIgnoreCase));

    private static string[] ResolveRelativePathArgs(string[] args, string baseDir)
    {
        if (args.Length == 0) return args;
        var resolved = (string[])args.Clone();
        for (int i = 0; i < resolved.Length - 1; i++)
        {
            if (!PathValueFlags.Contains(resolved[i].Trim())) continue;
            string val = resolved[i + 1].Trim().Trim('"');
            if (!string.IsNullOrWhiteSpace(val) && !Path.IsPathRooted(val))
                resolved[i + 1] = Path.GetFullPath(val, baseDir);
            i++;
        }
        return resolved;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunAndWait(string[] args)
    {
        string playerExe = RootLauncherHost.ResolveTargetExecutable(AppContext.BaseDirectory, PlayerRelativePath, Environment.ProcessPath);
        bool redirOut = Console.IsOutputRedirected;
        bool redirErr = Console.IsErrorRedirected;
        if (!redirOut && !redirErr)
        {
            try { _ = AttachConsole(AttachParentProcess); } catch (DllNotFoundException) { }
        }

        if (!File.Exists(playerExe))
        {
            Console.Error.WriteLine($"reStation application binary not found at '{playerExe}'.");
            Console.Error.Flush();
            return 1;
        }

        var psi = new ProcessStartInfo
        {
            FileName = playerExe,
            WorkingDirectory = Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = redirOut,
            RedirectStandardError = redirErr
        };
        foreach (string arg in args) psi.ArgumentList.Add(arg);

        using Process? proc = Process.Start(psi);
        if (proc is null)
        {
            Console.Error.WriteLine($"Failed to start '{playerExe}'.");
            Console.Error.Flush();
            return 1;
        }

        Task? outTask = redirOut ? proc.StandardOutput.BaseStream.CopyToAsync(Console.OpenStandardOutput()) : null;
        Task? errTask = redirErr ? proc.StandardError.BaseStream.CopyToAsync(Console.OpenStandardError()) : null;
        proc.WaitForExit();
        if (outTask is not null) outTask.GetAwaiter().GetResult();
        if (errTask is not null) errTask.GetAwaiter().GetResult();
        Console.Out.Flush();
        Console.Error.Flush();
        return proc.ExitCode;
    }
}
