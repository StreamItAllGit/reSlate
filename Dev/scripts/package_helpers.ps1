#Requires -Version 5.1
# @module: Dev/scripts/package_helpers.ps1
# @purpose: Shared packaging helpers for build_release.ps1 (NTFS hardlinks, binary purity, Portable.zip, SharedLibs SDK, release junctions).
# @depends-on: Dev/scripts/sanitize_release_docs.ps1
# @exports: Starter Phase 2A stub (populated by RS-307)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Resolve-CodeSigningCertificate {
    param([string]$ExplicitThumbprint = "")
    if (-not [string]::IsNullOrWhiteSpace($ExplicitThumbprint)) {
        $found = Get-ChildItem @("Cert:\CurrentUser\My", "Cert:\LocalMachine\My") -ErrorAction SilentlyContinue |
            Where-Object { $_.Thumbprint -ieq $ExplicitThumbprint.Trim() } | Select-Object -First 1
        if ($found) { return $found }
    }
    return Get-ChildItem @("Cert:\CurrentUser\My", "Cert:\LocalMachine\My") -ErrorAction SilentlyContinue |
        Where-Object { $_.HasPrivateKey -and ($_.EnhancedKeyUsageList | Where-Object { $_.ObjectId -eq "1.3.6.1.5.5.7.3.3" }) -and $_.NotAfter -gt (Get-Date) } |
        Sort-Object NotAfter -Descending | Select-Object -First 1
}
