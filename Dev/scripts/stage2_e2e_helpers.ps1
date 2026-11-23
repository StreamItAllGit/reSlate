#Requires -Version 5.1
# @module: Dev/scripts/stage2_e2e_helpers.ps1
# @purpose: Shared Win32 visible-window, loopback automation, PNG verification, and 4-drive sandbox helpers for run_stage2_sandbox_e2e.ps1.
# @depends-on: System.Drawing, System.Net.Sockets, Foundation/Dev/tools/sandbox_ctl.py
# @exports: Win32VisibleWindow, Show-VisibleWindowOnTop, Send-WindowClose, Get-FreeLoopbackPort, Invoke-LoopbackJson, Wait-AutomationHealth, Wait-TreeControl, Wait-TreeControlAbsent, Find-TreeNodeRecursive, Quote-WindowsArg, Start-ProcessWithEnv, Read-EnvKeyMap, Resolve-FourDriveSandboxConfig, Resolve-FoundationSandboxRunner, Get-SandboxChildEnv, Test-SandboxHostClean, Resolve-SharedLibsSdkDir, Test-Stage2SharedLibsCompilerSmoke, Assert-ValidPng

Set-StrictMode -Version Latest

$script:PreservedSandboxOsVars = @(
    "SystemDrive", "SystemRoot", "WINDIR", "COMSPEC", "PATH", "PATHEXT",
    "ProgramData", "ProgramFiles", "ProgramFiles(x86)", "CommonProgramFiles", "CommonProgramFiles(x86)",
    "ALLUSERSPROFILE", "PUBLIC", "OS", "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE",
    "DOTNET_ROOT", "DOTNET_CLI_TELEMETRY_OPTOUT"
)

if (-not ("Win32VisibleWindow" -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public static class Win32VisibleWindow
{
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    public static IntPtr FindVisibleTopLevelWindow(int pid, int minWidth, int minHeight)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
        {
            uint ownerPid;
            GetWindowThreadProcessId(hWnd, out ownerPid);
            if (ownerPid != (uint)pid) return true;
            if (!IsWindowVisible(hWnd)) return true;
            RECT r;
            if (!GetWindowRect(hWnd, out r)) return true;
            int w = r.Right - r.Left;
            int h = r.Bottom - r.Top;
            if (r.Left <= -10000 || r.Top <= -10000) return true;
            if (w < minWidth || h < minHeight) return true;
            found = hWnd;
            return false;
        }, IntPtr.Zero);
        return found;
    }
}
"@
}

Add-Type -AssemblyName System.Drawing

function Show-VisibleWindowOnTop([System.Diagnostics.Process]$Proc, [int]$MinWidth = 500, [int]$MinHeight = 300, [int]$WaitSeconds = 15) {
    $hwnd = [IntPtr]::Zero
    $maxTries = [Math]::Max(1, $WaitSeconds * 10)
    for ($i = 0; $i -lt $maxTries; $i++) {
        if ($Proc.HasExited) {
            throw "Process ($($Proc.Id)) exited ($($Proc.ExitCode)) before showing a visible >= ${MinWidth}x${MinHeight} window."
        }
        $hwnd = [Win32VisibleWindow]::FindVisibleTopLevelWindow($Proc.Id, $MinWidth, $MinHeight)
        if ($hwnd -ne [IntPtr]::Zero) { break }
        Start-Sleep -Milliseconds 100
    }
    if ($hwnd -eq [IntPtr]::Zero) {
        throw "No visible on-screen window (>= ${MinWidth}x${MinHeight}) appeared for PID $($Proc.Id) within ${WaitSeconds}s."
    }
    [void][Win32VisibleWindow]::ShowWindow($hwnd, 9)
    [void][Win32VisibleWindow]::SetWindowPos($hwnd, [IntPtr](-1), 0, 0, 0, 0, 0x0001 -bor 0x0002 -bor 0x0040)
    [void][Win32VisibleWindow]::SetForegroundWindow($hwnd)
    return $hwnd
}

function Send-WindowClose([IntPtr]$Hwnd) {
    if ($Hwnd -ne [IntPtr]::Zero) {
        [void][Win32VisibleWindow]::PostMessage($Hwnd, [uint32]0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    }
}

function Get-FreeLoopbackPort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
    $listener.Stop()
    return $port
}

function Invoke-LoopbackJson([int]$Port, [string]$Token, [string]$Method, [string]$Route, $Body = $null) {
    $headers = @{ Authorization = "Bearer $Token" }
    $uri = "http://127.0.0.1:$Port$Route"
    if ($null -eq $Body) { return Invoke-RestMethod -Uri $uri -Method $Method -Headers $headers -TimeoutSec 10 }
    return Invoke-RestMethod -Uri $uri -Method $Method -Headers $headers -ContentType "application/json" -Body ($Body | ConvertTo-Json -Compress) -TimeoutSec 10
}

