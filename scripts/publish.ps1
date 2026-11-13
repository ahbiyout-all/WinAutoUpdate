<#
.SYNOPSIS
Publishes the UpdateAgent LabManager, Service, SessionHelper and CLI for a pilot deployment.

.DESCRIPTION
Produces four self-contained (or framework-dependent) folders:

  publish\labmanager          Teacher PC web dashboard/API
  publish\agent\service       Student PC Windows service
  publish\agent\sessionhelper Student PC overlay helper (WPF)
  publish\cli                 Optional local CLI for scan/preflight/status/maestro

Run from a PowerShell prompt. No administrator rights are required for publishing.

.EXAMPLE
.\scripts\publish.ps1

.EXAMPLE
.\scripts\publish.ps1 -FrameworkDependent
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$FrameworkDependent,
    [string]$OutputRoot,
    [switch]$NoClean
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot "publish"
}
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)

$projects = @(
    [pscustomobject]@{
        Name    = "LabManager"
        Project = Join-Path $repoRoot "src\UpdateAgent.LabManager\UpdateAgent.LabManager.csproj"
        Output  = Join-Path $OutputRoot "labmanager"
    },
    [pscustomobject]@{
        Name    = "Service"
        Project = Join-Path $repoRoot "src\UpdateAgent.Service\UpdateAgent.Service.csproj"
        Output  = Join-Path $OutputRoot "agent\service"
    },
    [pscustomobject]@{
        Name    = "SessionHelper"
        Project = Join-Path $repoRoot "src\UpdateAgent.SessionHelper\UpdateAgent.SessionHelper.csproj"
        Output  = Join-Path $OutputRoot "agent\sessionhelper"
    },
    [pscustomobject]@{
        Name    = "Cli"
        Project = Join-Path $repoRoot "src\UpdateAgent.Cli\UpdateAgent.Cli.csproj"
        Output  = Join-Path $OutputRoot "cli"
    }
)

$selfContained = if ($FrameworkDependent) { "false" } else { "true" }

foreach ($project in $projects) {
    if (-not $NoClean -and (Test-Path -LiteralPath $project.Output)) {
        Remove-Item -LiteralPath $project.Output -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $project.Output | Out-Null

    Write-Host ""
    Write-Host "== Publishing $($project.Name) ==" -ForegroundColor Cyan
    Write-Host "   $($project.Project)"
    Write-Host "   -> $($project.Output)"

    & dotnet publish $project.Project `
        --configuration $Configuration `
        --runtime $Runtime `
        --self-contained $selfContained `
        --output $project.Output `
        --nologo

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $($project.Name) (exit code $LASTEXITCODE)."
    }
}

$size = (Get-ChildItem -LiteralPath $OutputRoot -Recurse -File |
    Measure-Object -Property Length -Sum).Sum

Write-Host ""
Write-Host ("Publish complete: {0} ({1:N1} MB)" -f $OutputRoot, ($size / 1MB)) -ForegroundColor Green
Write-Host ""
Write-Host "Next steps:"
Write-Host "  1) Teacher PC: scripts\configure-labmanager.ps1, then start publish\labmanager\UpdateAgent.LabManager.exe"
Write-Host "  2) Optional MSI: scripts\build-msi.ps1 -> artifacts\msi\UpdateAgent.Agent-<version>-x64.msi"
Write-Host "  3) Student PC (MSI): scripts\install-agent-msi.ps1 -LabManagerUrl http://<teacher-ip>:5080 -ConfirmMaestro"
Write-Host "     Student PC (legacy): scripts\install-service.ps1 -LabManagerUrl http://<teacher-ip>:5080"
Write-Host "  4) Optional on a student PC: publish\cli\UpdateAgent.Cli.exe preflight --confirm-maestro"
Write-Host "  5) Full runbook: docs\PILOT.md (MSI design: docs\MSI.md)"
Write-Host ""
Write-Host "Note: re-publishing resets publish\labmanager\appsettings.json and"
Write-Host "      publish\agent\service\appsettings.json to their defaults."
Write-Host "      Re-run the configure scripts after every publish."
