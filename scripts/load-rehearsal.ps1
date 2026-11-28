<#
.SYNOPSIS
Runs a bounded synthetic 50-PC check-in load rehearsal against a real LabManager instance.

.DESCRIPTION
This script sends real POST /api/agents/checkin requests with the real agent token
header and camelCase DTO shape used by UpdateAgent.Service. It is a synthetic load
test: virtual agent IDs are generated locally and no student PC is installed or
controlled. It never proves a real-PC pilot.

Token values are never accepted as parameters. Only token FILE PATHS are accepted;
the first line of each file is read, normalized and validated. Placeholder, empty,
too-short or unreadable token files fail closed. Token values are never written to
URLs, console output, the report or any created artifact.

By default GET /api/health must return 2xx before any load is generated.
-SkipPreflight bypasses only that network check; token validation still runs.
With -AdminTokenFile the script also starts and cancels a small number of synthetic
jobs through the real admin endpoints, and briefly opens/reopens the SSE stream
at GET /api/admin/live. No feature-update policy is ever enabled.

The UTF-8 (no BOM) JSON report records UTC time, product version, parameters
without token values, status/latency/recovery statistics, sanitized failures and
SHA-256 hashes of artifacts created before the report. Press Ctrl+C to cancel:
the key press is converted into a cancellation request, in-flight HTTP requests
receive the cancellation token and the script still writes a report. Non-zero
exit codes: 1 = preflight/token/fatal/cancel, 2 = any HTTP 5xx, 3 = transport
failures above the documented threshold, 4 = success rate below the documented
threshold or an authentication/throttling failure.

.EXAMPLE
.\scripts\load-rehearsal.ps1 -AgentTokenFile .\secure\agent-token.txt

.EXAMPLE
.\scripts\load-rehearsal.ps1 -LabManagerUrl http://192.168.0.10:5080 -AgentTokenFile .\secure\agent-token.txt -AgentCount 50 -DurationSeconds 30 -IntervalSeconds 5 -OutputPath .\deploy\load-rehearsal\report.json

.EXAMPLE
.\scripts\load-rehearsal.ps1 -AgentTokenFile .\secure\agent-token.txt -AdminTokenFile .\secure\admin-token.txt -MaxConcurrency 10 -SkipPreflight
#>
[CmdletBinding()]
param(
    [string]$LabManagerUrl = 'http://127.0.0.1:5080',
    [string]$AgentTokenFile,
    [string]$AdminTokenFile,
    [ValidateRange(1, 200)]
    [int]$AgentCount = 50,
    [ValidateRange(5, 600)]
    [int]$DurationSeconds = 30,
    [ValidateRange(1, 60)]
    [int]$IntervalSeconds = 5,
    [ValidateRange(1, 50)]
    [int]$MaxConcurrency = 10,
    [ValidateRange(1, 60)]
    [int]$RequestTimeoutSeconds = 10,
    [string]$OutputPath,
    [switch]$SkipPreflight
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http

$repoRoot = Split-Path -Parent $PSScriptRoot
$rehearsalVersion = '1.0'
$minTokenLength = 8
$maxTransportFailurePercent = 5
$minSuccessPercent = 95
$maxFailureRecords = 50
$maxAdminJobTargets = 2

if ([string]::IsNullOrWhiteSpace($AgentTokenFile)) {
    throw 'AgentTokenFile is required and must be a file path, never a token value.'
}
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repoRoot 'deploy\load-rehearsal\load-rehearsal-report.json'
}
$reportPath = [IO.Path]::GetFullPath($OutputPath)
$reportDir = Split-Path -Parent $reportPath
if (-not (Test-Path -LiteralPath $reportDir -PathType Container)) {
    New-Item -ItemType Directory -Path $reportDir -Force | Out-Null
}

$script:agentToken = ''
$script:adminToken = ''
$script:statusCounts = [ordered]@{}
$script:agentStates = @()
$script:failureRecords = New-Object 'System.Collections.Generic.List[object]'
$script:truncatedFailureCount = 0
$script:completedRequests = 0
$script:successCount = 0
$script:httpFailureCount = 0
$script:transportFailureCount = 0
$script:serverErrorCount = 0
$script:authFailureCount = 0
$script:throttledCount = 0
$script:latencies = New-Object 'System.Collections.Generic.List[double]'
$script:runId = [guid]::NewGuid().ToString('N').Substring(0, 12)
$script:artifacts = @()
$script:runStatus = 'started'
$script:finalMessage = 'Rehearsal started.'
$script:deadlineReached = $false
$script:userCancelled = $false
$script:inFlightAtCancellation = 0
$script:client = New-Object System.Net.Http.HttpClient

function Get-SafeMessage {
    param([AllowNull()][object]$Message)

    $text = [string]$Message
    if ([string]::IsNullOrWhiteSpace($text)) { return 'unknown error' }
    foreach ($secret in @($script:agentToken, $script:adminToken)) {
        if (-not [string]::IsNullOrEmpty($secret)) {
            $text = $text.Replace($secret, '[redacted]')
        }
    }
    $text = [regex]::Replace($text, '(?i)(X-(?:Agent|Admin)-Token\s*[:=]\s*)[^\s,;]+', '$1[redacted]')
    $text = [regex]::Replace($text, '[\r\n\t]+', ' ')
    if ($text.Length -gt 240) { $text = $text.Substring(0, 240) + '...' }
    return $text
}

