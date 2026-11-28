<#
.SYNOPSIS
Shared runtime-layout detection for self-contained and framework-dependent builds.

.DESCRIPTION
Reads only publish/staging metadata (*.runtimeconfig.json, runtime host files and
assembly counts). No token, URL or configuration secret is read or logged here.

A layout is classified as framework-dependent only when every runtime config
uses framework references (no includedFrameworks), no self-contained runtime
host files are present, and the assembly count is below the configured
boundary. Anything ambiguous is reported as 'unknown' so callers fail closed.
#>
Set-StrictMode -Version Latest

# Framework-dependent publish folders stay well below this assembly count;
# self-contained win-x64 folders stay at or above it. Values are inspected by
# tests/UpdateAgent.Tests/Improvement08PackageSlimmingTests.cs.
$script:FrameworkDependentMaxAssemblies = 80
$script:SelfContainedMinAssemblies = 80
$script:SelfContainedRuntimeMarkers = @(
    'hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll', 'clrjit.dll', 'System.Private.CoreLib.dll'
)

function Get-PackageRuntimeConfigState {
    param([string]$Path)

    $configs = @(Get-ChildItem -LiteralPath $Path -File -Filter '*.runtimeconfig.json' -ErrorAction SilentlyContinue)
    if ($configs.Count -eq 0) { return 'unknown' }

    $state = $null
    foreach ($config in $configs) {
        try {
            $json = Get-Content -LiteralPath $config.FullName -Raw | ConvertFrom-Json
        }
        catch {
            return 'unknown'
        }
        $optionsProperty = $json.PSObject.Properties['runtimeOptions']
        if ($null -eq $optionsProperty -or $null -eq $optionsProperty.Value) { return 'unknown' }
        $options = $optionsProperty.Value

        $includedProperty = $options.PSObject.Properties['includedFrameworks']
        $frameworkProperty = $options.PSObject.Properties['framework']
        $frameworksProperty = $options.PSObject.Properties['frameworks']
        $hasIncluded = ($null -ne $includedProperty -and $null -ne $includedProperty.Value)
        $hasFramework = (($null -ne $frameworkProperty -and $null -ne $frameworkProperty.Value) -or
            ($null -ne $frameworksProperty -and $null -ne $frameworksProperty.Value))

        # Both or neither means the layout cannot be trusted.
        if ($hasIncluded -eq $hasFramework) { return 'unknown' }
        $current = if ($hasIncluded) { 'self-contained' } else { 'framework-dependent' }
        if ($null -ne $state -and $state -ne $current) { return 'unknown' }
        $state = $current
    }
    return $state
}

function Get-PackageRuntimeFrameworks {
    param([string]$Path)

    $frameworks = New-Object System.Collections.Generic.List[string]
    $configs = @(Get-ChildItem -LiteralPath $Path -File -Filter '*.runtimeconfig.json' -ErrorAction SilentlyContinue)
    foreach ($config in $configs) {
        try {
            $json = Get-Content -LiteralPath $config.FullName -Raw | ConvertFrom-Json
        }
        catch {
            continue
        }
        $optionsProperty = $json.PSObject.Properties['runtimeOptions']
        if ($null -eq $optionsProperty -or $null -eq $optionsProperty.Value) { continue }
        $options = $optionsProperty.Value
        foreach ($propertyName in @('framework', 'frameworks', 'includedFrameworks')) {
            $property = $options.PSObject.Properties[$propertyName]
            if ($null -eq $property -or $null -eq $property.Value) { continue }
            $entries = @($property.Value)
            foreach ($entry in $entries) {
                $nameProperty = $entry.PSObject.Properties['name']
                $versionProperty = $entry.PSObject.Properties['version']
                if ($null -eq $nameProperty -or $null -eq $versionProperty) { continue }
                $text = ("{0} {1}" -f $nameProperty.Value, $versionProperty.Value)
                if (-not $frameworks.Contains($text)) { $frameworks.Add($text) }
            }
        }
    }
    return @($frameworks | Sort-Object)
}

