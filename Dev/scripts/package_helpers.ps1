#Requires -Version 5.1
# @module: Dev/scripts/package_helpers.ps1
# @purpose: Shared packaging helpers for build_release.ps1 (cert lookup, code-only SLOC gate, binary purity, .ready/manifests, NTFS hardlinks, Portable.zip, SharedLibs, junctions).
# @depends-on: Dev/scripts/sanitize_release_docs.ps1
# @exports: Resolve-CodeSigningCertificate, Invoke-AuthenticodeSign, Invoke-SevenZip, Assert-CodeOnlySlocGate, Assert-BinaryPurity, Assert-InstallerPurity, Write-EngineStageMetadata, Build-EndUserDocMap, Publish-ReleaseBundle, Publish-SharedLibsSdk

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Resolve-CodeSigningCertificate {
    param([string]$ExplicitThumbprint = "")
    $candidate = @($ExplicitThumbprint, $env:CODESIGN_CERT_THUMBPRINT, $env:RESLATE_SIGN_THUMBPRINT) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -First 1
    $stores = @("Cert:\CurrentUser\My", "Cert:\LocalMachine\My")
    if (-not [string]::IsNullOrWhiteSpace($candidate)) {
        $found = Get-ChildItem $stores -ErrorAction SilentlyContinue |
            Where-Object { $_.Thumbprint -ieq $candidate.Trim() } | Select-Object -First 1
        if (-not $found) { throw "Code-signing certificate '$candidate' not found in CurrentUser\My or LocalMachine\My." }
        return $found
    }
    $auto = Get-ChildItem $stores -ErrorAction SilentlyContinue |
        Where-Object {
            if (-not ($_.HasPrivateKey -and ($_.EnhancedKeyUsageList | Where-Object { $_.ObjectId -eq "1.3.6.1.5.5.7.3.3" }) -and $_.NotAfter -gt (Get-Date))) { return $false }
            $chain = New-Object System.Security.Cryptography.X509Certificates.X509Chain
            $chain.Build($_)
        } |
        Sort-Object NotAfter -Descending | Select-Object -First 1
    if (-not $auto) { throw "No trusted Code Signing (EKU 1.3.6.1.5.5.7.3.3) certificate found. Pass -CertThumbprint or set CODESIGN_CERT_THUMBPRINT / RESLATE_SIGN_THUMBPRINT." }
    return $auto
}

function Invoke-AuthenticodeSign($Certificate, [string]$PathToSign) {
    $res = Set-AuthenticodeSignature -Certificate $Certificate -HashAlgorithm SHA256 -FilePath $PathToSign
    if ($res.Status -ne "Valid") {
        throw "Authenticode signing failed for ${PathToSign}: $($res.Status) - $($res.StatusMessage)"
    }
}

function Invoke-SevenZip([string]$SevenZipExe, [string[]]$SevenZipArgs) {
    & $SevenZipExe @SevenZipArgs | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "7z.exe exited with $LASTEXITCODE ($($SevenZipArgs -join ' '))" }
}

function Get-CodeOnlySloc([string]$FilePath) {
    $isPs1 = [System.IO.Path]::GetExtension($FilePath) -ieq ".ps1"
    $inBlock = $false; $sloc = 0
    foreach ($ln in [System.IO.File]::ReadAllLines($FilePath)) {
        $t = $ln.Trim()
        if ($t.Length -eq 0) { continue }
        if ($isPs1) {
            if ($inBlock) { if ($t -match '#>') { $inBlock = $false }; continue }
            if ($t.StartsWith("#")) { continue }
            if ($t.StartsWith("<#")) { if ($t -notmatch '#>') { $inBlock = $true }; continue }
        } else {
            if ($inBlock) { if ($t -match '\*/') { $inBlock = $false }; continue }
            if ($t.StartsWith("//")) { continue }
            if ($t.StartsWith("/*")) { if ($t -notmatch '\*/') { $inBlock = $true }; continue }
        }
        $sloc++
    }
    return $sloc
}

