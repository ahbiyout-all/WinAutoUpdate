<#
.SYNOPSIS
Builds the UpdateAgent Inno Setup installers from the published output.

.DESCRIPTION
Stages publish\ into artifacts\inno\stage, removes debug artifacts, documentation
and portable/archive files, verifies that only runtime files are included, then
compiles both installers with Inno Setup 6:

  artifacts\inno\UpdateAgent.Agent-Setup-<version>-x64.exe
  artifacts\inno\UpdateAgent.LabManager-Setup-<version>-x64.exe

Each installer installs individual (unpacked, multi-file) application folders.
The setup files never contain LabManager URL, agent token or any other secret.

Run from a normal PowerShell prompt. Inno Setup 6 must be installed.

.EXAMPLE
.\scripts\publish.ps1
.\scripts\build-inno.ps1

.EXAMPLE
.\scripts\build-inno.ps1 -NoPublish -SkipValidation

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
    [switch]$KeepStaging,
    [switch]$SkipValidation
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($PublishRoot)) { $PublishRoot = Join-Path $repoRoot "publish" }
if ([string]::IsNullOrWhiteSpace($OutputRoot))  { $OutputRoot  = Join-Path $repoRoot "artifacts\inno" }
if ([string]::IsNullOrWhiteSpace($StagingRoot)) { $StagingRoot = Join-Path $repoRoot "artifacts\inno\stage" }

$PublishRoot = [IO.Path]::GetFullPath($PublishRoot)
$OutputRoot  = [IO.Path]::GetFullPath($OutputRoot)
$StagingRoot = [IO.Path]::GetFullPath($StagingRoot)

$innoDir = Join-Path $repoRoot "installer\InnoSetup"
$agentIss = Join-Path $innoDir "UpdateAgent.Agent.iss"
$labManagerIss = Join-Path $innoDir "UpdateAgent.LabManager.iss"
$noticeRtf = Join-Path $repoRoot "installer\UpdateAgent.Agent.Installer\payload\notice.ko.rtf"
$payloadAppSettings = Join-Path $repoRoot "installer\UpdateAgent.Agent.Installer\payload\appsettings.json"

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

function Assert-PublishFolder {
    param([string]$Path, [string]$ExeName)

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Publish folder not found: $Path. Run scripts\publish.ps1 first."
    }
    if (-not (Test-Path -LiteralPath (Join-Path $Path $ExeName))) {
        throw "Publish output is incomplete (missing $ExeName): $Path. Run scripts\publish.ps1 first."
    }
}

function Find-Iscc {
    param([string]$Explicit)

    if (-not [string]::IsNullOrWhiteSpace($Explicit)) {
        if (-not (Test-Path -LiteralPath $Explicit)) {
            throw "ISCC.exe not found: $Explicit"
        }
        return [IO.Path]::GetFullPath($Explicit)
    }

    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 7\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 7\ISCC.exe")
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
        ".lnk", ".url"
    )
    $blockedFolders = @("docs", "doc", "samples", "examples", "portable")
    $blockedNames = @("web.config")

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

        if ($inBlockedFolder -or
            $blockedNames -contains $_.Name.ToLowerInvariant() -or
            $blockedExtensions -contains $_.Extension.ToLowerInvariant()) {
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

if (-not (Test-Path -LiteralPath $agentIss)) { throw "Inno script not found: $agentIss" }
if (-not (Test-Path -LiteralPath $labManagerIss)) { throw "Inno script not found: $labManagerIss" }
if (-not (Test-Path -LiteralPath $noticeRtf)) { throw "License notice not found: $noticeRtf" }

$serviceSource       = Join-Path $PublishRoot "agent\service"
$sessionHelperSource = Join-Path $PublishRoot "agent\sessionhelper"
$cliSource           = Join-Path $PublishRoot "cli"
$labManagerSource    = Join-Path $PublishRoot "labmanager"

Assert-PublishFolder $serviceSource "UpdateAgent.Service.exe"
Assert-PublishFolder $sessionHelperSource "UpdateAgent.SessionHelper.exe"
Assert-PublishFolder $cliSource "UpdateAgent.Cli.exe"
Assert-PublishFolder $labManagerSource "UpdateAgent.LabManager.exe"

if (-not $NoPublish) {
    $needsPublish = $false
    foreach ($source in @($serviceSource, $sessionHelperSource, $cliSource, $labManagerSource)) {
        if (-not (Test-Path -LiteralPath $source)) { $needsPublish = $true }
    }

    if (-not (Test-Path -LiteralPath $PublishRoot)) { $needsPublish = $true }

    if ($needsPublish) {
        Write-Host "Publish output is missing. Running scripts\publish.ps1..." -ForegroundColor Cyan
        & (Join-Path $PSScriptRoot "publish.ps1")
        if ($LASTEXITCODE -ne 0) { throw "publish.ps1 failed (exit code $LASTEXITCODE)." }
    }
}

Write-Host "Product version : $ProductVersion"
Write-Host "Publish root    : $PublishRoot"
Write-Host "Staging root    : $StagingRoot"
Write-Host "Output root     : $OutputRoot"
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
    [pscustomobject]@{ Source = $cliSource;           Destination = (Join-Path $agentStage "Cli") }
    [pscustomobject]@{ Source = $labManagerSource;    Destination = $labManagerStage }
)

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
$removed += @(Remove-UnwantedStagedFiles -Root $labManagerStage)
if ($removed.Count -gt 0) {
    Write-Host ("Removed {0} non-runtime files from staging (docs, symbols, archives)." -f $removed.Count)
    foreach ($item in $removed) { Write-Verbose "  removed: $item" }
}

