<#
.SYNOPSIS
Updates the UpdateAgent agent configuration on an installed student PC.

.DESCRIPTION
Targets %ProgramData%\UpdateAgent\agent.json for MSI installations. Falls back to
the legacy script installation (C:\ProgramData\UpdateAgent\app\service\appsettings.json)
when that is what is present, or when -Legacy / -InstallRoot is specified.

Only the values you pass are changed. Optionally restarts the service.

Run from an elevated PowerShell prompt.

.EXAMPLE
.\scripts\configure-agent.ps1 -LabManagerUrl "http://192.168.0.10:5080" -AgentTokenFile ".\agent-token.txt"

.EXAMPLE
.\scripts\configure-agent.ps1 -DisableOverlay -RestartService

.EXAMPLE
.\scripts\configure-agent.ps1 -Legacy -InstallRoot "C:\ProgramData\UpdateAgent\app" -DisableDryRun
#>
[CmdletBinding()]
param(
    [string]$ConfigPath,
    [string]$InstallRoot,
    [switch]$Legacy,
    [string]$DataRoot = "C:\ProgramData\UpdateAgent",
    [string]$ServiceName = "UpdateAgentService",
    [string]$LabManagerUrl,
    [string]$AgentTokenFile,
    [string]$SessionHelperPath,
    [switch]$EnableOverlay,
    [switch]$DisableOverlay,
    [switch]$EnableDryRun,
    [switch]$DisableDryRun,
    [switch]$RestartService
)

$ErrorActionPreference = "Stop"

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "This script must run from an elevated PowerShell (Run as administrator)."
    }
}

function Resolve-Secret {
    param([string]$Value, [string]$File)
    if (-not [string]::IsNullOrWhiteSpace($Value)) { return $Value.Trim() }
    if (-not [string]::IsNullOrWhiteSpace($File)) {
        if (-not (Test-Path -LiteralPath $File)) { throw "Secret file not found: $File" }
        return (Get-Content -LiteralPath $File -Raw).Trim().TrimStart([char]0xFEFF)
    }
    $secure = Read-Host -AsSecureString "Agent token (must match LabManager AgentToken)"
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringAuto($bstr).Trim()
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    }
}

function Set-JsonProperty {
    param($Object, [string]$Name, $Value)
    if ($null -eq $Object) { throw "Target JSON object is null." }
    if ($Object.PSObject.Properties.Name -contains $Name) {
        $Object.$Name = $Value
    }
    else {
        $Object | Add-Member -MemberType NoteProperty -Name $Name -Value $Value
    }
}

function Write-JsonAtomic {
    param([string]$Path, $Object)

    $directory = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }

    $json = $Object | ConvertTo-Json -Depth 12
    $temp = "$Path.tmp"
    [IO.File]::WriteAllText($temp, $json, (New-Object System.Text.UTF8Encoding($false)))
    Move-Item -LiteralPath $temp -Destination $Path -Force
}

Assert-Administrator

if ($EnableOverlay -and $DisableOverlay) {
    throw "Specify only one of -EnableOverlay or -DisableOverlay."
}
if ($EnableDryRun -and $DisableDryRun) {
    throw "Specify only one of -EnableDryRun or -DisableDryRun."
}

$DataRoot = [IO.Path]::GetFullPath($DataRoot)
$msiServiceExe = Join-Path $env:ProgramFiles "UpdateAgent\Service\UpdateAgent.Service.exe"
$msiConfigPath = Join-Path $DataRoot "agent.json"

$legacyRoot = "C:\ProgramData\UpdateAgent\app"
if ($PSBoundParameters.ContainsKey('InstallRoot')) {
    $legacyRoot = [IO.Path]::GetFullPath($InstallRoot)
}
$legacyConfigPath = Join-Path $legacyRoot "service\appsettings.json"

$mode = $null
if (-not [string]::IsNullOrWhiteSpace($ConfigPath)) {
    $ConfigPath = [IO.Path]::GetFullPath($ConfigPath)
    $mode = if ($ConfigPath -like "*\app\service\appsettings.json") { "legacy" } else { "msi" }
}
elseif ($Legacy -or $PSBoundParameters.ContainsKey('InstallRoot')) {
    $ConfigPath = $legacyConfigPath
    $mode = "legacy"
}
elseif (Test-Path -LiteralPath $msiServiceExe) {
    $ConfigPath = $msiConfigPath
    $mode = "msi"
}
elseif (Test-Path -LiteralPath $legacyConfigPath) {
    $ConfigPath = $legacyConfigPath
    $mode = "legacy"
    Write-Warning "MSI installation not found. Using the legacy installation: $legacyConfigPath"
}
else {
    throw "No UpdateAgent installation found. Looked for '$msiServiceExe' and '$legacyConfigPath'. Run install-agent-msi.ps1 or install-service.ps1 first."
}

