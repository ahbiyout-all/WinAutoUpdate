<#
.SYNOPSIS
Builds the UpdateAgent Agent MSI from the published output.

.DESCRIPTION
Stages publish\ into artifacts\staging, replaces the service appsettings.json with
the sanitized MSI template, removes debug artifacts, builds the WiX project, runs
ICE validation and writes the MSI plus SHA-256/build-info.json to artifacts\msi.

The MSI never contains LabManager URL, agent token or any other secret.

Signing shares scripts\sign-artifacts.ps1 with build-inno.ps1: staged assemblies
are signed before WiX builds the MSI, the final MSI is signed after validation,
and every signed file is verified with "signtool verify /pa /all".

Run from a normal PowerShell prompt. WiX 5.0.2 is restored from NuGet.

.PARAMETER Sign
Optional Authenticode signing. Without -Sign the build is unsigned and does
not require signtool.exe.

.PARAMETER CertificateThumbprint
SHA-1 thumbprint of the code signing certificate. When omitted, the helper
reads UPDATEAGENT_SIGN_THUMBPRINT from the environment. No thumbprint is
stored in the repository.

.PARAMETER TimestampUrl
Optional RFC 3161 timestamp URL passed to signtool.

.PARAMETER FrameworkDependent
Opt in to packaging the smaller framework-dependent publish output. The default
stays self-contained. Publish folders are validated before staging, and the
staged files must keep their runtimeconfig.json/deps.json files. Ambiguity fails
closed. Framework-dependent packages require the matching Microsoft .NET Desktop
Runtime (win-x64) on every target PC.

.EXAMPLE
.\scripts\publish.ps1
.\scripts\build-msi.ps1

.EXAMPLE
.\scripts\build-msi.ps1 -ProductVersion <version> -SkipValidate

.EXAMPLE
.\scripts\publish.ps1 -FrameworkDependent
.\scripts\build-msi.ps1 -FrameworkDependent

.EXAMPLE
$env:UPDATEAGENT_SIGN_THUMBPRINT = '<thumbprint-from-secret-store>'
.\scripts\build-msi.ps1 -Sign -TimestampUrl "http://timestamp.digicert.com"
#>
[CmdletBinding()]
param(
    [string]$ProductVersion,
    [string]$Configuration = "Release",
    [string]$PublishRoot,
    [string]$OutputRoot,
    [string]$StagingRoot,
    [switch]$KeepStaging,
    [switch]$SkipValidate,
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
if ([string]::IsNullOrWhiteSpace($OutputRoot))  { $OutputRoot  = Join-Path $repoRoot "artifacts\msi" }
if ([string]::IsNullOrWhiteSpace($StagingRoot)) { $StagingRoot = Join-Path $repoRoot "artifacts\staging" }

$PublishRoot = [IO.Path]::GetFullPath($PublishRoot)
$OutputRoot  = [IO.Path]::GetFullPath($OutputRoot)
$StagingRoot = [IO.Path]::GetFullPath($StagingRoot)

$wixProjectDir = Join-Path $repoRoot "installer\UpdateAgent.Agent.Installer"
$wixProject = Join-Path $wixProjectDir "UpdateAgent.Agent.Installer.wixproj"
$payloadAppSettings = Join-Path $wixProjectDir "payload\appsettings.json"

function Get-VersionFromBuildProps {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Directory.Build.props not found: $Path"
    }

    $text = Get-Content -LiteralPath $Path -Raw
    if ($text -notmatch '<Version>\s*([^<]+?)\s*</Version>') {
        throw "No <Version> element found in $Path."
    }

    # MSI ProductVersion accepts digits only: drop '-' prerelease and '+commit' metadata.
    return ($Matches[1] -split '[+-]')[0]
}

function Assert-WixFallbackVersion {
    param([string]$Path, [string]$Expected)

    $text = Get-Content -LiteralPath $Path -Raw
    $match = [regex]::Match($text, '<ProductVersion[^>]*>([^<]+)</ProductVersion>')
    if (-not $match.Success) {
        throw "No fallback ProductVersion found in $Path."
    }
    $fallback = $match.Groups[1].Value.Trim()
    if ($fallback -ne $Expected) {
        throw ("Stale fallback ProductVersion in {0}: {1} (expected {2})." -f $Path, $fallback, $Expected)
    }
}

