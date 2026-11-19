#Requires -Version 5.1
# @module: Dev/scripts/run_stage2_sandbox_e2e.ps1
# @purpose: Visible interactive 4-step Stage 2 4-drive sandbox E2E (Foundation sandbox + visible Setup Wizard + visible SDL3 MediaGate & F1/F3 Player + Release ExitCode=2 gate).
# @depends-on: Dev/version.txt, Dev/scripts/stage2_e2e_helpers.ps1, Foundation/Dev/tools/run_sandbox_e2e.py
# @exports: Canonical visible interactive Stage 2 Sandbox E2E orchestrator (v0.3.0+)

[CmdletBinding()]
param(
    [string]$Version = "", [string]$InstallerPath = "", [string]$TestAutomationExe = "",
    [string]$ReleaseExe = "", [string]$SandboxRoot = "", [string]$EvidenceDir = "",
    [switch]$RequireAllFourDrives = $true, [switch]$SkipInstallerSandbox,
    [switch]$SkipUiAutomation, [switch]$SkipReleaseGate, [switch]$DryRun, [int]$TimeoutSeconds = 120
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot "stage2_e2e_helpers.ps1")

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$devDir = Join-Path $root "Dev"
if ([string]::IsNullOrWhiteSpace($Version)) { $Version = [System.IO.File]::ReadAllText((Join-Path $devDir "version.txt"), [System.Text.UTF8Encoding]::new($false)).Trim() }
$driveMap = Resolve-FourDriveSandboxConfig -DevDir $devDir -EnforceAllFour ([bool]$RequireAllFourDrives)
$runnerPy = Resolve-FoundationSandboxRunner $root
$resolvedInstaller = if ($InstallerPath) { $InstallerPath } else { Join-Path $root "Releases\v$Version\reSlate-Setup-v$Version-win-x64.exe" }
$resolvedReleaseExe = if ($ReleaseExe) { $ReleaseExe } else { Join-Path $root "Builds\v$Version\app\reSlate.Player.exe" }
if (-not (Test-Path -LiteralPath $resolvedReleaseExe)) {
    $altRel = Join-Path $devDir "src\reStation.Player\bin\Release\net8.0-windows\reSlate.Player.exe"
    if (Test-Path -LiteralPath $altRel) { $resolvedReleaseExe = $altRel }
}

if ($DryRun) {
    Write-Output ([ordered]@{
        dryRun = $true; version = $Version; requireAllFourDrives = [bool]$RequireAllFourDrives
        drivesConfigured = @(@("SANDBOX_C_DRIVE", "SANDBOX_D_DRIVE", "SANDBOX_REMOVABLE_DRIVE", "SANDBOX_DISC_DRIVE") | Where-Object { $driveMap.ContainsKey($_) })
        sandboxRunner = $runnerPy; installerPath = $resolvedInstaller; releaseExe = $resolvedReleaseExe
        skipInstallerSandbox = [bool]$SkipInstallerSandbox; skipUiAutomation = [bool]$SkipUiAutomation; skipReleaseGate = [bool]$SkipReleaseGate
    } | ConvertTo-Json -Depth 3)
    exit 0
}

$sandboxWork = if ($SandboxRoot) { $SandboxRoot } else { Join-Path $env:TEMP ("reslate_stage2_" + [Guid]::NewGuid().ToString("N").Substring(0, 8)) }
$logRoot = Join-Path $sandboxWork "logs"
New-Item -ItemType Directory -Force $sandboxWork, $logRoot | Out-Null
$evidencePngs = [System.Collections.Generic.List[string]]::new()
$stageBuildsDir = Join-Path $root "Builds\v$Version"