function Assert-CodeOnlySlocGate([string]$DevDir, [int]$MaxCodeSloc = 400) {
    $violations = @()
    $csFiles = @(Get-ChildItem (Join-Path $DevDir "src") -Filter *.cs -Recurse -File |
        Where-Object { $_.FullName -notmatch '\\(?:obj|bin|reStation\.Tests|tests)\\' })
    $psFiles = @(Get-ChildItem (Join-Path $DevDir "scripts") -Filter *.ps1 -Recurse -File)
    foreach ($f in ($csFiles + $psFiles)) {
        $sloc = Get-CodeOnlySloc $f.FullName
        $head = (@(Get-Content -LiteralPath $f.FullName -TotalCount 15) -join "`n")
        $cap = if ($head -match '@loc-allow:') { 600 } else { $MaxCodeSloc }
        if ($sloc -gt $cap) { $violations += "$($f.FullName) ($sloc code SLOC > $cap)" }
    }
    if ($violations.Count -gt 0) { throw "Standard 32 code-only SLOC ceiling violated:`n$($violations -join "`n")" }
}

function Assert-BinaryPurity([string]$AppDir, [string]$LauncherExe) {
    $forbidden = @("Foundation.Automation", "System.Windows.Forms")
    $targets = @("reSlate.Player.dll", "reSlate.Player.exe", "reEngine.Host.dll", "reSlate.Compiler.dll", "reSlate.Decomp.dll") |
        ForEach-Object { Join-Path $AppDir $_ }
    $targets += $LauncherExe
    foreach ($binPath in $targets) {
        if (-not (Test-Path -LiteralPath $binPath)) { throw "Binary missing for purity check: $binPath" }
        $raw = [System.IO.File]::ReadAllBytes($binPath)
        $ascii = [System.Text.Encoding]::ASCII.GetString($raw)
        $utf16 = [System.Text.Encoding]::Unicode.GetString($raw, 0, $raw.Length - ($raw.Length % 2))
        foreach ($tok in $forbidden) {
            if ($ascii.IndexOf($tok, [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
                $utf16.IndexOf($tok, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                throw "Release binary purity violation in '$binPath': found '$tok'."
            }
        }
        $deps = [System.IO.Path]::ChangeExtension($binPath, ".deps.json")
        if (Test-Path -LiteralPath $deps) {
            $depsText = Read-Utf8Text $deps
            foreach ($tok in $forbidden) {
                if ($depsText.IndexOf($tok, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                    throw "Release deps.json purity violation in '$deps': found '$tok'."
                }
            }
        }
    }
    $probeRoot = Join-Path $env:TEMP ("reslate_purity_" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
    $prevIso = $env:APP_ISOLATED_TEST
    $env:APP_ISOLATED_TEST = "1"
    try {
        New-Item -ItemType Directory -Force (Join-Path $probeRoot "data"), (Join-Path $probeRoot "logs") | Out-Null
        $playerExe = Join-Path $AppDir "reSlate.Player.exe"
        $probeProc = Start-Process -FilePath $playerExe -WindowStyle Hidden -Wait -PassThru -ArgumentList @("--automation-port", "5199", "--data-dir", (Join-Path $probeRoot "data"), "--log-dir", (Join-Path $probeRoot "logs"))
        if ($probeProc.ExitCode -ne 2) { throw "Release purity gate failed: expected ExitCode=2 for --automation-port, got $($probeProc.ExitCode)." }
    } finally {
        if ($null -eq $prevIso) { Remove-Item Env:APP_ISOLATED_TEST -ErrorAction SilentlyContinue } else { $env:APP_ISOLATED_TEST = $prevIso }
        Remove-Item -LiteralPath $probeRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Assert-InstallerPurity([string]$InstallerExe) {
    if (-not (Test-Path -LiteralPath $InstallerExe)) { throw "Installer missing for purity check: $InstallerExe" }
    $raw = [System.IO.File]::ReadAllBytes($InstallerExe)
    $ascii = [System.Text.Encoding]::ASCII.GetString($raw)
    $utf16 = [System.Text.Encoding]::Unicode.GetString($raw, 0, $raw.Length - ($raw.Length % 2))
    foreach ($tok in @("Foundation.Automation", "InstallerAutomationHook")) {
        if ($ascii.IndexOf($tok, [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $utf16.IndexOf($tok, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            throw "Release installer purity violation in '$InstallerExe': found '$tok'."
        }
    }
    $prevIso = $env:APP_ISOLATED_TEST
    $env:APP_ISOLATED_TEST = "1"
    try {
        $probeProc = Start-Process -FilePath $InstallerExe -WindowStyle Hidden -Wait -PassThru -ArgumentList @("--automation-port", "5199")
        if ($probeProc.ExitCode -ne 2) { throw "Release installer purity gate failed: expected ExitCode=2 for --automation-port, got $($probeProc.ExitCode)." }
    } finally {
        if ($null -eq $prevIso) { Remove-Item Env:APP_ISOLATED_TEST -ErrorAction SilentlyContinue } else { $env:APP_ISOLATED_TEST = $prevIso }
    }
}

function Write-EngineStageMetadata([string]$StageDir, [string]$StageApp, [string]$Version) {
    Write-Utf8Text (Join-Path $StageApp "version.txt") $Version
    $nowUtc = [DateTime]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
    $readyJson = ([ordered]@{ engineId = "reSlate"; appId = "reslate"; version = $Version; completedAtUtc = $nowUtc } | ConvertTo-Json -Depth 3) + "`r`n"
    Write-Utf8Text (Join-Path $StageDir ".ready") $readyJson
    foreach ($pair in @(
        @{ Path = (Join-Path $StageDir "engine.manifest.json"); Decomp = "app/reSlate.Decomp.dll" },
        @{ Path = (Join-Path $StageApp "engine.manifest.json"); Decomp = "reSlate.Decomp.dll" }
    )) {
        $m = ([ordered]@{
            schemaVersion = 1; engineId = "reSlate"; appId = "reslate"; displayName = "reSlate"; version = $Version
            entryExecutable = "reSlate.exe"; compatibleEngineTrack = "0.3.x"; supportedEngineTracks = @("0.3.x", "0.2.x", "0.1.x")
            decompProviderAssembly = $pair.Decomp; platformId = "ps1"; updatedAtUtc = $nowUtc
        } | ConvertTo-Json -Depth 4) + "`r`n"
        Write-Utf8Text $pair.Path $m
    }
}

function Build-EndUserDocMap([string]$RootDir, [string]$DevDir, [string]$Version) {
    $docsDir = Join-Path $DevDir "docs"
    $guideSections = @((Read-Utf8Text (Join-Path $docsDir "USER_GUIDE.md")))
    Get-ChildItem $docsDir -Filter "USER_GUIDE.*.md" -File -ErrorAction SilentlyContinue | Sort-Object Name | ForEach-Object {
        $guideSections += (Read-Utf8Text $_.FullName)
    }
    $rawNotes = (@'
# reSlate - Release v__VER__

Clean-room MPL-2.0 SlateOne static recompilation engine (`reSlate.Decomp` + local AOT cache), MIPS32 R3000A + GTE lifter, OpenGL 3.3 / Software render backends, and modular BYO-media runtime (`AllowAutoStart = false`).

## Highlights
- Installs per-user under `%LOCALAPPDATA%\Programs\StreamItAll\reEngine\engines\reSlate\v__VER__\` with `.ready` discovery marker, `reSlate.exe`, `--profile Lightweight|Full`, `--reengine-dir`, and opt-in `--migrate-legacy`.
- Clean-room SlateOne `.decomp` provider (`reSlate.Decomp.dll`) with `D4_CleanSource` 6-tell / `-Wall -Wextra -Wno-unused-parameter` validation and player-PC `<dataDir>\cache\` AOT compiler + live Tier 3 JIT/IR.
- Unified `SIO0 0x81` + BIOS `B0:0x4E/0x4F/0x50` `_new_card` Slot 1+2 `.mcr` persistence, `60 Hz` SPU lock (`44.1 kHz`) with `VSync(n)`/`TargetFps` cadence, and single-edge menu input.
- `reEngine.Host.Contracts` alignment (`reEngine.Host.dll`), canonical `data\config\settings.json`, 7-file `Portable.zip` release bundle, and zero-automation `Release` binaries (`ExitCode = 2`).
'@).Replace('__VER__', $Version)
    return [ordered]@{
        "README.md"        = Sanitize-MarkdownForEndUser ($guideSections -join "`r`n`r`n")
        "FEATURES.md"      = Sanitize-MarkdownForEndUser (Read-Utf8Text (Join-Path $RootDir "FEATURES.md"))
        "RELEASE_NOTES.md" = Sanitize-MarkdownForEndUser $rawNotes
        "CHANGELOG.md"     = Sanitize-MarkdownForEndUser (Read-Utf8Text (Join-Path $RootDir "CHANGELOG.md"))
    }
}

function Ensure-ReleaseHubJunctions([string]$RootDir, [string]$ReleasesDir) {
    $groupDir = Split-Path -Parent $RootDir
    $projectsRoot = Split-Path -Parent $groupDir
    $hubParent = Join-Path (Join-Path $projectsRoot "Releases") (Split-Path -Leaf $groupDir)
    New-Item -ItemType Directory -Force $hubParent | Out-Null
    foreach ($name in @("reStation", "reSlate")) {
        $jPath = Join-Path $hubParent $name
        if (Test-Path -LiteralPath $jPath) {
            $jItem = Get-Item -LiteralPath $jPath -Force
            $isReparse = ($jItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0
            $target = ($jItem.Target | Select-Object -First 1)
            if ($isReparse -and $target -and ($target.TrimEnd('\') -ieq $ReleasesDir.TrimEnd('\'))) { continue }
            if ($isReparse) { cmd /c rmdir "`"$jPath`"" | Out-Null } else { Remove-Item -LiteralPath $jPath -Recurse -Force }
        }
        & cmd /c mklink /J "`"$jPath`"" "`"$ReleasesDir`"" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Failed creating hub junction '$jPath' -> '$ReleasesDir'" }
    }
}

