#Requires -Version 5.1
# @module: Dev/scripts/sanitize_release_docs.ps1
# @purpose: UTF-8 end-user Markdown sanitization, relative link rewrite, trademark/PII leak checks, and version/archive pruning.
# @depends-on: System.IO, System.Text.UTF8Encoding
# @exports: Read-Utf8Text, Write-Utf8Text, Sanitize-MarkdownForEndUser, Assert-EndUserMarkdownClean, Prune-VersionDirectories, Prune-MinorVersionArchives

Set-StrictMode -Version Latest

function Read-Utf8Text([Parameter(Mandatory = $true)][string]$Path) {
    return [System.IO.File]::ReadAllText($Path, [System.Text.UTF8Encoding]::new($false))
}

function Write-Utf8Text([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)][string]$Content) {
    [System.IO.File]::WriteAllText($Path, $Content, [System.Text.UTF8Encoding]::new($false))
}

function Repair-Utf8Mojibake([string]$Text) {
    if ([string]::IsNullOrEmpty($Text)) { return $Text }
    $fixed = $Text
    $fixed = $fixed.Replace(([string][char]0x00C2 + [char]0x00A7), [string][char]0x00A7)
    $fixed = $fixed.Replace(([string][char]0x00C2 + [char]0x00B7), [string][char]0x00B7)
    $fixed = $fixed.Replace(([string][char]0x00E2 + [char]0x20AC + [char]0x201D), [string][char]0x2014)
    $fixed = $fixed.Replace(([string][char]0x00E2 + [char]0x20AC + [char]0x201C), [string][char]0x2013)
    $fixed = $fixed.Replace(([string][char]0x00E2 + [char]0x20AC + [char]0x2122), "'")
    $fixed = $fixed.Replace(([string][char]0x00E2 + [char]0x20AC + [char]0x0153), '"')
    $fixed = $fixed.Replace(([string][char]0x00E2 + [char]0x20AC + [char]0x009D), '"')
    return $fixed
}

function Get-InternalLeakPattern {
    $parts = [System.Collections.Generic.List[string]]@(
        'file:///',
        '@(?:pland|inspecter|dean|devv|tessa|nora|milo|simon|julian|holly|rowan|coordinator|monitor|everyone)[a-z0-9-]*',
        '\bStandards?\s+\d+',
        '_DevBible',
        '\bDev Bible\b',
        'task_graph\.json',
        'audit_(?:installer|dossier)\.ps1',
        'Dev[\\/](?:src|tests|docs|scripts|lib|assets|version\.txt|app\.manifest\.json)',
        'SharedLibs[\\/]',
        'Foundation[\\/]Dev',
        '[CD]:[\\/](?:Projects|Users|Programs)[\\/]',
        'collection://[0-9a-f-]+',
        '\b4[0-9a-f]{7}-5a45-[0-9a-f-]{14,}\b'
    )
    if (-not [string]::IsNullOrWhiteSpace($env:USERNAME) -and $env:USERNAME.Length -gt 2) {
        $parts.Add('\b' + [regex]::Escape($env:USERNAME) + '\b')
    }
    if (-not [string]::IsNullOrWhiteSpace($env:COMPUTERNAME) -and $env:COMPUTERNAME.Length -gt 2) {
        $parts.Add('\b' + [regex]::Escape($env:COMPUTERNAME) + '\b')
    }
    return '(?i)' + ($parts -join '|')
}

$script:InternalLeakRegex = Get-InternalLeakPattern
$script:TrademarkRegex = '(?i)\b(?:PlayStation|DualShock|PocketStation|Sony|Nintendo|Sega|King''?s\s*Field|KingsField|Harvest\s*Moon|HMBackToNature|Metal\s*Gear)\b'

