<#
.SYNOPSIS
Installs or upgrades the UpdateAgent agent from the MSI and applies machine configuration.

.DESCRIPTION
Runs the MSI (minimal UI, or silent with -Quiet), then writes
%ProgramData%\UpdateAgent\agent.json with the LabManager URL and agent token,
restricts the file ACL, enables service failure recovery flags and starts the
service. The token is never passed to msiexec, MSI properties or MSI logs.

Existing agent.json values are preserved on upgrade unless a parameter overrides
them. If a legacy script installation exists, its URL/token are reused when the
parameters are omitted.

If an existing UpdateAgent Agent MSI is already registered, the script asks
whether to overwrite/upgrade, remove it first, or cancel. Use -Force (or -Quiet)
to upgrade without prompting.

Run from an elevated 64-bit PowerShell prompt.

.EXAMPLE
$env:UPDATEAGENT_AGENT_TOKEN = Get-Content ".\agent-token.txt" -TotalCount 1
.\scripts\install-agent-msi.ps1 -LabManagerUrl "http://192.168.0.10:5080" -ConfirmMaestro

.EXAMPLE
.\scripts\install-agent-msi.ps1 `
  -MsiPath ".\artifacts\msi\UpdateAgent.Agent-<version>-x64.msi" `
  -LabManagerUrl "http://192.168.0.10:5080" `
  -AgentTokenFile ".\pilot-agent-token.txt" `
  -ConfirmMaestro -Quiet
#>
[CmdletBinding()]
param(
    [string]$MsiPath,
    [string]$LabManagerUrl,
    [string]$AgentTokenFile,
    [switch]$DryRun,
    [switch]$DisableOverlay,
    [switch]$ConfirmMaestro,
    [switch]$SkipStart,
    [switch]$Quiet,
    [switch]$Force,
    [string]$LogPath,
    [string]$ServiceName = "UpdateAgentService",
    [string]$DataRoot = "C:\ProgramData\UpdateAgent",
    [string]$LegacyInstallRoot = "C:\ProgramData\UpdateAgent\app"
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

function Assert-64BitProcess {
    if (-not [Environment]::Is64BitProcess) {
        throw "Run the 64-bit PowerShell. The MSI is x64-only and would resolve the wrong Program Files folder."
    }
}

function Read-FirstTokenLine {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Secret file not found: $Path" }
    $lines = @(Get-Content -LiteralPath $Path -TotalCount 1)
    if ($lines.Count -eq 0) { return '' }
    return ([string]$lines[0]).TrimStart([char]0xFEFF).Trim()
}

function Resolve-Secret {
    param(
        [string]$Value,
        [string]$File,
        [string]$Prompt,
        [string]$EnvName,
        [switch]$PreferFile
    )

    if (-not [string]::IsNullOrWhiteSpace($Value)) { return $Value.TrimStart([char]0xFEFF).Trim() }

    # An explicitly supplied file always wins over a stale session/CI environment token.
    if ($PreferFile -and -not [string]::IsNullOrWhiteSpace($File)) {
        return Read-FirstTokenLine -Path $File
    }

    if (-not [string]::IsNullOrWhiteSpace($EnvName)) {
        $fromEnv = [Environment]::GetEnvironmentVariable($EnvName)
        if (-not [string]::IsNullOrWhiteSpace($fromEnv)) { return $fromEnv.TrimStart([char]0xFEFF).Trim() }
    }

    if (-not [string]::IsNullOrWhiteSpace($File)) {
        return Read-FirstTokenLine -Path $File
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

function Write-JsonAtomic {
    param([string]$Path, $Object)

    $directory = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }

    $json = $Object | ConvertTo-Json -Depth 12
    $temp = "$Path.tmp"
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force }
    [IO.File]::WriteAllText($temp, $json, (New-Object System.Text.UTF8Encoding($false)))
    # Restrict the temporary file before the secret exists at the final path.
    Set-SecretAcl -Path $temp -ThrowOnFailure
    Move-Item -LiteralPath $temp -Destination $Path -Force
}

function Set-SecretAcl {
    param([string]$Path, [switch]$ThrowOnFailure)

    # The agent token is a secret: Administrators full control, SYSTEM read-only.
    & icacls $Path /inheritance:r /grant:r "*S-1-5-32-544:(F)" "*S-1-5-18:(R)" | Out-Null
    if ($LASTEXITCODE -ne 0) {
        if ($ThrowOnFailure) { throw "Could not tighten the ACL on $Path. Installation aborted." }
        Write-Warning "Could not tighten the ACL on $Path. Check permissions manually."
    }
}

function Test-MaestroInstalled {
    $sources = New-Object System.Collections.Generic.List[string]

    $roots = @($env:ProgramFiles, ${env:ProgramFiles(x86)}) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    foreach ($root in $roots) {
        if (-not (Test-Path -LiteralPath $root)) { continue }
        Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -like "*Maestro*" } |
            ForEach-Object { $sources.Add($_.FullName) }
    }

    Get-Service -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like "*Maestro*" -or $_.DisplayName -like "*Maestro*" } |
        ForEach-Object { $sources.Add("service:$($_.Name)") }

    $uninstallRoots = @(
        "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*",
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*"
    )
    foreach ($root in $uninstallRoots) {
        Get-ItemProperty -Path $root -ErrorAction SilentlyContinue |
            Where-Object { $_.DisplayName -like "*Maestro*" } |
            ForEach-Object { $sources.Add("app:$($_.DisplayName)") }
    }

    return @($sources | Select-Object -Unique)
}

