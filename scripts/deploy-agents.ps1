<#
.SYNOPSIS
Generates self-contained silent-install packages for student PCs from an inventory CSV.

.DESCRIPTION
For each ComputerName in the CSV, creates <OutputDir>\<ComputerName>\ containing:
  - the Inno Agent setup executable
  - the agent token file (used only through the installer's /AgentTokenFile switch)
  - install-agent.cmd, which runs the silent install and removes the token file

No token is ever placed on a command line. Generation is the default and performs
no remote action. Packages can be copied to each PC (USB/RDP) or wrapped by
PsExec/WinRM. Use -ExecuteLocal to run one package on the current PC.

.EXAMPLE
.\scripts\deploy-agents.ps1 -InventoryCsv .\scripts\agents.sample.csv -AgentTokenFile .\secure\agent-token.txt

.EXAMPLE
.\scripts\deploy-agents.ps1 -InventoryCsv .\inventory.csv -AgentTokenFile .\secure\agent-token.txt -LabManagerUrl http://192.168.0.10:5080 -ExecuteLocal -ComputerName STUDENT-01
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InventoryCsv,

    [Parameter(Mandatory = $true)]
    [string]$AgentTokenFile,

    [string]$InstallerPath,
    [string]$LabManagerUrl = 'http://labmanager:5080',
    [string]$OutputDir,
    [string]$ComputerName,
    [switch]$ExecuteLocal
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $repoRoot 'deploy\agents'
}
$OutputDir = [IO.Path]::GetFullPath($OutputDir)

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Local execution must run from an elevated PowerShell (Run as administrator).'
    }
}

function Resolve-Installer {
    param([string]$Explicit)

    if (-not [string]::IsNullOrWhiteSpace($Explicit)) {
        $full = [IO.Path]::GetFullPath($Explicit)
        if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
            throw "Agent installer not found: $full"
        }
        return (Get-Item -LiteralPath $full)
    }

    $artifactDir = Join-Path $repoRoot 'artifacts\inno'
    $candidates = @()
    if (Test-Path -LiteralPath $artifactDir -PathType Container) {
        $candidates = @(Get-ChildItem -LiteralPath $artifactDir -Filter 'UpdateAgent.Agent-Setup-*-x64.exe' -File |
            Sort-Object LastWriteTime -Descending)
    }
    if ($candidates.Count -eq 0) {
        throw 'No Agent installer found. Run scripts\build-inno.ps1 -AgentOnly or pass -InstallerPath.'
    }
    return $candidates[0]
}

function Resolve-AgentTokenFile {
    param([string]$Path)

    $full = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
        throw "Agent token file not found: $full"
    }
    $value = (Get-Content -LiteralPath $full -Raw).Trim().TrimStart([char]0xFEFF)
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "Agent token file is empty: $full"
    }
    $placeholders = @('change-me-agent-token', 'sample-agent-token-replace-before-pilot')
    if ($placeholders -contains $value) {
        throw 'The agent token is a default/sample placeholder. Generate a real token before deployment.'
    }
    return $full
}

$LabManagerUrl = $LabManagerUrl.Trim().TrimEnd('/')
$uri = $null
if (-not [Uri]::TryCreate($LabManagerUrl, [UriKind]::Absolute, [ref]$uri) -or
    ($uri.Scheme -ne 'http' -and $uri.Scheme -ne 'https')) {
    throw "LabManagerUrl must be an absolute http/https URL: $LabManagerUrl"
}

if (-not (Test-Path -LiteralPath $InventoryCsv -PathType Leaf)) {
    throw "Inventory CSV not found: $InventoryCsv"
}
$inventoryPath = [IO.Path]::GetFullPath($InventoryCsv)
$rows = @(Import-Csv -LiteralPath $inventoryPath)
if ($rows.Count -eq 0) {
    throw "Inventory CSV has no data rows: $inventoryPath"
}
if ($rows[0].PSObject.Properties.Name -notcontains 'ComputerName') {
    throw 'Inventory CSV must have a ComputerName column (see scripts\agents.sample.csv).'
}

$targets = @()
foreach ($row in $rows) {
    $name = ([string]$row.ComputerName).Trim()
    if ([string]::IsNullOrWhiteSpace($name)) {
        throw 'Inventory CSV contains an empty ComputerName.'
    }
    $properties = $row.PSObject.Properties.Name
    $mac = if ($properties -contains 'MAC') { ([string]$row.MAC).Trim() } else { '' }
    $ip = if ($properties -contains 'IP') { ([string]$row.IP).Trim() } else { '' }
    $targets += [pscustomobject]@{ ComputerName = $name; MAC = $mac; IP = $ip }
}

if ($PSBoundParameters.ContainsKey('ComputerName')) {
    $targets = @($targets | Where-Object { $_.ComputerName -ieq $ComputerName })
    if ($targets.Count -eq 0) {
        throw "ComputerName not found in inventory: $ComputerName"
    }
}