function Get-TokenFromFile {
    param(
        [string]$Path,
        [string]$Label
    )

    $full = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
        throw "$Label token file not found: $full"
    }
    # The server reads only the first line. Never read the whole token file.
    $firstLine = @(Get-Content -LiteralPath $full -TotalCount 1)
    if ($firstLine.Count -eq 0) { $firstLine = @('') }
    $value = ([string]$firstLine[0]).TrimStart([char]0xFEFF).Trim()
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "$Label token file is empty: $full"
    }
    $lower = $value.ToLowerInvariant()
    if ($lower.StartsWith('change-me-') -or
        ($lower.StartsWith('sample-') -and $lower.EndsWith('-replace-before-pilot'))) {
        throw "$Label token is a default/sample placeholder. Generate a real token before the rehearsal."
    }
    if ($value.Length -lt $minTokenLength) {
        throw "$Label token is too short (minimum $minTokenLength characters): $full"
    }
    return $value
}

function Get-ProductVersion {
    $props = Join-Path $repoRoot 'Directory.Build.props'
    if (-not (Test-Path -LiteralPath $props -PathType Leaf)) { return 'unknown' }
    $text = Get-Content -LiteralPath $props -Raw
    if ($text -match '<Version>([^<]+)</Version>') { return $Matches[1].Trim() }
    return 'unknown'
}

function Get-SafeBaseUrl {
    param([string]$Url)

    $value = ([string]$Url).Trim().TrimEnd('/')
    $uri = $null
    if (-not [Uri]::TryCreate($value, [UriKind]::Absolute, [ref]$uri)) {
        throw 'LabManagerUrl must be an absolute http(s) URL.'
    }
    if ($uri.Scheme -notin @('http', 'https')) {
        throw 'LabManagerUrl must use http or https.'
    }
    if (-not [string]::IsNullOrEmpty($uri.UserInfo)) {
        throw 'LabManagerUrl must not contain credentials.'
    }
    if (-not [string]::IsNullOrWhiteSpace($uri.Query) -or -not [string]::IsNullOrWhiteSpace($uri.Fragment)) {
        throw 'LabManagerUrl must not contain a query string or fragment.'
    }
    return $uri.GetLeftPart([UriPartial]::Path).TrimEnd('/')
}

function Invoke-HealthPreflight {
    param([string]$BaseUrl)

    $healthUrl = $BaseUrl + '/api/health'
    try {
        $response = $script:client.GetAsync($healthUrl).GetAwaiter().GetResult()
        try {
            $statusCode = [int]$response.StatusCode
        }
        finally {
            $response.Dispose()
        }
        if ($statusCode -lt 200 -or $statusCode -ge 300) {
            throw "Preflight failed: $healthUrl returned HTTP $statusCode."
        }
        return [pscustomobject]@{ Status = 'passed'; StatusCode = $statusCode; Message = 'Health check returned 2xx.' }
    }
    catch {
        throw ('Preflight failed: ' + (Get-SafeMessage -Message $_.Exception.Message) + ' Use -SkipPreflight only for an intentionally offline rehearsal.')
    }
}

function Write-InputsArtifact {
    param(
        [string]$BaseUrl,
        [string]$RunStatus
    )

    $inputsPath = [IO.Path]::Combine($reportDir, 'load-rehearsal-' + $script:runId + '.inputs.json')
    $inputs = [ordered]@{
        runId = $script:runId
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        labManagerUrl = $BaseUrl
        status = $RunStatus
        parameters = [ordered]@{
            agentCount = $AgentCount
            durationSeconds = $DurationSeconds
            intervalSeconds = $IntervalSeconds
            maxConcurrency = $MaxConcurrency
            requestTimeoutSeconds = $RequestTimeoutSeconds
            skipPreflight = [bool]$SkipPreflight
            agentTokenFile = $AgentTokenFile
            adminTokenFile = if ([string]::IsNullOrWhiteSpace($AdminTokenFile)) { '' } else { $AdminTokenFile }
        }
        secretHandling = 'Token values are read from the first line of each file and are not stored in this artifact.'
    }
    $json = $inputs | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText($inputsPath, $json + "`r`n", (New-Object System.Text.UTF8Encoding($false)))
    $hash = (Get-FileHash -LiteralPath $inputsPath -Algorithm SHA256).Hash.ToLowerInvariant()
    return [pscustomobject]@{
        kind = 'inputs'
        path = $inputsPath
        sha256 = $hash
    }
}

function Add-FailureRecord {
    param(
        [int]$AgentIndex,
        [string]$Kind,
        [int]$StatusCode,
        [string]$Message
    )

    if ($script:failureRecords.Count -lt $maxFailureRecords) {
        $script:failureRecords.Add([ordered]@{
            agentIndex = $AgentIndex
            kind = $Kind
            statusCode = $StatusCode
            message = (Get-SafeMessage -Message $Message)
        })
    }
    else {
        # Count only records that were omitted from the report, not every failure.
        $script:truncatedFailureCount++
    }
}

