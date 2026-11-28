<#
.SYNOPSIS
Publishes the UpdateAgent LabManager, Service, SessionHelper and CLI for a pilot deployment.

.DESCRIPTION
Produces five self-contained (or framework-dependent) folders from six projects:

  publish\labmanager          Teacher PC headless API + native WPF desktop client
  publish\agent\service       Student PC Windows service
  publish\agent\sessionhelper Student PC overlay helper (WPF)
  publish\agent\desktop       Student PC status desktop app (WPF)
  publish\cli                 Optional local CLI for scan/preflight/status/maestro

With -AgentOnly only the four student-PC folders are produced
(agent\service, agent\sessionhelper, agent\desktop, cli). Use it for a student-only build.

Run from a PowerShell prompt. No administrator rights are required for publishing.

.PARAMETER FrameworkDependent
Opt in to a smaller framework-dependent publish. The default stays self-contained.
Framework-dependent output requires the matching Microsoft .NET Desktop Runtime
(win-x64) on every target PC. The publish layout is validated after publishing;
an ambiguous or mismatched layout fails closed with guidance.

.EXAMPLE
.\scripts\publish.ps1

.EXAMPLE
.\scripts\publish.ps1 -AgentOnly

.EXAMPLE
.\scripts\publish.ps1 -FrameworkDependent
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$FrameworkDependent,
    [switch]$AgentOnly,
    [string]$OutputRoot,
    [switch]$NoClean
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot "publish"
}
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)

# English-only layout checks; shared with the installer and deployment scripts.
. (Join-Path $PSScriptRoot "package-layout.ps1")

$projects = @(
    [pscustomobject]@{
        Name    = "LabManager"
        Project = Join-Path $repoRoot "src\UpdateAgent.LabManager\UpdateAgent.LabManager.csproj"
        Output  = Join-Path $OutputRoot "labmanager"
        # The server and desktop launcher share this folder; clean it once below.
        Clean   = $false
    },
    [pscustomobject]@{
        # Both executables must be installed in the same folder, so do not clean it here.
        Name    = "LabManagerDesktop"
        Project = Join-Path $repoRoot "src\UpdateAgent.LabManager.Desktop\UpdateAgent.LabManager.Desktop.csproj"
        Output  = Join-Path $OutputRoot "labmanager"
        Clean   = $false
    },
    [pscustomobject]@{
        Name    = "Service"
        Project = Join-Path $repoRoot "src\UpdateAgent.Service\UpdateAgent.Service.csproj"
        Output  = Join-Path $OutputRoot "agent\service"
        Clean   = $true
    },
    [pscustomobject]@{
        Name    = "SessionHelper"
        Project = Join-Path $repoRoot "src\UpdateAgent.SessionHelper\UpdateAgent.SessionHelper.csproj"
        Output  = Join-Path $OutputRoot "agent\sessionhelper"
        Clean   = $true
    },
    [pscustomobject]@{
        Name    = "AgentDesktop"
        Project = Join-Path $repoRoot "src\UpdateAgent.Agent.Desktop\UpdateAgent.Agent.Desktop.csproj"
        Output  = Join-Path $OutputRoot "agent\desktop"
        Clean   = $true
    },
    [pscustomobject]@{
        Name    = "Cli"
        Project = Join-Path $repoRoot "src\UpdateAgent.Cli\UpdateAgent.Cli.csproj"
        Output  = Join-Path $OutputRoot "cli"
        Clean   = $true
    }
)

$selfContained = if ($FrameworkDependent) { "false" } else { "true" }

if ($AgentOnly) {
    $projects = @($projects | Where-Object {
        $_.Name -ne "LabManager" -and $_.Name -ne "LabManagerDesktop"
    })
    Write-Host "Agent-only publish: LabManager and its desktop client will not be published." -ForegroundColor Yellow
}

# The LabManager server and desktop launcher are installed into one folder.
# Cleaning between the two publishes would remove the launcher; clean the shared folder once before both.
if (-not $NoClean -and -not $AgentOnly) {
    $labManagerOutput = Join-Path $OutputRoot "labmanager"
    if (Test-Path -LiteralPath $labManagerOutput) {
        Remove-Item -LiteralPath $labManagerOutput -Recurse -Force
    }
}

