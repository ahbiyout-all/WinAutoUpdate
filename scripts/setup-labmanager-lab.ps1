<#
.SYNOPSIS
Prepares the teacher PC to host the UpdateAgent LabManager for a lab deployment.

.DESCRIPTION
One-shot setup for the hub-and-spoke (~50 PCs) topology:
  1. Writes real admin/agent tokens and the listen URL into the installed or
     published appsettings.json. Default and sample tokens are rejected.
  2. Opens inbound TCP <Port> on Domain/Private network profiles only.
  3. Registers an at-logon scheduled task that starts the headless API server.
  4. Starts the server when needed and verifies GET /api/health.

Run on the teacher PC from an elevated PowerShell.

.EXAMPLE
.\scripts\setup-labmanager-lab.ps1 -AdminTokenFile "C:\secure\admin-token.txt" -AgentTokenFile "C:\secure\agent-token.txt"

.EXAMPLE
.\scripts\setup-labmanager-lab.ps1 -ConfigPath "C:\Program Files\UpdateAgent\LabManager\appsettings.json" -Port 5080 -DryRun
#>
[CmdletBinding()]
param(
    [string]$ConfigPath,
    [string]$AdminTokenFile,
    [string]$AgentTokenFile,
    [int]$Port = 5080,
    [string]$HealthUrl,
    [string]$TaskName = 'UpdateAgent LabManager (headless)',
    [switch]$SkipConfigure,
    [switch]$SkipFirewall,
    [switch]$SkipAutoStart,
    [switch]$SkipStart,
    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$placeholderTokens = @(
    'change-me-admin-token', 'change-me-agent-token',
    'sample-admin-token-replace-before-pilot', 'sample-agent-token-replace-before-pilot'
)

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'This script must run from an elevated PowerShell (Run as administrator).'
    }
}

function Resolve-ConfigPath {
    param([string]$Explicit)

    if (-not [string]::IsNullOrWhiteSpace($Explicit)) {
        $full = [IO.Path]::GetFullPath($Explicit)
        if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
            throw "LabManager appsettings.json not found: $full"
        }
        return $full
    }

    $candidates = @()
    foreach ($root in @(${env:ProgramFiles}, ${env:ProgramFiles(x86)})) {
        if (-not [string]::IsNullOrWhiteSpace($root)) {
            $candidates += (Join-Path $root 'UpdateAgent\LabManager\appsettings.json')
        }
    }
    $candidates += (Join-Path $repoRoot 'publish\labmanager\appsettings.json')

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return [IO.Path]::GetFullPath($candidate)
        }
    }
    throw 'LabManager appsettings.json not found. Install LabManager, run scripts\publish.ps1, or pass -ConfigPath.'
}

function Read-TokenFile {
    param([string]$Path, [string]$Label)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw "The $Label token file is required unless -SkipConfigure is used."
    }
    $full = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
        throw "$Label token file not found: $full"
    }
    $value = (Get-Content -LiteralPath $full -Raw).Trim().TrimStart([char]0xFEFF)
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "$Label token file is empty: $full"
    }
    if ($placeholderTokens -contains $value) {
        throw "$Label token is a default/sample placeholder. Generate a real token before configuring the lab."
    }
    return $value
}

function Test-Health {
    param([string]$Url)
    try {
        $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 5 -ErrorAction Stop
        return ([int]$response.StatusCode -ge 200 -and [int]$response.StatusCode -lt 300)
    }
    catch {
        return $false
    }
}

Assert-Administrator

if ($Port -lt 1 -or $Port -gt 65535) {
    throw "Port must be between 1 and 65535: $Port"
}

$configPath = Resolve-ConfigPath -Explicit $ConfigPath
$installDir = Split-Path -Parent $configPath
$serverExe = Join-Path $installDir 'UpdateAgent.LabManager.exe'
if ([string]::IsNullOrWhiteSpace($HealthUrl)) {
    $HealthUrl = "http://127.0.0.1:$Port/api/health"
}

if ($installDir -like "*\publish\labmanager") {
    Write-Warning 'Using a publish folder, not the installed product. Install UpdateAgent.LabManager on the teacher PC for a permanent lab host.'
}
if (-not (Test-Path -LiteralPath $serverExe -PathType Leaf)) {
    Write-Warning "Server executable not found beside the config: $serverExe"
}

