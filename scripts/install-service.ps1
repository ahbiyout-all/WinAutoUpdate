<#
.SYNOPSIS
Installs or updates the UpdateAgent agent on a student PC.

.DESCRIPTION
Copies the published service and overlay helper into C:\ProgramData\UpdateAgent\app,
writes the LabManager URL/token into appsettings.json, tightens the config ACL,
and registers the service to start automatically as LocalSystem.

Run from an elevated PowerShell prompt (Run as administrator).

.EXAMPLE
$env:UPDATEAGENT_AGENT_TOKEN = "<agent-token>"
.\scripts\install-service.ps1 -LabManagerUrl "http://192.168.0.10:5080"

.EXAMPLE
.\scripts\install-service.ps1 `
  -LabManagerUrl "http://192.168.0.10:5080" `
  -AgentTokenFile ".\pilot-agent-token.txt" `
  -DryRun
#>
[CmdletBinding()]
param(
    [string]$LabManagerUrl,
    [string]$AgentTokenFile,
    [string]$ServiceSource,
    [string]$HelperSource,
    [string]$InstallRoot = "C:\ProgramData\UpdateAgent\app",
    [string]$ServiceName = "UpdateAgentService",
    [string]$SessionHelperPath,
    [switch]$DisableOverlay,
    [switch]$DryRun,
    [switch]$SkipStart
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

function Resolve-Secret {
    param(
        [string]$Value,
        [string]$File,
        [string]$EnvName,
        [string]$Prompt
    )

    if (-not [string]::IsNullOrWhiteSpace($Value)) { return $Value.Trim() }

    if (-not [string]::IsNullOrWhiteSpace($File)) {
        if (-not (Test-Path -LiteralPath $File)) { throw "Secret file not found: $File" }
        return (Get-Content -LiteralPath $File -Raw).TrimStart([char]0xFEFF).Trim()
    }

    if (-not [string]::IsNullOrWhiteSpace($EnvName)) {
        $fromEnv = [Environment]::GetEnvironmentVariable($EnvName, "Process")
        if (-not [string]::IsNullOrWhiteSpace($fromEnv)) { return $fromEnv.Trim() }
    }

    $secure = Read-Host -AsSecureString $Prompt
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

function Remove-DirectoryWithRetry {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return }

    for ($attempt = 1; $attempt -le 5; $attempt++) {
        try {
            Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
            return
        }
        catch {
            if ($attempt -eq 5) { throw }
            Start-Sleep -Seconds 2
        }
    }
}

Assert-Administrator

if ([string]::IsNullOrWhiteSpace($LabManagerUrl)) {
    throw "LabManagerUrl is required. Example: -LabManagerUrl http://192.168.0.10:5080"
}

$LabManagerUrl = $LabManagerUrl.Trim().TrimEnd('/')
$uri = $null
if (-not [Uri]::TryCreate($LabManagerUrl, [UriKind]::Absolute, [ref]$uri) -or
    ($uri.Scheme -ne 'http' -and $uri.Scheme -ne 'https')) {
    throw "LabManagerUrl must be an absolute http/https URL: $LabManagerUrl"
}

$AgentToken = Resolve-Secret `
    -Value '' `
    -File $AgentTokenFile `
    -EnvName "UPDATEAGENT_AGENT_TOKEN" `
    -Prompt "Agent token (must match LabManager AgentToken)"

if ([string]::IsNullOrWhiteSpace($AgentToken)) {
    throw "Agent token is required."
}
$lowerAgentToken = $AgentToken.ToLowerInvariant()
if ($lowerAgentToken.StartsWith('change-me-') -or
    ($lowerAgentToken.StartsWith('sample-') -and $lowerAgentToken.EndsWith('-replace-before-pilot'))) {
    throw "The agent token is a default/sample placeholder. Generate a real token before rollout."
}

if ([string]::IsNullOrWhiteSpace($ServiceSource)) {
    $ServiceSource = Join-Path $repoRoot "publish\agent\service"
}
if ([string]::IsNullOrWhiteSpace($HelperSource)) {
    $HelperSource = Join-Path $repoRoot "publish\agent\sessionhelper"
}

$ServiceSource = [IO.Path]::GetFullPath($ServiceSource)
$HelperSource = [IO.Path]::GetFullPath($HelperSource)
$InstallRoot = [IO.Path]::GetFullPath($InstallRoot)

$serviceExeName = "UpdateAgent.Service.exe"
$helperExeName = "UpdateAgent.SessionHelper.exe"

if (-not (Test-Path -LiteralPath (Join-Path $ServiceSource $serviceExeName))) {
    throw "Service publish output not found: $ServiceSource\$serviceExeName. Run scripts\publish.ps1 first."
}
if (-not (Test-Path -LiteralPath (Join-Path $HelperSource $helperExeName))) {
    throw "SessionHelper publish output not found: $HelperSource\$helperExeName. Run scripts\publish.ps1 first."
}

# Validate all explicit inputs before stopping the service or removing any files.
$serviceDir = Join-Path $InstallRoot "service"
$helperDir = Join-Path $InstallRoot "sessionhelper"
if ($PSBoundParameters.ContainsKey('SessionHelperPath') -and
    -not [string]::IsNullOrWhiteSpace($SessionHelperPath)) {
    $SessionHelperPath = [IO.Path]::GetFullPath($SessionHelperPath)
    if (-not (Test-Path -LiteralPath $SessionHelperPath)) {
        throw "SessionHelper executable not found: $SessionHelperPath"
    }
}
else {
    # The default path is inside the install root and is validated after files are copied.
    $SessionHelperPath = ''
}
if (-not (Test-Path -LiteralPath (Join-Path $ServiceSource "appsettings.json"))) {
    throw "appsettings.json not found in service publish output: $ServiceSource"
}

Write-Host "Install root : $InstallRoot"
Write-Host "Service      : $ServiceName"
Write-Host "LabManager   : $LabManagerUrl"
Write-Host "Dry run      : $([bool]$DryRun)"
Write-Host "Overlay      : $(-not $DisableOverlay)"
Write-Host ""

$existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existingService -and $existingService.Status -ne 'Stopped') {
    Write-Host "Stopping $ServiceName..."
    Stop-Service -Name $ServiceName -Force
    try {
        $existingService.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
    catch {
        Write-Warning "Service did not report Stopped within 30 seconds; continuing."
    }
}

New-Item -ItemType Directory -Force -Path $InstallRoot | Out-Null
Remove-DirectoryWithRetry $serviceDir
Remove-DirectoryWithRetry $helperDir
New-Item -ItemType Directory -Force -Path $serviceDir, $helperDir | Out-Null

Write-Host "Copying service files..."
Copy-Item -Path (Join-Path $ServiceSource "*") -Destination $serviceDir -Recurse -Force
Write-Host "Copying overlay helper files..."
Copy-Item -Path (Join-Path $HelperSource "*") -Destination $helperDir -Recurse -Force

if ([string]::IsNullOrWhiteSpace($SessionHelperPath)) {
    $SessionHelperPath = Join-Path $helperDir $helperExeName
}
if (-not (Test-Path -LiteralPath $SessionHelperPath)) {
    throw "SessionHelper executable not found: $SessionHelperPath"
}

$configPath = Join-Path $serviceDir "appsettings.json"

$config = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
$agent = $config.Agent
if ($null -eq $agent) {
    $agent = [pscustomobject]@{}
    Set-JsonProperty -Object $config -Name "Agent" -Value $agent
}

Set-JsonProperty $agent "LabManagerUrl" $LabManagerUrl
Set-JsonProperty $agent "AgentToken" $AgentToken
Set-JsonProperty $agent "SessionHelperPath" $SessionHelperPath
Set-JsonProperty $agent "OverlayEnabled" (-not $DisableOverlay)
Set-JsonProperty $agent "DryRun" ([bool]$DryRun)

$json = $config | ConvertTo-Json -Depth 12
# Restrict the temporary file before the token is moved into place.
$configTempPath = "$configPath.tmp"
if (Test-Path -LiteralPath $configTempPath) { Remove-Item -LiteralPath $configTempPath -Force }
[IO.File]::WriteAllText($configTempPath, $json, (New-Object System.Text.UTF8Encoding($false)))
& icacls $configTempPath /inheritance:r /grant:r "*S-1-5-32-544:(F)" "*S-1-5-18:(R)" | Out-Null
if ($LASTEXITCODE -ne 0) {
    Remove-Item -LiteralPath $configTempPath -Force -ErrorAction SilentlyContinue
    throw "Could not tighten the appsettings.json ACL. Configuration was not changed."
}
Move-Item -LiteralPath $configTempPath -Destination $configPath -Force
Write-Host "Wrote agent configuration: $configPath"

$serviceExe = Join-Path $serviceDir $serviceExeName

if (-not $existingService) {
    Write-Host "Creating service $ServiceName..."
    New-Service `
        -Name $ServiceName `
        -BinaryPathName "`"$serviceExe`"" `
        -DisplayName "UpdateAgent Service" `
        -StartupType Automatic | Out-Null
}
else {
    Write-Host "Updating existing service $ServiceName..."
    & sc.exe config $ServiceName binPath= "`"$serviceExe`"" start= auto | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sc.exe config failed for $ServiceName." }
}