$uniqueNames = @($targets | ForEach-Object { $_.ComputerName } | Sort-Object -Unique)
if ($uniqueNames.Count -ne $targets.Count) {
    throw 'Inventory CSV contains duplicate ComputerName values.'
}

$installer = Resolve-Installer -Explicit $InstallerPath
$tokenPath = Resolve-AgentTokenFile -Path $AgentTokenFile

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$cmdTemplate = @'
@echo off
setlocal EnableDelayedExpansion
net session >nul 2>&1
if errorlevel 1 (
  echo [UpdateAgent] Run this script from an elevated command prompt.
  exit /b 1
)

set "PKG=%~dp0"
set "SETUP=%PKG%__SETUP__"
set "TOKEN=%PKG%agent-token.txt"
set "URL=__URL__"

if not exist "%SETUP%" (
  echo [UpdateAgent] Installer not found: %SETUP%
  exit /b 2
)
if not exist "%TOKEN%" (
  echo [UpdateAgent] Token file not found: %TOKEN%
  exit /b 2
)

icacls "%TOKEN%" /inheritance:r /grant:r "*S-1-5-32-544:(F)" "*S-1-5-18:(R)" >nul 2>&1

echo [UpdateAgent] Installing Agent for __PC__
echo [UpdateAgent] LabManager: %URL%
"%SETUP%" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LabManagerUrl=%URL% /AgentTokenFile="%TOKEN%"
if errorlevel 1 (
  echo [UpdateAgent] Install failed with code !ERRORLEVEL!. Token file kept for retry.
  exit /b 3
)

del /f /q "%TOKEN%" >nul 2>&1
echo [UpdateAgent] Install complete. Token file removed.
echo [UpdateAgent] Confirm this PC appears online in the LabManager within 90 seconds.
endlocal
'@

$packages = @()
foreach ($target in $targets) {
    $safeName = [regex]::Replace($target.ComputerName, '[^A-Za-z0-9._-]', '_')
    $packageDir = Join-Path $OutputDir $safeName
    New-Item -ItemType Directory -Force -Path $packageDir | Out-Null

    Copy-Item -LiteralPath $installer.FullName -Destination (Join-Path $packageDir $installer.Name) -Force
    Copy-Item -LiteralPath $tokenPath -Destination (Join-Path $packageDir 'agent-token.txt') -Force

    $cmd = $cmdTemplate.
        Replace('__SETUP__', $installer.Name).
        Replace('__URL__', $LabManagerUrl).
        Replace('__PC__', $target.ComputerName)
    $cmd = [regex]::Replace($cmd, "\r?\n", "`r`n")
    [IO.File]::WriteAllText((Join-Path $packageDir 'install-agent.cmd'), $cmd, (New-Object System.Text.UTF8Encoding($false)))

    $packages += [pscustomobject]@{
        ComputerName = $target.ComputerName
        PackageDir   = $packageDir
        MAC          = $target.MAC
        IP           = $target.IP
    }
}

$manifestPath = Join-Path $OutputDir 'manifest.csv'
$packages | Select-Object ComputerName, IP, MAC, PackageDir | Export-Csv -LiteralPath $manifestPath -NoTypeInformation -Encoding UTF8

$readme = @'
UpdateAgent student agent deployment packages
=============================================

Each <ComputerName> folder is self-contained:
  UpdateAgent.Agent-Setup-<version>-x64.exe  silent installer
  agent-token.txt                            shared agent token (do not distribute widely)
  install-agent.cmd                          elevated silent install + token cleanup

Run install-agent.cmd on the matching student PC from an elevated prompt.
The token is passed to the installer with /AgentTokenFile and is deleted after
a successful install. If the install fails, the token file is kept for retry.

Protect this output folder and delete it after the rollout is verified.
See docs/DEPLOYMENT.md for the full lab runbook.
'@
[IO.File]::WriteAllText((Join-Path $OutputDir 'README.txt'), $readme, (New-Object System.Text.UTF8Encoding($false)))

Write-Host ''
Write-Host "Generated $($packages.Count) agent package(s) in $OutputDir" -ForegroundColor Green
Write-Host "  Installer : $($installer.Name)"
Write-Host "  LabManager: $LabManagerUrl"
foreach ($package in $packages) {
    Write-Host "  $($package.ComputerName) -> $($package.PackageDir)"
}
Write-Host ''
Write-Host 'Token files are copies of the agent token. Protect the output folder and remove it after rollout.' -ForegroundColor Yellow

if ($ExecuteLocal) {
    Assert-Administrator
    if ($packages.Count -ne 1) {
        throw 'Use -ComputerName to select exactly one PC when -ExecuteLocal is set.'
    }
    $localCmd = Join-Path $packages[0].PackageDir 'install-agent.cmd'
    Write-Host ''
    Write-Host "Running $localCmd on this PC..." -ForegroundColor Cyan
    & cmd.exe /c "`"$localCmd`""
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "Local install failed (exit code $exitCode)."
    }
    Write-Host 'Local install completed.' -ForegroundColor Green
}
