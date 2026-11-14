// @module: reStation.Launcher.Program
// @purpose: Clean-root reStation.exe launcher forwarding CLI/GUI to app\reStation.Player.exe via embedded Foundation.Launcher & Foundation.Core JobObjectGuard.
// @depends-on: System.Diagnostics, System.Reflection, System.Runtime.CompilerServices, System.Runtime.InteropServices, Foundation.Launcher, Foundation.Core
// @exports: Program
// @owner: devv-pkg (Wave 2 Slice 3)

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

    static Program()
    {
        AppDomain.CurrentDomain.AssemblyResolve += static (_, e) =>
        {
            string? shortName = new AssemblyName(e.Name).Name;
            string? resourceName = string.Equals(shortName, "Foundation.Launcher", StringComparison.OrdinalIgnoreCase)
                ? "Foundation.Launcher.dll"
                : string.Equals(shortName, "Foundation.Core", StringComparison.OrdinalIgnoreCase)
                    ? "Foundation.Core.dll"
                    : null;
            if (resourceName is null) return null;

            using Stream? stream = typeof(Program).Assembly.GetManifestResourceStream(resourceName);
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
    private static int RunLauncher(string[] args)
    {
        string[] resolvedArgs = LauncherArgsHelper.ResolveRelativePathArgs(args, Environment.CurrentDirectory);
        return LauncherArgsHelper.RequiresSyncWait(args)
            ? RunAndWait(resolvedArgs)
            : RootLauncherHost.Run(PlayerRelativePath, "reStation", resolvedArgs);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunAndWait(string[] args)
    {
        string playerExe = RootLauncherHost.ResolveTargetExecutable(AppContext.BaseDirectory, PlayerRelativePath, Environment.ProcessPath);
        bool redirOut = Console.IsOutputRedirected;
        bool redirErr = Console.IsErrorRedirected;
        if (!redirOut && !redirErr)
        {
            try
            {
                _ = AttachConsole(AttachParentProcess);
            }
            catch (DllNotFoundException ex)
            {
                string warn = $"[WARN] [LAUNCHER] AttachConsole unavailable: {ex.Message}";
                Trace.WriteLine(warn);
                Console.Error.WriteLine(warn);
            }
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

        IDisposable? jobGuard = null;
        try
        {
            jobGuard = LauncherArgsHelper.AttachJobGuard(proc);
            Task? outTask = redirOut ? proc.StandardOutput.BaseStream.CopyToAsync(Console.OpenStandardOutput()) : null;
            Task? errTask = redirErr ? proc.StandardError.BaseStream.CopyToAsync(Console.OpenStandardError()) : null;
            proc.WaitForExit();
            if (outTask is not null) outTask.GetAwaiter().GetResult();
            if (errTask is not null) errTask.GetAwaiter().GetResult();
            Console.Out.Flush();
            Console.Error.Flush();
            return proc.ExitCode;
        }
        finally
        {
            jobGuard?.Dispose();
        }
    }
}