foreach ($project in $projects) {
    if (-not $NoClean -and $project.Clean -and (Test-Path -LiteralPath $project.Output)) {
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

$expectedMode = if ($FrameworkDependent) { "framework-dependent" } else { "self-contained" }
Write-Host ""
Write-Host "Validating publish layout (expected: $expectedMode)..." -ForegroundColor Cyan
foreach ($project in $projects) {
    $info = Assert-PackageLayout -Path $project.Output `
        -ExpectFrameworkDependent:$FrameworkDependent -Label $project.Name
    Write-Host ("  {0}: {1} ({2} assemblies, {3} runtime markers)" -f
        $project.Name, $info.Mode, $info.AssemblyCount, $info.RuntimeMarkerCount)
}
if ($FrameworkDependent) {
    Write-Host "Prerequisite: install the matching Microsoft .NET Desktop Runtime (win-x64) on every target PC." -ForegroundColor Yellow
}

$size = (Get-ChildItem -LiteralPath $OutputRoot -Recurse -File |
    Measure-Object -Property Length -Sum).Sum

Write-Host ""
Write-Host ("Publish complete: {0} ({1:N1} MB)" -f $OutputRoot, ($size / 1MB)) -ForegroundColor Green
Write-Host ""
$installerSwitch = if ($FrameworkDependent) { " -FrameworkDependent" } else { "" }
if ($AgentOnly) {
    Write-Host "Next steps:"
    Write-Host "  1) Build the versioned agent output: build.bat"
    Write-Host "  2) Installer (agent only): scripts\build-inno.ps1$installerSwitch -AgentOnly -NoPublish"
    Write-Host "  3) Student PC: put agent-install.ini + token file next to UpdateAgent.Agent-Setup-<version>-x64.exe, then run it with /VERYSILENT /SUPPRESSMSGBOXES /NORESTART (or use scripts\deploy-agents.ps1)"
    Write-Host "  4) Optional on a student PC: publish\cli\UpdateAgent.Cli.exe preflight --confirm-maestro"
    Write-Host "  5) Full runbook: docs\PILOT.md (installers: docs\INSTALLER.md, release: docs\RELEASE.md)"
} else {
    Write-Host "Next steps:"
    Write-Host "  1) Teacher PC: scripts\configure-labmanager.ps1, then start publish\labmanager\UpdateAgent.LabManager.Desktop.exe"
    Write-Host "  2) Installer (recommended): scripts\build-inno.ps1$installerSwitch -> artifacts\inno\*Setup*.exe"
    Write-Host "     Alternative MSI: scripts\build-msi.ps1$installerSwitch -> artifacts\msi\UpdateAgent.Agent-<version>-x64.msi"
    Write-Host "  3) Student PC (Inno): put agent-install.ini + token file next to UpdateAgent.Agent-Setup-<version>-x64.exe, then run it with /VERYSILENT /SUPPRESSMSGBOXES /NORESTART (or use scripts\deploy-agents.ps1)"
    Write-Host "     Student PC (MSI): scripts\install-agent-msi.ps1 -LabManagerUrl http://<teacher-ip>:5080 -ConfirmMaestro"
    Write-Host "     Student PC (legacy): scripts\install-service.ps1 -LabManagerUrl http://<teacher-ip>:5080"
    Write-Host "  4) Optional on a student PC: publish\cli\UpdateAgent.Cli.exe preflight --confirm-maestro"
    Write-Host "  5) Full runbook: docs\PILOT.md (installers: docs\INSTALLER.md, release: docs\RELEASE.md, MSI: docs\MSI.md)"
}
if ($FrameworkDependent) {
    Write-Host "  6) Deployment: pass -FrameworkDependent to scripts\deploy-agents.ps1 after confirming the runtime prerequisite."
}
Write-Host ""
Write-Host "Note: re-publishing resets publish\agent\service\appsettings.json to its defaults."
if (-not $AgentOnly) {
    Write-Host "      publish\labmanager\appsettings.json is also reset."
}
Write-Host "      Re-run the configure scripts after every publish."

