using System.Text;
using System.Text.RegularExpressions;

namespace UpdateAgent.Tests;

/// <summary>
/// Improvement 1: static regressions for the synthetic 50-PC load rehearsal script
/// and the pilot/deployment runbooks. No token values are read or recorded by these tests.
/// </summary>
public sealed class Improvement01LoadRehearsalTests
{
    [Fact]
    public void Script_UsesTheRealCheckInContract()
    {
        var text = Read("scripts/load-rehearsal.ps1");

        Assert.Contains("/api/agents/checkin", text);
        Assert.Contains("[System.Net.Http.HttpMethod]::Post", text);
        Assert.Contains("'application/json'", text);
        Assert.Contains("$request.Headers.Add('X-Agent-Token', $script:agentToken)", text);
        Assert.Contains("agentId = $AgentId", text);
        Assert.Contains("machineName = $MachineName", text);
        Assert.Contains("agentVersion = $AgentVersion", text);
        Assert.Contains("telemetry = [ordered]@{", text);
        Assert.Contains("commandResults = @()", text);
        Assert.Contains("sentAt = $timestamp", text);
        Assert.Contains("state = 'idle'", text);
        Assert.DoesNotContain("/api/agents/simulate", text);
    }

    [Fact]
    public void Preflight_FailsClosedUnlessExplicitlySkipped()
    {
        var text = Read("scripts/load-rehearsal.ps1");

        Assert.Contains("[switch]$SkipPreflight", text);
        Assert.Contains("Get-SafeBaseUrl -Url $LabManagerUrl", text);
        Assert.Contains("$healthUrl = $BaseUrl + '/api/health'", text);
        Assert.Contains("if ($statusCode -lt 200 -or $statusCode -ge 300)", text);
        Assert.Contains("Preflight failed", text);

        var tokenValidation = text.IndexOf("$script:agentToken = Get-TokenFromFile", StringComparison.Ordinal);
        var skipDecision = text.IndexOf("if ($SkipPreflight)", StringComparison.Ordinal);
        Assert.True(tokenValidation >= 0 && skipDecision > tokenValidation);
    }

    [Fact]
    public void TokenFiles_ReadOnlyTheFirstLineAndFailClosed()
    {
        var function = ExtractFunction(Read("scripts/load-rehearsal.ps1"), "function Get-TokenFromFile");

        Assert.Contains("Get-Content -LiteralPath $full -TotalCount 1", function);
        Assert.DoesNotContain("-Raw", function);
        Assert.Contains("token file is empty", function);
        Assert.Contains("StartsWith('change-me-')", function);
        Assert.Contains("EndsWith('-replace-before-pilot')", function);
        Assert.Contains("$minTokenLength = 8", Read("scripts/load-rehearsal.ps1"));
        Assert.Contains("[char]0xFEFF", function);
    }

