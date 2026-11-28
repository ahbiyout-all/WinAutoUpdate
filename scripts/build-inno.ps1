<#
.SYNOPSIS
Builds the UpdateAgent Inno Setup installers from the published output.

.DESCRIPTION
Stages publish\ into artifacts\inno\stage, removes debug artifacts, documentation
and portable/archive files, verifies that only runtime files are included, then
compiles the installers with Inno Setup 6:

  artifacts\inno\UpdateAgent.Agent-Setup-<version>-x64.exe
  artifacts\inno\UpdateAgent.LabManager-Setup-<version>-x64.exe

With -AgentOnly only the student-PC installer is staged and compiled.

Each installer installs individual (unpacked, multi-file) application folders.
LabManager URL is never embedded. When the UPDATEAGENT_* token environment
variables are set, the setup files include a first-install-only token file:
  agent-token.txt      single line, used by the Agent installer on first install
  labmanager-token.txt two lines (AdminToken, AgentToken), first install only
Updates ignore these embedded files and keep the existing configuration.

Run from a normal PowerShell prompt. Inno Setup 6 must be installed.

.PARAMETER Sign
Optional Authenticode signing. With -Sign, staged UpdateAgent assemblies are
signed before ISCC runs, the final setup EXEs are signed after ISCC, and every
signed file is verified with "signtool verify /pa /all". Without -Sign the
build is unsigned and does not require signtool.exe.

.PARAMETER CertificateThumbprint
SHA-1 thumbprint of the code signing certificate. When omitted, the helper
reads UPDATEAGENT_SIGN_THUMBPRINT from the environment. No thumbprint is
stored in the repository.

.PARAMETER TimestampUrl
Optional RFC 3161 timestamp URL passed to signtool.

.PARAMETER FrameworkDependent
Opt in to packaging the smaller framework-dependent publish output. The default
stays self-contained. When this script has to publish, the switch is forwarded
to publish.ps1. Existing publish folders and staged files are validated against
the requested mode; ambiguity fails closed. Framework-dependent packages require
the matching Microsoft .NET Desktop Runtime (win-x64) on every target PC.

.EXAMPLE
.\scripts\publish.ps1
.\scripts\build-inno.ps1

.EXAMPLE
.\scripts\build-inno.ps1 -AgentOnly

.EXAMPLE
.\scripts\build-inno.ps1 -NoPublish -SkipValidation

.EXAMPLE
$env:UPDATEAGENT_SIGN_THUMBPRINT = '<thumbprint-from-secret-store>'
.\scripts\build-inno.ps1 -Sign -TimestampUrl "http://timestamp.digicert.com"