& sc.exe description $ServiceName "Windows Update automation agent for computer labs" | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Warning "Could not set service description for $ServiceName." }
& sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Warning "Could not set service failure actions for $ServiceName." }
& sc.exe failureflag $ServiceName 1 | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Warning "Could not set service failure flags for $ServiceName." }

if (-not $SkipStart) {
    Write-Host "Starting $ServiceName..."
    Start-Service -Name $ServiceName
    Start-Sleep -Seconds 3

    $running = Get-Service -Name $ServiceName
    Write-Host "Service status: $($running.Status)" -ForegroundColor Green
    if ($running.Status -ne 'Running') {
        Write-Warning "Service is not running. Check the service log under C:\ProgramData\UpdateAgent\logs."
    }
}
else {
    Write-Host "Service start skipped (-SkipStart)."
}

Write-Host ""
Write-Host "Installation complete." -ForegroundColor Green
Write-Host "  Service exe : $serviceExe"
Write-Host "  Helper exe  : $SessionHelperPath"
Write-Host "  Config      : $configPath"
Write-Host "  Agent data  : C:\ProgramData\UpdateAgent"
Write-Host "  Service log : C:\ProgramData\UpdateAgent\logs\service-*.log"
Write-Host ""
Write-Host "Verify: Get-Service $ServiceName"
Write-Host "Then check the teacher dashboard for this PC (online within ~30 seconds)."