function Publish-ReleaseBundle([string]$RootDir, [string]$ReleasesDir, [string]$Version, [string]$BuiltInstallerPath, [string]$StageDir, [string]$WorkTemp, $DocMap, [string]$SevenZipExe, $Cert) {
    $relVerDir = Join-Path $ReleasesDir "v$Version"
    $relLatestDir = Join-Path $ReleasesDir "Latest"
    foreach ($d in @($relVerDir, $relLatestDir)) {
        if (Test-Path -LiteralPath $d) { Remove-Item -LiteralPath $d -Recurse -Force }
        New-Item -ItemType Directory -Force $d | Out-Null
    }
    $installerName = "reSlate-Setup-v$Version-win-x64.exe"
    $installerPath = Join-Path $relVerDir $installerName
    Copy-Item -LiteralPath $BuiltInstallerPath -Destination $installerPath -Force
    Invoke-AuthenticodeSign $Cert $installerPath
    foreach ($kv in $DocMap.GetEnumerator()) {
        $outDoc = Join-Path $relVerDir $kv.Key
        Write-Utf8Text $outDoc $kv.Value
        Assert-EndUserMarkdownClean $outDoc
    }
    $portableZipName = "reSlate-v$Version-win-x64-Portable.zip"
    $portableZipPath = Join-Path $relVerDir $portableZipName
    $portableStage = Join-Path $WorkTemp "portable_stage"
    if (Test-Path -LiteralPath $portableStage) { Remove-Item -LiteralPath $portableStage -Recurse -Force }
    New-Item -ItemType Directory -Force $portableStage | Out-Null
    Copy-Item -Path (Join-Path $StageDir "*") -Destination $portableStage -Recurse -Force
    foreach ($kv in $DocMap.GetEnumerator()) { Write-Utf8Text (Join-Path $portableStage $kv.Key) $kv.Value }
    $badPortable = @(Get-ChildItem -Path $portableStage -Recurse -File | Where-Object { $_.Name -ilike "*-Setup-*.exe" -or $_.Name -ieq "AGENTS.md" })
    if ($badPortable.Count -gt 0) { throw "Portable staging violation: $($badPortable.FullName -join ', ')" }
    [System.IO.Compression.ZipFile]::CreateFromDirectory($portableStage, $portableZipPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)

    $allNames = @($installerName, $portableZipName, "README.md", "FEATURES.md", "RELEASE_NOTES.md", "CHANGELOG.md")
    $sumLines = foreach ($fn in $allNames) { "$((Get-FileHash (Join-Path $relVerDir $fn) -Algorithm SHA256).Hash.ToLowerInvariant())  $fn" }
    [System.IO.File]::WriteAllText((Join-Path $relVerDir "SHA256SUMS.txt"), ($sumLines -join "`r`n") + "`r`n", [System.Text.Encoding]::ASCII)
    foreach ($fn in ($allNames + @("SHA256SUMS.txt"))) {
        $src = Join-Path $relVerDir $fn; $dst = Join-Path $relLatestDir $fn
        try { New-Item -ItemType HardLink -Path $dst -Target $src | Out-Null } catch { Copy-Item -LiteralPath $src -Destination $dst -Force }
    }
    foreach ($pair in @(@{ Zip = "v$Version.7z"; Dir = $relVerDir }, @{ Zip = "Latest.7z"; Dir = $relLatestDir })) {
        $arch = Join-Path $ReleasesDir $pair.Zip
        if (Test-Path -LiteralPath $arch) { Remove-Item -LiteralPath $arch -Force }
        Invoke-SevenZip $SevenZipExe @("a", "-t7z", "-mx=9", "-mmt=on", $arch, (Join-Path $pair.Dir "*"))
    }
    Prune-VersionDirectories -ParentDir $ReleasesDir -KeepCount 2 -KeepVersionName "v$Version" -KeepArchiveMinors 3
    Ensure-ReleaseHubJunctions -RootDir $RootDir -ReleasesDir $ReleasesDir
    return @{ RelVerDir = $relVerDir; InstallerPath = $installerPath }
}