function Invoke-TrademarkScrubOutsideLegal([string]$Line) {
    $l = [regex]::Replace($Line, '(?i)\bPlayStation\b', 'SlateOne')
    $l = [regex]::Replace($l, '(?i)\bDualShock\b', 'Gamepad')
    $l = [regex]::Replace($l, '(?i)\bPocketStation\b', 'handheld dock')
    $l = [regex]::Replace($l, '(?i)\bSony Interactive Entertainment(?:\s+Inc\.?)?\b', 'hardware vendor')
    $l = [regex]::Replace($l, '(?i)\bSony\b', 'hardware vendor')
    $l = [regex]::Replace($l, '(?i)\bNintendo\b', 'CartOne vendor')
    $l = [regex]::Replace($l, '(?i)\bSega\b', 'Blast16 vendor')
    $l = [regex]::Replace($l, '(?i)\b(?:King''?s\s*Field|KingsField)\b', 'CrownDepths')
    $l = [regex]::Replace($l, '(?i)\b(?:Harvest\s*Moon|HMBackToNature)\b', 'MeadowValley')
    $l = [regex]::Replace($l, '(?i)\bMetal\s*Gear\b', 'IronFrame')
    return $l
}

function Sanitize-MarkdownForEndUser([string]$Text) {
    $clean = Repair-Utf8Mojibake $Text
    $sectionSign = [string][char]0x00A7
    $enDash = [string][char]0x2013
    $handlePat = '`?@(?:pland|inspecter|dean|devv|tessa|nora|milo|simon|julian|holly|rowan|coordinator|monitor|everyone)[a-z0-9-]*`?'
    $stdPat = "(?i)``?(?:per\s+)?\bStandards?\s+\d+(?:\s*(?:$enDash|-)\s*\d+)?(?:\s*$sectionSign[\d.]+)?``?"
    $relLinkPat = '\[([^\]]+)\]\((?:\./|\.\.[\\/])*(?:Dev|src|tests|docs|scripts|lib|assets)[\\/][^)]+\)'

    $inLicenseSection = $false
    $filtered = foreach ($line in ($clean -split "\r?\n")) {
        if ($line -match '^\s*#{1,6}\s+(?:\d+\.\s*)?License\s*&\s*Legal\b') {
            $inLicenseSection = $true
        } elseif ($line -match '^\s*##\s+') {
            $inLicenseSection = $false
        }

        $l = [regex]::Replace($line, '\[([^\]]+)\]\(file:///[^)]+\)', '$1')
        $l = [regex]::Replace($l, $relLinkPat, '$1')
        $l = [regex]::Replace($l, '\(\s*file:///[^)]+\s*\)', '')
        $l = [regex]::Replace($l, '`file:///[^`]*`', '')
        $l = [regex]::Replace($l, 'file:///[^\s\)\]`"]+', '')
        if ($l -match '^\s*[-*]\s+\*\*(?:Entry Points|Verification Tests|Extension Point[^*]*|Tests):\*\*') {
            continue
        }
        $l = [regex]::Replace($l, $handlePat, '')
        $l = [regex]::Replace($l, $stdPat, '')
        if (-not $inLicenseSection) {
            $l = Invoke-TrademarkScrubOutsideLegal $l
        }
        $l = [regex]::Replace($l, '\s*\(\s*\)', '')
        $l = [regex]::Replace($l, '(?<=\S) {2,}', ' ')
        if ($l -match $script:InternalLeakRegex) {
            continue
        }
        $l.TrimEnd()
    }
    $joined = ($filtered -join "`r`n")
    $joined = [regex]::Replace($joined, "(\r?\n){3,}", "`r`n`r`n")
    return ($joined.Trim() + "`r`n")
}

function Assert-EndUserMarkdownClean([Parameter(Mandatory = $true)][string]$FilePath) {
    $content = Read-Utf8Text $FilePath
    $mojibakeSection = [string][char]0x00C2 + [char]0x00A7
    if ($content.Contains($mojibakeSection) -or $content.Contains([string][char]0x00E2 + [char]0x20AC)) {
        throw "End-user markdown encoding leak (mojibake) detected in '$FilePath'."
    }
    $inLicenseSection = $false
    $lineNum = 0
    foreach ($line in ($content -split "\r?\n")) {
        $lineNum++
        if ($line -match '^\s*#{1,6}\s+(?:\d+\.\s*)?License\s*&\s*Legal\b') {
            $inLicenseSection = $true
        } elseif ($line -match '^\s*##\s+') {
            $inLicenseSection = $false
        }
        if ($line -match $script:InternalLeakRegex) {
            throw "End-user markdown internal leak detected in '${FilePath}':L${lineNum}: $line"
        }
        if ((-not $inLicenseSection) -and ($line -match $script:TrademarkRegex)) {
            throw "End-user markdown trademark leak detected outside License & Legal in '${FilePath}':L${lineNum}: $line"
        }
    }
}

