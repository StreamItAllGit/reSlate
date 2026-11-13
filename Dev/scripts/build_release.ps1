#Requires -Version 5.1
# @module: Dev/scripts/build_release.ps1
# @purpose: Build, test, sign, stage Builds\, package Installers\, emit 6-file Releases\, SharedLibs SDK, and source Archive.
# @depends-on: Dev/version.txt, Dev/src/reStation.slnx, Dev/assets/bootstrap/reEngine-Setup-v0.1.0-win-x64.exe, audit_installer.ps1
# @exports: Canonical release pipeline (v0.1.0+)

[CmdletBinding()]
param(
    [string]$Version = "",
    [string]$CertThumbprint = "A5414BDD36F7F113C7B2356F189B7A43F69C35E6",
    [string]$BootstrapExe = "",
    [switch]$SkipSourceArchive,
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$devDir = Join-Path $root "Dev"
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = (Get-Content (Join-Path $devDir "version.txt") -Raw).Trim()
}

if ([string]::IsNullOrWhiteSpace($BootstrapExe)) {
    $BootstrapExe = Join-Path $devDir ("assets\bootstrap\reEngine-Setup-v{0}-win-x64.exe" -f $Version)
    if (-not (Test-Path -LiteralPath $BootstrapExe)) {
        $fallback = Get-ChildItem (Join-Path $devDir "assets\bootstrap") -Filter "reEngine-Setup-*.exe" -File -ErrorAction SilentlyContinue |
            Sort-Object Name -Descending | Select-Object -First 1
        if ($fallback) { $BootstrapExe = $fallback.FullName }
    }
}
if (-not (Test-Path -LiteralPath $BootstrapExe)) {
    throw "reEngine bootstrap installer not found: $BootstrapExe"
}

$sevenZip = Join-Path $devDir "assets\native\7z.exe"
if (-not (Test-Path -LiteralPath $sevenZip)) { throw "7z.exe not found at $sevenZip" }

$cert = Get-ChildItem @("Cert:\CurrentUser\My", "Cert:\LocalMachine\My") -ErrorAction SilentlyContinue |
    Where-Object { $_.Thumbprint -ieq $CertThumbprint } | Select-Object -First 1
if (-not $cert) {
    throw "Code-signing certificate $CertThumbprint not found in CurrentUser\My or LocalMachine\My."
}

function Invoke-Sign([string]$PathToSign) {
    $res = Set-AuthenticodeSignature -Certificate $cert -HashAlgorithm SHA256 -FilePath $PathToSign
    if ($res.Status -ne "Valid") {
        throw "Authenticode signing failed for ${PathToSign}: $($res.Status) - $($res.StatusMessage)"
    }
}

function Invoke-7z([string[]]$SevenZipArgs) {
    & $sevenZip @SevenZipArgs | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "7z.exe exited with $LASTEXITCODE ($($SevenZipArgs -join ' '))" }
}

function Sanitize-MarkdownForEndUser([string]$Text) {
    $lines = $Text -split "\r?\n"
    $filtered = foreach ($line in $lines) {
        if ($line -match 'Dev\\(?:src|tests|docs\\plans|docs\\reports)|Sandbox\\|--test-mode|--visible-test|@pland|@inspecter|@dean|@devv|@tessa') {
            continue
        }
        $line
    }
    return (($filtered -join "`r`n").Trim() + "`r`n")
}

