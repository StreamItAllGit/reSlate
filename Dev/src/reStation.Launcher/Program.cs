// @module: reStation.Launcher.Program
// @purpose: Minimal single-file clean-root launcher resolving app\reStation.Player.exe (or %LOCALAPPDATA% canonical track), AllowAutoStart = false, caller CWD preservation, and sync CLI forwarding.
// @depends-on: System, System.ComponentModel, System.Diagnostics, System.IO, System.Reflection, System.Runtime.CompilerServices, System.Runtime.InteropServices, System.Threading, Foundation.Core, Foundation.Launcher
// @exports: Program

using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Foundation.Core;
using Foundation.Launcher;

namespace reStation.Launcher;

internal static class Program
{
    public const bool AllowAutoStart = false;
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    static Program()
    {
        AppDomain.CurrentDomain.AssemblyResolve += static (_, e) =>
        {
            string? shortName = new AssemblyName(e.Name).Name;
            string? resourceName = shortName switch
            {
                "Foundation.Launcher" => "Foundation.Launcher.dll",
                "Foundation.Core" => "Foundation.Core.dll",
                _ => null
            };
            if (resourceName is null) return null;
            using Stream? stream = typeof(Program).Assembly.GetManifestResourceStream(resourceName);
            if (stream is null) return null;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return Assembly.Load(ms.ToArray());
        };
    }

    [STAThread]
    private static int Main(string[] args) => RunLauncher(args ?? Array.Empty<string>());

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunLauncher(string[] args)
    {
        string rootDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string[] resolvedArgs = LauncherArgsHelper.ResolveRelativePathArgs(args, Environment.CurrentDirectory);
        string[] forwardedArgs = LauncherArgsHelper.SanitizeAndPrepareForwardArgs(
            resolvedArgs, out string? reEngineDir, out string? suiteDir);

        string candidateExe = LauncherArgsHelper.ResolveTargetPlayerExecutable(rootDir, reEngineDir, suiteDir);
        string playerDir = Path.GetDirectoryName(candidateExe) ?? rootDir;
        string installRoot = string.Equals(Path.GetFileName(playerDir), "app", StringComparison.OrdinalIgnoreCase)
            ? (Path.GetDirectoryName(playerDir) ?? rootDir)
            : playerDir;
        string playerExe = RootLauncherHost.ResolveTargetExecutable(
            installRoot,
            Path.Combine("app", "reStation.Player.exe"),
            Environment.ProcessPath);
        if (!File.Exists(playerExe) && File.Exists(candidateExe))
        {
            playerExe = candidateExe;
        }

        if (!File.Exists(playerExe))
        {
            Console.Error.WriteLine($"reStation player binary not found at '{playerExe}'.");
            return 1;
        }

        bool syncWait = LauncherArgsHelper.RequiresSyncWait(forwardedArgs);
        return syncWait
            ? RunAndWait(playerExe, forwardedArgs)
            : LaunchDetached(playerExe, forwardedArgs);
    }

    private static int LaunchDetached(string playerExe, string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = playerExe,
            WorkingDirectory = Environment.CurrentDirectory,
            UseShellExecute = false
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using Process? proc = Process.Start(psi);
        return proc is null ? 1 : 0;
    }

    private static int RunAndWait(string playerExe, string[] args)
    {
        if (OperatingSystem.IsWindows() && !Console.IsOutputRedirected && !Console.IsErrorRedirected)
        {
            _ = AttachConsole(AttachParentProcess);
        }

        bool redirOut = Console.IsOutputRedirected;
        bool redirErr = Console.IsErrorRedirected;
        var psi = new ProcessStartInfo
        {
            FileName = playerExe,
            WorkingDirectory = Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = !redirOut && !redirErr,
            RedirectStandardOutput = redirOut,
            RedirectStandardError = redirErr
        };
        foreach (string a in args) psi.ArgumentList.Add(a);

        using Process? proc = Process.Start(psi);
        if (proc is null)
        {
            Console.Error.WriteLine($"Failed to start '{playerExe}'.");
            return 1;
        }

        JobObjectGuard? jobGuard = null;
        int cancelled = 0;
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            Interlocked.Exchange(ref cancelled, 1);
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                Trace.WriteLine($"Launcher cancel skip: {ex.Message}");
            }
        };
        Console.CancelKeyPress += onCancel;
        try
        {
            jobGuard = LauncherArgsHelper.AttachJobGuard(proc);
            if (jobGuard is null) Console.Error.WriteLine("[WARN] [LAUNCHER] JobObject kill-on-close guard unavailable.");
            if (redirOut) proc.OutputDataReceived += (_, ev) => { if (ev.Data is not null) Console.Out.WriteLine(ev.Data); };
            if (redirErr) proc.ErrorDataReceived += (_, ev) => { if (ev.Data is not null) Console.Error.WriteLine(ev.Data); };
            if (redirOut) proc.BeginOutputReadLine();
            if (redirErr) proc.BeginErrorReadLine();
            proc.WaitForExit();
            return Volatile.Read(ref cancelled) == 1 ? 130 : proc.ExitCode;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
            jobGuard?.Dispose();
        }
    }
}