function Get-LatestMsi {
    $candidates = @()

    $artifactRoot = Join-Path $repoRoot "artifacts\msi"
    if (Test-Path -LiteralPath $artifactRoot) {
        $candidates += Get-ChildItem -LiteralPath $artifactRoot -Filter "UpdateAgent.Agent-*-x64.msi" -File
    }

    $builtRoot = Join-Path $repoRoot "installer\UpdateAgent.Agent.Installer\bin\x64\Release"
    if (Test-Path -LiteralPath $builtRoot) {
        $candidates += Get-ChildItem -LiteralPath $builtRoot -Filter "UpdateAgent.Agent-*-x64.msi" -File -Recurse
    }

    $candidates = @($candidates)
    if ($candidates.Count -eq 0) {
        throw "No MSI found. Run scripts\build-msi.ps1 first, or pass -MsiPath."
    }

    $ranked = $candidates | ForEach-Object {
        $parsedVersion = $null
        if ($_.BaseName -match '^UpdateAgent\.Agent-(\d+\.\d+\.\d+)-x64$') {
            $parsedVersion = [version]::Parse($Matches[1])
        }
        [pscustomobject]@{
            FullName   = $_.FullName
            Version    = $parsedVersion
            Modified   = $_.LastWriteTimeUtc
        }
    }

    # 최신 빌드 시각보다 실제 제품 버전을 우선한다. 오래된 버전을 나중에 복사해도 다시 선택되지 않는다.
    return ($ranked |
        Sort-Object -Property @{ Expression = { if ($_.Version) { $_.Version } else { [version]'0.0.0' } }; Descending = $true },
                               @{ Expression = 'Modified'; Descending = $true } |
        Select-Object -First 1).FullName
}

function Get-InstalledAgent {
    # Detect an existing installation through MSI registration data (product code/version) and service presence.
    $uninstallRoots = @(
        "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*",
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*"
    )

    $products = @()
    foreach ($root in $uninstallRoots) {
        $products += Get-ItemProperty -Path $root -ErrorAction SilentlyContinue |
            Where-Object { $_.DisplayName -like "UpdateAgent Agent*" } |
            ForEach-Object {
                $productCode = $_.PSChildName
                $installerType = if ($productCode -like '*_is1' -or
                                     $_.UninstallString -match 'unins') { 'Inno' } else { 'Msi' }
                [pscustomobject]@{
                    ProductCode = $productCode
                    DisplayName = $_.DisplayName
                    DisplayVersion = $_.DisplayVersion
                    UninstallString = $_.UninstallString
                    InstallerType = $installerType
                }
            }
    }

    return @($products | Sort-Object DisplayName -Unique)
}

