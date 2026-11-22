<#
.SYNOPSIS
Generates self-contained silent-install packages for student PCs from an inventory CSV.

.DESCRIPTION
For each ComputerName in the CSV, creates <OutputDir>\<ComputerName>\ containing:
  - the Inno Agent setup executable
  - agent-install.ini with the LabManager URL and the token file name
  - the agent token file (referenced by the INI, never by a command line)
  - install-agent.cmd, which runs the silent install and removes the token file

The installer reads agent-install.ini from its own folder, so no settings are
passed on the command line. No token is ever placed on a command line.
Generation is the default and performs
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
        $candidates = @(Get-ChildItem -LiteralPath $artifactDir -Filter 'UpdateAgent.Agent-Setup-*-x64.exe' -File)
    }
    if ($candidates.Count -eq 0) {
        throw 'No Agent installer found. Run scripts\build-inno.ps1 -AgentOnly or pass -InstallerPath.'
    }

    # Select by parsed version first; a stale file copied later must not win.
    $parsed = @($candidates | ForEach-Object {
        $version = [version]'0.0.0'
        if ($_.Name -match 'Setup-(\d+)\.(\d+)\.(\d+)-x64\.exe$') {
            $version = [version]("$($Matches[1]).$($Matches[2]).$($Matches[3])")
        }
        [pscustomobject]@{ File = $_; Version = $version }
    })
    $sorted = @($parsed | Sort-Object -Property `
        @{Expression = { $_.Version }; Descending = $true}, `
        @{Expression = { $_.File.LastWriteTime }; Descending = $true})
    return $sorted[0].File
}

function Resolve-AgentTokenFile {
    param([string]$Path)

    $full = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
        throw "Agent token file not found: $full"
    }
    # The installer reads only the first line. Validate the same line here.
    $firstLine = (Get-Content -LiteralPath $full -TotalCount 1)
    if ($null -eq $firstLine) { $firstLine = '' }
    $value = ([string]$firstLine).Trim().TrimStart([char]0xFEFF)
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "Agent token file is empty: $full"
    }
    $lower = $value.ToLowerInvariant()
    if ($lower.StartsWith('change-me-') -or
        ($lower.StartsWith('sample-') -and $lower.EndsWith('-replace-before-pilot'))) {
        throw 'The agent token is a default/sample placeholder. Generate a real token before deployment.'
    }
    return $full
}

if ($ExecuteLocal) { Assert-Administrator }

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
    $safeName = [regex]::Replace($name, '[^A-Za-z0-9._-]', '_')
    $targets += [pscustomobject]@{ ComputerName = $name; SafeName = $safeName; MAC = $mac; IP = $ip }
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
$safeCollisions = @($targets | Group-Object -Property SafeName -CaseSensitive:$false |
    Where-Object { $_.Count -gt 1 })
if ($safeCollisions.Count -gt 0) {
    throw ("Inventory names map to duplicate package folders: {0}" -f
        (($safeCollisions | ForEach-Object { $_.Name }) -join ', '))
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
set "CONFIG=%PKG%agent-install.ini"
set "UPGRADE=0"
if /i "%~1"=="/Upgrade" set "UPGRADE=1"

if not exist "%SETUP%" (
  echo [UpdateAgent] Installer not found: %SETUP%
  exit /b 2
)
if not exist "%CONFIG%" (
  echo [UpdateAgent] Install config not found: %CONFIG%
  exit /b 2
)
if "!UPGRADE!"=="0" (
  if not exist "%TOKEN%" (
    echo [UpdateAgent] Token file not found: %TOKEN%
    echo [UpdateAgent] For an upgrade without a token file, run with /Upgrade.
    exit /b 2
  )
  icacls "%TOKEN%" /inheritance:r /grant:r "*S-1-5-32-544:(F)" "*S-1-5-18:(R)" >nul 2>&1
  if errorlevel 1 (
    echo [UpdateAgent] Failed to restrict token file ACL: %TOKEN%
    exit /b 4
  )
)

echo [UpdateAgent] Installing Agent for __PC__
echo [UpdateAgent] Settings: %CONFIG%
"%SETUP%" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
if errorlevel 1 (
  echo [UpdateAgent] Install failed with code !ERRORLEVEL!. Token file kept for retry.
  echo [UpdateAgent] Setup log: %TEMP%\Setup Log ^<date^>.txt
  exit /b 3
)

if exist "%TOKEN%" (
  del /f /q "%TOKEN%" >nul 2>&1
  if exist "%TOKEN%" (
    echo [UpdateAgent] Install complete but could not remove token file: %TOKEN%
    exit /b 5
  )
  echo [UpdateAgent] Token file removed.
) else (
  echo [UpdateAgent] No token file to remove (upgrade).
)
echo [UpdateAgent] Confirm this PC appears online in the LabManager within 90 seconds.
endlocal
'@

$iniTemplate = @'
[Agent]
LabManagerUrl=__URL__
AgentTokenFile=agent-token.txt
'@

$packages = @()
foreach ($target in $targets) {
    $safeName = $target.SafeName
    $packageDir = Join-Path $OutputDir $safeName
    New-Item -ItemType Directory -Force -Path $packageDir | Out-Null

    Copy-Item -LiteralPath $installer.FullName -Destination (Join-Path $packageDir $installer.Name) -Force
    $tokenCopy = Join-Path $packageDir 'agent-token.txt'
    Copy-Item -LiteralPath $tokenPath -Destination $tokenCopy -Force
    # install-agent.cmd applies the ACL again at install time; restrict the copy now too.
    & icacls.exe $tokenCopy '/inheritance:r' '/grant:r' '*S-1-5-32-544:(F)' '*S-1-5-18:(R)' 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Could not restrict ACL on generated token file: $tokenCopy"
    }

    $cmd = $cmdTemplate.
        Replace('__SETUP__', $installer.Name).
        Replace('__PC__', $safeName)
    $cmd = [regex]::Replace($cmd, "\r?\n", "`r`n")
    [IO.File]::WriteAllText((Join-Path $packageDir 'install-agent.cmd'), $cmd, (New-Object System.Text.UTF8Encoding($false)))

    $ini = [regex]::Replace(
        $iniTemplate.Replace('__URL__', $LabManagerUrl), "\r?\n", "`r`n")
    [IO.File]::WriteAllText((Join-Path $packageDir 'agent-install.ini'), $ini, (New-Object System.Text.UTF8Encoding($false)))

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
  agent-install.ini                          LabManager URL + token file name
  agent-token.txt                            shared agent token (do not distribute widely)
  install-agent.cmd                          elevated silent install + token cleanup

Run install-agent.cmd on the matching student PC from an elevated prompt.
The installer reads agent-install.ini from its own folder, so no settings are
passed on the command line. Generated token copies are restricted to
Administrators/SYSTEM and the script restricts them again before installing.
The token file is deleted after a successful install; if the install fails,
the token file is kept for retry.

For an upgrade after the token file has been deleted, run:
  install-agent.cmd /Upgrade
This keeps the existing agent.json when a previous installation is detected.

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
