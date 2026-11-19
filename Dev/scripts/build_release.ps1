#Requires -Version 5.1
# @module: Dev/scripts/build_release.ps1
# @purpose: Build, test, sign, verify purity, stage Builds\, package 7-file NTFS-hardlinked Releases\, SharedLibs\reSlate, and source Archive.
# @depends-on: Dev/version.txt, Dev/scripts/sanitize_release_docs.ps1, Dev/scripts/package_helpers.ps1, Dev/src/reStation.slnx, audit_installer.ps1
# @exports: Canonical release pipeline (v0.3.0+)

[CmdletBinding()]
param(
    [string]$Version = "",
    [string]$CertThumbprint = "",
    [string]$AuditScriptPath = "",
    [switch]$SkipSourceArchive,
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot "sanitize_release_docs.ps1")
. (Join-Path $PSScriptRoot "package_helpers.ps1")

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$devDir = Join-Path $root "Dev"
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = (Read-Utf8Text (Join-Path $devDir "version.txt")).Trim()
}

Get-ChildItem -Path $root -Recurse -Force -ErrorAction SilentlyContinue |
    Where-Object { -not $_.PSIsContainer -and ($_.Name -in @("desktop.ini", "Thumbs.db", "ehthumbs.db", ".DS_Store") -or $_.Name -like "._*") } |
    ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue }

$sevenZip = Join-Path $devDir "assets\native\7z.exe"
if (-not (Test-Path -LiteralPath $sevenZip)) { throw "7z.exe not found at $sevenZip" }
$cert = Resolve-CodeSigningCertificate -ExplicitThumbprint $CertThumbprint

Write-Host "[1/7] Enforcing Standard 32 code-only SLOC gate (<= 400 SLOC; Tests\ exempt)..."
Assert-CodeOnlySlocGate -DevDir $devDir -MaxCodeSloc 400

Write-Host "[2/7] Building pure Release solution (/warnaserror) and running Release tests..."
$slnxPath = Join-Path $devDir "src\reStation.slnx"
& dotnet build $slnxPath -c Release /warnaserror --disable-build-servers -p:UseSharedCompilation=false
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed ($LASTEXITCODE)" }

if (-not $SkipTests) {
    $prevIsolatedTest = $env:APP_ISOLATED_TEST
    $env:APP_ISOLATED_TEST = "1"
    try {
        & dotnet test $slnxPath -c Release --no-build --logger "console;verbosity=minimal"
        if ($LASTEXITCODE -ne 0) { throw "dotnet test failed ($LASTEXITCODE)" }
    } finally {
        if ($null -eq $prevIsolatedTest) { Remove-Item Env:APP_ISOLATED_TEST -ErrorAction SilentlyContinue } else { $env:APP_ISOLATED_TEST = $prevIsolatedTest }
    }
}

Write-Host "[3/7] Staging portable build in Builds\v$Version..."
$buildsRoot = Join-Path $root "Builds"
$stageDir = Join-Path $buildsRoot "v$Version"
$stageApp = Join-Path $stageDir "app"
if (Test-Path -LiteralPath $stageDir) { Remove-Item -LiteralPath $stageDir -Recurse -Force }
New-Item -ItemType Directory -Force $stageApp | Out-Null

foreach ($projRel in @("src\reStation.Player\reStation.Player.csproj", "src\reStation.Compiler\reStation.Compiler.csproj")) {
    & dotnet publish (Join-Path $devDir $projRel) `
        -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false `
        -p:DebugType=none -p:DebugSymbols=false -o $stageApp /warnaserror
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish $projRel failed ($LASTEXITCODE)" }
}

$decompOut = Join-Path $devDir "src\reStation.Decomp\bin\Release\net8.0"
$decompDll = Join-Path $decompOut "reSlate.Decomp.dll"
if (-not (Test-Path -LiteralPath $decompDll)) { throw "Required reSlate.Decomp.dll missing at $decompDll" }
Copy-Item -LiteralPath $decompDll -Destination (Join-Path $stageApp "reSlate.Decomp.dll") -Force
Get-ChildItem $decompOut -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -notin @("ref", "refint") } | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $stageApp $_.Name) -Recurse -Force
}