function Add-Latency {
    param([double]$Milliseconds)

    $script:latencies.Add($Milliseconds)
    $script:completedRequests++
}

function Set-AgentRequestCompleted {
    param(
        [int]$AgentIndex,
        [bool]$Succeeded
    )

    $state = $script:agentStates[$AgentIndex]
    if ($Succeeded) {
        if ($state.ConsecutiveFailures -gt 0) {
            $state.RecoveredAfterFailure = $true
        }
        $state.ConsecutiveFailures = 0
        $state.Successes++
        $script:successCount++
    }
    else {
        $state.ConsecutiveFailures++
        $state.Failures++
        if ($state.ConsecutiveFailures -gt $state.MaxConsecutiveFailures) {
            $state.MaxConsecutiveFailures = $state.ConsecutiveFailures
        }
    }
}

function New-CheckInJson {
    param(
        [string]$AgentId,
        [string]$MachineName,
        [string]$AgentVersion,
        [DateTimeOffset]$Now
    )

    $timestamp = $Now.ToString('o')
    $body = [ordered]@{
        agent = [ordered]@{
            agentId = $AgentId
            machineName = $MachineName
            agentVersion = $AgentVersion
            ipAddress = ''
            macAddresses = @()
            os = $null
            firstSeen = $timestamp
            lastSeen = $timestamp
            state = 'idle'
            maestroInstalled = $false
            maestroDetectionFailed = $false
            isLaptop = $false
            laptopDetectionFailed = $false
            hasBattery = $false
            freeDiskMb = 40960
        }
        telemetry = [ordered]@{
            agentId = $AgentId
            timestamp = $timestamp
            state = 'idle'
            currentJob = $null
            currentJobDetail = $null
            currentPhase = ''
            userLoggedOn = $false
            userSessionProbeFailed = $false
            loggedOnUsers = @()
            wuBusy = $false
            wuBusyProbeFailed = $false
            rebootPending = $false
            rebootPendingProbeFailed = $false
            maestro = $null
            freeDiskMb = 40960
            onAcPower = $true
            lastError = $null
        }
        commandResults = @()
        sentAt = $timestamp
    }
    return ($body | ConvertTo-Json -Depth 8 -Compress)
}

function New-RequestCts {
    param(
        [int]$Milliseconds,
        [System.Threading.CancellationToken]$ParentToken
    )

    # Windows PowerShell 5.1 exposes only the parameterless, TimeSpan and int
    # constructors. Register the parent token explicitly instead of relying on
    # a constructor overload that does not exist there.
    $source = New-Object System.Threading.CancellationTokenSource -ArgumentList $Milliseconds
    $registration = $null
    if ($ParentToken.CanBeCanceled) {
        $callback = [System.Action[object]]{
            param($state)
            ([System.Threading.CancellationTokenSource]$state).Cancel()
        }
        $registration = $ParentToken.Register($callback, $source)
    }
    return [pscustomobject]@{ Source = $source; Registration = $registration }
}

function Dispose-RequestCts {
    param([AllowNull()][object]$Holder)

    if ($null -eq $Holder) { return }
    if ($null -ne $Holder.Registration) { $Holder.Registration.Dispose() }
    $Holder.Source.Dispose()
}

function Start-CheckInTask {
    param(
        [string]$Url,
        [string]$JsonBody,
        [int]$AgentIndex,
        [System.Threading.CancellationToken]$CancelToken
    )

    $requestCts = New-RequestCts -Milliseconds ($RequestTimeoutSeconds * 1000) -ParentToken $CancelToken
    $request = New-Object System.Net.Http.HttpRequestMessage -ArgumentList @([System.Net.Http.HttpMethod]::Post, $Url)
    $request.Content = New-Object System.Net.Http.StringContent -ArgumentList @($JsonBody, [Text.Encoding]::UTF8, 'application/json')
    $request.Headers.Add('X-Agent-Token', $script:agentToken)
    try {
        $task = $script:client.SendAsync($request, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead, $requestCts.Source.Token)
        return [pscustomobject]@{
            Task = $task
            Request = $request
            Cts = $requestCts
            AgentIndex = $AgentIndex
            Stopwatch = [Diagnostics.Stopwatch]::StartNew()
        }
    }
    catch {
        $request.Dispose()
        Dispose-RequestCts -Holder $requestCts
        throw
    }
}