if (-not $SkipConfigure) {
    $adminToken = Read-TokenFile -Path $AdminTokenFile -Label 'Admin'
    $agentToken = Read-TokenFile -Path $AgentTokenFile -Label 'Agent'
    if ($adminToken -eq $agentToken) {
        throw 'Admin and agent tokens must be different values.'
    }

    if ($DryRun) {
        Write-Host "[DRY RUN] Would configure $configPath (tokens masked) and listen on http://0.0.0.0:$Port"
    }
    else {
        # Pass file paths rather than token values so no secret appears in a process argument list.
        & (Join-Path $PSScriptRoot 'configure-labmanager.ps1') `
            -PublishDir $installDir `
            -AdminTokenFile ([IO.Path]::GetFullPath($AdminTokenFile)) `
            -AgentTokenFile ([IO.Path]::GetFullPath($AgentTokenFile)) `
            -ListenUrl "http://0.0.0.0:$Port"
        Write-Host "Configured $configPath" -ForegroundColor Green
    }
}
else {
    Write-Host 'Skipped token configuration (-SkipConfigure).'
}

if (-not $SkipFirewall) {
    if ($DryRun) {
        Write-Host "[DRY RUN] Would run allow-labmanager.ps1 -Port $Port"
    }
    else {
        & (Join-Path $PSScriptRoot 'allow-labmanager.ps1') -Port $Port
        Write-Host "Firewall rules ensured for TCP $Port (Domain/Private)." -ForegroundColor Green
    }
}
else {
    Write-Host 'Skipped firewall configuration (-SkipFirewall).'
}

if (-not $SkipAutoStart) {
    if (-not (Test-Path -LiteralPath $serverExe -PathType Leaf)) {
        throw "Cannot register auto-start: $serverExe not found. Use -SkipAutoStart or point -ConfigPath at the installed product."
    }
    if ($DryRun) {
        Write-Host "[DRY RUN] Would register scheduled task '$TaskName' (at logon, highest run level)."
    }
    else {
        $existingTask = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
        if ($null -ne $existingTask) {
            Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
        }

        $logonUser = "$env:USERDOMAIN\$env:USERNAME"
        $action = New-ScheduledTaskAction -Execute $serverExe -Argument '--headless' -WorkingDirectory $installDir
        $trigger = New-ScheduledTaskTrigger -AtLogOn -User $logonUser
        $principal = New-ScheduledTaskPrincipal -UserId $logonUser -LogonType Interactive -RunLevel Highest
        $settings = New-ScheduledTaskSettingsSet `
            -AllowStartIfOnBatteries `
            -DontStopIfGoingOnBatteries `
            -StartWhenAvailable `
            -ExecutionTimeLimit (TimeSpan.Zero) `
            -MultipleInstances IgnoreNew

        Register-ScheduledTask `
            -TaskName $TaskName `
            -Action $action `
            -Trigger $trigger `
            -Principal $principal `
            -Settings $settings `
            -Description 'Starts the UpdateAgent LabManager API server in headless mode at teacher logon.' `
            -Force | Out-Null
        Write-Host "Scheduled task registered: $TaskName" -ForegroundColor Green
    }
}
else {
    Write-Host 'Skipped auto-start registration (-SkipAutoStart).'
}

if ($DryRun) {
    Write-Host "[DRY RUN] No changes were made. Health check skipped." -ForegroundColor Yellow
    return
}

$healthy = Test-Health -Url $HealthUrl
if (-not $healthy -and -not $SkipStart) {
    if (-not (Test-Path -LiteralPath $serverExe -PathType Leaf)) {
        throw "Cannot start the server: $serverExe not found."
    }
    Write-Host 'Starting the headless LabManager server...'
    Start-Process -FilePath $serverExe -ArgumentList '--headless' -WorkingDirectory $installDir -WindowStyle Hidden

    $deadline = (Get-Date).AddSeconds(25)
    while (-not (Test-Health -Url $HealthUrl) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
    }
    $healthy = Test-Health -Url $HealthUrl
}

if ($healthy) {
    Write-Host "Health check OK: $HealthUrl" -ForegroundColor Green
}
else {
    Write-Warning "Health check failed: $HealthUrl. Check %LOCALAPPDATA%\UpdateAgent\LabManager\logs and the configured tokens."
}

Write-Host ''
Write-Host 'Teacher PC setup complete.' -ForegroundColor Green
Write-Host "  Config      : $configPath"
Write-Host "  Listen      : http://0.0.0.0:$Port"
Write-Host "  Health      : $HealthUrl"
Write-Host '  DNS alias   : labmanager -> this PC (or use this PC IP in -LabManagerUrl)'
Write-Host '  Next step   : run scripts\deploy-agents.ps1 on the developer PC'