Write-Host "[1/7] Enforcing <= 400 LOC gate across Dev\src\ and Dev\scripts\..."
$locViolations = @()
$codeFiles = @(
    Get-ChildItem (Join-Path $devDir "src") -Filter *.cs -Recurse -File |
        Where-Object { $_.FullName -notmatch '\\(?:obj|bin)\\' }
) + @(
    Get-ChildItem (Join-Path $devDir "scripts") -Filter *.ps1 -Recurse -File
)
foreach ($f in $codeFiles) {
    $lineCount = (Get-Content -LiteralPath $f.FullName | Measure-Object -Line).Lines
    if ($lineCount -gt 400) {
        $locViolations += "$($f.FullName) ($lineCount lines)"
    }
}
if ($locViolations.Count -gt 0) {
    throw "LOC ceiling (> 400) violated:`n$($locViolations -join "`n")"
}

Write-Host "[2/7] Building Release solution (/warnaserror) and running tests..."
$slnxPath = Join-Path $devDir "src\reStation.slnx"
& dotnet build $slnxPath -c Release /warnaserror
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed ($LASTEXITCODE)" }

if (-not $SkipTests) {
    & dotnet test $slnxPath -c Release --no-build --logger "console;verbosity=minimal"
    if ($LASTEXITCODE -ne 0) { throw "dotnet test failed ($LASTEXITCODE)" }
}

Write-Host "[3/7] Staging portable build in Builds\v$Version..."
$stageDir = Join-Path $root "Builds\v$Version"
$stageApp = Join-Path $stageDir "app"
$stageRom = Join-Path $stageDir "data\rom"
if (Test-Path -LiteralPath $stageDir) { Remove-Item -LiteralPath $stageDir -Recurse -Force }
New-Item -ItemType Directory -Force $stageApp, $stageRom | Out-Null
[System.IO.File]::WriteAllText((Join-Path $stageRom ".keep"), "", [System.Text.UTF8Encoding]::new($false))

& dotnet publish (Join-Path $devDir "src\reStation.Player\reStation.Player.csproj") `
    -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false `
    -p:DebugType=none -p:DebugSymbols=false -o $stageApp /warnaserror
if ($LASTEXITCODE -ne 0) { throw "dotnet publish reStation.Player failed ($LASTEXITCODE)" }

& dotnet publish (Join-Path $devDir "src\reStation.Compiler\reStation.Compiler.csproj") `
    -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false `
    -p:DebugType=none -p:DebugSymbols=false -o $stageApp /warnaserror
if ($LASTEXITCODE -ne 0) { throw "dotnet publish reStation.Compiler failed ($LASTEXITCODE)" }

$sdlRoot = Join-Path $stageApp "SDL3.dll"
$sdlNative = Join-Path $stageApp "runtimes\win-x64\native\SDL3.dll"
if ((-not (Test-Path -LiteralPath $sdlRoot)) -and (Test-Path -LiteralPath $sdlNative)) {
    Copy-Item -LiteralPath $sdlNative -Destination $sdlRoot -Force
}
if (-not (Test-Path -LiteralPath $sdlRoot)) { throw "Required SDL3.dll missing in $stageApp" }

foreach ($assetName in @("7z.exe", "7z.dll", "LICENSE-7ZIP.txt")) {
    $dst = Join-Path $stageApp $assetName
    $src = Join-Path $devDir ("assets\native\" + $assetName)
    if ((-not (Test-Path -LiteralPath $dst)) -and (Test-Path -LiteralPath $src)) {
        Copy-Item -LiteralPath $src -Destination $dst -Force
    }
}
if (-not (Test-Path -LiteralPath (Join-Path $stageApp "7z.exe"))) { throw "7z.exe missing in $stageApp" }
if (-not (Test-Path -LiteralPath (Join-Path $stageApp "7z.dll"))) { throw "7z.dll missing in $stageApp" }

$launcherStage = Join-Path $env:TEMP ("restation_launcher_" + $Version)
if (Test-Path -LiteralPath $launcherStage) { Remove-Item -LiteralPath $launcherStage -Recurse -Force }
& dotnet publish (Join-Path $devDir "src\reStation.Launcher\reStation.Launcher.csproj") `
    -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true `
    -p:DebugType=none -p:DebugSymbols=false -o $launcherStage /warnaserror
if ($LASTEXITCODE -ne 0) { throw "dotnet publish reStation.Launcher failed ($LASTEXITCODE)" }

Copy-Item (Join-Path $launcherStage "reStation.exe") (Join-Path $stageDir "reStation.exe") -Force
Remove-Item -LiteralPath $launcherStage -Recurse -Force -ErrorAction SilentlyContinue

[System.IO.File]::WriteAllText((Join-Path $stageApp "version.txt"), "$Version", [System.Text.UTF8Encoding]::new($false))
Get-ChildItem $stageDir -Include *.pdb, *.xml -Recurse -File | Remove-Item -Force

$runtimeAgents = (@'
# Runtime Context: reStation (v__VER__)

- **Launcher:** `reStation.exe` (routes to `app\reStation.Player.exe`)
- **Compiler:** `app\reStation.Compiler.exe` (PS1 ELF -> static C#/.NET 8 `.recomp` compiler)
- **Preload Media:** `data\rom\` (gitignored user-provided PS1 `.cue`/`.bin`/`.chd`/`.iso`/`.7z`)
- **Data & Logs:** `data\settings.json` and `logs\` unless overridden by `--data-dir`, `--config`, `--log-dir`
- **Daemon Policy:** Zero background daemons; `AllowAutoStart = false`.
'@).Replace('__VER__', $Version)
[System.IO.File]::WriteAllText((Join-Path $stageDir "AGENTS.md"), $runtimeAgents + "`r`n", [System.Text.UTF8Encoding]::new($false))
[System.IO.File]::WriteAllText((Join-Path $stageApp "AGENTS.md"), $runtimeAgents + "`r`n", [System.Text.UTF8Encoding]::new($false))

Invoke-Sign (Join-Path $stageApp "reStation.Player.exe")
Invoke-Sign (Join-Path $stageApp "reStation.Compiler.exe")
Invoke-Sign (Join-Path $stageDir "reStation.exe")

$forbiddenExts = @(".pdb", ".db", ".sqlite", ".sqlite3", ".log")
$badFiles = @(Get-ChildItem $stageDir -Recurse -File | Where-Object {
    ($_.Extension -in $forbiddenExts) -or ($_.Name -ieq ".env") -or ($_.Name -ilike "*secret*.json")
})
if ($badFiles.Count -gt 0) {
    throw "Forbidden staging file(s) detected: $($badFiles.FullName -join ', ')"
}

Write-Host "[4/7] Building, signing & auditing single-EXE installer..."
$workTemp = Join-Path $env:TEMP ("restation_pkg_" + $Version)
if (Test-Path -LiteralPath $workTemp) { Remove-Item -LiteralPath $workTemp -Recurse -Force }
New-Item -ItemType Directory -Force $workTemp | Out-Null
$payloadZip = Join-Path $workTemp "payload.zip"
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stageDir, $payloadZip, [System.IO.Compression.CompressionLevel]::Optimal, $false)

$instOut = Join-Path $workTemp "installer_out"
& dotnet publish (Join-Path $devDir "src\reStation.Installer\reStation.Installer.csproj") `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -p:DebugSymbols=false `
    -p:InstallerPayloadZip="$payloadZip" -p:ReEngineBootstrapExe="$BootstrapExe" `
    -o $instOut /warnaserror
if ($LASTEXITCODE -ne 0) { throw "dotnet publish reStation.Installer failed ($LASTEXITCODE)" }

$builtInstaller = Get-ChildItem $instOut -Filter *.exe | Select-Object -First 1
if (-not $builtInstaller) { throw "No installer EXE generated under $instOut" }

$installerName = "reStation-Setup-v$Version-win-x64.exe"
$installersDir = Join-Path $root "Installers"
New-Item -ItemType Directory -Force $installersDir | Out-Null
$installerPath = Join-Path $installersDir $installerName
Copy-Item -LiteralPath $builtInstaller.FullName -Destination $installerPath -Force
Invoke-Sign $installerPath

$centralInstallers = Join-Path (Split-Path -Parent (Split-Path -Parent $root)) "Installers"
New-Item -ItemType Directory -Force $centralInstallers | Out-Null
Get-ChildItem $centralInstallers -Filter "reStation-Setup-*.exe" -File | Remove-Item -Force
Copy-Item -LiteralPath $installerPath -Destination (Join-Path $centralInstallers $installerName) -Force

$releaseNotes = (@'
# reStation - Release v__VER__

Reusable clean-room PS1 static recompilation engine, MIPS R3000A + GTE lifter, OpenGL 3.3 / CPU render backends, and modular BYO-media player (`AllowAutoStart = false`).

## Highlights
- Side-by-side `reEngine` track deployment with automatic embedded `reEngine` bootstrap and fail-closed `--test-mode` sandbox containment.
- Cycle-accurate MIPS R3000A CPU, GTE with PGXP precision tracking, MDEC 15/24-bit decode, SPU 24-voice ADSR + XA-ADPCM, and CD-ROM timing.
- Non-single-file win-x64 Player with SDL3 + OpenGL 3.3 / Software parity, save states, key bindings, and `.recomp` compiler CLI.
'@).Replace('__VER__', $Version)
[System.IO.File]::WriteAllText((Join-Path $installersDir "RELEASE_NOTES.md"), $releaseNotes + "`r`n", [System.Text.UTF8Encoding]::new($false))

$auditScript = Join-Path $env:USERPROFILE ".gemini\config\skills\release-packaging\scripts\audit_installer.ps1"
if (-not (Test-Path -LiteralPath $auditScript)) { throw "audit_installer.ps1 not found at $auditScript" }
& powershell -NoProfile -ExecutionPolicy Bypass -File $auditScript -InstallerPath $installersDir -StagingDir $stageDir -ExpectedVersion $Version
if ($LASTEXITCODE -ne 0) { throw "audit_installer.ps1 failed ($LASTEXITCODE)" }

Write-Host "[5/7] Assembling 6-file Releases\v$Version and Releases\Latest..."
$releasesDir = Join-Path $root "Releases"
$relVerDir = Join-Path $releasesDir "v$Version"
$relLatestDir = Join-Path $releasesDir "Latest"
foreach ($d in @($relVerDir, $relLatestDir)) {
    if (Test-Path -LiteralPath $d) { Remove-Item -LiteralPath $d -Recurse -Force }
    New-Item -ItemType Directory -Force $d | Out-Null
}

$guideSections = @((Get-Content (Join-Path $devDir "docs\USER_GUIDE.md") -Raw))
$guideSpokeDir = Join-Path $devDir "docs\user_guide"
if (Test-Path -LiteralPath $guideSpokeDir) {
    Get-ChildItem $guideSpokeDir -Filter *.md -File | Sort-Object Name | ForEach-Object {
        $guideSections += (Get-Content $_.FullName -Raw)
    }
}
$endUserReadme = Sanitize-MarkdownForEndUser ($guideSections -join "`r`n`r`n")
$endUserFeatures = Sanitize-MarkdownForEndUser (Get-Content (Join-Path $root "FEATURES.md") -Raw)
$changelogText = Get-Content (Join-Path $root "CHANGELOG.md") -Raw

foreach ($dir in @($relVerDir, $relLatestDir)) {
    Copy-Item -LiteralPath $installerPath -Destination (Join-Path $dir $installerName) -Force
    [System.IO.File]::WriteAllText((Join-Path $dir "README.md"), $endUserReadme, [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::WriteAllText((Join-Path $dir "FEATURES.md"), $endUserFeatures, [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::WriteAllText((Join-Path $dir "RELEASE_NOTES.md"), $releaseNotes + "`r`n", [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::WriteAllText((Join-Path $dir "CHANGELOG.md"), $changelogText, [System.Text.UTF8Encoding]::new($false))
    $sumLines = foreach ($fn in @($installerName, "README.md", "FEATURES.md", "RELEASE_NOTES.md", "CHANGELOG.md")) {
        $h = (Get-FileHash (Join-Path $dir $fn) -Algorithm SHA256).Hash.ToLowerInvariant()
        "$h  $fn"
    }
    [System.IO.File]::WriteAllText((Join-Path $dir "SHA256SUMS.txt"), ($sumLines -join "`r`n") + "`r`n", [System.Text.Encoding]::ASCII)
}

foreach ($archPath in @((Join-Path $releasesDir "v$Version.7z"), (Join-Path $releasesDir "Latest.7z"))) {
    if (Test-Path -LiteralPath $archPath) { Remove-Item -LiteralPath $archPath -Force }
}
Invoke-7z @("a", "-t7z", "-mx=9", "-mmt=on", (Join-Path $releasesDir "v$Version.7z"), (Join-Path $relVerDir "*"))
Invoke-7z @("a", "-t7z", "-mx=9", "-mmt=on", (Join-Path $releasesDir "Latest.7z"), (Join-Path $relLatestDir "*"))

Write-Host "[6/7] Publishing SharedLibs\reStation\v$Version and Latest..."
$sharedRoot = Join-Path (Split-Path -Parent (Split-Path -Parent $root)) "SharedLibs\reStation"
$contractsOut = Join-Path $devDir "src\reStation.Contracts\bin\Release\net8.0"
$coreOut = Join-Path $devDir "src\reStation.Core\bin\Release\net8.0"
foreach ($sdkDir in @((Join-Path $sharedRoot "v$Version"), (Join-Path $sharedRoot "Latest"))) {
    New-Item -ItemType Directory -Force $sdkDir | Out-Null
    $copiedNames = [System.Collections.Generic.List[string]]::new()
    foreach ($pair in @(
        @{ Dir = $contractsOut; Name = "reStation.Contracts.dll" },
        @{ Dir = $contractsOut; Name = "reStation.Contracts.xml" },
        @{ Dir = $contractsOut; Name = "reStation.Contracts.deps.json" },
        @{ Dir = $coreOut; Name = "reStation.Core.dll" },
        @{ Dir = $coreOut; Name = "reStation.Core.xml" },
        @{ Dir = $coreOut; Name = "reStation.Core.deps.json" },
        @{ Dir = $stageApp; Name = "reStation.Compiler.exe" },
        @{ Dir = $stageApp; Name = "reStation.Compiler.dll" },
        @{ Dir = $stageApp; Name = "reStation.Compiler.deps.json" },
        @{ Dir = $stageApp; Name = "reStation.Compiler.runtimeconfig.json" },
        @{ Dir = $stageApp; Name = "Foundation.Core.dll" },
        @{ Dir = $stageApp; Name = "Foundation.Retro.dll" }
    )) {
        $src = Join-Path $pair.Dir $pair.Name
        if (Test-Path -LiteralPath $src) {
            Copy-Item -LiteralPath $src -Destination (Join-Path $sdkDir $pair.Name) -Force
            $copiedNames.Add($pair.Name)
        }
    }
    Copy-Item -LiteralPath $installerPath -Destination (Join-Path $sdkDir $installerName) -Force
    $copiedNames.Add($installerName)
    $artifacts = $copiedNames | ForEach-Object {
        $p = Join-Path $sdkDir $_
        [ordered]@{
            fileName      = $_
            fileSizeBytes = (Get-Item -LiteralPath $p).Length
            sha256Hex     = (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
    $manifestJson = [ordered]@{
        schemaVersion = 1
        appId         = "restation"
        displayName   = "reStation"
        sdkVersion    = $Version
        version       = $Version
        updatedAtUtc  = [DateTime]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
        artifacts     = $artifacts
    } | ConvertTo-Json -Depth 5
    [System.IO.File]::WriteAllText((Join-Path $sdkDir "sdk-manifest.json"), $manifestJson + "`r`n", [System.Text.UTF8Encoding]::new($false))
}

if (-not $SkipSourceArchive) {
    Write-Host "[7/7] Updating Archive\reStation-v$Version-src.7z..."
    $archiveDir = Join-Path $root "Archive"
    New-Item -ItemType Directory -Force $archiveDir | Out-Null
    $srcArchive = Join-Path $archiveDir "reStation-v$Version-src.7z"
    if (Test-Path -LiteralPath $srcArchive) { Remove-Item -LiteralPath $srcArchive -Force }
    Push-Location $root
    try {
        Invoke-7z @(
            "a", "-t7z", "-mx=9",
            "-xr!bin", "-xr!obj", "-xr!*.user", "-x!Dev\assets\bootstrap\*.exe",
            $srcArchive,
            "Dev", "README.md", "FEATURES.md", "ROADMAP.md", "CHANGELOG.md", "AGENTS.md", "active.state"
        )
    } finally {
        Pop-Location
    }
}

Remove-Item -LiteralPath $workTemp -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "DONE: Build, signed installer, 6-file Releases, SharedLibs SDK, and archive staged for v$Version."