function Receive-OneResult {
    param(
        [object]$Pending,
        [System.Threading.CancellationToken]$WaitToken,
        [int]$WaitMs
    )

    if ($null -eq $Pending -or $Pending.Count -eq 0) { return }
    $tasks = [System.Threading.Tasks.Task[]]@($Pending | ForEach-Object { $_.Task })
    if ($tasks.Count -eq 0) { return }
    try {
        $index = [System.Threading.Tasks.Task]::WaitAny($tasks, $WaitMs, $WaitToken)
    }
    catch [OperationCanceledException] {
        return
    }
    if ($index -lt 0) { return }

    $entry = $Pending[$index]
    $Pending.RemoveAt($index)
    $statusCode = 0
    $failureKind = ''
    $failureMessage = ''
    $response = $null
    try {
        $response = $entry.Task.GetAwaiter().GetResult()
        if ($null -ne $response) { $statusCode = [int]$response.StatusCode }
    }
    catch {
        $failureKind = 'transport'
        $failureMessage = Get-SafeMessage -Message $_.Exception.Message
    }
    finally {
        if ($null -ne $response) { $response.Dispose() }
        $entry.Request.Dispose()
        Dispose-RequestCts -Holder $entry.Cts
        $entry.Stopwatch.Stop()
    }

    Add-Latency -Milliseconds ([double]$entry.Stopwatch.Elapsed.TotalMilliseconds)
    $statusKey = [string]$statusCode
    if ($failureKind -eq 'transport') { $statusKey = 'transport' }
    if (-not $script:statusCounts.Contains($statusKey)) { $script:statusCounts[$statusKey] = 0 }
    $script:statusCounts[$statusKey]++

    if ($failureKind -ne '') {
        $script:transportFailureCount++
        Set-AgentRequestCompleted -AgentIndex $entry.AgentIndex -Succeeded $false
        Add-FailureRecord -AgentIndex $entry.AgentIndex -Kind $failureKind -StatusCode 0 -Message $failureMessage
        return
    }

    if ($statusCode -ge 200 -and $statusCode -lt 300) {
        Set-AgentRequestCompleted -AgentIndex $entry.AgentIndex -Succeeded $true
        return
    }

    $script:httpFailureCount++
    if ($statusCode -ge 500) { $script:serverErrorCount++ }
    if ($statusCode -eq 401 -or $statusCode -eq 403) { $script:authFailureCount++ }
    if ($statusCode -eq 429) { $script:throttledCount++ }
    Set-AgentRequestCompleted -AgentIndex $entry.AgentIndex -Succeeded $false
    Add-FailureRecord -AgentIndex $entry.AgentIndex -Kind "http-$statusCode" -StatusCode $statusCode -Message "Check-in returned HTTP $statusCode."
}

function Invoke-AdminJson {
    param(
        [string]$Url,
        [string]$Method,
        [AllowNull()][string]$JsonBody,
        [System.Threading.CancellationToken]$CancelToken,
        [bool]$ReadBody
    )

    $requestCts = New-RequestCts -Milliseconds ($RequestTimeoutSeconds * 1000) -ParentToken $CancelToken
    $request = $null
    try {
        $methodValue = [System.Net.Http.HttpMethod]::Post
        if ($Method -eq 'GET') { $methodValue = [System.Net.Http.HttpMethod]::Get }
        $request = New-Object System.Net.Http.HttpRequestMessage -ArgumentList @($methodValue, $Url)
        if (-not [string]::IsNullOrEmpty($JsonBody)) {
            $request.Content = New-Object System.Net.Http.StringContent -ArgumentList @($JsonBody, [Text.Encoding]::UTF8, 'application/json')
        }
        $request.Headers.Add('X-Admin-Token', $script:adminToken)
        $response = $script:client.SendAsync($request, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead, $requestCts.Source.Token).GetAwaiter().GetResult()
        try {
            $statusCode = [int]$response.StatusCode
            $body = ''
            if ($ReadBody -and $statusCode -ge 200 -and $statusCode -lt 300) {
                $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            }
        }
        finally {
            $response.Dispose()
        }
        return [pscustomobject]@{ StatusCode = $statusCode; Body = $body; Error = '' }
    }
    catch {
        return [pscustomobject]@{ StatusCode = 0; Body = ''; Error = (Get-SafeMessage -Message $_.Exception.Message) }
    }
    finally {
        if ($null -ne $request) { $request.Dispose() }
        Dispose-RequestCts -Holder $requestCts
    }
}