function Publish-SharedLibsSdk([string]$RootDir, [string]$DevDir, [string]$StageApp, [string]$InstallerPath, [string]$Version) {
    $sharedRoot = Join-Path (Split-Path -Parent (Split-Path -Parent $RootDir)) "SharedLibs\reSlate"
    $contractsOut = Join-Path $DevDir "src\reStation.Contracts\bin\Release\net8.0"
    $coreOut = Join-Path $DevDir "src\reStation.Core\bin\Release\net8.0"
    $decompOut = Join-Path $DevDir "src\reStation.Decomp\bin\Release\net8.0"
    $installerName = [System.IO.Path]::GetFileName($InstallerPath)
    $sources = @(
        @{ Dir = $contractsOut; Names = @("reStation.Contracts.dll", "reStation.Contracts.xml", "reStation.Contracts.deps.json") },
        @{ Dir = $coreOut; Names = @("reStation.Core.dll", "reStation.Core.deps.json") },
        @{ Dir = $decompOut; Names = @("reSlate.Decomp.dll", "reSlate.Decomp.xml", "reSlate.Decomp.deps.json") },
        @{ Dir = $StageApp; Names = @("reSlate.Player.exe", "reSlate.Player.dll", "reSlate.Player.deps.json", "reSlate.Player.runtimeconfig.json",
            "reSlate.Compiler.exe", "reSlate.Compiler.dll", "reSlate.Compiler.deps.json", "reSlate.Compiler.runtimeconfig.json",
            "Foundation.Core.dll", "Foundation.Retro.dll", "reEngine.Core.dll", "reEngine.Host.dll", "SDL3.dll") }
    )
    foreach ($sdkDir in @((Join-Path $sharedRoot "v$Version"), (Join-Path $sharedRoot "Latest"))) {
        if (Test-Path -LiteralPath $sdkDir) { Remove-Item -LiteralPath $sdkDir -Recurse -Force }
        New-Item -ItemType Directory -Force $sdkDir | Out-Null
        $copied = [System.Collections.Generic.List[string]]::new()
        foreach ($srcGroup in $sources) {
            foreach ($fn in $srcGroup.Names) {
                $src = Join-Path $srcGroup.Dir $fn
                if (Test-Path -LiteralPath $src) { Copy-Item -LiteralPath $src -Destination (Join-Path $sdkDir $fn) -Force; $copied.Add($fn) }
            }
        }
        Copy-Item -LiteralPath $InstallerPath -Destination (Join-Path $sdkDir $installerName) -Force; $copied.Add($installerName)
        Copy-Item -LiteralPath (Join-Path $RootDir "README.md") -Destination (Join-Path $sdkDir "README.md") -Force; $copied.Add("README.md")
        Copy-Item -LiteralPath (Join-Path $RootDir "LICENSE") -Destination (Join-Path $sdkDir "LICENSE") -Force; $copied.Add("LICENSE")
        $nowUtc = [DateTime]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
        $engJson = ([ordered]@{
            schemaVersion = 1; engineId = "reSlate"; appId = "reslate"; displayName = "reSlate"; version = $Version
            entryExecutable = "reSlate.exe"; compatibleEngineTrack = "0.3.x"; supportedEngineTracks = @("0.3.x", "0.2.x", "0.1.x")
            decompProviderAssembly = "reSlate.Decomp.dll"; platformId = "ps1"; updatedAtUtc = $nowUtc
        } | ConvertTo-Json -Depth 4) + "`r`n"
        Write-Utf8Text (Join-Path $sdkDir "engine.manifest.json") $engJson; $copied.Add("engine.manifest.json")
        $artifacts = @($copied | ForEach-Object { $p = Join-Path $sdkDir $_; [ordered]@{ fileName = $_; fileSizeBytes = (Get-Item -LiteralPath $p).Length; sha256Hex = (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant() } })
        $sdkJson = ([ordered]@{ schemaVersion = 1; appId = "reslate"; displayName = "reSlate"; sdkVersion = $Version; version = $Version; updatedAtUtc = $nowUtc; artifacts = $artifacts } | ConvertTo-Json -Depth 5) + "`r`n"
        Write-Utf8Text (Join-Path $sdkDir "sdk-manifest.json") $sdkJson
    }
}
