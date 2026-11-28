<#
.SYNOPSIS
Shared Authenticode signing helpers for the UpdateAgent installer build scripts.

.DESCRIPTION
Dot-source this file only when the caller passes -Sign. The helpers resolve the
certificate thumbprint from an explicit parameter or from the
UPDATEAGENT_SIGN_THUMBPRINT environment variable, locate signtool.exe, sign
staged assemblies and final installer EXEs, and verify every signed file with
"signtool verify /pa /all".

No thumbprint, password or token value is stored in this file. A caller-supplied
value is passed to signtool only at build time and is never written by these
helpers to the console, a report or a log.

.EXAMPLE
. .\scripts\sign-artifacts.ps1
$context = New-SigningContext -Sign -CertificateThumbprint $CertificateThumbprint
#>

function Get-SigningThumbprint {
    param([string]$ProvidedThumbprint)

    $value = $ProvidedThumbprint
    if ([string]::IsNullOrWhiteSpace($value)) {
        $value = $env:UPDATEAGENT_SIGN_THUMBPRINT
    }
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "-Sign requires -CertificateThumbprint or the UPDATEAGENT_SIGN_THUMBPRINT environment variable."
    }

    # signtool accepts thumbprints with separators; normalize before validating.
    $normalized = (($value -replace '\s', '').Trim()).Trim([char]0xFEFF)
    if ($normalized -notmatch '^[0-9a-fA-F]{40}$') {
        throw "Certificate thumbprint must be a 40-character SHA-1 hex value. Supply it at build time; never store it in the repository."
    }
    return $normalized
}

function New-SigningContext {
    param(
        [switch]$Sign,
        [string]$CertificateThumbprint,
        [string]$TimestampUrl
    )

    # Unsigned builds must never look up signtool or require a thumbprint.
    if (-not $Sign) { return $null }

    $thumbprint = Get-SigningThumbprint -ProvidedThumbprint $CertificateThumbprint

    $signTool = Get-Command -Name 'signtool.exe' -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if (-not $signTool -or -not (Test-Path -LiteralPath $signTool.Source -PathType Leaf)) {
        throw "signtool.exe was not found on PATH. Install the Windows SDK or omit -Sign."
    }

    return [pscustomobject]@{
        Thumbprint   = $thumbprint
        SignToolPath = $signTool.Source
        TimestampUrl = $TimestampUrl
    }
}

function Invoke-AuthenticodeSign {
    param(
        [Parameter(Mandatory = $true)] [object]$Context,
        [Parameter(Mandatory = $true)] [string]$FilePath
    )

    if ($null -eq $Context) {
        throw "Signing context is missing. Pass -Sign to the build script."
    }
    if (-not (Test-Path -LiteralPath $FilePath -PathType Leaf)) {
        throw "File to sign was not found: $FilePath"
    }

    $arguments = @('sign', '/sha1', $Context.Thumbprint, '/fd', 'SHA256')
    if (-not [string]::IsNullOrWhiteSpace($Context.TimestampUrl)) {
        $arguments += @('/tr', $Context.TimestampUrl, '/td', 'SHA256')
    }
    $arguments += $FilePath

    # Clear a stale exit code left by an earlier native command in the caller.
    $global:LASTEXITCODE = 0
    & $Context.SignToolPath @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "signtool sign failed for $FilePath (exit code $LASTEXITCODE)."
    }
}

function Assert-AuthenticodeSignature {
    param(
        [Parameter(Mandatory = $true)] [object]$Context,
        [Parameter(Mandatory = $true)] [string]$FilePath
    )

    if ($null -eq $Context) {
        throw "Signing context is missing. Pass -Sign to the build script."
    }
    if (-not (Test-Path -LiteralPath $FilePath -PathType Leaf)) {
        throw "Signed file was not found: $FilePath"
    }

    # /pa uses the Authenticode verification policy; /all checks every
    # signature in the file, including nested and timestamp signatures.
    $global:LASTEXITCODE = 0
    & $Context.SignToolPath verify /pa /all $FilePath
    if ($LASTEXITCODE -ne 0) {
        throw "signtool verify failed for $FilePath (exit code $LASTEXITCODE)."
    }
}

function Invoke-StagedAssemblySigning {
    param(
        [Parameter(Mandatory = $true)] [object]$Context,
        [Parameter(Mandatory = $true)] [string]$StagingRoot
    )

    if (-not (Test-Path -LiteralPath $StagingRoot -PathType Container)) {
        throw "Signing directory was not found: $StagingRoot"
    }

    $assemblies = @(Get-ChildItem -LiteralPath $StagingRoot -Recurse -File |
        Where-Object { $_.Name -like 'UpdateAgent.*.exe' -or $_.Name -like 'UpdateAgent.*.dll' })

    Write-Host ""
    Write-Host ("Signing and verifying {0} staged UpdateAgent assemblies..." -f $assemblies.Count)
    foreach ($assembly in $assemblies) {
        Invoke-AuthenticodeSign -Context $Context -FilePath $assembly.FullName
        Assert-AuthenticodeSignature -Context $Context -FilePath $assembly.FullName
    }
    return $assemblies.Count
}

function Invoke-InstallerSigning {
    param(
        [Parameter(Mandatory = $true)] [object]$Context,
        [Parameter(Mandatory = $true)] [string[]]$FilePaths
    )

    foreach ($path in $FilePaths) {
        Write-Host ("Signing and verifying installer: {0}" -f (Split-Path -Leaf $path))
        Invoke-AuthenticodeSign -Context $Context -FilePath $path
        Assert-AuthenticodeSignature -Context $Context -FilePath $path
    }
}