function Get-LiveSnapshot {
    param(
        [string]$Url,
        [System.Threading.CancellationToken]$CancelToken
    )

    $liveTimeoutMs = [math]::Min(($RequestTimeoutSeconds * 1000), 5000)
    $requestCts = New-RequestCts -Milliseconds $liveTimeoutMs -ParentToken $CancelToken
    $request = $null
    $response = $null
    try {
        $request = New-Object System.Net.Http.HttpRequestMessage -ArgumentList @([System.Net.Http.HttpMethod]::Get, $Url)
        $request.Headers.Add('X-Admin-Token', $script:adminToken)
        $accept = New-Object System.Net.Http.Headers.MediaTypeWithQualityHeaderValue -ArgumentList 'text/event-stream'
        $request.Headers.Accept.Add($accept)
        $response = $script:client.SendAsync($request, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead, $requestCts.Source.Token).GetAwaiter().GetResult()
        $statusCode = [int]$response.StatusCode
        if ($statusCode -lt 200 -or $statusCode -ge 300) {
            return [pscustomobject]@{ StatusCode = $statusCode; SnapshotSeen = $false; Error = 'SSE endpoint did not return 2xx.' }
        }
        $stream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        $buffer = New-Object byte[] 4096
        $builder = New-Object Text.StringBuilder
        for ($i = 0; $i -lt 20; $i++) {
            $read = $stream.ReadAsync($buffer, 0, $buffer.Length, $requestCts.Source.Token).GetAwaiter().GetResult()
            if ($read -le 0) { break }
            [void]$builder.Append([Text.Encoding]::UTF8.GetString($buffer, 0, $read))
            if ($builder.ToString() -match 'event:\s*snapshot') {
                return [pscustomobject]@{ StatusCode = $statusCode; SnapshotSeen = $true; Error = '' }
            }
            if ($builder.Length -gt 65536) { break }
        }
        return [pscustomobject]@{ StatusCode = $statusCode; SnapshotSeen = $false; Error = 'No snapshot event was received before the timeout.' }
    }
    catch {
        return [pscustomobject]@{ StatusCode = 0; SnapshotSeen = $false; Error = (Get-SafeMessage -Message $_.Exception.Message) }
    }
    finally {
        if ($null -ne $response) { $response.Dispose() }
        if ($null -ne $request) { $request.Dispose() }
        Dispose-RequestCts -Holder $requestCts
    }
}

function Invoke-AdminChecks {
    param(
        [string]$BaseUrl,
        [string[]]$TargetAgentIds,
        [System.Threading.CancellationToken]$CancelToken
    )

    $result = [ordered]@{
        status = 'skipped'
        reason = 'Admin token file was not supplied.'
        startStatusCode = 0
        startedJobs = 0
        cancelledJobs = 0
        errors = @()
    }
    if ([string]::IsNullOrEmpty($script:adminToken)) { return [pscustomobject]$result }
    $result.status = 'attempted'
    $result.reason = ''

    $startBody = [ordered]@{
        agentIds = @($TargetAgentIds)
        maestroConfirmed = $false
        confirmedBy = 'teacher'
        note = 'Synthetic load rehearsal job'
        saveAsAgentPolicy = $false
    } | ConvertTo-Json -Depth 6 -Compress
    $start = Invoke-AdminJson -Url ($BaseUrl + '/api/admin/jobs/start') -Method 'POST' -JsonBody $startBody -CancelToken $CancelToken -ReadBody $true
    $result.startStatusCode = $start.StatusCode
    if ($start.StatusCode -lt 200 -or $start.StatusCode -ge 300) {
        $result.errors += (Get-SafeMessage -Message ($start.Error + ' ' + $start.Body))
        if (-not [string]::IsNullOrEmpty($start.Error)) { return [pscustomobject]$result }
    }

    $jobIds = @()
    if (-not [string]::IsNullOrWhiteSpace($start.Body)) {
        try {
            $parsed = $start.Body | ConvertFrom-Json -ErrorAction Stop
            $jobIds = @($parsed.jobIds | Where-Object { $_ -match '^[A-Za-z0-9_-]+$' })
        }
        catch {
            $result.errors += 'StartJobs response was not valid JSON.'
        }
    }
    $result.startedJobs = $jobIds.Count

    foreach ($jobId in $jobIds) {
        $cancel = Invoke-AdminJson -Url ($BaseUrl + '/api/admin/jobs/' + $jobId + '/cancel') -Method 'POST' -JsonBody '' -CancelToken $CancelToken -ReadBody $false
        if ($cancel.StatusCode -ge 200 -and $cancel.StatusCode -lt 300) {
            $result.cancelledJobs++
        }
        else {
            $result.errors += (Get-SafeMessage -Message ("Cancel failed with HTTP " + $cancel.StatusCode + ' ' + $cancel.Error))
        }
    }
    return [pscustomobject]$result
}

function Invoke-LiveChecks {
    param(
        [string]$BaseUrl,
        [System.Threading.CancellationToken]$CancelToken
    )

    $result = [ordered]@{
        status = 'skipped'
        reason = 'Admin token file was not supplied; GET /api/admin/live requires X-Admin-Token.'
        firstStatusCode = 0
        secondStatusCode = 0
        firstSnapshotSeen = $false
        secondSnapshotSeen = $false
        reconnectVerified = $false
        errors = @()
    }
    if ([string]::IsNullOrEmpty($script:adminToken)) { return [pscustomobject]$result }
    $result.status = 'attempted'
    $result.reason = ''

    $url = $BaseUrl + '/api/admin/live'
    $first = Get-LiveSnapshot -Url $url -CancelToken $CancelToken
    $result.firstStatusCode = $first.StatusCode
    $result.firstSnapshotSeen = $first.SnapshotSeen
    if (-not [string]::IsNullOrEmpty($first.Error)) { $result.errors += $first.Error }

    $second = Get-LiveSnapshot -Url $url -CancelToken $CancelToken
    $result.secondStatusCode = $second.StatusCode
    $result.secondSnapshotSeen = $second.SnapshotSeen
    if (-not [string]::IsNullOrEmpty($second.Error)) { $result.errors += $second.Error }

    $result.reconnectVerified = ($first.SnapshotSeen -and $second.SnapshotSeen)
    return [pscustomobject]$result
}