function Wait-AutomationHealth([System.Diagnostics.Process]$Proc, [int]$Port, [string]$Token, [int]$WaitSeconds = 20) {
    $deadline = (Get-Date).AddSeconds($WaitSeconds)
    while ((Get-Date) -lt $deadline) {
        if ($Proc.HasExited) { throw "Automation process exited ($($Proc.ExitCode)) before loopback health passed on port $Port." }
        try {
            $health = Invoke-LoopbackJson -Port $Port -Token $Token -Method "GET" -Route "/v1/health"
            if ($health.isPassed) { return $health }
        } catch { $null = $_ }
        Start-Sleep -Milliseconds 150
    }
    throw "Automation loopback failed to become healthy on 127.0.0.1:$Port within ${WaitSeconds}s."
}

function Find-TreeNodeRecursive($Node, [string]$ControlId) {
    if ($null -eq $Node) { return $null }
    if ($Node.id -eq $ControlId) { return $Node }
    if ($null -ne $Node.children) {
        foreach ($child in $Node.children) {
            $hit = Find-TreeNodeRecursive -Node $child -ControlId $ControlId
            if ($null -ne $hit) { return $hit }
        }
    }
    return $null
}

function Wait-TreeControl([int]$Port, [string]$Token, [string]$ControlId, [int]$TimeoutMs = 15000) {
    $deadline = (Get-Date).AddMilliseconds($TimeoutMs)
    while ((Get-Date) -lt $deadline) {
        $tree = Invoke-LoopbackJson -Port $Port -Token $Token -Method "GET" -Route "/v1/tree"
        $hit = Find-TreeNodeRecursive -Node $tree.data -ControlId $ControlId
        if ($null -ne $hit -and $hit.isVisible) { return $hit }
        Start-Sleep -Milliseconds 100
    }
    throw "Control '$ControlId' did not become visible in /v1/tree on port $Port within ${TimeoutMs}ms."
}

function Wait-TreeControlAbsent([int]$Port, [string]$Token, [string]$ControlId, [int]$TimeoutMs = 10000) {
    $deadline = (Get-Date).AddMilliseconds($TimeoutMs)
    while ((Get-Date) -lt $deadline) {
        $tree = Invoke-LoopbackJson -Port $Port -Token $Token -Method "GET" -Route "/v1/tree"
        $hit = Find-TreeNodeRecursive -Node $tree.data -ControlId $ControlId
        if ($null -eq $hit -or (-not $hit.isVisible)) { return }
        Start-Sleep -Milliseconds 100
    }
    throw "Control '$ControlId' stayed visible in /v1/tree on port $Port beyond ${TimeoutMs}ms."
}

function Quote-WindowsArg([string]$Arg) {
    if ($null -eq $Arg) { return '""' }
    if ($Arg.Length -eq 0) { return '""' }
    if ($Arg -notmatch '[\s"]') { return $Arg }
    $escaped = $Arg -replace '(\\*)"', '$1$1\"'
    $escaped = $escaped -replace '(\\+)$', '$1$1'
    return '"' + $escaped + '"'
}

function Start-ProcessWithEnv(
    [Parameter(Mandatory = $true)][string]$FilePath,
    [string[]]$ArgumentList = @(),
    [hashtable]$EnvOverrides = @{},
    [string]$WorkingDirectory = "",
    [switch]$ClearExistingEnv
) {
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $FilePath
    $psi.Arguments = (($ArgumentList | ForEach-Object { Quote-WindowsArg $_ }) -join " ")
    if (-not [string]::IsNullOrWhiteSpace($WorkingDirectory)) { $psi.WorkingDirectory = $WorkingDirectory }
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $false
    if ($ClearExistingEnv) {
        $preserved = @{}
        foreach ($osVar in $script:PreservedSandboxOsVars) {
            $osVal = [Environment]::GetEnvironmentVariable($osVar)
            if (-not [string]::IsNullOrWhiteSpace($osVal)) { $preserved[$osVar] = $osVal }
        }
        $psi.EnvironmentVariables.Clear()
        foreach ($pkv in $preserved.GetEnumerator()) { $psi.EnvironmentVariables[$pkv.Key] = $pkv.Value }
    }
    foreach ($kv in $EnvOverrides.GetEnumerator()) {
        if ($null -eq $kv.Value) {
            if ($psi.EnvironmentVariables.ContainsKey($kv.Key)) { $psi.EnvironmentVariables.Remove($kv.Key) }
        } else {
            $psi.EnvironmentVariables[$kv.Key] = [string]$kv.Value
        }
    }
    return [System.Diagnostics.Process]::Start($psi)
}

