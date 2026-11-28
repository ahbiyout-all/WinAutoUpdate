using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using UpdateAgent.LabManager.Configuration;
using UpdateAgent.LabManager.Live;
using UpdateAgent.LabManager.Services;
using UpdateAgent.LabManager.Storage;
using UpdateAgent.Service.Configuration;
using UpdateAgent.Service.Integrity;
using UpdateAgent.Shared.Models;
using UpdateAgent.Shared.Serialization;

namespace UpdateAgent.Tests;

/// <summary>
/// 개선 5(에이전트 변조·이탈 탐지)의 회귀 테스트. 허용 목록 지문, 비밀값 제외,
/// 기준선/재설정, 버전·이탈 정직성, UI/문서 정합성을 확인한다.
/// </summary>
public sealed class Improvement05TamperDetectionTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 11, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Fingerprint_UsesAllowlist_AndIgnoresTokenOrPasswordFields()
    {
        var root = NewTempRoot();
        try
        {
            var config = """
                {
                  "Agent": {
                    "LabManagerUrl": "http://127.0.0.1:5080",
                    "AgentId": "pc-1",
                    "OverlayEnabled": true,
                    "OverlayCompletionHoldSeconds": 20,
                    "AgentToken": "tok12",
                    "AdminToken": "adm12",
                    "Password": "pw123"
                  }
                }
                """;
            var path = WriteConfig(root, "config.json", config);
            var options = new AgentOptions
            {
                LabManagerUrl = "http://127.0.0.1:5080",
                OverlayEnabled = true,
                OverlayCompletionHoldSeconds = 20,
            };

            var first = ConfigFingerprint.Compute(path, "pc-1", options);
            Assert.False(first.Unavailable);
            Assert.NotNull(first.Fingerprint);
            Assert.StartsWith(ConfigFingerprint.Prefix, first.Fingerprint);

            // 비밀 필드만 바꾸면 지문이 그대로다.
            var secretsChanged = config
                .Replace("raw-agent-token-111", "raw-agent-token-999")
                .Replace("raw-admin-token-222", "raw-admin-token-888")
                .Replace("raw-password-333", "raw-password-777");
            var second = ConfigFingerprint.Compute(
                WriteConfig(root, "secrets.json", secretsChanged), "pc-1", options);
            Assert.Equal(first.Fingerprint, second.Fingerprint);

            // 허용 목록 값을 바꾸면 지문이 달라진다.
            var overlayChanged = config.Replace("\"OverlayCompletionHoldSeconds\": 20",
                "\"OverlayCompletionHoldSeconds\": 30");
            var third = ConfigFingerprint.Compute(
                WriteConfig(root, "overlay.json", overlayChanged), "pc-1", options);
            Assert.NotEqual(first.Fingerprint, third.Fingerprint);

            // 파일을 안 바꾸고 유효 설정(환경 변수/appsettings)만 바꿔도 달라져야 한다.
            var effectiveChanged = ConfigFingerprint.Compute(path, "pc-1", new AgentOptions
            {
                LabManagerUrl = "http://127.0.0.1:5080",
                OverlayEnabled = true,
                OverlayCompletionHoldSeconds = 30,
            });
            Assert.NotEqual(first.Fingerprint, effectiveChanged.Fingerprint);

            var serialized = UpdateAgentJson.Serialize(new { first, second, third, effectiveChanged });
            foreach (var secret in new[]
                     {
                         "raw-agent-token-111", "raw-agent-token-999", "raw-admin-token-222",
                         "raw-admin-token-888", "raw-password-333", "raw-password-777",
                     })
            {
                Assert.DoesNotContain(secret, serialized, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void SecretLikeValueInAllowlistedField_IsMaskedAsUnavailable()
    {
        var root = NewTempRoot();
        try
        {
            var path = WriteConfig(root, "config.json", """
                {
                  "Agent": {
                    "LabManagerUrl": "http://127.0.0.1:5080/?token=raw-url-token-42",
                    "OverlayEnabled": true
                  }
                }
                """);
            var options = new AgentOptions
            {
                LabManagerUrl = "http://127.0.0.1:5080/?token=raw-url-token-42",
            };

            var result = ConfigFingerprint.Compute(path, "pc-1", options);

            Assert.True(result.Unavailable);
            Assert.Null(result.Fingerprint);
            Assert.DoesNotContain("raw-url-token-42",
                UpdateAgentJson.Serialize(result), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task UnreadableConfig_ReportsUnavailable_AndLiveViewNeverSaysHealthy()
    {
        var root = NewTempRoot();
        try
        {
            var missing = ConfigFingerprint.Compute(Path.Combine(root, "absent.json"), "pc-1");
            Assert.True(missing.Unavailable);
            Assert.Null(missing.Fingerprint);

            var brokenPath = WriteConfig(root, "broken.json", "{ not json");
            var broken = ConfigFingerprint.Compute(brokenPath, "pc-1");
            Assert.True(broken.Unavailable);
            Assert.Null(broken.Fingerprint);

            await WithStoreAsync(async (store, _) =>
            {
                var integrity = new AgentIntegrityService(store);
                var agent = Agent("pc-4", null, unavailable: true);
                var result = await CheckInAsync(store, agent, integrity);
                Assert.Equal(StatusCodes.Status200OK, result.StatusCode);

                var state = await integrity.GetAsync("pc-4");
                Assert.NotNull(state);
                Assert.True(state!.FingerprintUnavailable);
                Assert.Null(state.BaselineFingerprint);

                var view = integrity.Evaluate(state, agent, Now);
                Assert.True(view.IntegrityUnavailable);

                var snapshot = await LiveSnapshotBuilder.BuildAsync(store, integrity);
                var live = Assert.Single(snapshot.Agents);
                Assert.True(live.IntegrityUnavailable);
                Assert.False(live.ConfigChanged);
            });
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Baseline_IsFirstSeenThenSticksUntilTeacherReset()
    {
        await WithStoreAsync(async (store, _) =>
        {
            var integrity = new AgentIntegrityService(store);
            var first = Agent("pc-1", "fp-a", unavailable: false);

            var baseline = await integrity.ObserveAsync("pc-1", first);
            Assert.Equal("fp-a", baseline.BaselineFingerprint);
            Assert.False(baseline.ConfigChanged);
            Assert.False(baseline.FingerprintUnavailable);

            var changed = await integrity.ObserveAsync("pc-1", first with { ConfigFingerprint = "fp-b" });
            Assert.True(changed.ConfigChanged);
            Assert.Equal("fp-a", changed.BaselineFingerprint);
            Assert.Contains(await store.GetAuditAsync(), audit => audit.Action == "config-changed");

            // 같은 변경이 여러 번 들어와도 플래그는 유지되고 기준선은 그대로다.
            var repeated = await integrity.ObserveAsync("pc-1", first with { ConfigFingerprint = "fp-b" });
            Assert.True(repeated.ConfigChanged);
            Assert.Equal(1, (await store.GetAuditAsync()).Count(audit => audit.Action == "config-changed"));

            var reset = await integrity.AcknowledgeAsync("pc-1");
            Assert.NotNull(reset);
            Assert.False(reset!.ConfigChanged);
            Assert.Equal("fp-b", reset.BaselineFingerprint);
            Assert.Contains(await store.GetAuditAsync(), audit => audit.Action == "config-baseline-reset");

            var accepted = await integrity.ObserveAsync("pc-1", first with { ConfigFingerprint = "fp-b" });
            Assert.False(accepted.ConfigChanged);
            var changedAgain = await integrity.ObserveAsync("pc-1", first with { ConfigFingerprint = "fp-c" });
            Assert.True(changedAgain.ConfigChanged);
            Assert.Equal("fp-b", changedAgain.BaselineFingerprint);
        });
    }

    [Fact]
    public async Task Baseline_IsSetWhenFirstReadableFingerprintArrives()
    {
        await WithStoreAsync(async (store, _) =>
        {
            var integrity = new AgentIntegrityService(store);
            var unavailableAgent = Agent("pc-5", null, unavailable: true);

            var first = await integrity.ObserveAsync("pc-5", unavailableAgent);
            Assert.Null(first.BaselineFingerprint);
            Assert.True(first.FingerprintUnavailable);

            // 지문을 읽을 수 없었던 첫 체크인은 기준선이 아니며, 처음 읽은 지문이 기준선이 된다.
            var readable = await integrity.ObserveAsync(
                "pc-5", unavailableAgent with { ConfigFingerprint = "fp-later", ConfigFingerprintUnavailable = false });
            Assert.Equal("fp-later", readable.BaselineFingerprint);
            Assert.Equal("fp-later", readable.LastFingerprint);
            Assert.False(readable.ConfigChanged);
            Assert.False(readable.FingerprintUnavailable);

            var changed = await integrity.ObserveAsync(
                "pc-5", unavailableAgent with { ConfigFingerprint = "fp-other", ConfigFingerprintUnavailable = false });
            Assert.True(changed.ConfigChanged);
            Assert.Equal("fp-later", changed.BaselineFingerprint);
            Assert.Equal(1, (await store.GetAuditAsync()).Count(audit => audit.Action == "config-changed"));
        });
    }

    [Fact]
    public async Task UnobservedFingerprintDifference_NeverReportsHealthy()
    {
        await WithStoreAsync(async (store, _) =>
        {
            var integrity = new AgentIntegrityService(store);
            var observed = Agent("pc-6", "fp-a", unavailable: false);
            var state = await integrity.ObserveAsync("pc-6", observed);

            // 관찰 실패 등으로 저장된 마지막 값과 현재 에이전트 값이 다르면 확인 불가로 남는다.
            var current = observed with { ConfigFingerprint = "fp-b" };
            var view = integrity.Evaluate(state, current, Now);

            Assert.True(view.IntegrityUnavailable);
            Assert.True(view.ConfigChanged);
        });
    }

    [Fact]
    public async Task VersionDrift_WithUnconfiguredExpectation_StaysUnknown()
    {
        await WithStoreAsync(async (store, _) =>
        {
            var integrity = new AgentIntegrityService(store);
            var agent = Agent("pc-1", "fp-a", unavailable: false, version: "0.1.44");
            var state = await integrity.ObserveAsync("pc-1", agent);

            var unconfigured = integrity.Evaluate(state, agent, Now);
            Assert.True(unconfigured.VersionUnknown);
            Assert.False(unconfigured.VersionMismatch);

            var drifted = new AgentIntegrityService(store, "0.1.45").Evaluate(state, agent, Now);
            Assert.True(drifted.VersionMismatch);
            Assert.False(drifted.VersionUnknown);

            var missingReport = new AgentIntegrityService(store, "0.1.45")
                .Evaluate(state, agent with { AgentVersion = "" }, Now);
            Assert.True(missingReport.VersionUnknown);
            Assert.False(missingReport.VersionMismatch);

            Assert.Null(AgentIntegrityService.CompareVersion(null, "0.1.44"));
            Assert.Null(AgentIntegrityService.CompareVersion("0.1.45", null));
            Assert.False(AgentIntegrityService.CompareVersion("v0.1.44", "0.1.44"));
        });
    }

    [Fact]
    public async Task StaleCheckIn_IsDepartedWithExplicitReason()
    {
        await WithStoreAsync(async (store, databasePath) =>
        {
            var integrity = new AgentIntegrityService(store);
            var old = UtcTimestamps.Now.AddMinutes(-10);
            var agent = Agent("pc-9", "fp-a", unavailable: false, lastSeen: old);
            var result = await CheckInAsync(store, agent, integrity);
            Assert.Equal(StatusCodes.Status200OK, result.StatusCode);

            // 체크인은 서버 수신 시각으로 저장되므로 테스트에서만 오래된 last_seen으로 되돌린다.
            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "UPDATE agents SET last_seen = $ts WHERE agent_id = $id;";
                command.Parameters.AddWithValue("$ts", UtcTimestamps.Format(old));
                command.Parameters.AddWithValue("$id", "pc-9");
                await command.ExecuteNonQueryAsync();
            }

            var snapshot = await LiveSnapshotBuilder.BuildAsync(store, integrity);
            var live = Assert.Single(snapshot.Agents);
            Assert.True(live.Departed);
            Assert.Equal("체크인 없음", live.DepartureReason);
            Assert.Equal(AgentIntegrityService.DepartureReasonText, live.DepartureReason);
            Assert.Equal(1, snapshot.StaleCount);
        });
    }

    [Fact]
    public async Task ValidCheckIn_PersistsBaseline_AndReportsServiceAlive()
    {
        await WithStoreAsync(async (store, _) =>
        {
            var integrity = new AgentIntegrityService(store);
            var agent = Agent("pc-2", "cfg-sha256:abc123", unavailable: false);
            Assert.True(agent.ServiceAlive);

            var result = await CheckInAsync(store, agent, integrity);
            Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
            Assert.Contains("\"serviceAlive\":true", UpdateAgentJson.Serialize(agent));

            var state = await integrity.GetAsync("pc-2");
            Assert.NotNull(state);
            Assert.Equal("cfg-sha256:abc123", state!.BaselineFingerprint);
            Assert.False(state.ConfigChanged);
            Assert.False(state.FingerprintUnavailable);

            var snapshot = await LiveSnapshotBuilder.BuildAsync(store, integrity);
            var live = Assert.Single(snapshot.Agents);
            Assert.False(live.IntegrityUnavailable);
            Assert.False(live.ConfigChanged);
            Assert.True(live.VersionUnknown); // 기대 버전이 없으면 정상이라고 하지 않는다.
        });
    }

    [Fact]
    public void TeacherUiAndDocs_ShowAllFourFlags_WithoutClaimsOnMissingData()
    {
        var monitor = Read("src/UpdateAgent.LabManager/wwwroot/monitor.js");
        var monitorFlags = Slice(monitor, "function integrityChips", "function formatTime");
        foreach (var marker in new[]
                 { "무결성 확인 불가", "설정 변경", "버전 불일치", "버전 확인 불가", "이탈" })
        {
            Assert.Contains(marker, monitorFlags);
        }
        Assert.DoesNotContain("정상", monitorFlags);
        Assert.Contains("체크인 없음", monitor);
        Assert.Contains("integrity/baseline", monitor);
        Assert.Contains("기준 재설정", monitor);

        var app = Read("src/UpdateAgent.LabManager/wwwroot/app.js");
        var appFlags = Slice(app, "function integrityChips", "function isActiveJobState");
        foreach (var marker in new[]
                 { "무결성 확인 불가", "설정 변경", "버전 불일치", "버전 확인 불가", "이탈" })
        {
            Assert.Contains(marker, appFlags);
        }
        Assert.DoesNotContain("정상", appFlags);
        Assert.Contains("체크인 없음", app);

        var program = Read("src/UpdateAgent.LabManager/Program.cs");
        Assert.Contains("/api/admin/agents/{agentId}/integrity/baseline", program);
        Assert.Contains("AgentIntegrityService", program);
        Assert.Contains("ExpectedAgentVersion", program);
        Assert.Contains("BuildAsync(labStore, integrity, cancellationToken)", program);

        var worker = Read("src/UpdateAgent.Service/Worker.cs");
        Assert.Contains("ServiceAlive = true", worker);
        Assert.Contains("ConfigFingerprint.Compute", worker);

        var worklog = Read("docs/WORKLOG.md");
        var heading = "## 2026-11-28 개선 5 — 에이전트 변조·이탈 탐지";
        var start = worklog.IndexOf(heading, StringComparison.Ordinal);
        var next = worklog.IndexOf("## 2026-11-28 개선 4", StringComparison.Ordinal);
        Assert.True(start >= 0 && next > start, "개선 5는 개선 4 위에 있어야 합니다.");
        var section = worklog[start..next];
        var tableStart = section.IndexOf("| 회차 |", StringComparison.Ordinal);
        var tableEnd = section.IndexOf("\n\n", tableStart, StringComparison.Ordinal);
        Assert.True(tableStart >= 0 && tableEnd > tableStart);
        var table = section[tableStart..tableEnd];
        Assert.Equal(10, Regex.Matches(table, @"^\| \d+ \|", RegexOptions.Multiline).Count);

        var previous = -1;
        foreach (var dimension in new[]
                 {
                     "요구사항 정합성", "DTO/DB 마이그레이션 호환성", "지문 허용 목록/비밀값 제외",
                     "기준선/변경 감지·재설정", "버전 불일치·이탈 정직성", "UI 표시 정확성",
                     "실패 모드/설정 읽기 불가", "한국어 문서/UI 정합성", "회귀 범위", "최종 검증",
                 })
        {
            var position = table.IndexOf(dimension, StringComparison.Ordinal);
            Assert.True(position > previous, $"회차 순서가 올바르지 않습니다: {dimension}");
            previous = position;
        }

        var deployment = Read("docs/DEPLOYMENT.md");
        foreach (var keyword in new[]
                 {
                     "8-F", "탐지할 수 있는 것", "탐지할 수 없는 것", "로컬 관리자",
                     "체크인 없음", "ExpectedAgentVersion", "기준선", "없음", "확인 불가",
                 })
        {
            Assert.Contains(keyword, deployment);
        }
    }

    [Fact]
    public async Task NoTokenValue_LeaksIntoFingerprintStateAuditOrResponse()
    {
        const string secret = "tok123456";
        var passwordKey = "Pass" + "word";
        var root = NewTempRoot();
        try
        {
            var path = WriteConfig(root, "config.json", $$"""
                {
                  "Agent": {
                    "LabManagerUrl": "http://127.0.0.1:5080",
                    "AgentToken": "{{secret}}",
                    "AdminToken": "{{secret}}",
                    "{{passwordKey}}": "{{secret}}"
                  }
                }
                """);
            var fingerprint = ConfigFingerprint.Compute(path, "pc-1", new AgentOptions());
            Assert.DoesNotContain(secret, UpdateAgentJson.Serialize(fingerprint),
                StringComparison.OrdinalIgnoreCase);

            await WithStoreAsync(async (store, _) =>
            {
                var integrity = new AgentIntegrityService(store);
                var agent = Agent("pc-1", fingerprint.Fingerprint, unavailable: fingerprint.Unavailable);
                var result = await CheckInAsync(store, agent, integrity);
                var state = await integrity.GetAsync("pc-1");
                var serialized = UpdateAgentJson.Serialize(new
                {
                    agent,
                    result,
                    state,
                    audits = await store.GetAuditAsync(),
                });

                Assert.DoesNotContain(secret, serialized, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("AgentToken", serialized, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("AdminToken", serialized, StringComparison.OrdinalIgnoreCase);
            });

            var code = Read("src/UpdateAgent.Service/Integrity/ConfigFingerprint.cs");
            Assert.DoesNotContain("AgentToken", code);
            Assert.DoesNotContain("AdminToken", code);
            Assert.DoesNotContain("password=", code, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static AgentInfo Agent(
        string id,
        string? fingerprint,
        bool unavailable,
        string version = "0.1.44",
        DateTimeOffset? lastSeen = null) =>
        new()
        {
            AgentId = id,
            MachineName = id.ToUpperInvariant(),
            AgentVersion = version,
            LastSeen = lastSeen ?? Now,
            ConfigFingerprint = fingerprint,
            ConfigFingerprintUnavailable = unavailable,
            ServiceAlive = true,
        };

    private static async Task<CheckInProcessingResult> CheckInAsync(
        LabStore store,
        AgentInfo agent,
        AgentIntegrityService integrity)
    {
        var request = new AgentCheckInRequest
        {
            Agent = agent,
            Telemetry = new AgentTelemetry { AgentId = agent.AgentId },
        };
        return await AgentCheckInHandler.ProcessAsync(
            request,
            store,
            new AgentPolicyService(store),
            new LiveEventBus(),
            new CheckinGateRegistry(),
            default,
            integrity);
    }

    private static string WriteConfig(string root, string name, string json)
    {
        var path = Path.Combine(root, name);
        File.WriteAllText(path, json);
        return path;
    }

    private static string Slice(string text, string startMarker, string endMarker)
    {
        var start = text.IndexOf(startMarker, StringComparison.Ordinal);
        var end = text.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, $"구간을 찾지 못했습니다: {startMarker}");
        return text[start..end];
    }

    private static string NewTempRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "UpdateAgentImprovement05", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task WithStoreAsync(Func<LabStore, string, Task> action)
    {
        var root = NewTempRoot();
        try
        {
            var database = Path.Combine(root, "lab.db");
            var store = new LabStore(database);
            await store.InitializeAsync();
            await action(store, database);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(root);
        }
    }

    private static void TryDelete(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string Read(string relativePath)
    {
        var path = Path.Combine(FindRepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"Missing repository file: {relativePath}");
        return File.ReadAllText(path, System.Text.Encoding.UTF8).Replace("\r\n", "\n");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Repository root not found.");
    }
}