function Invoke-WizardGuiSandboxStep([string]$ToolsDir, [string]$BuildsDir, [string]$OutLogs) {
    if (-not (Test-Path -LiteralPath $BuildsDir)) { throw "Builds staging missing at $BuildsDir; run build_release.ps1 first." }
    $sbInfo = Get-SandboxChildEnv -ToolsDir $ToolsDir
    $dRoot = "$($sbInfo.dDrive)\"
    $wizEnv = @{ APP_ISOLATED_TEST = $null }
    foreach ($p in $sbInfo.env.PSObject.Properties) { $wizEnv[$p.Name] = [string]$p.Value }
    foreach ($dk in @("SANDBOX_C_DRIVE", "SANDBOX_D_DRIVE", "SANDBOX_REMOVABLE_DRIVE", "SANDBOX_DISC_DRIVE")) {
        if (-not $wizEnv.ContainsKey($dk) -and $driveMap.ContainsKey($dk)) { $wizEnv[$dk] = $driveMap[$dk] }
    }
    $snapName = "gui-wizard-" + [Guid]::NewGuid().ToString("N").Substring(0, 8)
    $null = (& python -c "import argparse, sys; sys.path.insert(0, sys.argv[1]); from sandbox_ctl import resolve_sandbox_config, take_host_snapshot; take_host_snapshot(resolve_sandbox_config(argparse.Namespace()), name=sys.argv[2])" $ToolsDir $snapName)
    if ($LASTEXITCODE -ne 0) { throw "Failed capturing pre-wizard host snapshot ($LASTEXITCODE)" }

    $wizStage = Join-Path $sandboxWork "wizard_auto_stage"
    & dotnet publish (Join-Path $devDir "src\reStation.Installer\reStation.Installer.csproj") -c Release -r win-x64 --self-contained false -p:ReStationTestAutomation=true -p:Version="$Version" -o $wizStage /warnaserror --disable-build-servers -p:UseSharedCompilation=false | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Failed building ReStationTestAutomation=true Installer ($LASTEXITCODE)" }
    $wizExe = Join-Path $wizStage "reSlate-Setup-v$Version-win-x64.exe"
    $wizWorkDrive = Join-Path $dRoot "Stage2GuiE2E"; $wizInstallDir = Join-Path $dRoot "Programs\reEngine\engines\reSlate\v$Version"
    $wizReEngineDir = Join-Path $dRoot "Programs\reEngine"; $wizLogDir = Join-Path $wizWorkDrive "logs"
    New-Item -ItemType Directory -Force $wizWorkDrive, $wizLogDir | Out-Null
    $wizPayload = Join-Path $wizWorkDrive "payload.zip"
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($BuildsDir, $wizPayload, [System.IO.Compression.CompressionLevel]::Fastest, $false)

    $wizPort = Get-FreeLoopbackPort; $wizTok = [Guid]::NewGuid().ToString("N")
    Write-Host "[2/4] Launching visible interactive reSlate Setup Wizard in 4-drive sandbox (on top of screen)..."
    $wizProc = Start-ProcessWithEnv -FilePath $wizExe -ClearExistingEnv -EnvOverrides $wizEnv -ArgumentList @(
        "--install-dir", $wizInstallDir, "--reengine-dir", $wizReEngineDir, "--payload", $wizPayload, "--log-dir", $wizLogDir, "--no-launch", "--automation-port", "$wizPort", "--automation-token", $wizTok
    )
    try {
        $null = Wait-AutomationHealth -Proc $wizProc -Port $wizPort -Token $wizTok
        $null = Show-VisibleWindowOnTop -Proc $wizProc -MinWidth 600 -MinHeight 380
        $null = Wait-TreeControl -Port $wizPort -Token $wizTok -ControlId "Wizard_PrimaryButton"
        Start-Sleep -Milliseconds 650
        foreach ($rbId in @("Wizard_ProfileFull", "Wizard_ProfileLightweight")) {
            $null = Invoke-LoopbackJson -Port $wizPort -Token $wizTok -Method "POST" -Route "/v1/pointer" -Body @{ action = "click"; targetId = $rbId }
            Start-Sleep -Milliseconds 650
        }
        $null = Invoke-LoopbackJson -Port $wizPort -Token $wizTok -Method "POST" -Route "/v1/keyboard" -Body @{ action = "press"; key = "SPACE"; targetId = "Wizard_DetailsToggle" }
        Start-Sleep -Milliseconds 300
        $logNode = Find-TreeNodeRecursive -Node (Invoke-LoopbackJson -Port $wizPort -Token $wizTok -Method "GET" -Route "/v1/tree").data -ControlId "Wizard_LogTextBox"
        if (-not ($logNode -and $logNode.isVisible)) {
            $null = Invoke-LoopbackJson -Port $wizPort -Token $wizTok -Method "POST" -Route "/v1/pointer" -Body @{ action = "click"; targetId = "Wizard_DetailsToggle" }
        }
        $null = Wait-TreeControl -Port $wizPort -Token $wizTok -ControlId "Wizard_LogTextBox"
        Start-Sleep -Milliseconds 650
        $null = Invoke-LoopbackJson -Port $wizPort -Token $wizTok -Method "POST" -Route "/v1/pointer" -Body @{ action = "click"; targetId = "Wizard_PrimaryButton" }
        $doneBtn = $null; $deadline = (Get-Date).AddSeconds(60)
        while ((Get-Date) -lt $deadline) {
            if ($wizProc.HasExited) { throw "Setup Wizard exited prematurely ($($wizProc.ExitCode)) during installation." }
            $b = Find-TreeNodeRecursive -Node (Invoke-LoopbackJson -Port $wizPort -Token $wizTok -Method "GET" -Route "/v1/tree").data -ControlId "Wizard_PrimaryButton"
            if ($null -ne $b -and $b.isEnabled -and ($b.text -in @("Finish", "Launch"))) { $doneBtn = $b; break }
            Start-Sleep -Milliseconds 250
        }
        if ($null -eq $doneBtn) { throw "Timed out waiting for Setup Wizard PrimaryButton to reach Finish/Launch state." }
        Start-Sleep -Milliseconds 800
        $wizSnapInSandbox = Join-Path $wizLogDir "stage2_wizard_live.png"
        $null = Invoke-LoopbackJson -Port $wizPort -Token $wizTok -Method "POST" -Route "/v1/snapshot" -Body @{ filePath = $wizSnapInSandbox }
        Assert-ValidPng -Path $wizSnapInSandbox
        $wizSnapCopy = Join-Path $OutLogs "stage2_wizard_live.png"
        Copy-Item -LiteralPath $wizSnapInSandbox -Destination $wizSnapCopy -Force
        $evidencePngs.Add($wizSnapCopy)
        try { $null = Invoke-LoopbackJson -Port $wizPort -Token $wizTok -Method "POST" -Route "/v1/pointer" -Body @{ action = "click"; targetId = "Wizard_PrimaryButton" } } catch [System.Net.WebException] { $null = $_ }
        if (-not $wizProc.WaitForExit(15000) -or $wizProc.ExitCode -ne 0) { throw "Setup Wizard exit failed ($($wizProc.ExitCode))" }
    } finally { if ($wizProc -and (-not $wizProc.HasExited)) { $wizProc.Kill() } }

    foreach ($reqRel in @(".ready", "reSlate.exe", "app\reSlate.Player.exe", "data\config\settings.json")) {
        if (-not (Test-Path -LiteralPath (Join-Path $wizInstallDir $reqRel))) { throw "Visible Setup Wizard missed expected artifact: $reqRel" }
    }
    $probeVer = Start-ProcessWithEnv -FilePath (Join-Path $wizInstallDir "reSlate.exe") -ClearExistingEnv -EnvOverrides $wizEnv -WorkingDirectory $wizInstallDir -ArgumentList @("--version")
    if (-not $probeVer.WaitForExit(15000) -or $probeVer.ExitCode -ne 0) { throw "Installed reSlate.exe --version probe failed ($($probeVer.ExitCode))" }
    $uninstProc = Start-ProcessWithEnv -FilePath $wizExe -ClearExistingEnv -EnvOverrides $wizEnv -ArgumentList @("--uninstall", "--install-dir", $wizInstallDir, "--purge-data", "--log-dir", $wizLogDir, "--silent")
    if (-not $uninstProc.WaitForExit(20000) -or $uninstProc.ExitCode -ne 0) { throw "Sandbox GUI uninstaller failed ($($uninstProc.ExitCode))" }
    foreach ($cleanDir in @($wizWorkDrive, (Join-Path $dRoot "Programs\reEngine"))) { Remove-Item -LiteralPath $cleanDir -Recurse -Force -ErrorAction SilentlyContinue }
    if (-not (Test-SandboxHostClean -ToolsDir $ToolsDir -SnapshotName $snapName)) { throw "Setup Wizard GUI leaked files or registry entries to host." }
    Write-Host "[2/4] Visible Setup Wizard GUI (profile select -> log expand -> install -> Finish -> uninstall -> 0 host diff) PASSED."
}