$hostDst = Join-Path $stageApp "reEngine.Host.dll"
$hostSrc = Join-Path $devDir "lib\reEngine.Host.dll"
if ((-not (Test-Path -LiteralPath $hostDst)) -and (Test-Path -LiteralPath $hostSrc)) {
    Copy-Item -LiteralPath $hostSrc -Destination $hostDst -Force
}
if (-not (Test-Path -LiteralPath $hostDst)) { throw "Required reEngine.Host.dll missing in $stageApp" }

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

$launcherStage = Join-Path $env:TEMP ("reslate_launcher_" + $Version)
if (Test-Path -LiteralPath $launcherStage) { Remove-Item -LiteralPath $launcherStage -Recurse -Force }
& dotnet publish (Join-Path $devDir "src\reStation.Launcher\reStation.Launcher.csproj") `
    -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true `
    -p:DebugType=none -p:DebugSymbols=false -o $launcherStage /warnaserror
if ($LASTEXITCODE -ne 0) { throw "dotnet publish reStation.Launcher failed ($LASTEXITCODE)" }
Copy-Item (Join-Path $launcherStage "reSlate.exe") (Join-Path $stageDir "reSlate.exe") -Force
Remove-Item -LiteralPath $launcherStage -Recurse -Force -ErrorAction SilentlyContinue

Write-EngineStageMetadata -StageDir $stageDir -StageApp $stageApp -Version $Version
$rootLicense = Join-Path $root "LICENSE"
if (-not (Test-Path -LiteralPath $rootLicense)) { throw "Root LICENSE file not found at $rootLicense" }
Copy-Item -LiteralPath $rootLicense -Destination (Join-Path $stageApp "LICENSE") -Force

$settingsExample = Join-Path $devDir "data\settings.example.json"
if (Test-Path -LiteralPath $settingsExample) {
    Copy-Item -LiteralPath $settingsExample -Destination (Join-Path $stageDir "settings.example.json") -Force
}

Get-ChildItem $stageDir -Include *.pdb, *.xml -Recurse -File | Remove-Item -Force
Prune-VersionDirectories -ParentDir $buildsRoot -KeepCount 2 -KeepVersionName "v$Version"

Invoke-AuthenticodeSign $cert (Join-Path $stageApp "reSlate.Player.exe")
Invoke-AuthenticodeSign $cert (Join-Path $stageApp "reSlate.Compiler.exe")
Invoke-AuthenticodeSign $cert (Join-Path $stageDir "reSlate.exe")

Assert-BinaryPurity -AppDir $stageApp -LauncherExe (Join-Path $stageDir "reSlate.exe")

foreach ($cliExe in @((Join-Path $stageApp "reSlate.Player.exe"), (Join-Path $stageDir "reSlate.exe"))) {
    $helpOut = (& $cliExe --help | Out-String)
    if ($LASTEXITCODE -ne 0) { throw "CLI --help check failed ($LASTEXITCODE) for $cliExe" }
    foreach ($requiredFlag in @("--preload-media", "--wait", "--version", "--require-backend", "--doctor")) {
        if ($helpOut -notmatch [regex]::Escape($requiredFlag)) {
            throw "CLI --help parity check failed for ${cliExe}: missing '$requiredFlag'."
        }
    }
    $verOut = (& $cliExe --version | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or ($verOut -notmatch [regex]::Escape("reSlate v$Version"))) {
        throw "CLI --version parity check failed ($LASTEXITCODE) for ${cliExe}: '$verOut'"
    }
}

$forbiddenExts = @(".pdb", ".db", ".sqlite", ".sqlite3", ".log")
$badFiles = @(Get-ChildItem $stageDir -Recurse -File | Where-Object {
    ($_.Extension -in $forbiddenExts) -or ($_.Name -ieq ".env") -or ($_.Name -ilike "*secret*.json") -or ($_.Name -ieq "AGENTS.md")
})
if ($badFiles.Count -gt 0) { throw "Forbidden staging file(s) detected: $($badFiles.FullName -join ', ')" }

Write-Host "[4/7] Building slim single-EXE installer..."
$workTemp = Join-Path $env:TEMP ("reslate_pkg_" + $Version)
if (Test-Path -LiteralPath $workTemp) { Remove-Item -LiteralPath $workTemp -Recurse -Force }
New-Item -ItemType Directory -Force $workTemp | Out-Null
$payloadZip = Join-Path $workTemp "payload.zip"
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stageDir, $payloadZip, [System.IO.Compression.CompressionLevel]::Optimal, $false)