Write-Host ""
Write-Host "Checking the included file set..." -ForegroundColor Cyan
if (-not $SkipValidation) {
    Assert-IncludedFileSet -Root $agentStage -Label "Agent" -RequiredFiles @(
        "Service\UpdateAgent.Service.exe",
        "Service\appsettings.json",
        "SessionHelper\UpdateAgent.SessionHelper.exe",
        "Cli\UpdateAgent.Cli.exe"
    )
    Assert-IncludedFileSet -Root $labManagerStage -Label "LabManager" -RequiredFiles @(
        "UpdateAgent.LabManager.exe",
        "appsettings.json",
        "wwwroot\index.html"
    )
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

Write-Host ""
Write-Host "Building LabManager installer..." -ForegroundColor Cyan
& $iscc "/DAppVersion=$ProductVersion" "/DStageRoot=$labManagerStageArg" "/DOutputDir=$outputArg" "/DNoticeRtf=$noticeArg" $labManagerIss
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup failed for the LabManager installer (exit code $LASTEXITCODE)."
}

$setupFiles = @(
    [pscustomobject]@{ Name = "UpdateAgent.Agent-Setup-$ProductVersion-x64.exe"; Product = "UpdateAgent Agent" }
    [pscustomobject]@{ Name = "UpdateAgent.LabManager-Setup-$ProductVersion-x64.exe"; Product = "UpdateAgent LabManager" }
)

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

$buildInfo = [pscustomobject]@{
    productVersion  = $ProductVersion
    platform        = "x64"
    selfContained   = $true
    unpackedInstall = $true
    innoVersion     = ((& $iscc /? 2>$null | Select-Object -First 1) -replace '^Inno Setup\s+', '' -replace '\s+Command-Line Compiler.*$', '')
    gitCommit       = "$gitCommit".Trim()
    builtAtUtc      = (Get-Date).ToUniversalTime().ToString("o")
    setupFiles      = $results
    containsSecrets = $false
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
Write-Host "  Build info: $buildInfoPath"
Write-Host ""
Write-Host "Next steps:"
Write-Host "  1) Agent (student PC): UpdateAgent.Agent-Setup-$ProductVersion-x64.exe"
Write-Host "     Elevated: scripts\install-agent-msi.ps1 handles MSI; for Inno use the setup UI or /VERYSILENT."
Write-Host "  2) LabManager (teacher PC): UpdateAgent.LabManager-Setup-$ProductVersion-x64.exe"
Write-Host "  3) Full guide: docs\INSTALLER.md"

# Version probe commands can leave a non-zero $LASTEXITCODE after a successful build.
$global:LASTEXITCODE = 0
