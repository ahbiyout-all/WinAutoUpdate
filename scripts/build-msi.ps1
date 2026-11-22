<#
.SYNOPSIS
Builds the UpdateAgent Agent MSI from the published output.

.DESCRIPTION
Stages publish\ into artifacts\staging, replaces the service appsettings.json with
the sanitized MSI template, removes debug artifacts, builds the WiX project, runs
ICE validation and writes the MSI plus SHA-256/build-info.json to artifacts\msi.

The MSI never contains LabManager URL, agent token or any other secret.

Run from a normal PowerShell prompt. WiX 5.0.2 is restored from NuGet.

.EXAMPLE
.\scripts\publish.ps1
.\scripts\build-msi.ps1

.EXAMPLE
.\scripts\build-msi.ps1 -ProductVersion 0.1.21 -SkipValidate

.EXAMPLE
.\scripts\build-msi.ps1 -Sign -CertificateThumbprint "ABC123..." -TimestampUrl "http://timestamp.digicert.com"
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
    [switch]$Sign,
    [string]$CertificateThumbprint,
    [string]$TimestampUrl
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot

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

function Invoke-SignTool {
    param([string]$FilePath)

    $signtool = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if (-not $signtool) {
        throw "signtool.exe was not found on PATH. Install the Windows SDK or omit -Sign."
    }

    $arguments = @("sign", "/sha1", $CertificateThumbprint, "/fd", "SHA256")
    if (-not [string]::IsNullOrWhiteSpace($TimestampUrl)) {
        $arguments += @("/tr", $TimestampUrl, "/td", "SHA256")
    }
    $arguments += $FilePath

    & $signtool.Source @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "signtool failed for $FilePath (exit code $LASTEXITCODE)."
    }
}

if ($Sign -and [string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
    throw "-Sign requires -CertificateThumbprint."
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

if (-not (Test-Path -LiteralPath $payloadAppSettings)) {
    throw "Sanitized appsettings template not found: $payloadAppSettings"
}
if (-not (Test-Path -LiteralPath $wixProject)) {
    throw "WiX project not found: $wixProject"
}

Write-Host "Product version : $ProductVersion"
Write-Host "Publish root    : $PublishRoot"
Write-Host "Staging root    : $StagingRoot"
Write-Host "Output root     : $OutputRoot"
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
if ($template -match 'change-me-') {
    throw "The MSI appsettings template still contains a default token."
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

if ($Sign) {
    $assemblies = Get-ChildItem -LiteralPath $StagingRoot -Recurse -File |
        Where-Object { $_.Name -like "UpdateAgent.*.exe" -or $_.Name -like "UpdateAgent.*.dll" }

    Write-Host ""
    Write-Host "Signing $($assemblies.Count) UpdateAgent assemblies..."
    foreach ($assembly in $assemblies) {
        Invoke-SignTool $assembly.FullName
    }
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

$destinationMsi = Join-Path $OutputRoot $msiName
Copy-Item -LiteralPath $builtMsi -Destination $destinationMsi -Force

if ($Sign) {
    Write-Host "Signing MSI..."
    Invoke-SignTool $destinationMsi
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
$buildInfo = [pscustomobject]@{
    product           = "UpdateAgent Agent"
    productVersion    = $ProductVersion
    configuration     = $Configuration
    platform          = "x64"
    selfContained     = $true
    msiFile           = $msiName
    msiSizeBytes      = $msiSize
    sha256            = $hash
    stagedFiles       = $stagedFiles
    wixVersion        = "$wixVersion".Trim()
    gitCommit         = "$gitCommit".Trim()
    builtAtUtc        = (Get-Date).ToUniversalTime().ToString("o")
    signed            = [bool]$Sign
    containsSecrets   = $false
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