.EXAMPLE
.\scripts\build-inno.ps1 -IsccPath "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
#>
[CmdletBinding()]
param(
    [string]$ProductVersion,
    [string]$PublishRoot,
    [string]$OutputRoot,
    [string]$StagingRoot,
    [string]$IsccPath,
    [switch]$NoPublish,
    [switch]$AgentOnly,
    [switch]$KeepStaging,
    [switch]$SkipValidation,
    [switch]$FrameworkDependent,
    [switch]$Sign,
    [string]$CertificateThumbprint,
    [string]$TimestampUrl
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot

# Shared runtime-layout checks; no tokens, URLs or config secrets are inspected.
. (Join-Path $PSScriptRoot "package-layout.ps1")

if ([string]::IsNullOrWhiteSpace($PublishRoot)) { $PublishRoot = Join-Path $repoRoot "publish" }
if ([string]::IsNullOrWhiteSpace($OutputRoot))  { $OutputRoot  = Join-Path $repoRoot "artifacts\inno" }
if ([string]::IsNullOrWhiteSpace($StagingRoot)) { $StagingRoot = Join-Path $repoRoot "artifacts\inno\stage" }

$PublishRoot = [IO.Path]::GetFullPath($PublishRoot)
$OutputRoot  = [IO.Path]::GetFullPath($OutputRoot)
$StagingRoot = [IO.Path]::GetFullPath($StagingRoot)

$signingContext = $null
if ($Sign) {
    . (Join-Path $PSScriptRoot "sign-artifacts.ps1")
    $signingContext = New-SigningContext -Sign -CertificateThumbprint $CertificateThumbprint -TimestampUrl $TimestampUrl
}

# Any terminating failure removes the staging tree so embedded token files never
# remain on disk. -KeepStaging explicitly opts out for debugging.
trap {
    if (-not $KeepStaging -and (Test-Path -LiteralPath $StagingRoot)) {
        Remove-Item -LiteralPath $StagingRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    throw
}

$innoDir = Join-Path $repoRoot "installer\InnoSetup"
$agentIss = Join-Path $innoDir "UpdateAgent.Agent.iss"
$labManagerIss = Join-Path $innoDir "UpdateAgent.LabManager.iss"
$noticeRtf = Join-Path $repoRoot "installer\UpdateAgent.Agent.Installer\payload\notice.ko.rtf"
$payloadAppSettings = Join-Path $repoRoot "installer\UpdateAgent.Agent.Installer\payload\appsettings.json"

$bundledAgentTokenName = 'agent-token.txt'
$bundledLabManagerTokenName = 'labmanager-token.txt'
$allowedBundledTokenNames = @($bundledAgentTokenName, $bundledLabManagerTokenName)

function Get-VersionFromBuildProps {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Directory.Build.props not found: $Path"
    }

    $text = Get-Content -LiteralPath $Path -Raw
    if ($text -notmatch '<Version>\s*([^<]+?)\s*</Version>') {
        throw "No <Version> element found in $Path."
    }

    return ($Matches[1] -split '[+-]')[0]
}

function ConvertTo-ThreePartVersion {
    param([string]$Value)

    if ($Value -notmatch '^\d+(\.\d+){0,2}$') {
        throw "Version must look like Major.Minor.Build (digits only): $Value"
    }

    $parts = @($Value.Split('.'))
    while ($parts.Count -lt 3) { $parts += '0' }
    return ($parts[0..2] -join '.')
}

function Get-FirstLine {
    param([string]$Value)

    if ($null -eq $Value) { return '' }
    $lines = @($Value -split '[\r\n]+')
    if ($lines.Count -eq 0) { return '' }
    # TokenGuard.Normalize in the server trims whitespace and a leading BOM in
    # that order. PowerShell 7 (the release runner) does not treat U+FEFF as
    # whitespace, so strip it explicitly before checking placeholders/length.
    return ([string]$lines[0]).Trim().TrimStart([char]0xFEFF).Trim()
}

function Assert-BundledTokenValue {
    param([string]$Value, [string]$Name)

    $token = Get-FirstLine $Value
    if ([string]::IsNullOrWhiteSpace($token)) {
        throw "$Name is empty. Set a real token before building a release installer."
    }
    $lower = $token.ToLowerInvariant()
    if ($lower.StartsWith('change-me-') -or
        ($lower.StartsWith('sample-') -and $lower.EndsWith('-replace-before-pilot'))) {
        throw "$Name is still a placeholder. Set a real token before building a release installer."
    }
    # 8 mirrors TokenGuard.MinimumLength; the server rejects shorter tokens.
    $minimumTokenLength = 8
    if ($token.Length -lt $minimumTokenLength) {
        throw "$Name is too short (minimum $minimumTokenLength characters). Set a real token before building a release installer."
    }
    return $token
}

function Write-BundledTokenFile {
    param([string]$Path, [string[]]$Lines)

    $content = ''
    if ($Lines.Count -gt 0) {
        $content = ($Lines -join "`r`n") + "`r`n"
    }
    [IO.File]::WriteAllText($Path, $content, (New-Object System.Text.UTF8Encoding($false)))
}

function Assert-PublishFolder {
    param([string]$Path, [string]$ExeName)

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Publish folder not found: $Path. Run scripts\publish.ps1 first."
    }
    if (-not (Test-Path -LiteralPath (Join-Path $Path $ExeName))) {
        throw "Publish output is incomplete (missing $ExeName): $Path. Run scripts\publish.ps1 first."
    }
}

