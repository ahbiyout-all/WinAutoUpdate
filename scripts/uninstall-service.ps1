<#
.SYNOPSIS
Uninstalls the UpdateAgent agent from a student PC.

.DESCRIPTION
Stops and deletes the Windows service. Optionally removes the installed program
files and the agent data directory (%ProgramData%\UpdateAgent).

Run from an elevated PowerShell prompt.

.EXAMPLE
.\scripts\uninstall-service.ps1

.EXAMPLE
.\scripts\uninstall-service.ps1 -RemoveFiles -RemoveData -Force
#>
[CmdletBinding()]
param(
    [string]$ServiceName = "UpdateAgentService",
    [string]$InstallRoot = "C:\ProgramData\UpdateAgent\app",
    [string]$DataRoot = "C:\ProgramData\UpdateAgent",
    [switch]$RemoveFiles,
    [switch]$RemoveData,
    [switch]$Force
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
$InstallRoot = [IO.Path]::GetFullPath($InstallRoot)
$DataRoot = [IO.Path]::GetFullPath($DataRoot)

if (-not $Force) {
    Write-Host "This will remove service '$ServiceName'."
    if ($RemoveFiles) { Write-Host "  Program files: $InstallRoot" }
    if ($RemoveData)  { Write-Host "  Agent data   : $DataRoot (jobs, logs, markers)" }
    $answer = Read-Host "Continue? (y/N)"
    if ($answer -notin @("y", "yes")) {
        Write-Host "Aborted."
        return
    }
}

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') {
        Write-Host "Stopping $ServiceName..."
        Stop-Service -Name $ServiceName -Force
        try {
            $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
        }
        catch {
            Write-Warning "Service did not report Stopped within 30 seconds."
        }
    }

    Write-Host "Deleting service $ServiceName..."
    & sc.exe delete $ServiceName | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "sc.exe delete returned $LASTEXITCODE. The service may already be marked for deletion."
    }

    for ($i = 0; $i -lt 15; $i++) {
        Start-Sleep -Seconds 1
        if (-not (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) { break }
    }
}
else {
    Write-Host "Service $ServiceName is not installed."
}

if ($RemoveFiles) {
    if (Test-Path -LiteralPath $InstallRoot) {
        Write-Host "Removing program files: $InstallRoot"
        Remove-Item -LiteralPath $InstallRoot -Recurse -Force
    }
    else {
        Write-Host "Program files not found: $InstallRoot"
    }
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

Write-Host ""
Write-Host "Uninstall complete." -ForegroundColor Green