    [Fact]
    public void TokenValues_AreNotCommandLineParametersOrUrlCredentials()
    {
        var text = Read("scripts/load-rehearsal.ps1");
        var start = text.IndexOf("param(", StringComparison.Ordinal);
        var end = text.IndexOf(")\n\nSet-StrictMode", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var parameters = text.Substring(start, end - start);

        Assert.Contains("[string]$AgentTokenFile", parameters);
        Assert.Contains("[string]$AdminTokenFile", parameters);
        Assert.DoesNotMatch(@"-(?:Agent|Admin)Token(?!File)\b", parameters);
        Assert.DoesNotContain("UPDATEAGENT_AGENT_TOKEN", text);
        Assert.DoesNotContain("UPDATEAGENT_ADMIN_TOKEN", text);
        Assert.Contains("LabManagerUrl must not contain credentials.", text);
        Assert.DoesNotContain("?token=", text);
    }

    [Fact]
    public void CheckIns_AreBoundedCancellableAndHardTimed()
    {
        var text = Read("scripts/load-rehearsal.ps1");

        Assert.Contains("[ValidateRange(1, 50)]", text);
        Assert.Contains("[int]$MaxConcurrency = 10", text);
        Assert.Contains("while ($pending.Count -ge $MaxConcurrency", text);
        Assert.Contains("New-Object System.Threading.CancellationTokenSource", text);
        Assert.Contains("$rootCts.CancelAfter($hardTimeoutMs)", text);
        Assert.Contains("hardTimeoutMs = ($DurationSeconds + $RequestTimeoutSeconds + 30) * 1000", text);
        Assert.Contains("[System.Threading.Tasks.Task]::WaitAny", text);
        Assert.Contains("[System.Net.Http.HttpCompletionOption]::ResponseHeadersRead", text);
        Assert.Contains("catch [OperationCanceledException]", text);
        Assert.Contains("finally {", text);
        Assert.Contains("$rootCts.Cancel()", text);
    }

    [Fact]
    public void RequestTimeouts_UseParentRegistrationOnPowerShell51()
    {
        var text = Read("scripts/load-rehearsal.ps1");

        Assert.Contains("function New-RequestCts", text);
        Assert.Contains("function Dispose-RequestCts", text);
        Assert.Contains("$ParentToken.Register($callback, $source)", text);
        Assert.Contains("New-RequestCts -Milliseconds", text);
        Assert.DoesNotContain("-ArgumentList @(($RequestTimeoutSeconds * 1000), $CancelToken)", text);
        Assert.DoesNotContain("-ArgumentList @($liveTimeoutMs, $CancelToken)", text);
    }

    [Fact]
    public void GenericLists_AreNotWrappedWithArraySubexpression()
    {
        var text = Read("scripts/load-rehearsal.ps1");

        // Windows PowerShell 5.1 throws "Argument types do not match" for @($genericList).
        Assert.DoesNotContain("@($pending)", text);
        Assert.DoesNotContain("@($script:failureRecords)", text);
        Assert.Contains("$pending.ToArray()", text);
        Assert.Contains("$script:failureRecords.ToArray()", text);
    }

    [Fact]
    public void FailureTruncation_CountsOnlyOmittedRecords()
    {
        var text = Read("scripts/load-rehearsal.ps1");
        var function = ExtractFunction(text, "function Add-FailureRecord");

        Assert.Contains("if ($script:failureRecords.Count -lt $maxFailureRecords)", function);
        Assert.Contains("else", function);
        var increment = function.IndexOf("$script:truncatedFailureCount++", StringComparison.Ordinal);
        var capCheck = function.IndexOf("if ($script:failureRecords.Count", StringComparison.Ordinal);
        Assert.True(increment > capCheck, "The truncation counter must only run in the else branch.");
    }

    [Fact]
    public void ValidationFailure_DoesNotReportRepositoryRootAsUrl()
    {
        var text = Read("scripts/load-rehearsal.ps1");

        Assert.Contains("$baseUrl = ''", text);
        Assert.DoesNotContain("$baseUrl = $repoRoot", text);
        var validated = text.IndexOf("$baseUrl = Get-SafeBaseUrl -Url $LabManagerUrl", StringComparison.Ordinal);
        var reportCall = text.IndexOf("Write-RehearsalReport -BaseUrl $baseUrl", StringComparison.Ordinal);
        Assert.True(validated >= 0 && reportCall > validated);
    }

    [Fact]
    public void SyntheticAgentIds_AreStableAndSafe()
    {
        var text = Read("scripts/load-rehearsal.ps1");

        Assert.Contains("AgentId = 'rehearsal-pc-' + $suffix", text);
        Assert.DoesNotContain("AgentId = 'rehearsal-' + $script:runId", text);
        Assert.Contains("MachineName = 'LOAD-REHEARSAL-' + $suffix", text);
        Assert.Contains("server's 200-agent registry", text);
        Assert.Contains("한 번에 하나의 리허설만 실행", Read("docs/DEPLOYMENT.md"));
    }

    [Fact]
    public void Cancellation_CapturesStateAndStillWritesTheReport()
    {
        var text = Read("scripts/load-rehearsal.ps1");

        Assert.Contains("[Console]::add_CancelKeyPress($script:cancelKeyHandler)", text);
        Assert.Contains("$Event.Cancel = $true", text);
        Assert.Contains("$rootCts.Cancel()", text);
        Assert.Contains("[Console]::remove_CancelKeyPress($script:cancelKeyHandler)", text);
        Assert.Contains("$script:inFlightAtCancellation = $pending.Count", text);
        Assert.Contains("$script:deadlineReached = (-not $script:userCancelled)", text);
        Assert.Contains("if ($script:userCancelled -or $script:deadlineReached)", text);
        Assert.Contains("A cancelled run must never be reported as passed", text);
        Assert.Contains("deadlineReached = ($rootCts.IsCancellationRequested -and -not $script:userCancelled)", text);
        Assert.Contains("inFlightAtCancellation = $script:inFlightAtCancellation", text);

        var removal = text.IndexOf("remove_CancelKeyPress", StringComparison.Ordinal);
        var reportCall = text.IndexOf("Write-RehearsalReport -BaseUrl", StringComparison.Ordinal);
        Assert.True(removal >= 0 && reportCall > removal);
    }

    [Fact]
    public void Report_ContainsIntegrityFieldsAndNeverEmitsSecrets()
    {
        var text = Read("scripts/load-rehearsal.ps1");
        var start = text.IndexOf("$report = [ordered]@{", StringComparison.Ordinal);
        var end = text.IndexOf("$json = $report | ConvertTo-Json", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var report = text.Substring(start, end - start);

        foreach (var field in new[]
        {
            "generatedAtUtc", "version", "parameters", "preflight", "counts",
            "statusDistribution", "latencyMs", "recovery", "failures",
            "truncatedFailureCount", "thresholds", "liveStream", "jobChecks", "artifacts",
        })
        {
            Assert.Contains(field, report);
        }
        Assert.Contains("Get-FileHash", text);
        Assert.Contains("sha256 = $hash", text);
        Assert.Contains("UTF8Encoding($false)", text);
        Assert.Contains("Report SHA-256:", text);
        Assert.Contains("secretHandling = 'Token values", report);
        Assert.DoesNotContain("$script:agentToken", report);
        Assert.DoesNotContain("$script:adminToken", report);
        Assert.DoesNotContain("X-Agent-Token", report);
        Assert.DoesNotContain("X-Admin-Token", report);

        foreach (var line in text.Split('\n').Where(line => line.Contains("Write-Host", StringComparison.Ordinal)))
        {
            Assert.DoesNotContain("$script:agentToken", line);
            Assert.DoesNotContain("$script:adminToken", line);
        }
    }

    [Fact]
    public void AdminChecks_UseRealEndpointsAndNeverEnableFeatureUpdates()
    {
        var text = Read("scripts/load-rehearsal.ps1");

        Assert.Contains("$request.Headers.Add('X-Admin-Token', $script:adminToken)", text);
        Assert.Contains("'/api/admin/jobs/start'", text);
        Assert.Contains("'/api/admin/jobs/' + $jobId + '/cancel'", text);
        Assert.Contains("'/api/admin/live'", text);
        Assert.Contains("maestroConfirmed = $false", text);
        Assert.Contains("saveAsAgentPolicy = $false", text);
        Assert.DoesNotContain("includeFeatureUpdates", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("approvedFeatureRelease", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LiveCheck_SkipsSafelyWithoutAnAdminToken()
    {
        var text = Read("scripts/load-rehearsal.ps1");
        var function = ExtractFunction(text, "function Invoke-LiveChecks");

        Assert.Contains("Admin token file was not supplied; GET /api/admin/live requires X-Admin-Token.", function);
        Assert.Contains("if ([string]::IsNullOrEmpty($script:adminToken))", function);
        Assert.Contains("reconnectVerified = $false", function);
        Assert.Contains("firstSnapshotSeen", function);
        Assert.Contains("secondSnapshotSeen", function);
        Assert.Contains("event:\\s*snapshot", Read("scripts/load-rehearsal.ps1"));
    }

    [Fact]
    public void Docs_IncludeTheRealPilotChecklistAndLoadRehearsalRunbook()
    {
        var pilot = Read("docs/PILOT.md");
        Assert.Contains("C:\\labmanager-setup.log", pilot);
        Assert.Contains("icacls", pilot);
        Assert.Contains("Get-NetFirewallRule -Group 'UpdateAgent LabManager'", pilot);
        Assert.Contains("/api/health", pilot);
        Assert.Contains("Setup Log", pilot);
        Assert.Contains("configure-labmanager.ps1", pilot);

        var deployment = Read("docs/DEPLOYMENT.md");
        Assert.Contains("scripts/load-rehearsal.ps1", deployment);
        Assert.Contains("합성 리허설이며 실 PC 50대 증명이 아닙니다", deployment);
        Assert.Contains("종료 코드 2", deployment);
        Assert.Contains("전송 실패율 > 5%", deployment);
        Assert.Contains("성공률 < 95%", deployment);
        Assert.Contains("truncatedFailureCount", deployment);

        var worklog = Read("docs/WORKLOG.md");
        Assert.Contains("실 PC 파일럿 + 50대 부하 리허설 자동화", worklog);
        Assert.Contains("10회", worklog);
    }

    [Fact]
    public void Script_IsEnglishAndWindowsPowerShell51Compatible()
    {
        var text = Read("scripts/load-rehearsal.ps1");
        Assert.DoesNotContain("??", text);
        Assert.DoesNotContain("-AsHashtable", text);
        Assert.DoesNotContain("ForEach-Object -Parallel", text);
        Assert.DoesNotContain("::new()", text);

        var helpEnd = text.IndexOf("#>", StringComparison.Ordinal);
        Assert.True(helpEnd > 0);
        Assert.DoesNotMatch(@"[\uAC00-\uD7A3]", text.Substring(0, helpEnd));
        Assert.DoesNotMatch(@"[\uAC00-\uD7A3]", text);

        var bytes = File.ReadAllBytes(Path.Combine(FindRepoRoot(), "scripts", "load-rehearsal.ps1"));
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
    }

    private static string ExtractFunction(string text, string marker)
    {
        var start = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing function: {marker}");
        var end = text.IndexOf("\n}\n", start, StringComparison.Ordinal);
        Assert.True(end > start, $"Unterminated function: {marker}");
        return text.Substring(start, end - start);
    }

    private static string Read(string relativePath)
    {
        var path = Path.Combine(FindRepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"Missing repository file: {relativePath}");
        return File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Repository root not found.");
    }
}