function Remove-InstalledAgent {
    param([object[]]$Products)

    foreach ($product in $Products) {
        Write-Host "Removing $($product.DisplayName) $($product.DisplayVersion) ($($product.InstallerType))..."

        if ($product.InstallerType -eq 'Inno') {
            $uninstallExe = "$($product.UninstallString)"
            if ($uninstallExe -match '^"([^"]+)"') {
                $uninstallExe = $Matches[1]
            }
            else {
                $uninstallExe = ($uninstallExe -split '\s+/', 2)[0].Trim('"')
            }
            if (-not (Test-Path -LiteralPath $uninstallExe)) {
                # 레지스트리만 남은 경우: 이미 제거된 것으로 보고 설치를 계속한다.
                Write-Warning "Inno uninstaller not found; skipping removal of stale registration: $uninstallExe"
                continue
            }
            $removeProcess = Start-Process -FilePath $uninstallExe `
                -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -Wait -PassThru
            if ($removeProcess.ExitCode -ne 0 -and $removeProcess.ExitCode -ne 3010 -and $removeProcess.ExitCode -ne 1641) {
                throw "Could not remove the existing Inno installation (exit code $($removeProcess.ExitCode))."
            }

            # Inno may keep deleting from a temporary copy after the process exits.
            $innoUninstallKeys = @(
                "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{6F0C2E8A-9B3D-4A71-8E52-1C7D4B9A3F60}_is1",
                "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{6F0C2E8A-9B3D-4A71-8E52-1C7D4B9A3F60}_is1"
            )
            for ($i = 0; $i -lt 30; $i++) {
                $stillRegistered = $false
                foreach ($key in $innoUninstallKeys) {
                    if (Test-Path -LiteralPath $key) { $stillRegistered = $true; break }
                }
                if (-not $stillRegistered) { break }
                Start-Sleep -Seconds 1
            }
            foreach ($key in $innoUninstallKeys) {
                if (Test-Path -LiteralPath $key) {
                    throw "Timed out waiting for the existing Inno installation to finish uninstalling. Registry key still exists: $key"
                }
            }
            continue
        }

        if ($product.ProductCode -notmatch '^\{[0-9A-Fa-f-]{36}\}$') { continue }

        $removeLog = Join-Path $env:TEMP ("UpdateAgent.Agent-remove-{0}.log" -f (Get-Date -Format "yyyyMMdd-HHmmss"))
        $removeArgs = @("/x", $product.ProductCode, "/qn", "/norestart", "/l*v", "`"$removeLog`"")
        $removeProcess = Start-Process -FilePath "msiexec.exe" -ArgumentList $removeArgs -Wait -PassThru
        if ($removeProcess.ExitCode -notin @(0, 1605, 3010, 1641)) {
            throw "Could not remove the existing MSI installation (exit code $($removeProcess.ExitCode)). See $removeLog"
        }
    }
}

Assert-Administrator
Assert-64BitProcess

$MaestroInstall = Test-MaestroInstalled
if ($MaestroInstall.Count -gt 0 -and -not $ConfirmMaestro) {
    throw @"
Maestro was detected on this PC:
  $($MaestroInstall -join "`r`n  ")

Before installing or upgrading:
  1. Set Maestro 'auto restore on every boot' to manual/off.
  2. Run this script again with -ConfirmMaestro.
  3. After installation, save the current state in Maestro and re-enable auto restore.
"@
}

if ([string]::IsNullOrWhiteSpace($MsiPath)) {
    $MsiPath = Get-LatestMsi
}
$MsiPath = [IO.Path]::GetFullPath($MsiPath)
if (-not (Test-Path -LiteralPath $MsiPath)) {
    throw "MSI not found: $MsiPath"
}

$DataRoot = [IO.Path]::GetFullPath($DataRoot)
$configPath = Join-Path $DataRoot "agent.json"
$serviceDirectory = Join-Path $env:ProgramFiles "UpdateAgent\Service"
$sessionHelperPath = Join-Path $env:ProgramFiles "UpdateAgent\SessionHelper\UpdateAgent.SessionHelper.exe"

$existing = $null
if (Test-Path -LiteralPath $configPath) {
    $existing = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
}

$legacyConfigPath = Join-Path $LegacyInstallRoot "service\appsettings.json"
$legacy = $null
if (Test-Path -LiteralPath $legacyConfigPath) {
    $legacy = Get-Content -LiteralPath $legacyConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
}

if ([string]::IsNullOrWhiteSpace($LabManagerUrl)) {
    if ($existing -and -not [string]::IsNullOrWhiteSpace($existing.Agent.LabManagerUrl)) {
        $LabManagerUrl = $existing.Agent.LabManagerUrl
    }
    elseif ($legacy -and -not [string]::IsNullOrWhiteSpace($legacy.Agent.LabManagerUrl)) {
        $LabManagerUrl = $legacy.Agent.LabManagerUrl
        Write-Host "Reusing LabManagerUrl from the legacy installation."
    }
    else {
        throw "LabManagerUrl is required for a first install. Example: -LabManagerUrl http://192.168.0.10:5080"
    }
}

$LabManagerUrl = $LabManagerUrl.Trim().TrimEnd('/')
$uri = $null
if (-not [Uri]::TryCreate($LabManagerUrl, [UriKind]::Absolute, [ref]$uri) -or
    ($uri.Scheme -ne 'http' -and $uri.Scheme -ne 'https')) {
    throw "LabManagerUrl must be an absolute http/https URL: $LabManagerUrl"
}

if ($PSBoundParameters.ContainsKey('AgentTokenFile') -and
    [string]::IsNullOrWhiteSpace($AgentTokenFile)) {
    throw "AgentTokenFile was explicitly empty. Supply a token file path or omit the parameter."
}

$tokenProvided =
    -not [string]::IsNullOrWhiteSpace($AgentTokenFile) -or
    -not [string]::IsNullOrWhiteSpace($env:UPDATEAGENT_AGENT_TOKEN)

if ($tokenProvided) {
    $resolvedToken = Resolve-Secret `
        -Value '' `
        -EnvName 'UPDATEAGENT_AGENT_TOKEN' `
        -File $AgentTokenFile `
        -PreferFile:$PSBoundParameters.ContainsKey('AgentTokenFile') `
        -Prompt "Agent token (must match LabManager AgentToken)"
}
elseif ($existing -and -not [string]::IsNullOrWhiteSpace($existing.Agent.AgentToken)) {
    $resolvedToken = $existing.Agent.AgentToken
    Write-Host "Reusing the existing agent token from agent.json."
}
elseif ($legacy -and -not [string]::IsNullOrWhiteSpace($legacy.Agent.AgentToken)) {
    $resolvedToken = $legacy.Agent.AgentToken
    Write-Host "Reusing the agent token from the legacy installation."
}
else {
    $resolvedToken = Resolve-Secret `
        -Value "" `
        -EnvName 'UPDATEAGENT_AGENT_TOKEN' `
        -File "" `
        -Prompt "Agent token (must match LabManager AgentToken)"
}

if ([string]::IsNullOrWhiteSpace($resolvedToken)) {
    throw "Agent token is required."
}
$normalizedToken = $resolvedToken.TrimStart([char]0xFEFF).Trim()
$lowerToken = $normalizedToken.ToLowerInvariant()
if ($lowerToken.StartsWith('change-me-') -or
    ($lowerToken.StartsWith('sample-') -and $lowerToken.EndsWith('-replace-before-pilot'))) {
    throw "Placeholder agent tokens are not allowed. Supply a real token file or UPDATEAGENT_AGENT_TOKEN."
}
$resolvedToken = $normalizedToken

# All inputs are now validated: only after this point may an existing installation
# be removed. Config/legacy tokens were read above so an uninstall cannot lose them.
# MSI MajorUpgrade cannot remove an existing Inno installation, so silently remove it first.
$installedProducts = @(Get-InstalledAgent)
$innoProducts = @($installedProducts | Where-Object { $_.InstallerType -eq 'Inno' })
$msiProducts = @($installedProducts | Where-Object { $_.InstallerType -ne 'Inno' })

if ($innoProducts.Count -gt 0) {
    Write-Host ""
    Write-Host "An existing Inno Setup installation was found. Removing it before MSI install:" -ForegroundColor Yellow
    foreach ($product in $innoProducts) {
        Write-Host ("  {0} {1} ({2})" -f $product.DisplayName, $product.DisplayVersion, $product.ProductCode)
    }
    Remove-InstalledAgent -Products $innoProducts
    Write-Host ""
}

# The same MSI product can be handled by MajorUpgrade, so do not prompt with -Quiet/-Force.
if ($msiProducts.Count -gt 0) {
    Write-Host ""
    Write-Host "An existing UpdateAgent Agent MSI was found:" -ForegroundColor Yellow
    foreach ($product in $msiProducts) {
        Write-Host ("  {0} {1} ({2})" -f $product.DisplayName, $product.DisplayVersion, $product.ProductCode)
    }
    Write-Host ""

    if ($Quiet -or $Force) {
        Write-Host "Upgrading the existing installation without prompting (-Quiet/-Force)."
    }
    else {
        Write-Host "  [1] Overwrite / upgrade (recommended)"
        Write-Host "  [2] Remove the existing version first, then install"
        Write-Host "  [3] Cancel"
        $choice = (Read-Host "Choose 1, 2 or 3").Trim()

        switch ($choice) {
            "1" { Write-Host "Upgrading the existing installation." }
            "2" { Remove-InstalledAgent -Products $msiProducts }
            "3" { throw "Installation cancelled by the user." }
            default { throw "Invalid choice. Installation cancelled." }
        }
    }
    Write-Host ""
}

$msiArguments = @("/i", "`"$MsiPath`"", "/norestart")
if ($Quiet) {
    $msiArguments += "/qn"
}
if ([string]::IsNullOrWhiteSpace($LogPath)) {
    $LogPath = Join-Path $env:TEMP ("UpdateAgent.Agent-msi-{0}.log" -f (Get-Date -Format "yyyyMMdd-HHmmss"))
}
$LogPath = [IO.Path]::GetFullPath($LogPath)
$logDirectory = Split-Path -Parent $LogPath
if (-not (Test-Path -LiteralPath $logDirectory)) {
    New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
}
$msiArguments += @("/l*v", "`"$LogPath`"")

Write-Host "MSI          : $MsiPath"
Write-Host "LabManager   : $LabManagerUrl"
Write-Host "Config       : $configPath"
Write-Host "Interface    : $(if ($Quiet) { 'silent (/qn)' } else { 'minimal UI' })"
Write-Host "MSI log      : $LogPath"
Write-Host ""

if ($legacy) {
    Write-Host "Legacy installation detected: $LegacyInstallRoot" -ForegroundColor Yellow
    Write-Host "URL/token were reused when available. Remove the legacy folder only after verification." -ForegroundColor Yellow
    Write-Host ""
}

$process = Start-Process -FilePath "msiexec.exe" -ArgumentList $msiArguments -Wait -PassThru
$exitCode = $process.ExitCode

switch ($exitCode) {
    0     { Write-Host "MSI installation succeeded." }
    3010  { Write-Warning "MSI installation succeeded. A reboot is recommended to finish." }
    1641  { Write-Warning "MSI installation succeeded and requested a restart." }
    1638  { throw "Another version of UpdateAgent Agent is already registered with this MSI. Use a newer version for an upgrade, or repair/uninstall it first. See $LogPath" }
    default { throw "msiexec failed with exit code $exitCode. See $LogPath" }
}

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $service) {
    throw "MSI completed but service '$ServiceName' was not registered. See $LogPath"
}

$agentConfig = $null
if ($existing) {
    $agentConfig = $existing
}
else {
    $agentConfig = [pscustomobject]@{}
}
$agent = $agentConfig.Agent
if ($null -eq $agent) {
    $agent = [pscustomobject]@{}
    Set-JsonProperty -Object $agentConfig -Name "Agent" -Value $agent
}

Set-JsonProperty $agent "LabManagerUrl" $LabManagerUrl
Set-JsonProperty $agent "AgentToken" $resolvedToken.Trim()
Set-JsonProperty $agent "SessionHelperPath" $sessionHelperPath

if (-not (Test-Path -LiteralPath $sessionHelperPath)) {
    Write-Warning "SessionHelper not found: $sessionHelperPath (the overlay will not appear)."
}

# Only override these when asked; otherwise keep existing values (appsettings defaults apply when absent).
if ($DisableOverlay) {
    Set-JsonProperty $agent "OverlayEnabled" $false
}
if ($DryRun) {
    Set-JsonProperty $agent "DryRun" $true
}

Write-JsonAtomic -Path $configPath -Object $agentConfig

# util:ServiceConfig sets failure actions; it cannot set the non-crash failure flag.
& sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Warning "Could not set service failure actions. Run 'sc.exe failure $ServiceName ...' manually."
}
& sc.exe failureflag $ServiceName 1 | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Warning "Could not set service failure flags. Run 'sc.exe failureflag $ServiceName 1' manually."
}

if ($SkipStart) {
    Write-Host "Service start skipped (-SkipStart)." -ForegroundColor Yellow
}
else {
    Write-Host "Restarting $ServiceName..."
    if ($service.Status -ne 'Stopped') {
        Restart-Service -Name $ServiceName -Force
    }
    else {
        Start-Service -Name $ServiceName
    }

    Start-Sleep -Seconds 3
    $service = Get-Service -Name $ServiceName
    Write-Host "Service status: $($service.Status)" -ForegroundColor Green
    if ($service.Status -ne 'Running') {
        Write-Warning "Service is not running. Check $DataRoot\logs and the MSI log."
    }
}

if ($legacy) {
    Write-Host ""
    Write-Host "After verifying the dashboard shows this PC online:" -ForegroundColor Yellow
    Write-Host "  Remove-Item `"$LegacyInstallRoot`" -Recurse -Force"
    Write-Host "  Then save the current state in Maestro and re-enable auto restore."
}

Write-Host ""
Write-Host "Installation complete." -ForegroundColor Green
Write-Host "  Service exe : $serviceDirectory\UpdateAgent.Service.exe"
Write-Host "  Helper exe  : $sessionHelperPath"
Write-Host "  Config      : $configPath (token hidden by ACL)"
Write-Host "  Agent data  : $DataRoot"
Write-Host "  Service log : $DataRoot\logs\service-*.log"
Write-Host "  MSI log     : $LogPath"
Write-Host ""
Write-Host "Verify: Get-Service $ServiceName"
Write-Host "The teacher dashboard should show this PC online within ~30 seconds."