function Get-LatencyStats {
    if ($script:latencies.Count -eq 0) {
        return [ordered]@{ sampleCount = 0; minMs = $null; avgMs = $null; p95Ms = $null; maxMs = $null }
    }
    $sorted = @($script:latencies | Sort-Object)
    $sum = 0.0
    foreach ($value in $sorted) { $sum += $value }
    $p95Index = [int][math]::Ceiling(0.95 * $sorted.Count) - 1
    if ($p95Index -lt 0) { $p95Index = 0 }
    return [ordered]@{
        sampleCount = $sorted.Count
        minMs = [math]::Round($sorted[0], 2)
        avgMs = [math]::Round($sum / $sorted.Count, 2)
        p95Ms = [math]::Round($sorted[$p95Index], 2)
        maxMs = [math]::Round($sorted[$sorted.Count - 1], 2)
    }
}

function Write-RehearsalReport {
    param(
        [string]$BaseUrl,
        [object]$Preflight,
        [object]$LiveResult,
        [object]$JobResult,
        [int]$ExitCode
    )

    $transportPercent = 0.0
    $successPercent = 0.0
    if ($script:completedRequests -gt 0) {
        $transportPercent = [math]::Round((100.0 * $script:transportFailureCount / $script:completedRequests), 2)
        $successPercent = [math]::Round((100.0 * $script:successCount / $script:completedRequests), 2)
    }
    $recovered = @($script:agentStates | Where-Object { $_.RecoveredAfterFailure -and $_.ConsecutiveFailures -eq 0 })
    $unrecovered = @($script:agentStates | Where-Object { $_.Failures -gt 0 -and $_.ConsecutiveFailures -gt 0 } | ForEach-Object { $_.AgentId })
    $maxConsecutive = 0
    foreach ($state in $script:agentStates) {
        if ($state.MaxConsecutiveFailures -gt $maxConsecutive) { $maxConsecutive = $state.MaxConsecutiveFailures }
    }

    $report = [ordered]@{
        schemaVersion = 1
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        status = $script:runStatus
        message = $script:finalMessage
        version = [ordered]@{
            product = (Get-ProductVersion)
            rehearsalScript = $rehearsalVersion
        }
        parameters = [ordered]@{
            labManagerUrl = $BaseUrl
            agentCount = $AgentCount
            durationSeconds = $DurationSeconds
            intervalSeconds = $IntervalSeconds
            maxConcurrency = $MaxConcurrency
            requestTimeoutSeconds = $RequestTimeoutSeconds
            skipPreflight = [bool]$SkipPreflight
            agentTokenFile = $AgentTokenFile
            adminTokenFile = if ([string]::IsNullOrWhiteSpace($AdminTokenFile)) { '' } else { $AdminTokenFile }
        }
        secretHandling = 'Token values were read from the first line of each file and are not stored in URLs, console output, this report or created artifacts.'
        preflight = if ($null -eq $Preflight) { [ordered]@{ status = 'skipped'; statusCode = 0; message = 'No preflight was attempted.' } } else { $Preflight }
        counts = [ordered]@{
            requestedAgents = $AgentCount
            completedRequests = $script:completedRequests
            success = $script:successCount
            httpFailure = $script:httpFailureCount
            transportFailure = $script:transportFailureCount
            serverError5xx = $script:serverErrorCount
            unauthorized = $script:authFailureCount
            throttled = $script:throttledCount
            transportFailurePercent = $transportPercent
            successPercent = $successPercent
        }
        statusDistribution = $script:statusCounts
        latencyMs = (Get-LatencyStats)
        recovery = [ordered]@{
            agentsWithFailures = @($script:agentStates | Where-Object { $_.Failures -gt 0 }).Count
            agentsRecoveredAfterFailure = $recovered.Count
            maxConsecutiveFailures = $maxConsecutive
            unrecoveredAgentIds = $unrecovered
        }
        failures = $script:failureRecords.ToArray()
        truncatedFailureCount = $script:truncatedFailureCount
        thresholds = [ordered]@{
            failOnAny5xx = $true
            failOnAnyUnauthorized = $true
            failOnAnyThrottled = $true
            maxTransportFailurePercent = $maxTransportFailurePercent
            minSuccessPercent = $minSuccessPercent
            exitCode = $ExitCode
        }
        liveStream = $LiveResult
        jobChecks = $JobResult
        artifacts = @($script:artifacts)
        cancellation = [ordered]@{
            deadlineReached = $script:deadlineReached
            userCancelled = $script:userCancelled
            inFlightAtCancellation = $script:inFlightAtCancellation
        }
    }
    $json = $report | ConvertTo-Json -Depth 12
    [IO.File]::WriteAllText($reportPath, $json + "`r`n", (New-Object System.Text.UTF8Encoding($false)))
    $reportHash = (Get-FileHash -LiteralPath $reportPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host ("Report: " + $reportPath)
    Write-Host ("Report SHA-256: " + $reportHash)
}

# Keep the reported URL empty until Get-SafeBaseUrl has validated it. Reporting
# the repository root here would mislead readers when URL validation fails.
$baseUrl = ''
$preflight = $null
$liveResult = [pscustomobject]([ordered]@{
    status = 'skipped'
    reason = 'No live stream check was attempted.'
    firstStatusCode = 0
    secondStatusCode = 0
    firstSnapshotSeen = $false
    secondSnapshotSeen = $false
    reconnectVerified = $false
    errors = @()
})
$jobResult = [pscustomobject]([ordered]@{
    status = 'skipped'
    reason = 'No admin job check was attempted.'
    startStatusCode = 0
    startedJobs = 0
    cancelledJobs = 0
    errors = @()
})
$exitCode = 0
$rootCts = New-Object System.Threading.CancellationTokenSource
$hardTimeoutMs = ($DurationSeconds + $RequestTimeoutSeconds + 30) * 1000
$rootCts.CancelAfter($hardTimeoutMs)
$pending = New-Object 'System.Collections.Generic.List[object]'
$runWatch = [Diagnostics.Stopwatch]::StartNew()

# PowerShell stops the pipeline on Ctrl+C, which would skip the report write after
# finally. Convert the key press into a normal cancellation request instead.
$script:cancelKeyHandler = {
    param($Sender, [System.ConsoleCancelEventArgs]$Event)
    $Event.Cancel = $true
    $script:userCancelled = $true
    $rootCts.Cancel()
}
[Console]::add_CancelKeyPress($script:cancelKeyHandler)

try {
    $baseUrl = Get-SafeBaseUrl -Url $LabManagerUrl
    $script:agentToken = Get-TokenFromFile -Path $AgentTokenFile -Label 'Agent'
    if (-not [string]::IsNullOrWhiteSpace($AdminTokenFile)) {
        $script:adminToken = Get-TokenFromFile -Path $AdminTokenFile -Label 'Admin'
        if ($script:adminToken -eq $script:agentToken) {
            throw 'Admin and agent token files contain the same value; they must be distinct.'
        }
    }
    $script:artifacts = @(Write-InputsArtifact -BaseUrl $baseUrl -RunStatus 'running')

    if ($SkipPreflight) {
        $preflight = [pscustomobject]@{ status = 'skipped'; statusCode = 0; message = 'Preflight was skipped by request; token validation still ran.' }
        Write-Host 'Preflight skipped by request.'
    }
    else {
        $preflight = Invoke-HealthPreflight -BaseUrl $baseUrl
        Write-Host 'Health check OK: ' + ($baseUrl + '/api/health')
    }

    $checkInUrl = $baseUrl + '/api/agents/checkin'
    $productVersion = Get-ProductVersion
    for ($i = 0; $i -lt $AgentCount; $i++) {
        $suffix = $i.ToString('D3')
        # Stable IDs let repeated rehearsals reuse the same synthetic PCs instead
        # of filling the server's 200-agent registry with a new fleet on every run.
        $script:agentStates += [pscustomobject]@{
            AgentId = 'rehearsal-pc-' + $suffix
            MachineName = 'LOAD-REHEARSAL-' + $suffix
            Successes = 0
            Failures = 0
            ConsecutiveFailures = 0
            MaxConsecutiveFailures = 0
            RecoveredAfterFailure = $false
        }
    }

    $wave = 0
    while (-not $rootCts.IsCancellationRequested -and $runWatch.Elapsed.TotalSeconds -lt $DurationSeconds) {
        $wave++
        for ($i = 0; $i -lt $AgentCount; $i++) {
            while ($pending.Count -ge $MaxConcurrency -and -not $rootCts.IsCancellationRequested) {
                Receive-OneResult -Pending $pending -WaitToken $rootCts.Token -WaitMs 50
            }
            if ($rootCts.IsCancellationRequested) { break }
            $state = $script:agentStates[$i]
            $jsonBody = New-CheckInJson -AgentId $state.AgentId -MachineName $state.MachineName -AgentVersion $productVersion -Now ([DateTimeOffset]::UtcNow)
            $entry = Start-CheckInTask -Url $checkInUrl -JsonBody $jsonBody -AgentIndex $i -CancelToken $rootCts.Token
            $pending.Add($entry)
        }
        $nextWaveAt = $wave * $IntervalSeconds
        while (-not $rootCts.IsCancellationRequested -and $runWatch.Elapsed.TotalSeconds -lt $nextWaveAt) {
            Receive-OneResult -Pending $pending -WaitToken $rootCts.Token -WaitMs 50
            $rootCts.Token.WaitHandle.WaitOne(100) | Out-Null
        }
    }

    $stalled = 0
    while ($pending.Count -gt 0) {
        $before = $pending.Count
        Receive-OneResult -Pending $pending -WaitToken ([System.Threading.CancellationToken]::None) -WaitMs 200
        if ($pending.Count -eq $before) {
            $stalled++
            if ($stalled -ge 10) {
                Write-Host 'Timed out while draining in-flight check-ins; cancelling remaining requests.'
                foreach ($entry in $pending.ToArray()) {
                    Add-Latency -Milliseconds ([double]$entry.Stopwatch.Elapsed.TotalMilliseconds)
                    $statusKey = 'transport-timeout'
                    if (-not $script:statusCounts.Contains($statusKey)) { $script:statusCounts[$statusKey] = 0 }
                    $script:statusCounts[$statusKey]++
                    $script:transportFailureCount++
                    Set-AgentRequestCompleted -AgentIndex $entry.AgentIndex -Succeeded $false
                    Add-FailureRecord -AgentIndex $entry.AgentIndex -Kind 'transport-timeout' -StatusCode 0 -Message 'Request exceeded the hard drain timeout.'
                    $entry.Cts.Source.Cancel()
                    $entry.Request.Dispose()
                    Dispose-RequestCts -Holder $entry.Cts
                    $entry.Stopwatch.Stop()
                }
                $pending.Clear()
                break
            }
        }
        else {
            $stalled = 0
        }
    }

    $script:inFlightAtCancellation = $pending.Count
    if ($rootCts.IsCancellationRequested) {
        $script:deadlineReached = (-not $script:userCancelled)
    }

    # A cancelled run must never be reported as passed just because all requests
    # that finished before cancellation were successful.
    if ($script:userCancelled -or $script:deadlineReached) {
        $exitCode = 1
        $script:runStatus = 'cancelled'
        $script:finalMessage = 'Rehearsal cancelled before completion.'
        Write-Host $script:finalMessage
    }
    else {
        $targetIds = @($script:agentStates | Select-Object -First ([math]::Min($maxAdminJobTargets, $AgentCount)) | ForEach-Object { $_.AgentId })
        $jobResult = Invoke-AdminChecks -BaseUrl $baseUrl -TargetAgentIds $targetIds -CancelToken $rootCts.Token
        $liveResult = Invoke-LiveChecks -BaseUrl $baseUrl -CancelToken $rootCts.Token

        if ($script:serverErrorCount -gt 0) {
            $exitCode = 2
            $script:runStatus = 'failed'
            $script:finalMessage = 'Rehearsal failed: at least one HTTP 5xx response was observed.'
        }
        elseif ($script:authFailureCount -gt 0 -or $script:throttledCount -gt 0) {
            $exitCode = 4
            $script:runStatus = 'failed'
            $script:finalMessage = 'Rehearsal failed: an authentication or throttling response was observed.'
        }
        elseif ($script:completedRequests -gt 0 -and (100.0 * $script:transportFailureCount / $script:completedRequests) -gt $maxTransportFailurePercent) {
            $exitCode = 3
            $script:runStatus = 'failed'
            $script:finalMessage = 'Rehearsal failed: transport failures exceeded the configured threshold.'
        }
        elseif ($script:completedRequests -gt 0 -and (100.0 * $script:successCount / $script:completedRequests) -lt $minSuccessPercent) {
            $exitCode = 4
            $script:runStatus = 'failed'
            $script:finalMessage = 'Rehearsal failed: success rate was below the configured threshold.'
        }
        elseif ($script:completedRequests -eq 0) {
            $exitCode = 3
            $script:runStatus = 'failed'
            $script:finalMessage = 'Rehearsal failed: no check-in attempts completed.'
        }
        else {
            $script:runStatus = 'passed'
            $script:finalMessage = 'Rehearsal completed; see the report for counts and advisory checks.'
        }
        Write-Host $script:finalMessage
        Write-Host ('Requests: ' + $script:completedRequests + ', success: ' + $script:successCount + ', 5xx: ' + $script:serverErrorCount + ', transport: ' + $script:transportFailureCount)
    }
}
catch [OperationCanceledException] {
    $script:inFlightAtCancellation = $pending.Count
    $script:deadlineReached = ($rootCts.IsCancellationRequested -and -not $script:userCancelled)
    $exitCode = 1
    $script:runStatus = 'cancelled'
    $script:finalMessage = 'Rehearsal cancelled before completion.'
    Write-Host $script:finalMessage
}
catch {
    $script:inFlightAtCancellation = $pending.Count
    $script:deadlineReached = ($rootCts.IsCancellationRequested -and -not $script:userCancelled)
    $exitCode = 1
    $script:runStatus = 'failed'
    $script:finalMessage = 'Rehearsal failed: ' + (Get-SafeMessage -Message $_.Exception.Message)
    Write-Host $script:finalMessage
}
finally {
    [Console]::remove_CancelKeyPress($script:cancelKeyHandler)
    $rootCts.Cancel()
    foreach ($entry in $pending.ToArray()) {
        $entry.Request.Dispose()
        Dispose-RequestCts -Holder $entry.Cts
        $entry.Stopwatch.Stop()
    }
    $pending.Clear()
    $runWatch.Stop()
    $rootCts.Dispose()
    $script:client.Dispose()
}

try {
    Write-RehearsalReport -BaseUrl $baseUrl -Preflight $preflight -LiveResult $liveResult -JobResult $jobResult -ExitCode $exitCode
}
catch {
    Write-Host ('Report write failed: ' + (Get-SafeMessage -Message $_.Exception.Message))
    if ($exitCode -eq 0) { $exitCode = 1 }
}

exit $exitCode