function Read-EnvKeyMap([string]$FilePath) {
    $map = @{}
    if (-not (Test-Path -LiteralPath $FilePath)) { return $map }
    foreach ($raw in [System.IO.File]::ReadAllLines($FilePath)) {
        $line = $raw.Trim()
        if ($line.Length -eq 0 -or $line.StartsWith("#") -or (-not $line.Contains("="))) { continue }
        if ($line.StartsWith("export ")) { $line = $line.Substring(7).TrimStart() }
        $idx = $line.IndexOf("=")
        $k = $line.Substring(0, $idx).Trim()
        $v = $line.Substring($idx + 1).Trim().Trim('"', "'")
        if ($k.Length -gt 0 -and $v.Length -gt 0) { $map[$k] = $v }
    }
    return $map
}

function Resolve-FourDriveSandboxConfig([string]$DevDir, [bool]$EnforceAllFour) {
    $secretsEnv = Join-Path $env:USERPROFILE ".gemini\.secrets\.env"
    $merged = Read-EnvKeyMap $secretsEnv
    foreach ($kv in (Read-EnvKeyMap (Join-Path $DevDir ".env")).GetEnumerator()) { $merged[$kv.Key] = $kv.Value }
    $roles = @("SANDBOX_C_DRIVE", "SANDBOX_D_DRIVE", "SANDBOX_REMOVABLE_DRIVE", "SANDBOX_DISC_DRIVE")
    foreach ($role in $roles) {
        $procVal = [Environment]::GetEnvironmentVariable($role)
        if (-not [string]::IsNullOrWhiteSpace($procVal)) { $merged[$role] = $procVal.Trim() }
    }
    $missing = @($roles | Where-Object { -not $merged.ContainsKey($_) -or [string]::IsNullOrWhiteSpace($merged[$_]) })
    if ($EnforceAllFour -and $missing.Count -gt 0) {
        throw "Stage 2 4-Drive Sandbox requires all four drives ($($roles -join ', ')). Missing: $($missing -join ', ')."
    }
    return $merged
}

function Resolve-FoundationSandboxRunner([string]$RepoRoot) {
    if (-not [string]::IsNullOrWhiteSpace($env:FOUNDATION_SANDBOX_RUNNER) -and (Test-Path -LiteralPath $env:FOUNDATION_SANDBOX_RUNNER)) {
        return $env:FOUNDATION_SANDBOX_RUNNER
    }
    $candidates = [System.Collections.Generic.List[string]]@(
        (Join-Path (Split-Path -Parent $RepoRoot) "Foundation\Dev\tools\run_sandbox_e2e.py")
    )
    try {
        $gitCommon = (& git -C $RepoRoot rev-parse --git-common-dir 2>$null | Out-String).Trim()
        if ($gitCommon) {
            $primaryRepo = Split-Path -Parent ([System.IO.Path]::GetFullPath([System.IO.Path]::Combine($RepoRoot, $gitCommon)))
            $candidates.Add((Join-Path (Split-Path -Parent $primaryRepo) "Foundation\Dev\tools\run_sandbox_e2e.py"))
        }
    } catch { $null = $_ }
    return ($candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1)
}

function Get-SandboxChildEnv([string]$ToolsDir) {
    $pyCode = @'
import argparse, json, sys
sys.path.insert(0, sys.argv[1])
from sandbox_ctl import resolve_sandbox_config, init_sandbox
from run_sandbox_e2e import build_sandbox_child_env
cfg = resolve_sandbox_config(argparse.Namespace())
init_sandbox(cfg, skip_subst=False)
print(json.dumps({"dDrive": cfg["drives"]["SANDBOX_D_DRIVE"], "env": build_sandbox_child_env(cfg)}))
'@
    $raw = ($pyCode | & python - $ToolsDir)
    if ($LASTEXITCODE -ne 0) { throw "Failed initializing 4-drive sandbox env via sandbox_ctl.py ($LASTEXITCODE)" }
    $parsed = ($raw | ConvertFrom-Json)
    foreach ($osVar in @("SystemDrive", "ProgramData", "ProgramFiles", "ProgramFiles(x86)", "CommonProgramFiles", "CommonProgramFiles(x86)", "ALLUSERSPROFILE", "PUBLIC")) {
        $osVal = [Environment]::GetEnvironmentVariable($osVar)
        if (-not [string]::IsNullOrWhiteSpace($osVal) -and (-not $parsed.env.PSObject.Properties[$osVar])) {
            $parsed.env | Add-Member -NotePropertyName $osVar -NotePropertyValue $osVal
        }
    }
    return $parsed
}

