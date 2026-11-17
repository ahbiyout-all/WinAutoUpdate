<#
.SYNOPSIS
Opens or removes the Windows Firewall rules for the LabManager on the teacher PC.

.DESCRIPTION
Creates the default and version-appended rules with the same group and names as
the LabManager installer: "UpdateAgent LabManager" and
"UpdateAgent LabManager <version>". Inbound TCP 5080 (default) is allowed from
Private and Domain networks. Public networks stay blocked. Run on the teacher PC
as administrator.

.EXAMPLE
.\scripts\allow-labmanager.ps1

.EXAMPLE
.\scripts\allow-labmanager.ps1 -Port 5080 -ProductVersion 0.1.1

.EXAMPLE
.\scripts\allow-labmanager.ps1 -Remove
#>
[CmdletBinding()]
param(
    [int]$Port = 5080,
    [string]$ProductVersion,
    [string]$RuleGroup = "UpdateAgent LabManager",
    [switch]$Remove
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "This script must run from an elevated PowerShell (Run as administrator)."
    }
}

function Get-ProductVersion {
    $propsPath = Join-Path $repoRoot "Directory.Build.props"
    $text = Get-Content -LiteralPath $propsPath -Raw
    if ($text -notmatch '<Version>\s*([^<]+?)\s*</Version>') {
        throw "No <Version> element found in $propsPath."
    }
    return $Matches[1].Trim()
}

Assert-Administrator

if ([string]::IsNullOrWhiteSpace($ProductVersion)) {
    $ProductVersion = Get-ProductVersion
}

$ruleNames = @(
    "UpdateAgent LabManager",
    "UpdateAgent LabManager $ProductVersion"
)

$existing = @()
foreach ($ruleName in $ruleNames) {
    $existing += @(Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue)
}
$existing += @(Get-NetFirewallRule -Group $RuleGroup -ErrorAction SilentlyContinue)
$existing = @($existing | Select-Object -Unique)

if ($existing.Count -gt 0) {
    Write-Host "Removing $($existing.Count) existing UpdateAgent LabManager firewall rule(s)."
    $existing | Remove-NetFirewallRule
}

if ($Remove) {
    Write-Host "Firewall rules removed." -ForegroundColor Green
    return
}

foreach ($ruleName in $ruleNames) {
    New-NetFirewallRule `
        -DisplayName $ruleName `
        -Group $RuleGroup `
        -Description "Allow student PCs to check in with the UpdateAgent LabManager." `
        -Direction Inbound `
        -Action Allow `
        -Protocol TCP `
        -LocalPort $Port `
        -Profile Private, Domain | Out-Null
}

Write-Host "Firewall rules created:" -ForegroundColor Green
$ruleNames | ForEach-Object { Write-Host "  $_ (TCP $Port, Private/Domain)" }
Write-Host "Verify with: Get-NetFirewallRule -Group `"$RuleGroup`""
