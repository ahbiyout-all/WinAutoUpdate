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
With -UseEmbeddedToken the token file is not copied and the INI contains only
the LabManager URL; the installer uses its embedded first-install token.
Generation is the default and performs
no remote action apart from a preflight check of LabManager /api/health.
Packages can be copied to each PC (USB/RDP) or wrapped by PsExec/WinRM.
Use -ExecuteLocal to run one package on the current PC.

Generation writes deploy\agents\deployment-report.json. The report contains
file hashes and metadata only; it never contains the token value. -Verify reads
that report and checks the generated files without changing or regenerating
anything. -SkipPreflight bypasses only the network health check; the token file
is always validated for every generated deployment.

.EXAMPLE
.\scripts\deploy-agents.ps1 -InventoryCsv .\scripts\agents.sample.csv -AgentTokenFile .\secure\agent-token.txt

.EXAMPLE
.\scripts\deploy-agents.ps1 -InventoryCsv .\inventory.csv -AgentTokenFile .\secure\agent-token.txt -LabManagerUrl http://192.168.0.10:5080 -ExecuteLocal -ComputerName STUDENT-01

# Use the embedded first-install token when the setup exe was built with secrets.
.\scripts\deploy-agents.ps1 -InventoryCsv .\inventory.csv -UseEmbeddedToken

# Skip the remote health check for an offline staging run.
.\scripts\deploy-agents.ps1 -InventoryCsv .\inventory.csv -AgentTokenFile .\secure\agent-token.txt -SkipPreflight

# Check an existing output folder against its report (no files are modified).
.\scripts\deploy-agents.ps1 -Verify
#>
[CmdletBinding()]
param(
    # Required for generation, not used by -Verify.
    [string]$InventoryCsv,

    [string]$AgentTokenFile,

    [switch]$UseEmbeddedToken,

    # Skips the LabManager /api/health check only; token validation still runs.
    [switch]$SkipPreflight,

    # Checks the deployment report and files without generating or modifying anything.
    [switch]$Verify,

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
    $value = ([string]$firstLine).TrimStart([char]0xFEFF).Trim()
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "Agent token file is empty: $full"
    }
    $lower = $value.ToLowerInvariant()
    if ($lower.StartsWith('change-me-') -or
        ($lower.StartsWith('sample-') -and $lower.EndsWith('-replace-before-pilot'))) {
        throw 'The agent token is a default/sample placeholder. Generate a real token before deployment.'
    }
    # 8 mirrors TokenGuard.MinimumLength; the installer applies the same rule.
    $minimumTokenLength = 8
    if ($value.Length -lt $minimumTokenLength) {
        throw "Agent token is too short (minimum $minimumTokenLength characters): $full"
    }
    return $full
}

function Get-InstallerVersion {
    param([string]$Name)

    if ($Name -match 'Setup-(\d+)\.(\d+)\.(\d+)-x64\.exe$') {
        return "$($Matches[1]).$($Matches[2]).$($Matches[3])"
    }
    return $null
}

function Get-FileSha256 {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "File to hash not found: $Path"
    }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-SafeLabManagerUrl {
    param([string]$Url)

    # Drop userinfo before the URL reaches the report or the health check message.
    return [regex]::Replace($Url, '^([A-Za-z][A-Za-z0-9+.\-]*://)[^/@]*@', '$1')
}

function Invoke-LabManagerHealthCheck {
    param([string]$BaseUrl)

    $healthUrl = "$BaseUrl/api/health"
    $statusCode = 0
    try {
        $response = Invoke-WebRequest -Uri $healthUrl -UseBasicParsing -TimeoutSec 5 -ErrorAction Stop
        $statusCode = [int]$response.StatusCode
    }
    catch {
        $responseProperty = $_.Exception.PSObject.Properties['Response']
        if ($null -ne $responseProperty -and $null -ne $responseProperty.Value) {
            $statusProperty = $responseProperty.Value.PSObject.Properties['StatusCode']
            if ($null -ne $statusProperty -and $null -ne $statusProperty.Value) {
                try { $statusCode = [int]$statusProperty.Value } catch { $statusCode = 0 }
            }
        }
    }
    if ($statusCode -lt 200 -or $statusCode -ge 300) {
        $detail = 'no HTTP response'
        if ($statusCode -gt 0) { $detail = "HTTP $statusCode" }
        throw "Preflight failed: LabManager health check at $healthUrl did not return 2xx ($detail). No packages were created."
    }
}