function Assert-InnoScriptBom {
    param([string]$Path)

    $bytes = [IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 3 -or $bytes[0] -ne 0xEF -or $bytes[1] -ne 0xBB -or $bytes[2] -ne 0xBF) {
        throw "Inno Setup script must be saved as UTF-8 with BOM: $Path"
    }
}

function Assert-InnoFallbackVersion {
    param([string]$Path, [string]$Expected)

    $text = Get-Content -LiteralPath $Path -Raw
    if ($text -notmatch '#define\s+AppVersion\s+"([^"]+)"') {
        throw "No fallback AppVersion define found in $Path."
    }
    $fallback = $Matches[1].Trim()
    if ($fallback -ne $Expected) {
        throw ("Stale fallback AppVersion in {0}: {1} (expected {2})." -f $Path, $fallback, $Expected)
    }
}

function Find-Iscc {
    param([string]$Explicit)

    if (-not [string]::IsNullOrWhiteSpace($Explicit)) {
        if (-not (Test-Path -LiteralPath $Explicit -PathType Leaf)) {
            throw "ISCC.exe not found: $Explicit"
        }
        return [IO.Path]::GetFullPath($Explicit)
    }

    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe")
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }

    $command = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    # 마지막으로 설치 폴더를 직접 검색한다(러너의 설치 경로 차이 대응).
    foreach ($root in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) {
        if ([string]::IsNullOrWhiteSpace($root)) { continue }
        $found = Get-ChildItem -LiteralPath $root -Filter "ISCC.exe" -Recurse -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($found) { return $found.FullName }
    }

    throw "Inno Setup 6 compiler (ISCC.exe) was not found. Install Inno Setup 6 or pass -IsccPath."
}

function Get-BlockedEntries {
    param([string]$Root)

    # 인스톨러에는 실행에 필요한 런타임 파일만 넣는다.
    # 문서, 심볼, 로그, 포터블/압축 파일, 바로가기는 패키징하지 않는다.
    $blockedExtensions = @(
        ".pdb", ".xml", ".md", ".txt", ".rtf", ".pdf", ".doc", ".docx",
        ".log", ".tmp", ".msi", ".msix", ".cab", ".zip", ".7z", ".rar",
        ".lnk", ".url", ".br", ".gz"
    )
    $blockedFolders = @("docs", "doc", "samples", "examples", "portable")
    # UseStaticFiles가 원본 파일을 제공하므로 빌드가 만든 정적 자산 매니페스트는 설치하지 않는다.
    $blockedNames = @("web.config", "updateagent.labmanager.staticwebassets.endpoints.json")

    $blocked = New-Object System.Collections.Generic.List[string]

    Get-ChildItem -LiteralPath $Root -Recurse -File | ForEach-Object {
        $relative = $_.FullName.Substring($Root.Length).TrimStart('\', '/')
        $segments = @($relative -split '[\\/]')
        $inBlockedFolder = $false
        if ($segments.Count -gt 1) {
            $folderSegments = $segments[0..($segments.Count - 2)]
            $inBlockedFolder = [bool]($folderSegments |
                Where-Object { $blockedFolders -contains $_.ToLowerInvariant() })
        }

        $isBundledToken = $allowedBundledTokenNames -contains $_.Name.ToLowerInvariant()
        if (-not $isBundledToken -and
            ($inBlockedFolder -or
             $blockedNames -contains $_.Name.ToLowerInvariant() -or
             $blockedExtensions -contains $_.Extension.ToLowerInvariant())) {
            $blocked.Add($relative)
        }
    }

    return $blocked
}

function Remove-UnwantedStagedFiles {
    param([string]$Root)

    $blocked = @(Get-BlockedEntries -Root $Root)
    foreach ($relative in $blocked) {
        Remove-Item -LiteralPath (Join-Path $Root $relative) -Force
    }

    return $blocked
}

function Assert-IncludedFileSet {
    param([string]$Root, [string[]]$RequiredFiles, [string]$Label)

    $blocked = @(Get-BlockedEntries -Root $Root)
    if ($blocked.Count -gt 0) {
        throw ("{0}: non-runtime files are still staged: {1}" -f $Label, ($blocked -join ", "))
    }

    foreach ($required in $RequiredFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $Root $required))) {
            throw ("{0}: required file is missing from the staging set: {1}" -f $Label, $required)
        }
    }

    $files = @(Get-ChildItem -LiteralPath $Root -Recurse -File)
    $size = ($files | Measure-Object -Property Length -Sum).Sum
    Write-Host ("  {0}: {1} files, {2:N1} MB" -f $Label, $files.Count, ($size / 1MB))
}

