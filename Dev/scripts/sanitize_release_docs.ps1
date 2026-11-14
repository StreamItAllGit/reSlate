#Requires -Version 5.1
# @module: Dev/scripts/sanitize_release_docs.ps1
# @purpose: UTF-8 end-user Markdown sanitization, leak detection, and Builds/Releases version pruning.
# @depends-on: System.IO, System.Text.UTF8Encoding
# @exports: Read-Utf8Text, Write-Utf8Text, Sanitize-MarkdownForEndUser, Assert-EndUserMarkdownClean, Prune-VersionDirectories

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

$script:InternalLeakRegex = @(
    'file:///'
    '@(?:pland|inspecter|dean|devv|tessa|nora|milo|simon|julian|coordinator)\b'
    '\bStandards?\s+\d+'
    '_DevBible'
    'task_graph\.json'
    'audit_(?:installer|dossier)\.ps1'
    'Dev[\\/](?:src|tests|docs|scripts|lib|assets|version\.txt|app\.manifest\.json)'
    'Sandbox[\\/]'
    'SharedLibs[\\/]'
    'Foundation[\\/]Dev'
    '--test-mode'
    '--visible-test'
    '[CD]:[\\/](?:Projects|Users)[\\/]'
) -join '|'

function Sanitize-MarkdownForEndUser([string]$Text) {
    $clean = Repair-Utf8Mojibake $Text
    $lines = $clean -split "\r?\n"
    $filtered = foreach ($line in $lines) {
        $l = [regex]::Replace($line, '\[([^\]]+)\]\(file:///[^)]+\)', '$1')
        $l = [regex]::Replace($l, '\(\s*file:///[^)]+\s*\)', '')
        $l = [regex]::Replace($l, '`file:///[^`]*`', 'local-link')
        $l = [regex]::Replace($l, 'file:///[^\s\)\]`"]+', '')
        if ($l -match '^\s*[-*]\s+\*\*(?:Entry Points|Verification Tests|Extension Point[^*]*|Tests):\*\*') {
            continue
        }
        if ($l -match $script:InternalLeakRegex) {
            continue
        }
        $l
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
    $lineNum = 0
    foreach ($line in ($content -split "\r?\n")) {
        $lineNum++
        if ($line -match $script:InternalLeakRegex) {
            throw "End-user markdown internal leak detected in '${FilePath}':L${lineNum}: $line"
        }
    }
}

function Prune-VersionDirectories(
    [Parameter(Mandatory = $true)][string]$ParentDir,
    [int]$KeepCount = 2,
    [string]$KeepVersionName = ""
) {
    if (-not (Test-Path -LiteralPath $ParentDir)) { return }
    $versionDirs = Get-ChildItem $ParentDir -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^v\d+\.\d+\.\d+' } |
        Sort-Object {
            $verText = ($_.Name -replace '^v', '') -replace '-.*$', ''
            $parsed = [version]'0.0.0'
            if ([version]::TryParse($verText, [ref]$parsed)) { $parsed } else { [version]'0.0.0' }
        } -Descending
    foreach ($old in ($versionDirs | Select-Object -Skip $KeepCount)) {
        if (-not [string]::IsNullOrWhiteSpace($KeepVersionName) -and ($old.Name -eq $KeepVersionName)) { continue }
        Remove-Item -LiteralPath $old.FullName -Recurse -Force
    }
}