function Get-RequiredReportValue {
    param([object]$Object, [string]$Name, [string]$Context)

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) {
        throw "$Context is missing required field '$Name'."
    }
    if ($property.Value -is [string] -and [string]::IsNullOrWhiteSpace([string]$property.Value)) {
        throw "$Context has an empty field '$Name'."
    }
    return $property.Value
}

function Test-PackageFolderName {
    param([string]$Name)

    if ([string]::IsNullOrWhiteSpace($Name) -or $Name -match '[\\/]' -or
        $Name -eq '.' -or $Name -eq '..' -or
        $Name -ne [IO.Path]::GetFileName($Name)) {
        return $false
    }
    return $true
}

function Invoke-DeploymentVerification {
    param([string]$ReportPath)

    if (-not (Test-Path -LiteralPath $ReportPath -PathType Leaf)) {
        throw "Deployment report not found: $ReportPath"
    }
    try {
        $report = Get-Content -LiteralPath $ReportPath -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw "Deployment report is not valid JSON: $ReportPath"
    }
    if ($null -eq $report) { throw "Deployment report is empty: $ReportPath" }

    $expectedCountValue = Get-RequiredReportValue -Object $report -Name 'PackageCount' -Context 'Deployment report'
    $expectedCount = 0
    if (-not [int]::TryParse([string]$expectedCountValue, [ref]$expectedCount)) {
        throw "Deployment report has an invalid PackageCount: $expectedCountValue"
    }
    $reportPackagesValue = Get-RequiredReportValue -Object $report -Name 'Packages' -Context 'Deployment report'
    $reportPackages = @($reportPackagesValue)
    if ($expectedCount -ne $reportPackages.Count) {
        throw "PackageCount ($expectedCount) does not match the Packages array ($($reportPackages.Count))."
    }
    if ($expectedCount -eq 0) {
        throw 'Deployment report contains no packages.'
    }

    $reportInstallerName = [string](Get-RequiredReportValue -Object $report -Name 'InstallerName' -Context 'Deployment report')
    $reportInstallerSha = ([string](Get-RequiredReportValue -Object $report -Name 'InstallerSha256' -Context 'Deployment report')).ToLowerInvariant()
    $reportLabManagerUrl = [string](Get-RequiredReportValue -Object $report -Name 'LabManagerUrl' -Context 'Deployment report')

    foreach ($entry in $reportPackages) {
        $computerName = [string](Get-RequiredReportValue -Object $entry -Name 'ComputerName' -Context 'Deployment report package')
        $context = "Package $computerName"
        $packageFolder = [string](Get-RequiredReportValue -Object $entry -Name 'PackageDir' -Context $context)
        if (-not (Test-PackageFolderName -Name $packageFolder)) {
            throw "$context has an unsafe PackageDir: $packageFolder"
        }
        $packageDir = Join-Path $OutputDir $packageFolder
        if (-not (Test-Path -LiteralPath $packageDir -PathType Container)) {
            throw "$context directory not found: $packageDir"
        }

        $installerName = [string](Get-RequiredReportValue -Object $entry -Name 'InstallerName' -Context $context)
        if (-not [string]::Equals($installerName, $reportInstallerName, [StringComparison]::Ordinal)) {
            throw "$context records a different installer name than the deployment report."
        }
        $installerSha = ([string](Get-RequiredReportValue -Object $entry -Name 'InstallerSha256' -Context $context)).ToLowerInvariant()
        if (-not [string]::Equals($installerSha, $reportInstallerSha, [StringComparison]::Ordinal)) {
            throw "$context records a different installer hash than the deployment report."
        }
        $actualInstallerSha = Get-FileSha256 -Path (Join-Path $packageDir $installerName)
        if (-not [string]::Equals($actualInstallerSha, $installerSha, [StringComparison]::Ordinal)) {
            throw "$context installer SHA-256 mismatch: $installerName"
        }

        $configName = [string](Get-RequiredReportValue -Object $entry -Name 'ConfigName' -Context $context)
        $configSha = ([string](Get-RequiredReportValue -Object $entry -Name 'ConfigSha256' -Context $context)).ToLowerInvariant()
        $actualConfigSha = Get-FileSha256 -Path (Join-Path $packageDir $configName)
        if (-not [string]::Equals($actualConfigSha, $configSha, [StringComparison]::Ordinal)) {
            throw "$context config SHA-256 mismatch: $configName"
        }

        $cmdName = [string](Get-RequiredReportValue -Object $entry -Name 'CmdName' -Context $context)
        if (-not [string]::Equals($cmdName, 'install-agent.cmd', [StringComparison]::Ordinal)) {
            throw "$context has an unsafe CmdName: $cmdName"
        }
        $cmdSha = ([string](Get-RequiredReportValue -Object $entry -Name 'CmdSha256' -Context $context)).ToLowerInvariant()
        $actualCmdSha = Get-FileSha256 -Path (Join-Path $packageDir $cmdName)
        if (-not [string]::Equals($actualCmdSha, $cmdSha, [StringComparison]::Ordinal)) {
            throw "$context install wrapper SHA-256 mismatch: $cmdName"
        }

        $tokenPresentRaw = Get-RequiredReportValue -Object $entry -Name 'TokenPresent' -Context $context
        $tokenPresent = $false
        if (-not [bool]::TryParse([string]$tokenPresentRaw, [ref]$tokenPresent)) {
            throw "$context has an invalid TokenPresent value: $tokenPresentRaw"
        }
        $tokenNameProperty = $entry.PSObject.Properties['TokenName']
        $tokenName = ''
        if ($null -ne $tokenNameProperty -and $null -ne $tokenNameProperty.Value) {
            $tokenName = [string]$tokenNameProperty.Value
        }
        $tokenShaProperty = $entry.PSObject.Properties['TokenSha256']
        $tokenSha = ''
        if ($null -ne $tokenShaProperty -and $null -ne $tokenShaProperty.Value) {
            $tokenSha = ([string]$tokenShaProperty.Value).ToLowerInvariant()
        }

        if ($tokenPresent) {
            if ([string]::IsNullOrWhiteSpace($tokenName)) {
                throw "$context is marked TokenPresent but has no TokenName."
            }
            if ([string]::IsNullOrWhiteSpace($tokenSha)) {
                throw "$context is marked TokenPresent but has no TokenSha256."
            }
            $actualTokenSha = Get-FileSha256 -Path (Join-Path $packageDir $tokenName)
            if (-not [string]::Equals($actualTokenSha, $tokenSha, [StringComparison]::Ordinal)) {
                throw "$context token file SHA-256 mismatch: $tokenName"
            }
        }
        else {
            if (-not [string]::IsNullOrWhiteSpace($tokenSha)) {
                throw "$context uses an embedded token but records a TokenSha256."
            }
            $absentTokenName = 'agent-token.txt'
            if (-not [string]::IsNullOrWhiteSpace($tokenName)) { $absentTokenName = $tokenName }
            if (Test-Path -LiteralPath (Join-Path $packageDir $absentTokenName) -PathType Leaf) {
                throw "$context uses an embedded token but contains a token file: $absentTokenName"
            }
        }
    }

    $versionProperty = $report.PSObject.Properties['InstallerVersion']
    $versionText = 'not recorded'
    if ($null -ne $versionProperty -and $null -ne $versionProperty.Value) {
        $versionText = [string]$versionProperty.Value
    }
    Write-Host "Verification succeeded: $($reportPackages.Count) package(s) match the deployment report." -ForegroundColor Green
    Write-Host "  Report: $ReportPath"
    Write-Host "  Installer: $reportInstallerName (version $versionText)"
    Write-Host "  LabManager: $reportLabManagerUrl"
}

