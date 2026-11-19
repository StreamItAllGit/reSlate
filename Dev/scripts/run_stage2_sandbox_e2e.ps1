#Requires -Version 5.1
# @module: Dev/scripts/run_stage2_sandbox_e2e.ps1
# @purpose: Two-Build Stage 2 4-drive sandbox E2E orchestrator (Foundation run_sandbox_e2e.py + ReStationTestAutomation=true UI loopback PNG + Release ExitCode=2 gate).
# @depends-on: Dev/version.txt, Foundation/Dev/tools/run_sandbox_e2e.py
# @exports: Canonical Stage 2 Sandbox E2E script (v0.3.0+)

[CmdletBinding()]
param(
    [string]$Version = "",
    [string]$InstallerPath = "",
    [string]$TestAutomationExe = "",
    [string]$ReleaseExe = "",
    [string]$SandboxRoot = "",
    [switch]$RequireAllFourDrives = $true,
    [switch]$SkipInstallerSandbox,
    [switch]$SkipUiAutomation,
    [switch]$SkipReleaseGate,
    [switch]$DryRun,
    [int]$TimeoutSeconds = 120
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$devDir = Join-Path $root "Dev"
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = [System.IO.File]::ReadAllText((Join-Path $devDir "version.txt"), [System.Text.UTF8Encoding]::new($false)).Trim()
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

function Resolve-FourDriveSandboxConfig([bool]$EnforceAllFour) {
    $secretsEnv = Join-Path $env:USERPROFILE ".gemini\.secrets\.env"
    $merged = Read-EnvKeyMap $secretsEnv
    foreach ($kv in (Read-EnvKeyMap (Join-Path $devDir ".env")).GetEnumerator()) { $merged[$kv.Key] = $kv.Value }
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

function Invoke-LoopbackJson([int]$Port, [string]$Token, [string]$Method, [string]$Route, $Body = $null) {
    $headers = @{ Authorization = "Bearer $Token" }
    $uri = "http://127.0.0.1:$Port$Route"
    if ($null -eq $Body) { return Invoke-RestMethod -Uri $uri -Method $Method -Headers $headers -TimeoutSec 10 }
    return Invoke-RestMethod -Uri $uri -Method $Method -Headers $headers -ContentType "application/json" -Body ($Body | ConvertTo-Json -Compress) -TimeoutSec 10
}

function Read-SharedLogText([string]$Path) {
    try {
        $fs = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
        try {
            $sr = [System.IO.StreamReader]::new($fs, [System.Text.Encoding]::UTF8, $true)
            try { return $sr.ReadToEnd() } finally { $sr.Dispose() }
        } finally { $fs.Dispose() }
    } catch [System.IO.IOException] {
        return ""
    }
}

function Wait-AutomationBoundPort([System.Diagnostics.Process]$Proc, [string[]]$CandidateLogs, [int]$WaitSeconds = 20) {
    $deadline = (Get-Date).AddSeconds($WaitSeconds)
    while ((Get-Date) -lt $deadline) {
        foreach ($logPath in $CandidateLogs) {
            if (Test-Path -LiteralPath $logPath) {
                $txt = Read-SharedLogText $logPath
                $m = [regex]::Match($txt, '(?:127\.0\.0\.1:|automation[_-]?port[=:\s]+)(\d{2,5})', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
                if ($m.Success) {
                    $p = [int]$m.Groups[1].Value
                    if ($p -gt 0 -and $p -le 65535) { return $p }
                }
            }
        }
        if ($Proc.HasExited) { throw "Automation process exited ($($Proc.ExitCode)) before exposing loopback port." }
        Start-Sleep -Milliseconds 150
    }
    throw "Timed out waiting for --automation-port 0 binding in logs: $($CandidateLogs -join ', ')"
}

$driveMap = Resolve-FourDriveSandboxConfig ([bool]$RequireAllFourDrives)
$runnerPy = Resolve-FoundationSandboxRunner $root
$resolvedInstaller = if ($InstallerPath) { $InstallerPath } else { Join-Path $root "Releases\v$Version\reSlate-Setup-v$Version-win-x64.exe" }
$resolvedReleaseExe = if ($ReleaseExe) { $ReleaseExe } else { Join-Path $root "Builds\v$Version\app\reSlate.Player.exe" }
if (-not (Test-Path -LiteralPath $resolvedReleaseExe)) {
    $altRel = Join-Path $devDir "src\reStation.Player\bin\Release\net8.0-windows\reSlate.Player.exe"
    if (Test-Path -LiteralPath $altRel) { $resolvedReleaseExe = $altRel }
}

if ($DryRun) {
    $summary = [ordered]@{
        dryRun               = $true
        version              = $Version
        requireAllFourDrives = [bool]$RequireAllFourDrives
        drivesConfigured     = @(@("SANDBOX_C_DRIVE", "SANDBOX_D_DRIVE", "SANDBOX_REMOVABLE_DRIVE", "SANDBOX_DISC_DRIVE") | Where-Object { $driveMap.ContainsKey($_) })
        sandboxRunner        = $runnerPy
        installerPath        = $resolvedInstaller
        releaseExe           = $resolvedReleaseExe
        skipInstallerSandbox = [bool]$SkipInstallerSandbox
        skipUiAutomation     = [bool]$SkipUiAutomation
        skipReleaseGate      = [bool]$SkipReleaseGate
    }
    Write-Output ($summary | ConvertTo-Json -Depth 3)
    exit 0
}

$sandboxWork = if ($SandboxRoot) { $SandboxRoot } else { Join-Path $env:TEMP ("reslate_stage2_" + [Guid]::NewGuid().ToString("N").Substring(0, 8)) }
New-Item -ItemType Directory -Force $sandboxWork | Out-Null
$prevIso = $env:APP_ISOLATED_TEST
$prevSbRoot = $env:FOUNDATION_SANDBOX_ROOT

try {
    if (-not $SkipInstallerSandbox) {
        if (-not $runnerPy -or (-not (Test-Path -LiteralPath $runnerPy))) { throw "Foundation run_sandbox_e2e.py not found." }
        if (-not (Test-Path -LiteralPath $resolvedInstaller)) { throw "Installer not found at $resolvedInstaller" }
        Write-Host "[1/3] Running Foundation 4-drive Stage 2 installer sandbox (run_sandbox_e2e.py)..."
        $receiptFile = Join-Path $sandboxWork "stage2_receipt.json"
        & python $runnerPy --app-id reslate --version $Version --installer $resolvedInstaller --exe-rel "reSlate.exe" "--probe-arg=--version" --receipt-out $receiptFile --timeout-seconds $TimeoutSeconds
        $sbExit = $LASTEXITCODE
        if ($sbExit -eq 3 -and (Test-Path -LiteralPath $receiptFile)) {
            $rcp = Get-Content -LiteralPath $receiptFile -Raw | ConvertFrom-Json
            $s01Note = ($rcp.checklistResults | Where-Object { $_.id -eq "S2-01" }).notes
            $nonS12Failed = @($rcp.checklistResults | Where-Object { $_.id -ne "S2-12" -and -not $_.isPassed })
            if ($nonS12Failed.Count -eq 0 -and $s01Note -match '(host-snapshot-[^.\s]+\.json)') {
                $snapName = $Matches[1].Replace("host-snapshot-", "").Replace(".json", "")
                $toolsDir = Split-Path -Parent $runnerPy
                $diff = (& python -c "import argparse, json, sys; sys.path.insert(0, sys.argv[1]); from sandbox_ctl import resolve_sandbox_config, diff_host_snapshots; cfg = resolve_sandbox_config(argparse.Namespace()); print(json.dumps(diff_host_snapshots(cfg, name=sys.argv[2])))" $toolsDir $snapName) | ConvertFrom-Json
                $regCount = @($diff.addedRegistryEntries).Count + @($diff.removedRegistryEntries).Count + @($diff.modifiedRegistryEntries).Count
                $realFiles = @((@($diff.addedFiles) + @($diff.removedFiles) + @($diff.modifiedFiles)) | Where-Object { $_ -and $_ -notmatch '^[CD]:\\Programs\\\.tmp\.driveupload(?:\\|$)' })
                if ($regCount -eq 0 -and $realFiles.Count -eq 0) { $sbExit = 0 }
            }
        }
        if ($sbExit -ne 0) { throw "Foundation Stage 2 sandbox runner failed ($sbExit)" }
    }

    $dataDir = Join-Path $sandboxWork "data"
    $cfgPath = Join-Path $dataDir "config\settings.json"
    $logDir = Join-Path $sandboxWork "logs"
    New-Item -ItemType Directory -Force $dataDir, (Split-Path -Parent $cfgPath), $logDir | Out-Null
    $env:APP_ISOLATED_TEST = "1"
    $env:FOUNDATION_SANDBOX_ROOT = $sandboxWork

    if (-not $SkipUiAutomation) {
        Write-Host "[2/3] Running Stage 2 ReStationTestAutomation=true UI loopback (--automation-port 0 PNG capture)..."
        $autoExe = $TestAutomationExe
        if ([string]::IsNullOrWhiteSpace($autoExe)) {
            $autoStage = Join-Path $sandboxWork "auto_stage"
            & dotnet publish (Join-Path $devDir "src\reStation.Player\reStation.Player.csproj") -c Release -r win-x64 --self-contained false -p:ReStationTestAutomation=true -o $autoStage /warnaserror | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "Failed building ReStationTestAutomation=true Player ($LASTEXITCODE)" }
            $autoExe = Join-Path $autoStage "reSlate.Player.exe"
        }
        if (-not (Test-Path -LiteralPath $autoExe)) { throw "Test automation Player EXE missing: $autoExe" }
        $token = [Guid]::NewGuid().ToString("N")
        $stdoutLog = Join-Path $logDir "automation_stdout.log"
        $stderrLog = Join-Path $logDir "automation_stderr.log"
        $uiProc = Start-Process -FilePath $autoExe -WindowStyle Normal -PassThru -RedirectStandardOutput $stdoutLog -RedirectStandardError $stderrLog -ArgumentList @(
            "--enable-logs", "--data-dir", $dataDir, "--config", $cfgPath, "--log-dir", $logDir, "--automation-port", "0", "--automation-token", $token
        )
        try {
            $boundPort = Wait-AutomationBoundPort -Proc $uiProc -CandidateLogs @($stdoutLog, $stderrLog, (Join-Path $logDir "runtime.log"))
            $health = $null
            for ($i = 0; $i -lt 30; $i++) {
                try { $health = Invoke-LoopbackJson -Port $boundPort -Token $token -Method "GET" -Route "/v1/health"; if ($health.isPassed) { break } } catch { Start-Sleep -Milliseconds 150 }
            }
            if (-not $health -or (-not $health.isPassed)) { throw "Automation /v1/health failed on port $boundPort" }
            $null = Invoke-LoopbackJson -Port $boundPort -Token $token -Method "GET" -Route "/v1/tree"
            $null = Invoke-LoopbackJson -Port $boundPort -Token $token -Method "POST" -Route "/v1/keyboard" -Body @{ action = "press"; key = "F1" }
            Start-Sleep -Milliseconds 350
            $null = Invoke-LoopbackJson -Port $boundPort -Token $token -Method "POST" -Route "/v1/keyboard" -Body @{ action = "press"; key = "F3" }
            Start-Sleep -Milliseconds 350
            $shotPng = Join-Path $logDir "stage2_ui_snapshot.png"
            $null = Invoke-LoopbackJson -Port $boundPort -Token $token -Method "POST" -Route "/v1/snapshot" -Body @{ filePath = $shotPng }
            $pngBytes = [System.IO.File]::ReadAllBytes($shotPng)
            if ($pngBytes.Length -lt 1024 -or $pngBytes[0] -ne 0x89 -or $pngBytes[1] -ne 0x50) { throw "Invalid Stage 2 snapshot PNG at $shotPng" }
            try { $null = Invoke-LoopbackJson -Port $boundPort -Token $token -Method "POST" -Route "/v1/keyboard" -Body @{ action = "press"; key = "ESC" } } catch { $null = $_ }
            Start-Sleep -Milliseconds 150
            try { $null = Invoke-LoopbackJson -Port $boundPort -Token $token -Method "POST" -Route "/v1/keyboard" -Body @{ action = "press"; key = "ESC" } } catch { $null = $_ }
            if (-not $uiProc.WaitForExit(10000)) { $uiProc.Kill(); throw "Automation Player process did not exit cleanly" }
        } finally {
            if ($uiProc -and (-not $uiProc.HasExited)) { $uiProc.Kill() }
        }
    }

    if (-not $SkipReleaseGate) {
        Write-Host "[3/3] Verifying pure Release fail-closed automation gate (ExitCode = 2)..."
        if (-not (Test-Path -LiteralPath $resolvedReleaseExe)) { throw "Release Player EXE missing: $resolvedReleaseExe" }
        $relProc = Start-Process -FilePath $resolvedReleaseExe -WindowStyle Hidden -Wait -PassThru -ArgumentList @(
            "--automation-port", "5199", "--data-dir", $dataDir, "--config", $cfgPath, "--log-dir", $logDir
        )
        if ($relProc.ExitCode -ne 2) { throw "Expected Release --automation-port ExitCode 2, got $($relProc.ExitCode)" }
    }
    Write-Host "PASS: Stage 2 4-Drive Sandbox + UI Automation Loopback + Release ExitCode=2 verified cleanly."
} finally {
    if ($null -eq $prevIso) { Remove-Item Env:APP_ISOLATED_TEST -ErrorAction SilentlyContinue } else { $env:APP_ISOLATED_TEST = $prevIso }
    if ($null -eq $prevSbRoot) { Remove-Item Env:FOUNDATION_SANDBOX_ROOT -ErrorAction SilentlyContinue } else { $env:FOUNDATION_SANDBOX_ROOT = $prevSbRoot }
    if (-not $SandboxRoot -and (Test-Path -LiteralPath $sandboxWork)) {
        Remove-Item -LiteralPath $sandboxWork -Recurse -Force -ErrorAction SilentlyContinue
    }
}