$instOut = Join-Path $workTemp "installer_out"
& dotnet publish (Join-Path $devDir "src\reStation.Installer\reStation.Installer.csproj") `
    -c Release -r win-x64 --self-contained true `
    -p:Version="$Version" `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -p:DebugSymbols=false `
    -p:InstallerPayloadZip="$payloadZip" `
    -o $instOut /warnaserror
if ($LASTEXITCODE -ne 0) { throw "dotnet publish reStation.Installer failed ($LASTEXITCODE)" }
$builtInstaller = Get-ChildItem $instOut -Filter *.exe | Select-Object -First 1
if (-not $builtInstaller) { throw "No installer EXE generated under $instOut" }

Write-Host "[5/7] Assembling 7-file Releases\v$Version (+ Portable.zip), NTFS hardlinks & auditing..."
$releasesDir = Join-Path $root "Releases"
$docMap = Build-EndUserDocMap -RootDir $root -DevDir $devDir -Version $Version
$relBundle = Publish-ReleaseBundle -RootDir $root -ReleasesDir $releasesDir -Version $Version `
    -BuiltInstallerPath $builtInstaller.FullName -StageDir $stageDir -WorkTemp $workTemp `
    -DocMap $docMap -SevenZipExe $sevenZip -Cert $cert
Assert-InstallerPurity -InstallerExe $relBundle.InstallerPath

$resolvedAuditScript = if (-not [string]::IsNullOrWhiteSpace($AuditScriptPath)) { $AuditScriptPath } else { Join-Path $env:USERPROFILE ".gemini\config\skills\dev\scripts\audit_installer.ps1" }
if (-not [string]::IsNullOrWhiteSpace($AuditScriptPath) -and (-not (Test-Path -LiteralPath $resolvedAuditScript))) {
    throw "Specified -AuditScriptPath not found: $resolvedAuditScript"
}
if (Test-Path -LiteralPath $resolvedAuditScript) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File $resolvedAuditScript -InstallerPath $relBundle.RelVerDir -StagingDir $stageDir -ExpectedVersion $Version
    if ($LASTEXITCODE -ne 0) { throw "audit_installer.ps1 failed ($LASTEXITCODE)" }
} else {
    Write-Warning "audit_installer.ps1 not found at '$resolvedAuditScript'; skipping optional audit step."
}

Write-Host "[6/7] Publishing SharedLibs\reSlate\v$Version and Latest..."
Publish-SharedLibsSdk -RootDir $root -DevDir $devDir -StageApp $stageApp -InstallerPath $relBundle.InstallerPath -Version $Version

if (-not $SkipSourceArchive) {
    Write-Host "[7/7] Updating Archive\reSlate-v$Version-src.7z..."
    $archiveDir = Join-Path $root "Archive"
    New-Item -ItemType Directory -Force $archiveDir | Out-Null
    $srcArchive = Join-Path $archiveDir "reSlate-v$Version-src.7z"
    if (Test-Path -LiteralPath $srcArchive) { Remove-Item -LiteralPath $srcArchive -Force }
    Push-Location $root
    try {
        Invoke-SevenZip $sevenZip @("a", "-t7z", "-mx=9", "-xr!bin", "-xr!obj", "-xr!TestResults", "-xr!data", "-xr!logs",
            "-xr!.vs", "-xr!*.user", "-xr!desktop.ini", "-xr!Thumbs.db", "-xr!ehthumbs.db", "-xr!.DS_Store", "-xr!._*",
            "-x!Dev\assets\bootstrap\*.exe", $srcArchive, "Dev", "README.md", "FEATURES.md", "ROADMAP.md", "CHANGELOG.md",
            "AGENTS.md", "LICENSE", "SECURITY.md", "CONTRIBUTING.md", "active.state")
    } finally {
        Pop-Location
    }
    Prune-MinorVersionArchives -ArchiveDir $archiveDir -KeepMinorCount 3 -KeepVersionName "v$Version"
}

Remove-Item -LiteralPath $workTemp -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "DONE: Pure Release build, signed installer, 7-file NTFS-hardlinked Releases, SharedLibs\reSlate, and archive staged for v$Version."