function ConvertTo-MsiProductVersion {
    param([string]$Value)

    if ($Value -notmatch '^\d+(\.\d+){0,2}$') {
        throw "ProductVersion must look like Major.Minor.Build (digits only): $Value"
    }

    $parts = @($Value.Split('.'))
    while ($parts.Count -lt 3) { $parts += '0' }
    $parts = $parts[0..2]

    $numbers = @($parts | ForEach-Object { [int]$_ })
    if ($numbers[0] -gt 255 -or $numbers[1] -gt 255 -or $numbers[2] -gt 65535) {
        throw "ProductVersion is outside the Windows Installer limits (major <= 255, minor <= 255, build <= 65535): $Value"
    }

    return ($numbers -join '.')
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

function Remove-UnwantedStagedFiles {
    param([string]$Root)

    # MSI에는 실행에 필요한 런타임 파일만 넣는다(개별 파일/언팩 배포).
    # 문서, 심볼, 로그, 포터블/압축 파일, 바로가기는 패키징하지 않는다.
    $blockedExtensions = @(
        ".pdb", ".xml", ".md", ".txt", ".rtf", ".pdf", ".doc", ".docx",
        ".log", ".tmp", ".msi", ".msix", ".cab", ".zip", ".7z", ".rar",
        ".lnk", ".url"
    )
    $blockedFolders = @("docs", "doc", "samples", "examples", "portable")

    $removed = New-Object System.Collections.Generic.List[string]

    Get-ChildItem -LiteralPath $Root -Recurse -File | ForEach-Object {
        $relative = $_.FullName.Substring($Root.Length).TrimStart('\', '/')
        $segments = @($relative -split '[\\/]')
        $inBlockedFolder = $false
        if ($segments.Count -gt 1) {
            $folderSegments = $segments[0..($segments.Count - 2)]
            $inBlockedFolder = [bool]($folderSegments |
                Where-Object { $blockedFolders -contains $_.ToLowerInvariant() })
        }

        if ($inBlockedFolder -or $blockedExtensions -contains $_.Extension.ToLowerInvariant()) {
            $removed.Add($relative)
            Remove-Item -LiteralPath $_.FullName -Force
        }
    }

    return $removed
}

# Fail closed before staging when signing is requested but its prerequisites
# are missing. Unsigned builds never load the helper or look up signtool.exe.
$signingContext = $null
if ($Sign) {
    . (Join-Path $PSScriptRoot "sign-artifacts.ps1")
    $signingContext = New-SigningContext -Sign -CertificateThumbprint $CertificateThumbprint -TimestampUrl $TimestampUrl
}

if ([string]::IsNullOrWhiteSpace($ProductVersion)) {
    $ProductVersion = Get-VersionFromBuildProps (Join-Path $repoRoot "Directory.Build.props")
}
$ProductVersion = ConvertTo-MsiProductVersion $ProductVersion

$serviceSource       = Join-Path $PublishRoot "agent\service"
$sessionHelperSource = Join-Path $PublishRoot "agent\sessionhelper"
$agentDesktopSource  = Join-Path $PublishRoot "agent\desktop"
$cliSource           = Join-Path $PublishRoot "cli"

Assert-PublishFolder $serviceSource "UpdateAgent.Service.exe"
Assert-PublishFolder $sessionHelperSource "UpdateAgent.SessionHelper.exe"
Assert-PublishFolder $agentDesktopSource "UpdateAgent.Agent.Desktop.exe"
Assert-PublishFolder $cliSource "UpdateAgent.Cli.exe"
# 공통 Guard DLL도 게시 폴더에 있어야 MSI 페이로드에 포함된다.
Assert-PublishFolder $serviceSource "UpdateAgent.Guards.dll"

$runtimeMode = if ($FrameworkDependent) { "framework-dependent" } else { "self-contained" }
foreach ($source in @($serviceSource, $sessionHelperSource, $agentDesktopSource, $cliSource)) {
    Assert-PackageLayout -Path $source `
        -ExpectFrameworkDependent:$FrameworkDependent -Label $source | Out-Null
}

if (-not (Test-Path -LiteralPath $payloadAppSettings)) {
    throw "Sanitized appsettings template not found: $payloadAppSettings"
}
if (-not (Test-Path -LiteralPath $wixProject)) {
    throw "WiX project not found: $wixProject"
}
Assert-WixFallbackVersion -Path $wixProject -Expected $ProductVersion

Write-Host "Product version : $ProductVersion"
Write-Host "Publish root    : $PublishRoot"
Write-Host "Staging root    : $StagingRoot"
Write-Host "Output root     : $OutputRoot"
Write-Host "Runtime mode    : $runtimeMode"
if ($FrameworkDependent) {
    Write-Host "Prerequisite    : matching Microsoft .NET Desktop Runtime (win-x64) on every target PC" -ForegroundColor Yellow
}
Write-Host ""

if (Test-Path -LiteralPath $StagingRoot) {
    Remove-Item -LiteralPath $StagingRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $StagingRoot | Out-Null
New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null

$folders = @(
    [pscustomobject]@{ Source = $serviceSource;       Destination = (Join-Path $StagingRoot "agent\service") }
    [pscustomobject]@{ Source = $sessionHelperSource; Destination = (Join-Path $StagingRoot "agent\sessionhelper") }
    [pscustomobject]@{ Source = $agentDesktopSource;  Destination = (Join-Path $StagingRoot "agent\desktop") }
    [pscustomobject]@{ Source = $cliSource;           Destination = (Join-Path $StagingRoot "cli") }
)

foreach ($folder in $folders) {
    New-Item -ItemType Directory -Force -Path $folder.Destination | Out-Null
    Write-Host "Staging $($folder.Source)"
    Copy-Item -Path (Join-Path $folder.Source "*") -Destination $folder.Destination -Recurse -Force
}

$stagedServiceAppSettings = Join-Path $StagingRoot "agent\service\appsettings.json"
Copy-Item -LiteralPath $payloadAppSettings -Destination $stagedServiceAppSettings -Force

$template = Get-Content -LiteralPath $stagedServiceAppSettings -Raw
if ($template -match '(?i)(change-me-|sample-[^\r\n]*-replace-before-pilot)') {
    throw "The MSI appsettings template still contains a default or sample token."
}

Get-ChildItem -LiteralPath $StagingRoot -Recurse -File -Filter "appsettings.Development.json" |
    Remove-Item -Force

$removedFiles = @(Remove-UnwantedStagedFiles -Root $StagingRoot)
if ($removedFiles.Count -gt 0) {
    Write-Host ("Removed {0} non-runtime files from staging (docs, symbols, archives)." -f $removedFiles.Count)
    foreach ($removedFile in $removedFiles) {
        Write-Verbose "  removed: $removedFile"
    }
}

if ($FrameworkDependent) {
    Assert-FrameworkDependentRuntimeFiles -Root $StagingRoot -Label "MSI" | Out-Null
}

if ($Sign) {
    Invoke-StagedAssemblySigning -Context $signingContext -StagingRoot $StagingRoot | Out-Null
}

$stagedFiles = (Get-ChildItem -LiteralPath $StagingRoot -Recurse -File).Count
$stagedSize = (Get-ChildItem -LiteralPath $StagingRoot -Recurse -File |
    Measure-Object -Property Length -Sum).Sum
Write-Host ("Staged {0} files ({1:N1} MB) for the MSI." -f $stagedFiles, ($stagedSize / 1MB))

Write-Host ""
Write-Host "Building MSI..." -ForegroundColor Cyan
& dotnet build $wixProject `
    --configuration $Configuration `
    --target Rebuild `
    -p:Platform=x64 `
    -p:ProductVersion=$ProductVersion `
    -p:PublishRoot=$StagingRoot `
    -v minimal

if ($LASTEXITCODE -ne 0) {
    throw "MSI build failed (exit code $LASTEXITCODE)."
}

$msiName = "UpdateAgent.Agent-$ProductVersion-x64.msi"
$builtMsi = Join-Path $wixProjectDir "bin\x64\$Configuration\$msiName"
if (-not (Test-Path -LiteralPath $builtMsi)) {
    # ko-KR.wxl 때문에 WiX는 문화권 하위 폴더(bin\x64\Release\ko-KR)에 MSI를 만든다.
    $searchRoot = Join-Path $wixProjectDir "bin\x64\$Configuration"
    $candidate = Get-ChildItem -LiteralPath $searchRoot -Filter $msiName -File -Recurse -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($candidate) { $builtMsi = $candidate.FullName }
}
if (-not (Test-Path -LiteralPath $builtMsi)) {
    throw "Expected MSI was not produced. Looked under: $(Join-Path $wixProjectDir "bin\x64\$Configuration")"
}

if (-not $SkipValidate) {
    Write-Host ""
    Write-Host "Restoring WiX validation tool..." -ForegroundColor Cyan
    # The tool manifest lives at the repository root, regardless of the caller's location.
    Push-Location $repoRoot
    try {
        & dotnet tool restore
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet tool restore failed (exit code $LASTEXITCODE). Run 'dotnet tool restore' once with network access."
        }

        Write-Host "Validating MSI (ICE60 suppressed: neutral-language .NET hosts)..." -ForegroundColor Cyan
        & dotnet wix msi validate -sice ICE60 $builtMsi
        if ($LASTEXITCODE -ne 0) {
            throw "MSI validation failed (exit code $LASTEXITCODE)."
        }
    }
    finally {
        Pop-Location
    }
}

$destinationMsi = Join-Path $OutputRoot $msiName
Copy-Item -LiteralPath $builtMsi -Destination $destinationMsi -Force

if ($Sign) {
    Invoke-InstallerSigning -Context $signingContext -FilePaths @($destinationMsi)
}

$hash = (Get-FileHash -LiteralPath $destinationMsi -Algorithm SHA256).Hash.ToLowerInvariant()
$hashFile = "$destinationMsi.sha256"
[IO.File]::WriteAllText($hashFile, "$hash  $msiName`r`n", [Text.Encoding]::ASCII)

$wixVersion = (& dotnet wix --version 2>$null | Select-Object -First 1)
if ([string]::IsNullOrWhiteSpace($wixVersion)) { $wixVersion = "unknown" }

$gitCommit = ""
try {
    $gitCommit = (& git -C $repoRoot rev-parse --short HEAD 2>$null | Select-Object -First 1)
}
catch {
    $gitCommit = ""
}

$msiSize = (Get-Item -LiteralPath $destinationMsi).Length
$selfContained = -not $FrameworkDependent
$runtimePrerequisite = if ($FrameworkDependent) {
    "Microsoft .NET Desktop Runtime (win-x64) must be preinstalled on target PCs"
} else {
    "none (runtime bundled)"
}
$buildInfo = [pscustomobject]@{
    product             = "UpdateAgent Agent"
    productVersion      = $ProductVersion
    configuration       = $Configuration
    platform            = "x64"
    selfContained       = $selfContained
    runtimeMode         = $runtimeMode
    runtimePrerequisite = $runtimePrerequisite
    msiFile             = $msiName
    msiSizeBytes        = $msiSize
    sha256              = $hash
    stagedFiles         = $stagedFiles
    wixVersion          = "$wixVersion".Trim()
    gitCommit           = "$gitCommit".Trim()
    builtAtUtc          = (Get-Date).ToUniversalTime().ToString("o")
    signed            = [bool]$Sign
    containsSecrets     = $false
}
if ($Sign) {
    $buildInfo | Add-Member -NotePropertyName signatureVerification -NotePropertyValue 'signtool verify /pa /all'
}

$buildInfoPath = Join-Path $OutputRoot "build-info.json"
$buildInfo | ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath $buildInfoPath -Encoding UTF8

if (-not $KeepStaging) {
    Remove-Item -LiteralPath $StagingRoot -Recurse -Force
}

Write-Host ""
Write-Host ("MSI built: {0} ({1:N1} MB)" -f $destinationMsi, ($msiSize / 1MB)) -ForegroundColor Green
Write-Host "  SHA-256     : $hash"
Write-Host "  Build info  : $buildInfoPath"
Write-Host "  Runtime mode: $runtimeMode"
if ($Sign) {
    Write-Host "  Signed      : yes" -ForegroundColor Green
}
else {
    Write-Host "  Signed      : no (pilot builds are unsigned)" -ForegroundColor Yellow
}
Write-Host ""
Write-Host "Next steps:"
Write-Host "  1) Copy the MSI and scripts\ to a student PC."
Write-Host "  2) Elevated: scripts\install-agent-msi.ps1 -LabManagerUrl http://<teacher-ip>:5080 -ConfirmMaestro"
Write-Host "  3) Full runbook: docs\PILOT.md and docs\MSI.md"

# The last helper commands (git/dotnet version probes) can leave a non-zero
# $LASTEXITCODE even though the build succeeded.
$global:LASTEXITCODE = 0