if ($Verify) {
    if ($ExecuteLocal) {
        throw '-Verify cannot be combined with -ExecuteLocal.'
    }
    Invoke-DeploymentVerification -ReportPath (Join-Path $OutputDir 'deployment-report.json')
    return
}

if ($UseEmbeddedToken -and -not [string]::IsNullOrWhiteSpace($AgentTokenFile)) {
    throw 'Use either -AgentTokenFile or -UseEmbeddedToken, not both.'
}
if ((-not $UseEmbeddedToken) -and [string]::IsNullOrWhiteSpace($AgentTokenFile)) {
    throw 'Specify -AgentTokenFile, or -UseEmbeddedToken when the installer has a first-install token.'
}

if ($ExecuteLocal) { Assert-Administrator }

$LabManagerUrl = $LabManagerUrl.Trim().TrimEnd('/')
$uri = $null
if (-not [Uri]::TryCreate($LabManagerUrl, [UriKind]::Absolute, [ref]$uri) -or
    ($uri.Scheme -ne 'http' -and $uri.Scheme -ne 'https')) {
    throw "LabManagerUrl must be an absolute http/https URL: $LabManagerUrl"
}
$safeLabManagerUrl = Get-SafeLabManagerUrl -Url $LabManagerUrl

if ([string]::IsNullOrWhiteSpace($InventoryCsv)) {
    throw 'InventoryCsv is required for package generation.'
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
$tokenPath = $null
if (-not $UseEmbeddedToken) {
    $tokenPath = Resolve-AgentTokenFile -Path $AgentTokenFile
}

# Fail closed before creating the output directory or any package.
$preflightPerformed = -not $SkipPreflight
if ($preflightPerformed) {
    Invoke-LabManagerHealthCheck -BaseUrl $safeLabManagerUrl
}

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
set "UPGRADE_FLAG="
if /i "%~1"=="/Upgrade" (
  set "UPGRADE=1"
  set "UPGRADE_FLAG=/AgentTokenFile="
)

if not exist "%SETUP%" (
  echo [UpdateAgent] Installer not found: %SETUP%
  exit /b 2
)
if not exist "%CONFIG%" (
  echo [UpdateAgent] Install config not found: %CONFIG%
  exit /b 2
)
__TOKEN_BLOCK__
echo [UpdateAgent] Installing Agent for __PC__
echo [UpdateAgent] Settings: %CONFIG%
"%SETUP%" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART !UPGRADE_FLAG!
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
__TOKEN_LINE__
'@

if ($UseEmbeddedToken) {
    $tokenBlock = 'rem First-install token is embedded in the setup executable.'
    $tokenLine = ''
}
else {
    $tokenBlock = @'
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
'@
    $tokenLine = 'AgentTokenFile=agent-token.txt'
    # Only an upgrade clears the INI token reference. Passing /AgentTokenFile=
    # on a first install would discard the copied token file and use the
    # installer's embedded (usually empty) token instead.
}

$installerSha256 = Get-FileSha256 -Path $installer.FullName
$installerVersion = Get-InstallerVersion -Name $installer.Name
$reportPackages = @()
$packages = @()
foreach ($target in $targets) {
    $safeName = $target.SafeName
    $packageDir = Join-Path $OutputDir $safeName

    # Regeneration must never leave a stale token copy or an older setup exe behind.
    # Verify the target is a direct child of OutputDir before removing anything.
    $fullPackageDir = [IO.Path]::GetFullPath($packageDir)
    $packageParent = [IO.Path]::GetDirectoryName($fullPackageDir.TrimEnd([IO.Path]::DirectorySeparatorChar))
    if (-not [string]::Equals(
            $packageParent, $OutputDir.TrimEnd([IO.Path]::DirectorySeparatorChar),
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove a package path outside $OutputDir : $fullPackageDir"
    }
    if (Test-Path -LiteralPath $fullPackageDir) {
        if (-not (Test-Path -LiteralPath $fullPackageDir -PathType Container)) {
            throw "Package path is not a directory: $fullPackageDir"
        }
        Remove-Item -LiteralPath $fullPackageDir -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $fullPackageDir | Out-Null

    Copy-Item -LiteralPath $installer.FullName -Destination (Join-Path $fullPackageDir $installer.Name) -Force
    if (-not $UseEmbeddedToken) {
        $tokenCopy = Join-Path $fullPackageDir 'agent-token.txt'
        Copy-Item -LiteralPath $tokenPath -Destination $tokenCopy -Force
        # install-agent.cmd applies the ACL again at install time; restrict the copy now too.
        & icacls.exe $tokenCopy '/inheritance:r' '/grant:r' '*S-1-5-32-544:(F)' '*S-1-5-18:(R)' 2>$null | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Could not restrict ACL on generated token file: $tokenCopy"
        }
        # Keep the restriction above, but let the deploying user hash this copy
        # for the report and for -Verify without requiring elevation. Access is
        # still limited to Administrators, SYSTEM, and the current user.
        $operatorSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        & icacls.exe $tokenCopy '/grant' "*${operatorSid}:(F)" 2>$null | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Could not grant the current user access to the generated token file: $tokenCopy"
        }
    }
    else {
        # The package directory was recreated above, so no earlier token copy can remain.
        $staleTokens = @(Get-ChildItem -LiteralPath $fullPackageDir -Filter 'agent-token.txt' -File -Recurse)
        if ($staleTokens.Count -gt 0) {
            throw "Unexpected token file in an embedded-token package: $($staleTokens[0].FullName)"
        }
    }

    $cmd = $cmdTemplate.
        Replace('__SETUP__', $installer.Name).
        Replace('__PC__', $safeName).
        Replace('__TOKEN_BLOCK__', $tokenBlock)
    $cmd = [regex]::Replace($cmd, "\r?\n", "`r`n")
    [IO.File]::WriteAllText((Join-Path $fullPackageDir 'install-agent.cmd'), $cmd, (New-Object System.Text.UTF8Encoding($false)))

    $ini = [regex]::Replace(
        $iniTemplate.Replace('__URL__', $LabManagerUrl).Replace('__TOKEN_LINE__', $tokenLine),
        "\r?\n", "`r`n")
    [IO.File]::WriteAllText((Join-Path $fullPackageDir 'agent-install.ini'), $ini, (New-Object System.Text.UTF8Encoding($false)))

    $packageInstallerSha = Get-FileSha256 -Path (Join-Path $fullPackageDir $installer.Name)
    $configSha = Get-FileSha256 -Path (Join-Path $fullPackageDir 'agent-install.ini')
    $tokenName = $null
    $tokenSha256 = $null
    $tokenPresent = $false
    if (-not $UseEmbeddedToken) {
        $tokenName = 'agent-token.txt'
        $tokenSha256 = Get-FileSha256 -Path (Join-Path $fullPackageDir $tokenName)
        $tokenPresent = $true
    }
    $reportPackages += [pscustomobject]@{
        ComputerName    = $target.ComputerName
        PackageDir      = $safeName
        InstallerName   = $installer.Name
        InstallerSha256 = $packageInstallerSha
        ConfigName      = 'agent-install.ini'
        ConfigSha256    = $configSha
        CmdName         = 'install-agent.cmd'
        CmdSha256       = Get-FileSha256 -Path (Join-Path $fullPackageDir 'install-agent.cmd')
        TokenName       = $tokenName
        TokenSha256     = $tokenSha256
        TokenPresent    = $tokenPresent
    }

    $packages += [pscustomobject]@{
        ComputerName = $target.ComputerName
        PackageDir   = $fullPackageDir
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
  agent-install.ini                          LabManager URL __CONFIG_TOKEN__
__PACKAGE_TOKEN__  install-agent.cmd                          elevated silent install + token cleanup

Run install-agent.cmd on the matching student PC from an elevated prompt.
The installer reads agent-install.ini from its own folder, so no settings are
passed on the command line. __TOKEN_NOTE__

For an upgrade that needs no token file, run:
  install-agent.cmd /Upgrade
This keeps the existing agent.json when a previous installation is detected.

Protect this output folder and delete it after the rollout is verified.
See docs/DEPLOYMENT.md for the full lab runbook.
'@
if ($UseEmbeddedToken) {
    $readme = $readme.
        Replace('__CONFIG_TOKEN__', '(no token file path)').
        Replace('__PACKAGE_TOKEN__', '').
        Replace('__TOKEN_NOTE__', 'The setup executable contains the first-install token; updates ignore it.')
}
else {
    $readme = $readme.
        Replace('__CONFIG_TOKEN__', '+ token file name').
        Replace('__PACKAGE_TOKEN__', "  agent-token.txt                            shared agent token (do not distribute widely)`n").
        Replace('__TOKEN_NOTE__', 'Generated token copies are restricted to Administrators, SYSTEM, and the deploying user, and the script restricts them again before installing. The token file is deleted after a successful install; if the install fails, the token file is kept for retry.')
}
[IO.File]::WriteAllText((Join-Path $OutputDir 'README.txt'), $readme, (New-Object System.Text.UTF8Encoding($false)))

# Hashes and metadata only: no token value is ever written to this report.
$deploymentReport = [ordered]@{
    GeneratedAtUtc   = [DateTime]::UtcNow.ToString('o')
    InstallerName    = $installer.Name
    InstallerVersion = $installerVersion
    InstallerSha256  = $installerSha256
    LabManagerUrl    = $safeLabManagerUrl
    Preflight        = $preflightPerformed
    PackageCount     = $packages.Count
    Packages         = @($reportPackages)
}
$reportPath = Join-Path $OutputDir 'deployment-report.json'
$reportJson = $deploymentReport | ConvertTo-Json -Depth 6
[IO.File]::WriteAllText($reportPath, $reportJson + "`r`n", (New-Object System.Text.UTF8Encoding($false)))

Write-Host ''
Write-Host "Generated $($packages.Count) agent package(s) in $OutputDir" -ForegroundColor Green
Write-Host "  Installer : $($installer.Name)"
Write-Host "  LabManager: $LabManagerUrl"
foreach ($package in $packages) {
    Write-Host "  $($package.ComputerName) -> $($package.PackageDir)"
}
Write-Host ''
if ($UseEmbeddedToken) {
    Write-Host 'The token is embedded in the setup executable. Protect the output folder and remove it after rollout.' -ForegroundColor Yellow
}
else {
    Write-Host 'Token files are copies of the agent token. Protect the output folder and remove it after rollout.' -ForegroundColor Yellow
}

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