if ([string]::IsNullOrWhiteSpace($ProductVersion)) {
    $ProductVersion = Get-VersionFromBuildProps (Join-Path $repoRoot "Directory.Build.props")
}
$ProductVersion = ConvertTo-ThreePartVersion $ProductVersion

$iscc = Find-Iscc -Explicit $IsccPath
# iscc /? writes usage to stderr; on Windows PowerShell 5.1 with $ErrorActionPreference
# set to Stop this becomes a terminating error, so relax it only while probing.
$previousErrorAction = $ErrorActionPreference
try {
    $ErrorActionPreference = 'SilentlyContinue'
    $isccVersionText = (& $iscc /? 2>&1 | Select-Object -First 1)
}
finally {
    $ErrorActionPreference = $previousErrorAction
}
if ($isccVersionText -notmatch 'Inno Setup\s+6') {
    throw "Inno Setup 6 compiler is required (found: $isccVersionText): $iscc"
}

if (-not (Test-Path -LiteralPath $agentIss)) { throw "Inno script not found: $agentIss" }
if (-not $AgentOnly -and -not (Test-Path -LiteralPath $labManagerIss)) { throw "Inno script not found: $labManagerIss" }
Assert-InnoScriptBom -Path $agentIss
if (-not $AgentOnly) { Assert-InnoScriptBom -Path $labManagerIss }
Assert-InnoFallbackVersion -Path $agentIss -Expected $ProductVersion
if (-not $AgentOnly) { Assert-InnoFallbackVersion -Path $labManagerIss -Expected $ProductVersion }
if (-not (Test-Path -LiteralPath $noticeRtf)) { throw "License notice not found: $noticeRtf" }

$serviceSource       = Join-Path $PublishRoot "agent\service"
$sessionHelperSource = Join-Path $PublishRoot "agent\sessionhelper"
$agentDesktopSource  = Join-Path $PublishRoot "agent\desktop"
$cliSource           = Join-Path $PublishRoot "cli"
$labManagerSource    = Join-Path $PublishRoot "labmanager"

$requiredSources = @($serviceSource, $sessionHelperSource, $agentDesktopSource, $cliSource)
if (-not $AgentOnly) {
    $requiredSources += $labManagerSource
}

if (-not $NoPublish) {
    $needsPublish = -not (Test-Path -LiteralPath $PublishRoot)
    foreach ($source in $requiredSources) {
        if (-not (Test-Path -LiteralPath $source)) { $needsPublish = $true }
    }

    if ($needsPublish) {
        Write-Host "Publish output is missing. Running scripts\publish.ps1..." -ForegroundColor Cyan
        # $LASTEXITCODE is only set by native commands; clear the value left by
        # the earlier ISCC probe before invoking a PowerShell script.
        $global:LASTEXITCODE = 0
        $publishArgs = @()
        if ($AgentOnly) { $publishArgs += '-AgentOnly' }
        if ($FrameworkDependent) { $publishArgs += '-FrameworkDependent' }
        & (Join-Path $PSScriptRoot "publish.ps1") @publishArgs
        if (-not $? -or $LASTEXITCODE -ne 0) {
            throw "publish.ps1 failed (exit code $LASTEXITCODE)."
        }
    }
}