if ($mode -eq "legacy" -and -not (Test-Path -LiteralPath $ConfigPath)) {
    throw "Agent configuration not found: $ConfigPath. Run install-service.ps1 first."
}

$changes = New-Object System.Collections.Generic.List[string]

$config = $null
if (Test-Path -LiteralPath $ConfigPath) {
    $config = Get-Content -LiteralPath $ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
}
else {
    $config = [pscustomobject]@{}
}
$agent = $config.Agent
if ($null -eq $agent) {
    $agent = [pscustomobject]@{}
    Set-JsonProperty -Object $config -Name "Agent" -Value $agent
}

if ($PSBoundParameters.ContainsKey('LabManagerUrl')) {
    $normalized = $LabManagerUrl.Trim().TrimEnd('/')
    $uri = $null
    if (-not [Uri]::TryCreate($normalized, [UriKind]::Absolute, [ref]$uri) -or
        ($uri.Scheme -ne 'http' -and $uri.Scheme -ne 'https')) {
        throw "LabManagerUrl must be an absolute http/https URL: $normalized"
    }
    Set-JsonProperty $agent "LabManagerUrl" $normalized
    $changes.Add("LabManagerUrl=$normalized")
}

$tokenWasRequested =
    $PSBoundParameters.ContainsKey('AgentTokenFile') -or
    -not [string]::IsNullOrWhiteSpace($env:UPDATEAGENT_AGENT_TOKEN)

if ($tokenWasRequested) {
    $resolvedToken = ''
    if (-not [string]::IsNullOrWhiteSpace($env:UPDATEAGENT_AGENT_TOKEN)) {
        $resolvedToken = $env:UPDATEAGENT_AGENT_TOKEN
    }
    if ([string]::IsNullOrWhiteSpace($resolvedToken) -and -not [string]::IsNullOrWhiteSpace($AgentTokenFile)) {
        $resolvedToken = Resolve-Secret -Value "" -File $AgentTokenFile
    }
    if ([string]::IsNullOrWhiteSpace($resolvedToken)) {
        $resolvedToken = Resolve-Secret -Value "" -File ""
    }
    $resolvedToken = $resolvedToken.Trim().TrimStart([char]0xFEFF)
    if ([string]::IsNullOrWhiteSpace($resolvedToken)) {
        throw "Agent token is empty."
    }
    $lowerToken = $resolvedToken.ToLowerInvariant()
    if ($lowerToken.StartsWith('change-me-') -or
        ($lowerToken.StartsWith('sample-') -and $lowerToken.EndsWith('-replace-before-pilot'))) {
        throw "The agent token is a default/sample placeholder. Generate a real token first."
    }
    Set-JsonProperty $agent "AgentToken" $resolvedToken
    $changes.Add("AgentToken=***")
}

if ($PSBoundParameters.ContainsKey('SessionHelperPath')) {
    $helper = [IO.Path]::GetFullPath($SessionHelperPath)
    if (-not (Test-Path -LiteralPath $helper)) {
        throw "SessionHelper executable not found: $helper"
    }
    Set-JsonProperty $agent "SessionHelperPath" $helper
    $changes.Add("SessionHelperPath=$helper")
}

if ($EnableOverlay) {
    Set-JsonProperty $agent "OverlayEnabled" $true
    $changes.Add("OverlayEnabled=True")
}
if ($DisableOverlay) {
    Set-JsonProperty $agent "OverlayEnabled" $false
    $changes.Add("OverlayEnabled=False")
}
if ($EnableDryRun) {
    Set-JsonProperty $agent "DryRun" $true
    $changes.Add("DryRun=True")
}
if ($DisableDryRun) {
    Set-JsonProperty $agent "DryRun" $false
    $changes.Add("DryRun=False")
}

if ($changes.Count -eq 0) {
    throw "No changes specified. Pass at least one of: -LabManagerUrl, -AgentTokenFile, -SessionHelperPath, -EnableOverlay, -DisableOverlay, -EnableDryRun, -DisableDryRun."
}

Write-JsonAtomic -Path $ConfigPath -Object $config

& icacls $ConfigPath /inheritance:r /grant:r "*S-1-5-32-544:(F)" "*S-1-5-18:(R)" | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Warning "Could not tighten the ACL on $ConfigPath. Check permissions manually."
}

Write-Host "Updated $ConfigPath ($mode mode)" -ForegroundColor Green
foreach ($change in $changes) { Write-Host "  $change" }

if ($RestartService) {
    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if (-not $service) {
        Write-Warning "Service $ServiceName is not installed."
    }
    else {
        Write-Host "Restarting $ServiceName..."
        Restart-Service -Name $ServiceName -Force
        Start-Sleep -Seconds 3
        $service = Get-Service -Name $ServiceName
        Write-Host "Service status: $($service.Status)" -ForegroundColor Green
    }
}
else {
    Write-Host "Restart the service for changes to take effect: Restart-Service $ServiceName"
}
