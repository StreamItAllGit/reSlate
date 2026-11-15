#Requires -Version 5.1
# @module: Dev/scripts/build_release.ps1
# @purpose: Build, test, sign, stage Builds\, package slim Installers\, emit 6-file Releases\, SharedLibs SDK, and source Archive.
# @depends-on: Dev/version.txt, Dev/scripts/sanitize_release_docs.ps1, Dev/src/reStation.slnx, audit_installer.ps1
# @exports: Canonical release pipeline (v0.2.0+)

[CmdletBinding()]
param(
    [string]$Version = "",
    [string]$CertThumbprint = "A5414BDD36F7F113C7B2356F189B7A43F69C35E6",
    [switch]$SkipSourceArchive,
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot "sanitize_release_docs.ps1")

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$devDir = Join-Path $root "Dev"
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = (Read-Utf8Text (Join-Path $devDir "version.txt")).Trim()
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
    if ($lineCount -gt 400) { $locViolations += "$($f.FullName) ($lineCount lines)" }
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
$decompDll = Join-Path $decompOut "reStation.Decomp.dll"
if (-not (Test-Path -LiteralPath $decompDll)) { throw "Required reStation.Decomp.dll missing at $decompDll" }
Copy-Item -LiteralPath $decompDll -Destination (Join-Path $stageApp "reStation.Decomp.dll") -Force
Get-ChildItem $decompOut -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -notin @("ref", "refint") } | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $stageApp $_.Name) -Recurse -Force
}

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

Write-Utf8Text (Join-Path $stageApp "version.txt") "$Version"
$stagedEngineManifest = ([ordered]@{
    schemaVersion         = 1
    engineId              = "reStation"
    appId                 = "restation"
    displayName           = "reStation"
    version               = $Version
    compatibleEngineTrack = "0.2.x"
    supportedEngineTracks = @("0.2.x", "0.1.x")
    platformId            = "ps1"
} | ConvertTo-Json -Depth 4) + "`r`n"
Write-Utf8Text (Join-Path $stageDir "engine.manifest.json") $stagedEngineManifest
Write-Utf8Text (Join-Path $stageApp "engine.manifest.json") $stagedEngineManifest
$rootLicense = Join-Path $root "LICENSE"
if (-not (Test-Path -LiteralPath $rootLicense)) { throw "Root LICENSE file not found at $rootLicense" }
Copy-Item -LiteralPath $rootLicense -Destination (Join-Path $stageApp "LICENSE") -Force

$settingsExample = Join-Path $devDir "data\settings.example.json"
if (Test-Path -LiteralPath $settingsExample) {
    Copy-Item -LiteralPath $settingsExample -Destination (Join-Path $stageDir "settings.example.json") -Force
}

Get-ChildItem $stageDir -Include *.pdb, *.xml -Recurse -File | Remove-Item -Force
Prune-VersionDirectories -ParentDir $buildsRoot -KeepCount 2 -KeepVersionName "v$Version"

