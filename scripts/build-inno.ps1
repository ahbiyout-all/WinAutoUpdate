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
The setup files never contain LabManager URL, agent token or any other secret.

Run from a normal PowerShell prompt. Inno Setup 6 must be installed.

.EXAMPLE
.\scripts\publish.ps1
.\scripts\build-inno.ps1

.EXAMPLE
.\scripts\build-inno.ps1 -AgentOnly

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
    [switch]$AgentOnly,
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
        if (-not (Test-Path -LiteralPath $Explicit)) {
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
$isccVersionText = (& $iscc /? 2>$null | Select-Object -First 1)
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
        if ($AgentOnly) {
            & (Join-Path $PSScriptRoot "publish.ps1") -AgentOnly
        } else {
            & (Join-Path $PSScriptRoot "publish.ps1")
        }
        if ($LASTEXITCODE -ne 0) { throw "publish.ps1 failed (exit code $LASTEXITCODE)." }
    }
}

Assert-PublishFolder $serviceSource "UpdateAgent.Service.exe"
Assert-PublishFolder $sessionHelperSource "UpdateAgent.SessionHelper.exe"
Assert-PublishFolder $agentDesktopSource "UpdateAgent.Agent.Desktop.exe"
Assert-PublishFolder $cliSource "UpdateAgent.Cli.exe"
if (-not $AgentOnly) {
    Assert-PublishFolder $labManagerSource "UpdateAgent.LabManager.exe"
    Assert-PublishFolder $labManagerSource "UpdateAgent.LabManager.Desktop.exe"
}

$buildTarget = if ($AgentOnly) { "agent" } else { "agent+labmanager" }

Write-Host "Product version : $ProductVersion"
Write-Host "Build target    : $buildTarget"
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

Write-Host ""
Write-Host "Checking the included file set..." -ForegroundColor Cyan
if (-not $SkipValidation) {
    Assert-IncludedFileSet -Root $agentStage -Label "Agent" -RequiredFiles @(
        "Service\UpdateAgent.Service.exe",
        "Service\appsettings.json",
        "SessionHelper\UpdateAgent.SessionHelper.exe",
        "Desktop\UpdateAgent.Agent.Desktop.exe",
        "Cli\UpdateAgent.Cli.exe"
    )
    if (-not $AgentOnly) {
        Assert-IncludedFileSet -Root $labManagerStage -Label "LabManager" -RequiredFiles @(
            "UpdateAgent.LabManager.exe",
            "UpdateAgent.LabManager.Desktop.exe",
            "wwwroot\index.html",
            "appsettings.json"
        )
    }
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

$buildInfo = [pscustomobject]@{
    productVersion  = $ProductVersion
    target          = $buildTarget
    platform        = "x64"
    selfContained   = $true
    unpackedInstall = $true
    innoVersion     = $innoVersion
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
if (-not $AgentOnly) {
    Write-Host "  2) LabManager (teacher PC): UpdateAgent.LabManager-Setup-$ProductVersion-x64.exe"
    Write-Host "  3) Full guide: docs\INSTALLER.md"
} else {
    Write-Host "  2) Full guide: docs\INSTALLER.md"
}

# Version probe commands can leave a non-zero $LASTEXITCODE after a successful build.
$global:LASTEXITCODE = 0
