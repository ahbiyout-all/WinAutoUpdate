<#
.SYNOPSIS
Uninstalls the UpdateAgent Agent MSI from a student PC.

.DESCRIPTION
Removes the product through Windows Installer (service and Program Files), and
optionally deletes the agent data directory (%ProgramData%\UpdateAgent) that holds
agent.json, the job database, resume marker and logs.

Run from an elevated PowerShell prompt.

.EXAMPLE
.\scripts\uninstall-agent-msi.ps1

.EXAMPLE
.\scripts\uninstall-agent-msi.ps1 -RemoveData -Force
#>
[CmdletBinding()]
param(
    [string]$MsiPath,
    [switch]$RemoveData,
    [switch]$Force,
    [string]$ServiceName = "UpdateAgentService",
    [string]$DataRoot = "C:\ProgramData\UpdateAgent",
    [string]$LogPath
)

$ErrorActionPreference = "Stop"

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "This script must run from an elevated PowerShell (Run as administrator)."
    }
}

function Get-UpdateAgentProductCode {
    $root = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*"
    $entry = Get-ItemProperty -Path $root -ErrorAction SilentlyContinue |
        Where-Object { $_.DisplayName -eq "UpdateAgent Agent" } |
        Select-Object -First 1

    if ($entry) { return $entry.PSChildName }
    return $null
}

Assert-Administrator
$DataRoot = [IO.Path]::GetFullPath($DataRoot)

$uninstallTarget = $null
if (-not [string]::IsNullOrWhiteSpace($MsiPath)) {
    $uninstallTarget = [IO.Path]::GetFullPath($MsiPath)
    if (-not (Test-Path -LiteralPath $uninstallTarget)) {
        throw "MSI not found: $uninstallTarget"
    }
}
else {
    $productCode = Get-UpdateAgentProductCode
    if ([string]::IsNullOrWhiteSpace($productCode)) {
        Write-Host "UpdateAgent Agent is not registered with Windows Installer." -ForegroundColor Yellow
    }
    else {
        $uninstallTarget = $productCode
    }
}

if ($uninstallTarget -and -not $Force) {
    Write-Host "This will uninstall UpdateAgent Agent."
    if ($RemoveData) { Write-Host "  Agent data: $DataRoot (agent.json, jobs, logs, markers)" }
    $answer = Read-Host "Continue? (y/N)"
    if ($answer -notin @("y", "yes")) {
        Write-Host "Aborted."
        return
    }
}

if ($uninstallTarget) {
    if ([string]::IsNullOrWhiteSpace($LogPath)) {
        $LogPath = Join-Path $env:TEMP ("UpdateAgent.Agent-msi-uninstall-{0}.log" -f (Get-Date -Format "yyyyMMdd-HHmmss"))
    }
    $LogPath = [IO.Path]::GetFullPath($LogPath)
    $logDirectory = Split-Path -Parent $LogPath
    if (-not (Test-Path -LiteralPath $logDirectory)) {
        New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
    }

    Write-Host "Uninstalling: $uninstallTarget"
    Write-Host "MSI log     : $LogPath"

    $arguments = @("/x", "`"$uninstallTarget`"", "/qn", "/norestart", "/l*v", "`"$LogPath`"")
    $process = Start-Process -FilePath "msiexec.exe" -ArgumentList $arguments -Wait -PassThru
    $exitCode = $process.ExitCode

    switch ($exitCode) {
        0     { Write-Host "Uninstall succeeded." }
        3010  { Write-Warning "Uninstall succeeded. A reboot is recommended to finish." }
        1641  { Write-Warning "Uninstall succeeded and requested a restart." }
        1605  { Write-Host "The product was already uninstalled." -ForegroundColor Yellow }
        default { throw "msiexec /x failed with exit code $exitCode. See $LogPath" }
    }
}

# Defensive cleanup: the MSI owns the service, but a failed/interrupted install may leave it behind.
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') {
        Write-Host "Stopping leftover service $ServiceName..."
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        try {
            $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
        }
        catch {
            Write-Warning "Service did not report Stopped within 30 seconds."
        }
    }

    Write-Host "Deleting leftover service $ServiceName..."
    & sc.exe delete $ServiceName | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "sc.exe delete returned $LASTEXITCODE. The service may already be marked for deletion."
    }
}

$programFilesRoot = Join-Path $env:ProgramFiles "UpdateAgent"
if (Test-Path -LiteralPath $programFilesRoot) {
    Write-Warning "Program files still present: $programFilesRoot"
    Write-Warning "Re-run the uninstall, or delete the folder manually if the MSI is gone."
}

if ($RemoveData) {
    if (Test-Path -LiteralPath $DataRoot) {
        Write-Host "Removing agent data: $DataRoot"
        Remove-Item -LiteralPath $DataRoot -Recurse -Force
    }
    else {
        Write-Host "Agent data not found: $DataRoot"
    }
}
else {
    Write-Host "Agent data preserved: $DataRoot"
    Write-Host "  Use -RemoveData to delete agent.json, the job database, markers and logs."
}

Write-Host ""
Write-Host "Uninstall complete." -ForegroundColor Green
