#Requires -Version 5.1
# @module: Dev/scripts/package_helpers.ps1
# @purpose: Shared packaging helpers for build_release.ps1 (cert lookup, code-only SLOC gate, binary purity, .ready/manifests, NTFS hardlinks, Portable.zip, SharedLibs, junctions).
# @depends-on: Dev/scripts/sanitize_release_docs.ps1
# @exports: Resolve-CodeSigningCertificate, Invoke-AuthenticodeSign, Invoke-SevenZip, Assert-CodeOnlySlocGate, Assert-DirectoryAssembliesClean, Assert-BinaryPurity, Assert-InstallerPurity, New-EngineManifestJson, Write-EngineStageMetadata, Build-EndUserDocMap, Publish-ReleaseBundle, Assert-FrameworkDependentRuntimeConfig, Test-SharedLibsCompilerSmoke, Publish-SharedLibsSdk

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

function Assert-DirectoryAssembliesClean([string]$TargetDir, [string[]]$TargetFiles, [string[]]$ForbiddenTokens = @("Foundation.Automation", "System.Windows.Forms", "Foundation.UI")) {
    $badNames = @(Get-ChildItem -LiteralPath $TargetDir -Recurse -File -ErrorAction SilentlyContinue | Where-Object {
        $_.Name -like "*Foundation.Automation*" -or $_.Name -like "*System.Windows.Forms*" -or $_.Name -like "*Foundation.UI*"
    })
    if ($badNames.Count -gt 0) {
        throw "Forbidden assembly file(s) detected in '$TargetDir': $($badNames.FullName -join ', ')"
    }
    foreach ($binPath in $TargetFiles) {
        if (-not (Test-Path -LiteralPath $binPath)) { throw "Binary missing for purity check: $binPath" }
        $raw = [System.IO.File]::ReadAllBytes($binPath)
        $ascii = [System.Text.Encoding]::ASCII.GetString($raw)
        $utf16 = [System.Text.Encoding]::Unicode.GetString($raw, 0, $raw.Length - ($raw.Length % 2))
        foreach ($tok in $ForbiddenTokens) {
            if ($ascii.IndexOf($tok, [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
                $utf16.IndexOf($tok, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                throw "Release binary purity violation in '$binPath': found '$tok'."
            }
        }
        $deps = [System.IO.Path]::ChangeExtension($binPath, ".deps.json")
        if (Test-Path -LiteralPath $deps) {
            $depsText = Read-Utf8Text $deps
            foreach ($tok in $ForbiddenTokens) {
                if ($depsText.IndexOf($tok, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                    throw "Release deps.json purity violation in '$deps': found '$tok'."
                }
            }
        }
    }
}

function Assert-BinaryPurity([string]$AppDir, [string]$LauncherExe) {
    $targets = @("reSlate.Player.dll", "reSlate.Player.exe", "reEngine.Host.dll", "reSlate.Compiler.dll",
        "reSlate.Compiler.exe", "reSlate.Decomp.dll", "reStation.Contracts.dll", "reStation.Core.dll",
        "Foundation.Core.dll", "Foundation.Retro.dll", "reEngine.Core.dll") | ForEach-Object { Join-Path $AppDir $_ }
    $targets += $LauncherExe
    Assert-DirectoryAssembliesClean -TargetDir $AppDir -TargetFiles $targets
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

function New-EngineManifestJson([string]$Version, [string]$DecompAssembly, [string]$UpdatedAtUtc) {
    return ([ordered]@{
        '$schema' = "https://schemas.local/manifest.v2.json"
        schemaVersion = 2
        manifestType = "standalone-app"
        appId = "reslate"
        engineId = "reSlate"
        displayName = "reSlate"
        version = $Version
        updatedAtUtc = $UpdatedAtUtc
        suiteId = "reEngine"
        hostProtocolVersion = "0.5.0"
        minSuiteVersion = "0.5.0"
        entryExecutable = "reSlate.exe"
        compatibleEngineTrack = "0.3.x"
        supportedEngineTracks = @("0.3.x", "0.2.x", "0.1.x")
        decompProviderAssembly = $DecompAssembly
        platformId = "ps1"
    } | ConvertTo-Json -Depth 4) + "`r`n"
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
        Write-Utf8Text $pair.Path (New-EngineManifestJson -Version $Version -DecompAssembly $pair.Decomp -UpdatedAtUtc $nowUtc)
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

Clean-room MPL-2.0 SlateOne static recompilation engine (`reSlate.Decomp` + local AOT cache), MIPS32 R3000A + GTE lifter, Vulkan 1.3 / ParallelCPU / OpenGL 3.3 / Software render backends, Enhancement SDK (`hdpacks\` + Sub-Tick Interpolation), and modular BYO-media runtime (`AllowAutoStart = false`).

## Highlights
- Installs per-user under `%LOCALAPPDATA%\Programs\StreamItAll\reEngine\engines\reSlate\v__VER__\` (`suiteId: reEngine`) with `.ready` discovery marker, `reSlate.exe`, `--profile Lightweight|Full`, `--reengine-dir`, and opt-in `--migrate-legacy`.
- **Vulkan & Deterministic ParallelCPU Backends:** Hardware `Vulkan` backend alongside bit-exact `ParallelCPU`, `OpenGL 3.3`, and `Software` renderers, live `--doctor` probe, `HostViewportGeometry` aspect modes (`--aspect-mode`), and `overscanCropPx`.
- **Enhancement SDK & QoL Controls:** Clean-room `<dataDir>\hdpacks\` PNG texture overrides (`--hd-packs`), `ISubTickFrameHook` interpolation, audio-safe Fast-Forward (`F6` / `--fast-forward`), and Cutscene Skip (`F7`).
- **Framework-Dependent SDK & Clean Binaries:** `SharedLibs` compiler/player tools ship framework-dependent, zero-automation `Release` binaries (`ExitCode = 2`), and 7-file `Portable.zip` release bundle.

## Download Mirrors & Formats
- **Setup Installer:** `reSlate-Setup-v__VER__-win-x64.exe` *(Signed Setup Wizard & silent CLI installer)*
- **Portable Bundle:** `reSlate-v__VER__-win-x64-Portable.zip` *(Standalone no-installer release)*
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

function Publish-ReleaseBundle([string]$RootDir, [string]$ReleasesDir, [string]$Version, [string]$BuiltInstallerPath, [string]$StageDir, [string]$WorkTemp, $DocMap, [string]$SevenZipExe, $Cert, [switch]$SkipSigning) {
    $relVerDir = Join-Path $ReleasesDir "v$Version"
    $relLatestDir = Join-Path $ReleasesDir "Latest"
    foreach ($d in @($relVerDir, $relLatestDir)) {
        if (Test-Path -LiteralPath $d) { Remove-Item -LiteralPath $d -Recurse -Force }
        New-Item -ItemType Directory -Force $d | Out-Null
    }
    $installerName = "reSlate-Setup-v$Version-win-x64.exe"
    $installerPath = Join-Path $relVerDir $installerName
    Copy-Item -LiteralPath $BuiltInstallerPath -Destination $installerPath -Force
    if (-not $SkipSigning) { Invoke-AuthenticodeSign $Cert $installerPath }
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

function Assert-FrameworkDependentRuntimeConfig([string]$ConfigPath) {
    if (-not (Test-Path -LiteralPath $ConfigPath)) { throw "Missing runtimeconfig.json: $ConfigPath" }
    $cfg = (Read-Utf8Text $ConfigPath) | ConvertFrom-Json
    $ro = $cfg.runtimeOptions
    if ($null -eq $ro -or $ro.PSObject.Properties["includedFrameworks"]) {
        throw "SharedLibs runtimeconfig must be framework-dependent (found includedFrameworks): $ConfigPath"
    }
    if (-not ($ro.PSObject.Properties["framework"] -and $ro.framework.name)) {
        throw "SharedLibs runtimeconfig missing framework reference: $ConfigPath"
    }
}

function Test-SharedLibsCompilerSmoke([string]$SdkDir, [string]$Version) {
    $compilerExe = Join-Path $SdkDir "reSlate.Compiler.exe"
    $compilerDll = Join-Path $SdkDir "reSlate.Compiler.dll"
    $playerExe = Join-Path $SdkDir "reSlate.Player.exe"
    foreach ($pair in @(
        @{ File = $compilerExe; Args = @("--help"); Expect = "Usage: reSlate.Compiler" },
        @{ File = "dotnet"; Args = @($compilerDll, "--help"); Expect = "Usage: reSlate.Compiler" },
        @{ File = $playerExe; Args = @("--version"); Expect = "reSlate v$Version" }
    )) {
        $out = (& $pair.File @($pair.Args) 2>&1 | Out-String)
        if ($LASTEXITCODE -ne 0 -or $out -match 'hostpolicy\.dll|0x80008083' -or $out -notmatch [regex]::Escape($pair.Expect)) {
            throw "SharedLibs live smoke failed for '$($pair.File) $($pair.Args -join ' ')' (exit=$LASTEXITCODE): $out"
        }
    }
}

function Publish-SharedLibsSdk([string]$RootDir, [string]$DevDir, [string]$StageApp, [string]$InstallerPath, [string]$Version, $Cert = $null, [switch]$SkipSigning, [string]$SharedRootOverride = "") {
    $sharedRoot = if (-not [string]::IsNullOrWhiteSpace($SharedRootOverride)) { $SharedRootOverride } else { Join-Path (Split-Path -Parent (Split-Path -Parent $RootDir)) "SharedLibs\reSlate" }
    $pubTmp = Join-Path $env:TEMP ("reslate_sdk_pub_" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
    $compPub = Join-Path $pubTmp "compiler"; $playPub = Join-Path $pubTmp "player"
    New-Item -ItemType Directory -Force $compPub, $playPub | Out-Null
    try {
        & dotnet publish (Join-Path $DevDir "src\reStation.Compiler\reStation.Compiler.csproj") -c Release --no-self-contained -p:Version="$Version" -p:DebugType=none -p:DebugSymbols=false -o $compPub /warnaserror --disable-build-servers -p:UseSharedCompilation=false | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish (SharedLibs) reStation.Compiler failed ($LASTEXITCODE)" }
        & dotnet publish (Join-Path $DevDir "src\reStation.Player\reStation.Player.csproj") -c Release -r win-x64 --no-self-contained -p:Version="$Version" -p:DebugType=none -p:DebugSymbols=false -o $playPub /warnaserror --disable-build-servers -p:UseSharedCompilation=false | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish (SharedLibs) reStation.Player failed ($LASTEXITCODE)" }
        $decompOut = Join-Path $DevDir "src\reStation.Decomp\bin\Release\net8.0"
        if (-not (Test-Path -LiteralPath (Join-Path $decompOut "reSlate.Decomp.dll"))) {
            & dotnet build (Join-Path $DevDir "src\reStation.Decomp\reStation.Decomp.csproj") -c Release -p:Version="$Version" /warnaserror --disable-build-servers -p:UseSharedCompilation=false | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "dotnet build reStation.Decomp failed ($LASTEXITCODE)" }
        }
        $installerName = [System.IO.Path]::GetFileName($InstallerPath)
        $sources = @(
            @{ Dir = (Join-Path $DevDir "src\reStation.Contracts\bin\Release\net8.0"); Names = @("reStation.Contracts.dll", "reStation.Contracts.xml", "reStation.Contracts.deps.json") },
            @{ Dir = (Join-Path $DevDir "src\reStation.Core\bin\Release\net8.0"); Names = @("reStation.Core.dll", "reStation.Core.deps.json") },
            @{ Dir = $decompOut; Names = @("reSlate.Decomp.dll", "reSlate.Decomp.xml", "reSlate.Decomp.deps.json") },
            @{ Dir = $compPub; Names = @("reSlate.Compiler.exe", "reSlate.Compiler.dll", "reSlate.Compiler.deps.json", "reSlate.Compiler.runtimeconfig.json", "Foundation.Core.dll", "Foundation.Retro.dll", "reEngine.Core.dll", "reEngine.Host.dll") },
            @{ Dir = $playPub; Names = @("reSlate.Player.exe", "reSlate.Player.dll", "reSlate.Player.deps.json", "reSlate.Player.runtimeconfig.json", "SDL3-CS.dll") }
        )
        $sdkVerDir = Join-Path $sharedRoot "v$Version"
        $sdkLatestDir = Join-Path $sharedRoot "Latest"
        if (Test-Path -LiteralPath $sdkVerDir) { Remove-Item -LiteralPath $sdkVerDir -Recurse -Force }
        New-Item -ItemType Directory -Force $sdkVerDir | Out-Null
        $copied = [System.Collections.Generic.List[string]]::new()
        foreach ($srcGroup in $sources) {
            foreach ($fn in $srcGroup.Names) {
                $src = Join-Path $srcGroup.Dir $fn
                if (Test-Path -LiteralPath $src) { Copy-Item -LiteralPath $src -Destination (Join-Path $sdkVerDir $fn) -Force; $copied.Add($fn) }
            }
        }
        $sdlSrc = @((Join-Path $playPub "SDL3.dll"), (Join-Path $playPub "runtimes\win-x64\native\SDL3.dll"), (Join-Path $StageApp "SDL3.dll"), (Join-Path $DevDir "lib\SDL3.dll")) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
        if (-not $sdlSrc) { throw "SDL3.dll missing for SharedLibs staging." }
        Copy-Item -LiteralPath $sdlSrc -Destination (Join-Path $sdkVerDir "SDL3.dll") -Force; $copied.Add("SDL3.dll")
        if ((-not $SkipSigning) -and ($null -ne $Cert)) {
            Invoke-AuthenticodeSign $Cert (Join-Path $sdkVerDir "reSlate.Player.exe")
            Invoke-AuthenticodeSign $Cert (Join-Path $sdkVerDir "reSlate.Compiler.exe")
        }
        Copy-Item -LiteralPath $InstallerPath -Destination (Join-Path $sdkVerDir $installerName) -Force; $copied.Add($installerName)
        Copy-Item -LiteralPath (Join-Path $RootDir "README.md") -Destination (Join-Path $sdkVerDir "README.md") -Force; $copied.Add("README.md")
        Copy-Item -LiteralPath (Join-Path $RootDir "LICENSE") -Destination (Join-Path $sdkVerDir "LICENSE") -Force; $copied.Add("LICENSE")
        $nowUtc = [DateTime]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
        Write-Utf8Text (Join-Path $sdkVerDir "engine.manifest.json") (New-EngineManifestJson -Version $Version -DecompAssembly "reSlate.Decomp.dll" -UpdatedAtUtc $nowUtc)
        $copied.Add("engine.manifest.json")
        Assert-FrameworkDependentRuntimeConfig (Join-Path $sdkVerDir "reSlate.Compiler.runtimeconfig.json")
        Assert-FrameworkDependentRuntimeConfig (Join-Path $sdkVerDir "reSlate.Player.runtimeconfig.json")
        $sdkBinTargets = @("reSlate.Compiler.dll", "reSlate.Compiler.exe", "reSlate.Player.dll", "reSlate.Player.exe", "reSlate.Decomp.dll",
            "reStation.Contracts.dll", "reStation.Core.dll", "Foundation.Core.dll", "Foundation.Retro.dll", "reEngine.Core.dll", "reEngine.Host.dll") | ForEach-Object { Join-Path $sdkVerDir $_ }
        Assert-DirectoryAssembliesClean -TargetDir $sdkVerDir -TargetFiles $sdkBinTargets
        Test-SharedLibsCompilerSmoke -SdkDir $sdkVerDir -Version $Version
        $artifacts = @($copied | ForEach-Object { $p = Join-Path $sdkVerDir $_; [ordered]@{ fileName = $_; fileSizeBytes = (Get-Item -LiteralPath $p).Length; sha256Hex = (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant() } })
        $sdkJson = ([ordered]@{ schemaVersion = 1; appId = "reslate"; displayName = "reSlate"; sdkVersion = $Version; version = $Version; updatedAtUtc = $nowUtc; artifacts = $artifacts } | ConvertTo-Json -Depth 5) + "`r`n"
        Write-Utf8Text (Join-Path $sdkVerDir "sdk-manifest.json") $sdkJson
        if (Test-Path -LiteralPath $sdkLatestDir) { Remove-Item -LiteralPath $sdkLatestDir -Recurse -Force }
        New-Item -ItemType Directory -Force $sdkLatestDir | Out-Null
        foreach ($item in (Get-ChildItem -LiteralPath $sdkVerDir -File)) {
            $dst = Join-Path $sdkLatestDir $item.Name
            try { New-Item -ItemType HardLink -Path $dst -Target $item.FullName | Out-Null } catch { Copy-Item -LiteralPath $item.FullName -Destination $dst -Force }
        }
    } finally {
        if (Test-Path -LiteralPath $pubTmp) { Remove-Item -LiteralPath $pubTmp -Recurse -Force -ErrorAction SilentlyContinue }
    }
}