Assert-PublishFolder $serviceSource "UpdateAgent.Service.exe"
Assert-PublishFolder $sessionHelperSource "UpdateAgent.SessionHelper.exe"
Assert-PublishFolder $agentDesktopSource "UpdateAgent.Agent.Desktop.exe"
Assert-PublishFolder $cliSource "UpdateAgent.Cli.exe"
# 공통 Guard DLL은 실행 파일의 게시 폴더에 복사되어야 한다.
Assert-PublishFolder $serviceSource "UpdateAgent.Guards.dll"
if (-not $AgentOnly) {
    Assert-PublishFolder $labManagerSource "UpdateAgent.Guards.dll"
    Assert-PublishFolder $labManagerSource "UpdateAgent.LabManager.exe"
    Assert-PublishFolder $labManagerSource "UpdateAgent.LabManager.Desktop.exe"
}

$runtimeMode = if ($FrameworkDependent) { "framework-dependent" } else { "self-contained" }
foreach ($source in $requiredSources) {
    Assert-PackageLayout -Path $source `
        -ExpectFrameworkDependent:$FrameworkDependent -Label $source | Out-Null
}

$buildTarget = if ($AgentOnly) { "agent" } else { "agent+labmanager" }

Write-Host "Product version : $ProductVersion"
Write-Host "Build target    : $buildTarget"
Write-Host "Publish root    : $PublishRoot"
Write-Host "Staging root    : $StagingRoot"
Write-Host "Output root     : $OutputRoot"
Write-Host "Runtime mode    : $runtimeMode"
if ($FrameworkDependent) {
    Write-Host "Prerequisite    : matching Microsoft .NET Desktop Runtime (win-x64) on every target PC" -ForegroundColor Yellow
}
Write-Host "Inno compiler   : $iscc"
Write-Host ""

if (Test-Path -LiteralPath $StagingRoot) {
    Remove-Item -LiteralPath $StagingRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $StagingRoot | Out-Null
New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null

$agentStage = Join-Path $StagingRoot "agent"
$labManagerStage = Join-Path $StagingRoot "labmanager"
$stageFolders = @(
    [pscustomobject]@{ Source = $serviceSource;       Destination = (Join-Path $agentStage "Service") }
    [pscustomobject]@{ Source = $sessionHelperSource; Destination = (Join-Path $agentStage "SessionHelper") }
    [pscustomobject]@{ Source = $agentDesktopSource;  Destination = (Join-Path $agentStage "Desktop") }
    [pscustomobject]@{ Source = $cliSource;           Destination = (Join-Path $agentStage "Cli") }
)
if (-not $AgentOnly) {
    $stageFolders += [pscustomobject]@{ Source = $labManagerSource; Destination = $labManagerStage }
}

foreach ($folder in $stageFolders) {
    New-Item -ItemType Directory -Force -Path $folder.Destination | Out-Null
    Write-Host "Staging $($folder.Source)"
    Copy-Item -Path (Join-Path $folder.Source "*") -Destination $folder.Destination -Recurse -Force
}

# 서비스 appsettings.json은 비밀값이 없는 템플릿으로 교체한다(에이전트 설정은 ProgramData\agent.json 사용).
if (Test-Path -LiteralPath $payloadAppSettings) {
    Copy-Item -LiteralPath $payloadAppSettings -Destination (Join-Path $agentStage "Service\appsettings.json") -Force
}

Get-ChildItem -LiteralPath $StagingRoot -Recurse -File -Filter "appsettings.Development.json" |
    Remove-Item -Force

$removed = @(Remove-UnwantedStagedFiles -Root $agentStage)
if (-not $AgentOnly) {
    $removed += @(Remove-UnwantedStagedFiles -Root $labManagerStage)
}
if ($removed.Count -gt 0) {
    Write-Host ("Removed {0} non-runtime files from staging (docs, symbols, archives)." -f $removed.Count)
    foreach ($item in $removed) { Write-Verbose "  removed: $item" }
}

if ($FrameworkDependent) {
    Assert-FrameworkDependentRuntimeFiles -Root $agentStage -Label "Agent" | Out-Null
    if (-not $AgentOnly) {
        Assert-FrameworkDependentRuntimeFiles -Root $labManagerStage -Label "LabManager" | Out-Null
    }
}

# 첫 설치 전용 토큰 파일은 정리 이후에 스테이징한다(.txt는 기본 제외 대상).
# ISCC가 항상 같은 파일 목록을 컴파일하도록 값이 없어도 빈 파일을 넣는다.
# 첫 설치 인스톨러는 빈/플레이스홀더 토큰을 만나면 fail-closed로 중단한다.
# 업데이트 경로는 이 파일을 읽지 않으므로 토큰이 없어도 정상 동작한다.
$agentTokenFromEnv = $env:UPDATEAGENT_AGENT_TOKEN
$labAgentTokenFromEnv = $env:UPDATEAGENT_LABMANAGER_AGENT_TOKEN
$agentTokenLines = @()
$labManagerTokenLines = @()

# Validate every secret before writing any token file into staging. A failed
# validation must never leave real token values on the build machine.
$agentToken = ''
if (-not [string]::IsNullOrWhiteSpace($agentTokenFromEnv)) {
    $agentToken = Assert-BundledTokenValue -Value $agentTokenFromEnv -Name 'UPDATEAGENT_AGENT_TOKEN'
}

$adminToken = ''
$labAgentToken = ''
if (-not $AgentOnly) {
    $adminTokenFromEnv = $env:UPDATEAGENT_LABMANAGER_ADMIN_TOKEN
    $hasAdminToken = -not [string]::IsNullOrWhiteSpace($adminTokenFromEnv)
    $hasLabAgentToken = -not [string]::IsNullOrWhiteSpace($labAgentTokenFromEnv)
    if ($hasAdminToken -ne $hasLabAgentToken) {
        throw 'Set both UPDATEAGENT_LABMANAGER_ADMIN_TOKEN and UPDATEAGENT_LABMANAGER_AGENT_TOKEN, or neither.'
    }
    # Both variables name the same student-facing token. A mismatch would ship a
    # LabManager installer whose embedded token the server rejects (401).
    if ((-not [string]::IsNullOrWhiteSpace($agentTokenFromEnv)) -and $hasLabAgentToken) {
        $normalizedAgentToken = $agentToken.TrimStart([char]0xFEFF).Trim()
        $normalizedLabAgentToken = (Get-FirstLine $labAgentTokenFromEnv).TrimStart([char]0xFEFF).Trim()
        if ($normalizedAgentToken -cne $normalizedLabAgentToken) {
            throw 'UPDATEAGENT_AGENT_TOKEN and UPDATEAGENT_LABMANAGER_AGENT_TOKEN must match when both are set.'
        }
    }
    if ($hasAdminToken) {
        $adminToken = Assert-BundledTokenValue -Value $adminTokenFromEnv -Name 'UPDATEAGENT_LABMANAGER_ADMIN_TOKEN'
        $labAgentToken = Assert-BundledTokenValue -Value $labAgentTokenFromEnv -Name 'UPDATEAGENT_LABMANAGER_AGENT_TOKEN'
        # The server and the LabManager installer reject equal admin/agent tokens.
        # Fail at build time instead of shipping an installer that cannot start.
        if ($adminToken -ceq $labAgentToken) {
            throw 'UPDATEAGENT_LABMANAGER_ADMIN_TOKEN and UPDATEAGENT_LABMANAGER_AGENT_TOKEN must be different values.'
        }
    }
}

if ($agentToken -ne '') {
    $agentTokenLines = @($agentToken)
    Write-Host 'Embedded first-install agent token file (value hidden).'
}
Write-BundledTokenFile -Path (Join-Path $agentStage $bundledAgentTokenName) -Lines $agentTokenLines

if (-not $AgentOnly) {
    if ($adminToken -ne '') {
        $labManagerTokenLines = @($adminToken, $labAgentToken)
        Write-Host 'Embedded first-install LabManager token file (values hidden).'
    }
    Write-BundledTokenFile -Path (Join-Path $labManagerStage $bundledLabManagerTokenName) -Lines $labManagerTokenLines
}

# 빌드 메타데이터에 실제 시크릿 내장 여부를 정확히 기록한다.
$containsSecrets = ($agentTokenLines.Count -gt 0) -or
    ((-not $AgentOnly) -and ($labManagerTokenLines.Count -gt 0))

Write-Host ""
Write-Host "Checking the included file set..." -ForegroundColor Cyan
if (-not $SkipValidation) {
    Assert-IncludedFileSet -Root $agentStage -Label "Agent" -RequiredFiles @(
        "Service\UpdateAgent.Service.exe",
        "Service\UpdateAgent.Guards.dll",
        "Service\appsettings.json",
        "SessionHelper\UpdateAgent.SessionHelper.exe",
        "Desktop\UpdateAgent.Agent.Desktop.exe",
        "Cli\UpdateAgent.Cli.exe",
        $bundledAgentTokenName
    )
    if (-not $AgentOnly) {
        Assert-IncludedFileSet -Root $labManagerStage -Label "LabManager" -RequiredFiles @(
            "UpdateAgent.LabManager.exe",
            "UpdateAgent.LabManager.Desktop.exe",
            "UpdateAgent.Guards.dll",
            "wwwroot\index.html",
            "appsettings.json",
            $bundledLabManagerTokenName
        )
    }
}

# Sign the payload while it is still in staging, before ISCC embeds it in the
# setup EXEs. Verification runs per file so a failed signature stops the build.
if ($Sign) {
    Invoke-StagedAssemblySigning -Context $signingContext -StagingRoot $StagingRoot | Out-Null
}

$agentStageArg = [IO.Path]::GetFullPath($agentStage)
$labManagerStageArg = [IO.Path]::GetFullPath($labManagerStage)
$outputArg = [IO.Path]::GetFullPath($OutputRoot)
$noticeArg = [IO.Path]::GetFullPath($noticeRtf)

Write-Host ""
Write-Host "Building Agent installer..." -ForegroundColor Cyan
& $iscc "/DAppVersion=$ProductVersion" "/DStageRoot=$agentStageArg" "/DOutputDir=$outputArg" "/DNoticeRtf=$noticeArg" $agentIss
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup failed for the Agent installer (exit code $LASTEXITCODE)."
}

if (-not $AgentOnly) {
    Write-Host ""
    Write-Host "Building LabManager installer..." -ForegroundColor Cyan
    & $iscc "/DAppVersion=$ProductVersion" "/DStageRoot=$labManagerStageArg" "/DOutputDir=$outputArg" "/DNoticeRtf=$noticeArg" $labManagerIss
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup failed for the LabManager installer (exit code $LASTEXITCODE)."
    }
}

$setupFiles = @(
    [pscustomobject]@{ Name = "UpdateAgent.Agent-Setup-$ProductVersion-x64.exe"; Product = "UpdateAgent Agent" }
)
if (-not $AgentOnly) {
    $setupFiles += [pscustomobject]@{ Name = "UpdateAgent.LabManager-Setup-$ProductVersion-x64.exe"; Product = "UpdateAgent LabManager" }
}

# ISCC has produced the final EXEs. Sign and verify them before hashing so the
# SHA-256 sidecars describe the exact bytes that will be published.
if ($Sign) {
    $setupPaths = @($setupFiles | ForEach-Object { Join-Path $OutputRoot $_.Name })
    Invoke-InstallerSigning -Context $signingContext -FilePaths $setupPaths
}

$results = New-Object System.Collections.Generic.List[object]
foreach ($setupFile in $setupFiles) {
    $path = Join-Path $OutputRoot $setupFile.Name
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Expected installer was not produced: $path"
    }

    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText("$path.sha256", "$hash  $($setupFile.Name)`r`n", [Text.Encoding]::ASCII)

    $results.Add([pscustomobject]@{
        product        = $setupFile.Product
        productVersion = $ProductVersion
        setupFile      = $setupFile.Name
        sizeBytes      = (Get-Item -LiteralPath $path).Length
        sha256         = $hash
    })
}

$gitCommit = ""
try {
    $gitCommit = (& git -C $repoRoot rev-parse --short HEAD 2>$null | Select-Object -First 1)
}
catch {
    $gitCommit = ""
}

# iscc /?는 사용법을 stderr로 출력한다. Windows PowerShell 5.1에서 $ErrorActionPreference가
# Stop이면 이게 종료 오류가 되므로 버전 확인 동안만 잠시 완화한다.
$previousErrorAction = $ErrorActionPreference
try {
    $ErrorActionPreference = 'SilentlyContinue'
    $innoVersionText = (& $iscc /? 2>&1 | Select-Object -First 1)
}
finally {
    $ErrorActionPreference = $previousErrorAction
}
$innoVersion = ("$innoVersionText" -replace '^Inno Setup\s+', '' -replace '\s+Command-Line Compiler.*$', '')

$selfContained = -not $FrameworkDependent
$runtimePrerequisite = if ($FrameworkDependent) {
    "Microsoft .NET Desktop Runtime (win-x64) must be preinstalled on target PCs"
} else {
    "none (runtime bundled)"
}

$buildInfo = [pscustomobject]@{
    productVersion      = $ProductVersion
    target              = $buildTarget
    platform            = "x64"
    selfContained       = $selfContained
    runtimeMode         = $runtimeMode
    runtimePrerequisite = $runtimePrerequisite
    unpackedInstall     = $true
    innoVersion         = $innoVersion
    gitCommit           = "$gitCommit".Trim()
    builtAtUtc          = (Get-Date).ToUniversalTime().ToString("o")
    setupFiles          = $results
    containsSecrets     = [bool]$containsSecrets
}
if ($Sign) {
    $buildInfo | Add-Member -NotePropertyName signed -NotePropertyValue $true
    $buildInfo | Add-Member -NotePropertyName signatureVerification -NotePropertyValue 'signtool verify /pa /all'
}

$buildInfoPath = Join-Path $OutputRoot "build-info.json"
$buildInfo | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $buildInfoPath -Encoding UTF8

if (-not $KeepStaging) {
    Remove-Item -LiteralPath $StagingRoot -Recurse -Force
}

Write-Host ""
Write-Host "Inno Setup installers built:" -ForegroundColor Green
foreach ($result in $results) {
    Write-Host ("  {0} ({1:N1} MB)" -f (Join-Path $OutputRoot $result.setupFile), ($result.sizeBytes / 1MB))
    Write-Host ("    SHA-256: {0}" -f $result.sha256)
}
if ($Sign) {
    Write-Host "  Signed: yes (staged assemblies and setup EXEs; verified with signtool verify /pa /all)" -ForegroundColor Green
}
Write-Host "  Build info: $buildInfoPath"
Write-Host "  Runtime mode: $runtimeMode"
Write-Host ""
Write-Host "Next steps:"
Write-Host "  1) Agent (student PC): UpdateAgent.Agent-Setup-$ProductVersion-x64.exe"
Write-Host "     Elevated: scripts\install-agent-msi.ps1 handles MSI; for Inno use the setup UI or /VERYSILENT."
if (-not $AgentOnly) {
    Write-Host "  2) LabManager (teacher PC): UpdateAgent.LabManager-Setup-$ProductVersion-x64.exe"
    Write-Host "  3) Full guide: docs\INSTALLER.md"
} else {
    Write-Host "  2) Full guide: docs\INSTALLER.md"
}

# Version probe commands can leave a non-zero $LASTEXITCODE after a successful build.
$global:LASTEXITCODE = 0