$runtimeAgents = (@'
# Runtime Context: reStation (v__VER__)

- **Install Root:** `%LOCALAPPDATA%\Programs\StreamItAll\reEngine\engines\reStation\v__VER__\` (`reStation.exe` -> `app\reStation.Player.exe`)
- **Compiler & `.decomp`:** `app\reStation.Compiler.exe` + `app\reStation.Decomp.dll` (`<dataDir>\cache\` AOT primary; `--jit-bringup` optional fallback)
- **Preload Media:** Supplied by child `.recomp` game modules (`data\rom\`), never bundled inside the engine directory.
- **Data & Logs:** `data\settings.json` (`settings.example.json` reference) and `logs\` unless overridden by `--data-dir`, `--config`, `--log-dir`.
- **Daemon Policy:** Zero background daemons; `AllowAutoStart = false`.
'@).Replace('__VER__', $Version)
Write-Utf8Text (Join-Path $stageDir "AGENTS.md") ($runtimeAgents + "`r`n")
Write-Utf8Text (Join-Path $stageApp "AGENTS.md") ($runtimeAgents + "`r`n")

Invoke-Sign (Join-Path $stageApp "reStation.Player.exe")
Invoke-Sign (Join-Path $stageApp "reStation.Compiler.exe")
Invoke-Sign (Join-Path $stageDir "reStation.exe")

foreach ($cliExe in @((Join-Path $stageApp "reStation.Player.exe"), (Join-Path $stageDir "reStation.exe"))) {
    $helpOut = (& $cliExe --help | Out-String)
    if ($LASTEXITCODE -ne 0) { throw "CLI --help check failed ($LASTEXITCODE) for $cliExe" }
    foreach ($requiredFlag in @("--preload-media", "--wait", "--version")) {
        if ($helpOut -notmatch [regex]::Escape($requiredFlag)) {
            throw "CLI --help parity check failed for ${cliExe}: missing '$requiredFlag'."
        }
    }
    $verOut = (& $cliExe --version | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or ($verOut -notmatch [regex]::Escape("reStation v$Version"))) {
        throw "CLI --version parity check failed ($LASTEXITCODE) for ${cliExe}: '$verOut'"
    }
}

$forbiddenExts = @(".pdb", ".db", ".sqlite", ".sqlite3", ".log")
$badFiles = @(Get-ChildItem $stageDir -Recurse -File | Where-Object {
    ($_.Extension -in $forbiddenExts) -or ($_.Name -ieq ".env") -or ($_.Name -ilike "*secret*.json")
})
if ($badFiles.Count -gt 0) {
    throw "Forbidden staging file(s) detected: $($badFiles.FullName -join ', ')"
}

Write-Host "[4/7] Building, signing & auditing slim single-EXE installer..."
$workTemp = Join-Path $env:TEMP ("restation_pkg_" + $Version)
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

Clean-room MPL-2.0 PS1 static recompilation engine (`reStation.Decomp` + local AOT cache), MIPS R3000A + GTE lifter, OpenGL 3.3 / Software render backends, and modular BYO-media player (`AllowAutoStart = false`).

## Highlights
- Installs per-user under `%LOCALAPPDATA%\Programs\StreamItAll\reEngine\engines\reStation\v__VER__\` with `--profile Lightweight|Full`, `--reengine-dir`, and opt-in `--migrate-legacy` cleanup (zero nested `reEngine-Setup` bloat).
- Clean-room PS1 `.decomp` provider (`reStation.Decomp`) and player-PC `<dataDir>\cache\` AOT compiler with optional `--jit-bringup` / `--force-zero-rwx` diagnostic fallback.
- Live OpenGL 3.3 / Software hot-swap, default 4:3 framing with widescreen hook support, compact HUD strip, mid-stack quick-load, and simultaneous 3-slot keyboard/mouse + controller bindings.
- Clean portable packaging (`app\LICENSE` MPL-2.0 bundled, `settings.example.json`, zero engine `data\rom\` placeholder, sanitized UTF-8 release docs).
'@).Replace('__VER__', $Version)
$sanitizedReleaseNotes = Sanitize-MarkdownForEndUser $releaseNotes
$instReleaseNotesPath = Join-Path $installersDir "RELEASE_NOTES.md"
Write-Utf8Text $instReleaseNotesPath $sanitizedReleaseNotes
Assert-EndUserMarkdownClean $instReleaseNotesPath

$auditScript = Join-Path $env:USERPROFILE ".gemini\config\skills\dev\scripts\audit_installer.ps1"
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

$guideSections = @((Read-Utf8Text (Join-Path $devDir "docs\USER_GUIDE.md")))
$guideSpokeDir = Join-Path $devDir "docs\user_guide"
if (Test-Path -LiteralPath $guideSpokeDir) {
    Get-ChildItem $guideSpokeDir -Filter *.md -File | Sort-Object Name | ForEach-Object {
        $guideSections += (Read-Utf8Text $_.FullName)
    }
}
$endUserReadme = Sanitize-MarkdownForEndUser ($guideSections -join "`r`n`r`n")
$endUserFeatures = Sanitize-MarkdownForEndUser (Read-Utf8Text (Join-Path $root "FEATURES.md"))
$endUserChangelog = Sanitize-MarkdownForEndUser (Read-Utf8Text (Join-Path $root "CHANGELOG.md"))

foreach ($dir in @($relVerDir, $relLatestDir)) {
    Copy-Item -LiteralPath $installerPath -Destination (Join-Path $dir $installerName) -Force
    $relFiles = [ordered]@{
        "README.md"        = $endUserReadme
        "FEATURES.md"      = $endUserFeatures
        "RELEASE_NOTES.md" = $sanitizedReleaseNotes
        "CHANGELOG.md"     = $endUserChangelog
    }
    foreach ($docName in $relFiles.Keys) {
        $outDoc = Join-Path $dir $docName
        Write-Utf8Text $outDoc $relFiles[$docName]
        Assert-EndUserMarkdownClean $outDoc
    }
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
Prune-VersionDirectories -ParentDir $releasesDir -KeepCount 2 -KeepVersionName "v$Version"

Write-Host "[6/7] Publishing SharedLibs\reStation\v$Version and Latest..."
$sharedRoot = Join-Path (Split-Path -Parent (Split-Path -Parent $root)) "SharedLibs\reStation"
$contractsOut = Join-Path $devDir "src\reStation.Contracts\bin\Release\net8.0"
$coreOut = Join-Path $devDir "src\reStation.Core\bin\Release\net8.0"
foreach ($sdkDir in @((Join-Path $sharedRoot "v$Version"), (Join-Path $sharedRoot "Latest"))) {
    if ((Test-Path -LiteralPath $sdkDir) -and ((Split-Path -Leaf $sdkDir) -ine "v0.1.0")) {
        Remove-Item -LiteralPath $sdkDir -Recurse -Force
    }
    New-Item -ItemType Directory -Force $sdkDir | Out-Null
    $copiedNames = [System.Collections.Generic.List[string]]::new()
    foreach ($pair in @(
        @{ Dir = $contractsOut; Name = "reStation.Contracts.dll" },
        @{ Dir = $contractsOut; Name = "reStation.Contracts.xml" },
        @{ Dir = $contractsOut; Name = "reStation.Contracts.deps.json" },
        @{ Dir = $coreOut; Name = "reStation.Core.dll" },
        @{ Dir = $coreOut; Name = "reStation.Core.deps.json" },
        @{ Dir = $decompOut; Name = "reStation.Decomp.dll" },
        @{ Dir = $decompOut; Name = "reStation.Decomp.xml" },
        @{ Dir = $decompOut; Name = "reStation.Decomp.deps.json" },
        @{ Dir = $stageApp; Name = "reStation.Player.exe" },
        @{ Dir = $stageApp; Name = "reStation.Player.dll" },
        @{ Dir = $stageApp; Name = "reStation.Player.deps.json" },
        @{ Dir = $stageApp; Name = "reStation.Player.runtimeconfig.json" },
        @{ Dir = $stageApp; Name = "reStation.Compiler.exe" },
        @{ Dir = $stageApp; Name = "reStation.Compiler.dll" },
        @{ Dir = $stageApp; Name = "reStation.Compiler.deps.json" },
        @{ Dir = $stageApp; Name = "reStation.Compiler.runtimeconfig.json" },
        @{ Dir = $stageApp; Name = "Foundation.Core.dll" },
        @{ Dir = $stageApp; Name = "Foundation.Retro.dll" },
        @{ Dir = $stageApp; Name = "reEngine.Core.dll" }
    )) {
        $src = Join-Path $pair.Dir $pair.Name
        if (Test-Path -LiteralPath $src) {
            Copy-Item -LiteralPath $src -Destination (Join-Path $sdkDir $pair.Name) -Force
            $copiedNames.Add($pair.Name)
        }
    }
    Copy-Item -LiteralPath $installerPath -Destination (Join-Path $sdkDir $installerName) -Force
    $copiedNames.Add($installerName)
    $rootReadme = Join-Path $root "README.md"
    if (-not (Test-Path -LiteralPath $rootReadme)) { throw "Root README.md not found at $rootReadme" }
    Copy-Item -LiteralPath $rootReadme -Destination (Join-Path $sdkDir "README.md") -Force
    $copiedNames.Add("README.md")
    Copy-Item -LiteralPath $rootLicense -Destination (Join-Path $sdkDir "LICENSE") -Force
    $copiedNames.Add("LICENSE")

    $engineManifestJson = [ordered]@{
        schemaVersion         = 1
        engineId              = "reStation"
        appId                 = "restation"
        displayName           = "reStation"
        version               = $Version
        compatibleEngineTrack = "0.2.x"
        supportedEngineTracks = @("0.2.x", "0.1.x")
        platformId            = "ps1"
        updatedAtUtc          = [DateTime]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
    } | ConvertTo-Json -Depth 4
    Write-Utf8Text (Join-Path $sdkDir "engine.manifest.json") ($engineManifestJson + "`r`n")
    $copiedNames.Add("engine.manifest.json")

    $artifacts = $copiedNames | ForEach-Object {
        $p = Join-Path $sdkDir $_
        [ordered]@{ fileName = $_; fileSizeBytes = (Get-Item -LiteralPath $p).Length; sha256Hex = (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
    $manifestJson = [ordered]@{
        schemaVersion = 1; appId = "restation"; displayName = "reStation"; sdkVersion = $Version; version = $Version
        updatedAtUtc = [DateTime]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"); artifacts = $artifacts
    } | ConvertTo-Json -Depth 5
    Write-Utf8Text (Join-Path $sdkDir "sdk-manifest.json") ($manifestJson + "`r`n")
}

if (-not $SkipSourceArchive) {
    Write-Host "[7/7] Updating Archive\reStation-v$Version-src.7z..."
    $archiveDir = Join-Path $root "Archive"
    New-Item -ItemType Directory -Force $archiveDir | Out-Null
    $srcArchive = Join-Path $archiveDir "reStation-v$Version-src.7z"
    if (Test-Path -LiteralPath $srcArchive) { Remove-Item -LiteralPath $srcArchive -Force }
    Push-Location $root
    try {
        Invoke-7z @("a", "-t7z", "-mx=9", "-xr!bin", "-xr!obj", "-xr!*.user", "-x!Dev\assets\bootstrap\*.exe",
            $srcArchive, "Dev", "README.md", "FEATURES.md", "ROADMAP.md", "CHANGELOG.md", "AGENTS.md", "LICENSE", "active.state")
    } finally {
        Pop-Location
    }
}

Remove-Item -LiteralPath $workTemp -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "DONE: Build, signed installer, 6-file Releases, SharedLibs SDK, and archive staged for v$Version."