function Get-PackageLayoutInfo {
    param([string]$Path)

    $info = [pscustomobject]@{
        Path               = $Path
        Mode               = 'unknown'
        RuntimeConfigState = 'unknown'
        RuntimeConfigCount = 0
        RuntimeMarkerCount = 0
        AssemblyCount      = 0
        Frameworks         = @()
    }
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { return $info }

    $configs = @(Get-ChildItem -LiteralPath $Path -File -Filter '*.runtimeconfig.json' -ErrorAction SilentlyContinue)
    $info.RuntimeConfigCount = $configs.Count
    $info.RuntimeConfigState = Get-PackageRuntimeConfigState -Path $Path
    $info.Frameworks = @(Get-PackageRuntimeFrameworks -Path $Path)

    $markerCount = 0
    foreach ($name in $script:SelfContainedRuntimeMarkers) {
        if (Test-Path -LiteralPath (Join-Path $Path $name) -PathType Leaf) { $markerCount++ }
    }
    $info.RuntimeMarkerCount = $markerCount
    $info.AssemblyCount = @(Get-ChildItem -LiteralPath $Path -File -Filter '*.dll' -ErrorAction SilentlyContinue).Count

    if ($info.RuntimeConfigState -eq 'self-contained' -and
        $markerCount -eq $script:SelfContainedRuntimeMarkers.Count -and
        $info.AssemblyCount -ge $script:SelfContainedMinAssemblies) {
        $info.Mode = 'self-contained'
    }
    elseif ($info.RuntimeConfigState -eq 'framework-dependent' -and
        $markerCount -eq 0 -and
        $info.AssemblyCount -lt $script:FrameworkDependentMaxAssemblies) {
        $info.Mode = 'framework-dependent'
    }
    return $info
}

function Assert-PackageLayout {
    param(
        [string]$Path,
        [bool]$ExpectFrameworkDependent,
        [string]$Label
    )

    $info = Get-PackageLayoutInfo -Path $Path
    $expected = if ($ExpectFrameworkDependent) { 'framework-dependent' } else { 'self-contained' }
    if ($info.Mode -eq $expected) { return $info }

    $observed = $info.Mode
    if ($observed -eq 'unknown') {
        $observed = ("unknown (runtime config state={0}, runtime configs={1}, runtime markers={2}, assemblies={3})" -f
            $info.RuntimeConfigState, $info.RuntimeConfigCount, $info.RuntimeMarkerCount, $info.AssemblyCount)
    }

    if ($ExpectFrameworkDependent) {
        $message = ("[{0}] Runtime mode mismatch: expected framework-dependent, but observed {1} in {2}. " +
            "Framework-dependent packages require the matching Microsoft .NET Desktop Runtime (win-x64) " +
            "on every target PC. Re-run scripts\publish.ps1 -FrameworkDependent after cleaning stale output, " +
            "then rebuild with scripts\build-inno.ps1 -FrameworkDependent or scripts\build-msi.ps1 -FrameworkDependent.") -f
            $Label, $observed, $Path
        throw $message
    }

    $message = ("[{0}] Runtime mode mismatch: expected self-contained, but observed {1} in {2}. " +
        "Framework-dependent packages are opt-in and require the matching Microsoft .NET Desktop Runtime " +
        "(win-x64) on every target PC. Re-run scripts\publish.ps1 without -FrameworkDependent and rebuild " +
        "without the switch, or pass -FrameworkDependent deliberately only after confirming the runtime is installed.") -f
        $Label, $observed, $Path
    throw $message
}

function Assert-FrameworkDependentRuntimeFiles {
    param([string]$Root, [string]$Label)

    $runtimeConfigs = @(Get-ChildItem -LiteralPath $Root -Recurse -File -Filter '*.runtimeconfig.json' -ErrorAction SilentlyContinue)
    $depsFiles = @(Get-ChildItem -LiteralPath $Root -Recurse -File -Filter '*.deps.json' -ErrorAction SilentlyContinue)
    if ($runtimeConfigs.Count -eq 0 -or $depsFiles.Count -eq 0) {
        $message = ("[{0}] Framework-dependent runtime files are missing from staging: " +
            "runtimeconfig.json={1}, deps.json={2} in {3}") -f
            $Label, $runtimeConfigs.Count, $depsFiles.Count, $Root
        throw $message
    }
    return [pscustomobject]@{
        RuntimeConfigCount = $runtimeConfigs.Count
        DepsFileCount      = $depsFiles.Count
    }
}