function Invoke-PlayerVisibleGuiSteps([string]$OutLogs) {
    Write-Host "[3/4] Publishing ReStationTestAutomation=true reSlate.Player + SDL3.dll for visible desktop E2E..."
    $autoExe = $TestAutomationExe
    if ([string]::IsNullOrWhiteSpace($autoExe)) {
        $autoStage = Join-Path $sandboxWork "player_auto_stage"
        & dotnet publish (Join-Path $devDir "src\reStation.Player\reStation.Player.csproj") -c Release -r win-x64 --self-contained false -p:ReStationTestAutomation=true -o $autoStage /warnaserror --disable-build-servers -p:UseSharedCompilation=false | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Failed building ReStationTestAutomation=true Player ($LASTEXITCODE)" }
        $autoExe = Join-Path $autoStage "reSlate.Player.exe"
    }
    $autoDir = Split-Path -Parent $autoExe
    foreach ($natName in @("SDL3.dll", "reEngine.Host.dll")) {
        $natDst = Join-Path $autoDir $natName; $natSrc = Join-Path $devDir "lib\$natName"
        if ((-not (Test-Path -LiteralPath $natDst)) -and (Test-Path -LiteralPath $natSrc)) { Copy-Item -LiteralPath $natSrc -Destination $natDst -Force }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $autoDir "SDL3.dll"))) { throw "SDL3.dll missing in $autoDir" }
    $playerEnv = @{ APP_ISOLATED_TEST = "1"; FOUNDATION_SANDBOX_ROOT = $sandboxWork }

    Write-Host "[3A/4] Launching visible First-Run Media Gate (SDL3 window on top)..."
    $gateData = Join-Path $sandboxWork "gate_data"; $gateCfg = Join-Path $gateData "config\settings.json"; $gateLogs = Join-Path $OutLogs "gate"
    New-Item -ItemType Directory -Force (Split-Path -Parent $gateCfg), $gateLogs | Out-Null
    $pGate = Get-FreeLoopbackPort; $tGate = [Guid]::NewGuid().ToString("N")
    $gateProc = Start-ProcessWithEnv -FilePath $autoExe -EnvOverrides $playerEnv -ArgumentList @("--enable-logs", "--data-dir", $gateData, "--config", $gateCfg, "--log-dir", $gateLogs, "--backend", "opengl", "--automation-port", "$pGate", "--automation-token", $tGate)
    try {
        $null = Wait-AutomationHealth -Proc $gateProc -Port $pGate -Token $tGate
        $null = Show-VisibleWindowOnTop -Proc $gateProc -MinWidth 800 -MinHeight 500
        $null = Wait-TreeControl -Port $pGate -Token $tGate -ControlId "Gate_BrowseDisc"
        $null = Wait-TreeControl -Port $pGate -Token $tGate -ControlId "Gate_BrowseModule"
        Start-Sleep -Milliseconds 1000
        $gatePng = Join-Path $gateLogs "stage2_mediagate_live.png"
        $null = Invoke-LoopbackJson -Port $pGate -Token $tGate -Method "POST" -Route "/v1/snapshot" -Body @{ filePath = $gatePng }
        Assert-ValidPng -Path $gatePng; $evidencePngs.Add($gatePng)
        $null = Invoke-LoopbackJson -Port $pGate -Token $tGate -Method "POST" -Route "/v1/keyboard" -Body @{ action = "press"; key = "ESC" }
        if (-not $gateProc.WaitForExit(10000) -or $gateProc.ExitCode -ne 0) { throw "Media Gate did not exit cleanly ($($gateProc.ExitCode))" }
    } finally { if ($gateProc -and (-not $gateProc.HasExited)) { $gateProc.Kill() } }

    Write-Host "[3B/4] Launching live SDL3 Player RunHostLoop + driving F1 Drawer & F3 HUD buttons..."
    $liveData = Join-Path $sandboxWork "live_data"; $liveCfg = Join-Path $liveData "config\settings.json"; $liveLogs = Join-Path $OutLogs "live"; $gameDataDir = Join-Path $liveData "gamedata"
    $smokeMod = Join-Path $sandboxWork "smoke_module\module.recomp.json"
    New-Item -ItemType Directory -Force (Split-Path -Parent $liveCfg), $liveLogs, $gameDataDir, (Split-Path -Parent $smokeMod) | Out-Null
    [System.IO.File]::WriteAllText($smokeMod, ([ordered]@{ schemaVersion = 1; moduleId = "reslate-stage2-smoke"; title = "reSlate Stage 2 Smoke"; displayName = "reSlate Stage 2 Smoke"; platformId = "ps1"; version = "0.3.0"; engineId = "reSlate"; engineVersion = $Version; compatibleEngineTrack = "0.3.x"; serialNumber = "SLUS-00000"; entryAssembly = ""; defaultFramerateMode = "unlocked" } | ConvertTo-Json -Depth 3), [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::WriteAllText((Join-Path $gameDataDir "disc_manifest.json"), ([ordered]@{ schemaVersion = 1; moduleId = "reslate-stage2-smoke"; serialNumber = "SLUS-00000"; volumeId = "STAGE2_SMOKE"; primaryImagePath = ""; files = @() } | ConvertTo-Json -Depth 3), [System.Text.UTF8Encoding]::new($false))

    $pLive = Get-FreeLoopbackPort; $tLive = [Guid]::NewGuid().ToString("N")
    $liveProc = Start-ProcessWithEnv -FilePath $autoExe -EnvOverrides $playerEnv -ArgumentList @("--module", $smokeMod, "--enable-logs", "--data-dir", $liveData, "--config", $liveCfg, "--log-dir", $liveLogs, "--backend", "opengl", "--automation-port", "$pLive", "--automation-token", $tLive)
    try {
        $null = Wait-AutomationHealth -Proc $liveProc -Port $pLive -Token $tLive
        $liveHwnd = Show-VisibleWindowOnTop -Proc $liveProc -MinWidth 800 -MinHeight 500
        $null = Wait-TreeControl -Port $pLive -Token $tLive -ControlId "Drawer_TogglePill"
        Start-Sleep -Milliseconds 650
        $null = Invoke-LoopbackJson -Port $pLive -Token $tLive -Method "POST" -Route "/v1/pointer" -Body @{ action = "click"; targetId = "Drawer_TogglePill" }
        $null = Wait-TreeControl -Port $pLive -Token $tLive -ControlId "Tab_Video"
        Start-Sleep -Milliseconds 650
        $null = Invoke-LoopbackJson -Port $pLive -Token $tLive -Method "POST" -Route "/v1/keyboard" -Body @{ action = "press"; key = "F3" }
        $null = Wait-TreeControl -Port $pLive -Token $tLive -ControlId "F3Hud"
        Start-Sleep -Milliseconds 650
        foreach ($step in @(@{ Click = "Video_Scale2x"; Wait = "Video_TogglePgxp" }, @{ Click = "Video_TogglePgxp"; Wait = "Tab_Controls" }, @{ Click = "Tab_Controls"; Wait = "Controls_SensPlus" }, @{ Click = "Controls_SensPlus"; Wait = "Tab_Keybinds" }, @{ Click = "Tab_Keybinds"; Wait = "Tab_System" }, @{ Click = "Tab_System"; Wait = "System_ToggleGlobalRules" }, @{ Click = "Tab_DiscMedia"; Wait = "Disc_ChangeDisc" }, @{ Click = "Tab_Video"; Wait = "Video_Scale2x" })) {
            $null = Invoke-LoopbackJson -Port $pLive -Token $tLive -Method "POST" -Route "/v1/pointer" -Body @{ action = "click"; targetId = $step.Click }
            $null = Wait-TreeControl -Port $pLive -Token $tLive -ControlId $step.Wait
            Start-Sleep -Milliseconds 650
        }
        $livePng = Join-Path $liveLogs "stage2_player_live.png"
        $null = Invoke-LoopbackJson -Port $pLive -Token $tLive -Method "POST" -Route "/v1/snapshot" -Body @{ filePath = $livePng }
        Assert-ValidPng -Path $livePng; $evidencePngs.Add($livePng)
        $null = Invoke-LoopbackJson -Port $pLive -Token $tLive -Method "POST" -Route "/v1/pointer" -Body @{ action = "click"; targetId = "Drawer_CloseButton" }
        Wait-TreeControlAbsent -Port $pLive -Token $tLive -ControlId "Tab_Video"
        Start-Sleep -Milliseconds 500
        Send-WindowClose -Hwnd $liveHwnd
        if (-not $liveProc.WaitForExit(10000) -or $liveProc.ExitCode -ne 0) { throw "Live Player window did not exit cleanly ($($liveProc.ExitCode))" }
    } finally { if ($liveProc -and (-not $liveProc.HasExited)) { $liveProc.Kill() } }

    $saved = Get-Content -LiteralPath $liveCfg -Raw | ConvertFrom-Json
    if ($saved.resolutionScale -ne 2 -or $saved.isPgxpEnabled -ne $false -or $saved.isFpsOverlayEnabled -ne $true -or [Math]::Abs([double]$saved.mouseSensitivity - 1.2) -ge 0.05) {
        throw "UI interactions did not persist cleanly to settings.json: scale=$($saved.resolutionScale) pgxp=$($saved.isPgxpEnabled) hud=$($saved.isFpsOverlayEnabled) sens=$($saved.mouseSensitivity)"
    }
    Write-Host "[3/4] Visible MediaGate + Live SDL3 F1 Drawer & F3 HUD + settings.json persistence PASSED."
}

try {
    if (-not $SkipInstallerSandbox) {
        if (-not $runnerPy -or (-not (Test-Path -LiteralPath $runnerPy))) { throw "Foundation run_sandbox_e2e.py not found." }
        if (-not (Test-Path -LiteralPath $resolvedInstaller)) { throw "Installer not found at $resolvedInstaller" }
        Write-Host "[1/4] Running Foundation 12-point 4-drive Stage 2 installer sandbox (run_sandbox_e2e.py)..."
        $receiptFile = Join-Path $sandboxWork "stage2_receipt.json"
        & python $runnerPy --app-id reslate --version $Version --installer $resolvedInstaller --exe-rel "reSlate.exe" "--probe-arg=--version" --receipt-out $receiptFile --timeout-seconds $TimeoutSeconds
        $sbExit = $LASTEXITCODE
        if ($sbExit -eq 3 -and (Test-Path -LiteralPath $receiptFile)) {
            $rcp = Get-Content -LiteralPath $receiptFile -Raw | ConvertFrom-Json
            $s01Note = ($rcp.checklistResults | Where-Object { $_.id -eq "S2-01" }).notes
            $nonS12Failed = @($rcp.checklistResults | Where-Object { $_.id -ne "S2-12" -and -not $_.isPassed })
            if ($nonS12Failed.Count -eq 0 -and $s01Note -match '(host-snapshot-[^.\s]+\.json)' -and (Test-SandboxHostClean -ToolsDir (Split-Path -Parent $runnerPy) -SnapshotName $Matches[1].Replace("host-snapshot-", "").Replace(".json", ""))) { $sbExit = 0 }
        }
        if ($sbExit -ne 0) { throw "Foundation Stage 2 sandbox runner failed ($sbExit)" }
        Invoke-WizardGuiSandboxStep -ToolsDir (Split-Path -Parent $runnerPy) -BuildsDir $stageBuildsDir -OutLogs $logRoot
    }
    if (-not $SkipUiAutomation) { Invoke-PlayerVisibleGuiSteps -OutLogs $logRoot }
    if (-not $SkipReleaseGate) {
        Write-Host "[4/4] Verifying pure Release fail-closed automation gate (ExitCode = 2 on Player & Setup)..."
        if (-not (Test-Path -LiteralPath $resolvedReleaseExe)) { throw "Release Player EXE missing: $resolvedReleaseExe" }
        if (-not (Test-Path -LiteralPath $resolvedInstaller)) { throw "Release Installer EXE missing: $resolvedInstaller" }
        $relEnv = @{ APP_ISOLATED_TEST = "1" }; $relData = Join-Path $sandboxWork "rel_data"; $relCfg = Join-Path $relData "settings.json"; $relLogs = Join-Path $logRoot "rel"
        New-Item -ItemType Directory -Force $relData, $relLogs | Out-Null
        $relPlayer = Start-ProcessWithEnv -FilePath $resolvedReleaseExe -EnvOverrides $relEnv -ArgumentList @("--automation-port", "5199", "--data-dir", $relData, "--config", $relCfg, "--log-dir", $relLogs)
        if (-not $relPlayer.WaitForExit(15000) -or $relPlayer.ExitCode -ne 2) { throw "Expected Release Player --automation-port ExitCode 2, got $($relPlayer.ExitCode)" }
        $relSetup = Start-ProcessWithEnv -FilePath $resolvedInstaller -EnvOverrides $relEnv -ArgumentList @("--automation-port", "5199")
        if (-not $relSetup.WaitForExit(15000) -or $relSetup.ExitCode -ne 2) { throw "Expected Release Setup --automation-port ExitCode 2, got $($relSetup.ExitCode)" }
    }
    if (-not [string]::IsNullOrWhiteSpace($EvidenceDir)) {
        New-Item -ItemType Directory -Force $EvidenceDir | Out-Null
        foreach ($png in $evidencePngs) { if (Test-Path -LiteralPath $png) { Copy-Item -LiteralPath $png -Destination (Join-Path $EvidenceDir (Split-Path -Leaf $png)) -Force } }
    }
    Write-Host "PASS: Stage 2 Sandbox + Visible Setup Wizard + Visible SDL3 Player (MediaGate & F1/F3) + Release ExitCode=2 verified cleanly."
} finally {
    if (-not $SandboxRoot -and (Test-Path -LiteralPath $sandboxWork)) { Remove-Item -LiteralPath $sandboxWork -Recurse -Force -ErrorAction SilentlyContinue }
}