function Prune-MinorVersionArchives(
    [Parameter(Mandatory = $true)][string]$ArchiveDir,
    [int]$KeepMinorCount = 3,
    [string]$KeepVersionName = ""
) {
    if (-not (Test-Path -LiteralPath $ArchiveDir) -or $KeepMinorCount -lt 1) { return }
    $keepMinorKey = ""
    if (-not [string]::IsNullOrWhiteSpace($KeepVersionName)) {
        $km = [regex]::Match($KeepVersionName.Trim(), 'v?(\d+)\.(\d+)\.(\d+)')
        if ($km.Success) { $keepMinorKey = "$($km.Groups[1].Value).$($km.Groups[2].Value)" }
    }

    $entries = @(Get-ChildItem -LiteralPath $ArchiveDir -Filter *.7z -File -ErrorAction SilentlyContinue | ForEach-Object {
        $m = [regex]::Match($_.Name, '(?:^|[-_])v?(\d+)\.(\d+)\.(\d+)(?:-[^.]+)?(?:-[a-z0-9-]+)?\.7z$', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
        if ($m.Success) {
            $maj = [int]$m.Groups[1].Value
            $min = [int]$m.Groups[2].Value
            [PSCustomObject]@{ File = $_; MinorKey = "$maj.$min"; Score = ($maj * 100000L) + $min }
        }
    })
    if ($entries.Count -eq 0) { return }

    $keptMinors = @($entries | Sort-Object Score -Descending | Select-Object -ExpandProperty MinorKey -Unique | Select-Object -First $KeepMinorCount)
    foreach ($entry in $entries) {
        if (($keptMinors -contains $entry.MinorKey) -or ($keepMinorKey -and ($entry.MinorKey -eq $keepMinorKey))) { continue }
        Remove-Item -LiteralPath $entry.File.FullName -Force
    }
}

function Prune-VersionDirectories(
    [Parameter(Mandatory = $true)][string]$ParentDir,
    [int]$KeepCount = 2,
    [string]$KeepVersionName = "",
    [int]$KeepArchiveMinors = 3
) {
    if (-not (Test-Path -LiteralPath $ParentDir)) { return }
    $versionDirs = Get-ChildItem $ParentDir -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^v(\d+)\.(\d+)\.(\d+)(?:-(.+))?$' } |
        Sort-Object {
            $m = [regex]::Match($_.Name, '^v(\d+)\.(\d+)\.(\d+)(?:-(.+))?$')
            $maj = [int]$m.Groups[1].Value
            $min = [int]$m.Groups[2].Value
            $pat = [int]$m.Groups[3].Value
            $suf = $m.Groups[4].Value
            $rank = if ([string]::IsNullOrWhiteSpace($suf)) { 500 } elseif ($suf -match '^hf\.(\d+)$') { 1000 + [int]$matches[1] } else { 0 }
            ($maj * 100000000L) + ($min * 1000000L) + ($pat * 10000L) + $rank
        } -Descending
    foreach ($old in ($versionDirs | Select-Object -Skip $KeepCount)) {
        if (-not [string]::IsNullOrWhiteSpace($KeepVersionName) -and ($old.Name -eq $KeepVersionName)) { continue }
        Remove-Item -LiteralPath $old.FullName -Recurse -Force
    }
    if ($KeepArchiveMinors -gt 0) {
        Prune-MinorVersionArchives -ArchiveDir $ParentDir -KeepMinorCount $KeepArchiveMinors -KeepVersionName $KeepVersionName
    }
}