function Resolve-SharedLibsSdkDir([string]$RepoRoot, [string]$Version) {
    $candidates = [System.Collections.Generic.List[string]]@(
        (Join-Path (Split-Path -Parent (Split-Path -Parent $RepoRoot)) "SharedLibs\reSlate\v$Version")
    )
    try {
        $gitCommon = (& git -C $RepoRoot rev-parse --git-common-dir 2>$null | Out-String).Trim()
        if ($gitCommon) {
            $primaryRepo = Split-Path -Parent ([System.IO.Path]::GetFullPath([System.IO.Path]::Combine($RepoRoot, $gitCommon)))
            $candidates.Add((Join-Path (Split-Path -Parent (Split-Path -Parent $primaryRepo)) "SharedLibs\reSlate\v$Version"))
        }
    } catch { $null = $_ }
    return ($candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1)
}

function Test-Stage2SharedLibsCompilerSmoke([string]$SdkDir) {
    $compExe = Join-Path $SdkDir "reSlate.Compiler.exe"
    $compDll = Join-Path $SdkDir "reSlate.Compiler.dll"
    foreach ($pair in @(@{ File = $compExe; Args = @("--help") }, @{ File = "dotnet"; Args = @($compDll, "--help") })) {
        $out = (& $pair.File @($pair.Args) 2>&1 | Out-String)
        if ($LASTEXITCODE -ne 0 -or $out -match 'hostpolicy\.dll|0x80008083' -or $out -notmatch 'Usage: reSlate\.Compiler') {
            throw "Stage 2 SharedLibs compiler smoke failed for '$($pair.File) $($pair.Args -join ' ')' (exit=$LASTEXITCODE): $out"
        }
    }
}

function Test-SandboxHostClean([string]$ToolsDir, [string]$SnapshotName) {
    $diffJson = (& python -c "import argparse, json, sys; sys.path.insert(0, sys.argv[1]); from sandbox_ctl import resolve_sandbox_config, diff_host_snapshots; print(json.dumps(diff_host_snapshots(resolve_sandbox_config(argparse.Namespace()), name=sys.argv[2])))" $ToolsDir $SnapshotName)
    if ($LASTEXITCODE -ne 0) { return $false }
    $diff = $diffJson | ConvertFrom-Json
    $regCount = @($diff.addedRegistryEntries).Count + @($diff.removedRegistryEntries).Count + @($diff.modifiedRegistryEntries).Count
    $realFiles = @((@($diff.addedFiles) + @($diff.removedFiles) + @($diff.modifiedFiles)) | Where-Object { $_ -and $_ -notmatch '^[CD]:\\Programs\\\.tmp\.driveupload(?:\\|$)' })
    return ($regCount -eq 0 -and $realFiles.Count -eq 0)
}

function Assert-ValidPng([string]$Path, [int]$MinBytes = 4096) {
    if (-not (Test-Path -LiteralPath $Path)) { throw "Expected PNG screenshot missing at $Path" }
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt $MinBytes -or $bytes[0] -ne 0x89 -or $bytes[1] -ne 0x50 -or $bytes[2] -ne 0x4E -or $bytes[3] -ne 0x47) {
        throw "Invalid PNG file at $Path ($($bytes.Length) bytes)"
    }
    $bmp = [System.Drawing.Bitmap]::new($Path)
    try {
        if ($bmp.Width -lt 400 -or $bmp.Height -lt 300) {
            throw "PNG dimensions too small ($($bmp.Width)x$($bmp.Height)) at $Path"
        }
        $colors = [System.Collections.Generic.HashSet[int]]::new()
        $nonBlack = 0
        for ($gy = 0; $gy -lt 16; $gy++) {
            $py = [int](($gy + 0.5) * $bmp.Height / 16.0)
            for ($gx = 0; $gx -lt 16; $gx++) {
                $px = [int](($gx + 0.5) * $bmp.Width / 16.0)
                $c = $bmp.GetPixel($px, $py)
                [void]$colors.Add($c.ToArgb())
                if ($c.R -gt 8 -or $c.G -gt 8 -or $c.B -gt 8) { $nonBlack++ }
            }
        }
        if ($colors.Count -lt 3 -or $nonBlack -lt 4) {
            throw "PNG screenshot '$Path' appears blank/uniform (distinctColors=$($colors.Count), nonBlackSamples=$nonBlack)."
        }
    } finally {
        $bmp.Dispose()
    }
}

$script:SharedExeboxHelpers = Join-Path (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))) "Foundation\Dev\tools\ExeboxStreamHelpers.ps1"
if (Test-Path -LiteralPath $script:SharedExeboxHelpers) { . $script:SharedExeboxHelpers }
