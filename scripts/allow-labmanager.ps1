<#
.SYNOPSIS
Opens or removes the Windows Firewall rule for the LabManager on the teacher PC.

.DESCRIPTION
Allows inbound TCP 5080 (default) from other lab PCs on Private and Domain
networks. Public networks stay blocked. Run on the teacher PC as administrator.

.EXAMPLE
.\scripts\allow-labmanager.ps1

.EXAMPLE
.\scripts\allow-labmanager.ps1 -Port 5080

.EXAMPLE
.\scripts\allow-labmanager.ps1 -Remove
#>
[CmdletBinding()]
param(
    [int]$Port = 5080,
    [string]$RuleName = "UpdateAgent LabManager",
    [switch]$Remove
)

$ErrorActionPreference = "Stop"

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "This script must run from an elevated PowerShell (Run as administrator)."
    }
}

Assert-Administrator

$existing = Get-NetFirewallRule -DisplayName $RuleName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Removing existing firewall rule: $RuleName"
    $existing | Remove-NetFirewallRule
}

if ($Remove) {
    Write-Host "Firewall rule removed." -ForegroundColor Green
    return
}

New-NetFirewallRule `
    -DisplayName $RuleName `
    -Description "Allow student PCs to check in with the UpdateAgent LabManager." `
    -Direction Inbound `
    -Action Allow `
    -Protocol TCP `
    -LocalPort $Port `
    -Profile Private, Domain | Out-Null

Write-Host "Firewall rule created: $RuleName (TCP $Port, Private/Domain)" -ForegroundColor Green
Write-Host "Verify with: Get-NetFirewallRule -DisplayName `"$RuleName`""
