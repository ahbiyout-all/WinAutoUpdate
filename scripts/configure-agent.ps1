<#
.SYNOPSIS
Updates the UpdateAgent agent configuration on an installed student PC.

.DESCRIPTION
Edits C:\ProgramData\UpdateAgent\app\service\appsettings.json in place.
Only the values you pass are changed. Optionally restarts the service.

Run from an elevated PowerShell prompt.

.EXAMPLE
$env:UPDATEAGENT_AGENT_TOKEN = "<agent-token>"
.\scripts\configure-agent.ps1 -LabManagerUrl "http://192.168.0.10:5080"

.EXAMPLE
.\scripts\configure-agent.ps1 -DisableOverlay -RestartService
#>
[CmdletBinding()]
param(
    [string]$InstallRoot = "C:\ProgramData\UpdateAgent\app",
    [string]$ServiceName = "UpdateAgentService",
    [string]$LabManagerUrl,
    [string]$AgentToken,
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

Assert-Administrator

if ($EnableOverlay -and $DisableOverlay) {
    throw "Specify only one of -EnableOverlay or -DisableOverlay."
}
if ($EnableDryRun -and $DisableDryRun) {
    throw "Specify only one of -EnableDryRun or -DisableDryRun."
}

$InstallRoot = [IO.Path]::GetFullPath($InstallRoot)
$configPath = Join-Path $InstallRoot "service\appsettings.json"
if (-not (Test-Path -LiteralPath $configPath)) {
    throw "Agent configuration not found: $configPath. Run install-service.ps1 first."
}

$changes = New-Object System.Collections.Generic.List[string]
$config = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
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
    $PSBoundParameters.ContainsKey('AgentToken') -or
    $PSBoundParameters.ContainsKey('AgentTokenFile') -or
    -not [string]::IsNullOrWhiteSpace($env:UPDATEAGENT_AGENT_TOKEN)

if ($tokenWasRequested) {
    $resolvedToken = $AgentToken
    if ([string]::IsNullOrWhiteSpace($resolvedToken)) {
        $resolvedToken = $env:UPDATEAGENT_AGENT_TOKEN
    }
    if ([string]::IsNullOrWhiteSpace($resolvedToken) -and -not [string]::IsNullOrWhiteSpace($AgentTokenFile)) {
        $resolvedToken = Resolve-Secret -Value "" -File $AgentTokenFile
    }
    if ([string]::IsNullOrWhiteSpace($resolvedToken)) {
        $resolvedToken = Resolve-Secret -Value "" -File ""
    }
    if ([string]::IsNullOrWhiteSpace($resolvedToken)) {
        throw "Agent token is empty."
    }
    if ($resolvedToken -eq "change-me-agent-token") {
        Write-Warning "The default agent token is still in use."
    }
    Set-JsonProperty $agent "AgentToken" $resolvedToken.Trim()
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
    throw "No changes specified. Pass at least one of: -LabManagerUrl, -AgentToken, -AgentTokenFile, -SessionHelperPath, -EnableOverlay, -DisableOverlay, -EnableDryRun, -DisableDryRun."
}

$json = $config | ConvertTo-Json -Depth 12
[IO.File]::WriteAllText($configPath, $json, (New-Object System.Text.UTF8Encoding($false)))

& icacls $configPath /inheritance:r /grant:r "*S-1-5-32-544:(F)" "*S-1-5-18:(R)" | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Warning "Could not tighten the appsettings.json ACL. Check permissions manually."
}

Write-Host "Updated $configPath" -ForegroundColor Green
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
