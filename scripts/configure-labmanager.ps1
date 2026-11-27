<#
.SYNOPSIS
Configures the published UpdateAgent LabManager for a pilot.

.DESCRIPTION
Writes the admin token, agent token and listen URL into
publish\labmanager\appsettings.json. Run this before starting the LabManager.

.EXAMPLE
$env:UPDATEAGENT_ADMIN_TOKEN = "<admin-token>"
$env:UPDATEAGENT_AGENT_TOKEN = "<agent-token>"
.\scripts\configure-labmanager.ps1

.EXAMPLE
.\scripts\configure-labmanager.ps1 -ListenUrl "http://0.0.0.0:5080" -AdminTokenFile ".\pilot-admin-token.txt" -AgentTokenFile ".\pilot-agent-token.txt"
#>
[CmdletBinding()]
param(
    [string]$PublishDir,
    [string]$AdminTokenFile,
    [string]$AgentTokenFile,
    [string]$ListenUrl = "http://0.0.0.0:5080",
    [switch]$AllowDefaultTokens
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($PublishDir)) {
    $PublishDir = Join-Path $repoRoot "publish\labmanager"
}
$PublishDir = [IO.Path]::GetFullPath($PublishDir)

function Read-FirstTokenLine {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Secret file not found: $Path" }
    $lines = @(Get-Content -LiteralPath $Path -TotalCount 1)
    if ($lines.Count -eq 0) { return '' }
    return ([string]$lines[0]).TrimStart([char]0xFEFF).Trim()
}

function Resolve-Secret {
    param([string]$Value, [string]$File, [string]$EnvName, [string]$Prompt)
    if (-not [string]::IsNullOrWhiteSpace($Value)) { return $Value.TrimStart([char]0xFEFF).Trim() }
    if (-not [string]::IsNullOrWhiteSpace($File)) {
        return Read-FirstTokenLine -Path $File
    }
    if (-not [string]::IsNullOrWhiteSpace($EnvName)) {
        $fromEnv = [Environment]::GetEnvironmentVariable($EnvName, "Process")
        if (-not [string]::IsNullOrWhiteSpace($fromEnv)) { return $fromEnv.TrimStart([char]0xFEFF).Trim() }
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

function Write-ConfigAtomic {
    param([string]$Path, $Object)

    $json = $Object | ConvertTo-Json -Depth 12
    $temp = "$Path.tmp"
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force }
    [IO.File]::WriteAllText($temp, $json, (New-Object System.Text.UTF8Encoding($false)))

    # Tokens are secrets: do not rely on inherited ACLs, and keep the app's own
    # account able to read the installed configuration.
    $currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    & icacls $temp /inheritance:r /grant:r "*S-1-5-32-544:(F)" "*S-1-5-18:(R)" "*${currentSid}:(R)" | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue
        throw "Could not tighten the ACL on $temp. Configuration was not changed."
    }
    Move-Item -LiteralPath $temp -Destination $Path -Force
}

$configPath = Join-Path $PublishDir "appsettings.json"
if (-not (Test-Path -LiteralPath $configPath)) {
    throw "LabManager appsettings.json not found: $configPath. Run scripts\publish.ps1 first."
}

# 토큰 값은 명령줄에 받지 않는다(프로세스 목록/로그 유출 방지).
$AdminToken = Resolve-Secret `
    -Value '' `
    -File $AdminTokenFile `
    -EnvName "UPDATEAGENT_ADMIN_TOKEN" `
    -Prompt "Admin token (dashboard/API login)"

$AgentToken = Resolve-Secret `
    -Value '' `
    -File $AgentTokenFile `
    -EnvName "UPDATEAGENT_AGENT_TOKEN" `
    -Prompt "Agent token (must match every student PC)"

if ([string]::IsNullOrWhiteSpace($AdminToken)) { throw "Admin token is required." }
if ([string]::IsNullOrWhiteSpace($AgentToken)) { throw "Agent token is required." }

# Role separation: X-Admin-Token and X-Agent-Token must never carry the same value.
if (-not [string]::IsNullOrWhiteSpace($AdminToken) -and
    -not [string]::IsNullOrWhiteSpace($AgentToken)) {
    $normalizedAdminToken = $AdminToken.TrimStart([char]0xFEFF).Trim()
    $normalizedAgentToken = $AgentToken.TrimStart([char]0xFEFF).Trim()
    if ($normalizedAdminToken -ceq $normalizedAgentToken) {
        throw "Admin and agent tokens must be different values."
    }
}

function Test-PlaceholderToken {
    param([string]$Value)
    $lower = $Value.ToLowerInvariant()
    return ($lower.StartsWith('change-me-') -or
        ($lower.StartsWith('sample-') -and $lower.EndsWith('-replace-before-pilot')))
}

if (-not $AllowDefaultTokens -and
    (Test-PlaceholderToken $AdminToken -or Test-PlaceholderToken $AgentToken)) {
    throw "A default or sample token is still in use. Generate new tokens for the pilot, or pass -AllowDefaultTokens for local development only."
}

$uri = $null
if (-not [Uri]::TryCreate($ListenUrl, [UriKind]::Absolute, [ref]$uri) -or
    ($uri.Scheme -ne 'http' -and $uri.Scheme -ne 'https')) {
    throw "ListenUrl must be an absolute http/https URL: $ListenUrl"
}

$config = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
$lab = $config.LabManager
if ($null -eq $lab) {
    $lab = [pscustomobject]@{}
    Set-JsonProperty -Object $config -Name "LabManager" -Value $lab
}

Set-JsonProperty $lab "AdminToken" $AdminToken
Set-JsonProperty $lab "AgentToken" $AgentToken
Set-JsonProperty $lab "AllowDefaultTokens" ([bool]$AllowDefaultTokens)
Set-JsonProperty $config "Urls" $ListenUrl

Write-ConfigAtomic -Path $configPath -Object $config

Write-Host "Updated $configPath" -ForegroundColor Green
Write-Host "  AdminToken = ***"
Write-Host "  AgentToken = ***"
Write-Host "  Urls       = $ListenUrl"
Write-Host ""
Write-Host "Start the dashboard from its own folder so appsettings.json is picked up:"
Write-Host "  Set-Location `"$PublishDir`""
Write-Host "  .\UpdateAgent.LabManager.exe"
Write-Host ""
Write-Host "The dashboard is served at $ListenUrl (open http://<teacher-ip>:5080 from a browser)."
