using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UpdateAgent.LabManager.Auth;
using UpdateAgent.LabManager.Configuration;
using UpdateAgent.LabManager.Live;
using UpdateAgent.LabManager.Services;
using UpdateAgent.LabManager.Storage;
using UpdateAgent.Shared;
using UpdateAgent.Shared.Models;
using UpdateAgent.Shared.Updates;

namespace UpdateAgent.Tests;

/// <summary>2025/26 감사 25차 LabManager 수정 검증. 네트워크/실제 토큰을 사용하지 않는다.</summary>
public sealed class Audit25LabManagerTests
{
    private const int Ok = 200;
    private const int BadRequest = 400;

    // ---------------- A. 체크인 거부 결과 poison queue ----------------

    [Fact]
    public async Task CheckIn_MixedResultBatch_Returns200WithEveryOutcome()
    {
        await WithStoreAsync(async (store, _) =>
        {
            await store.AddCommandAsync(Command("cmd-ok", "pc-1"));

            var request = new AgentCheckInRequest
            {
                Agent = new AgentInfo { AgentId = "pc-1", MachineName = "PC-01" },
                Telemetry = new AgentTelemetry { AgentId = "pc-1" },
                CommandResults =
                [
                    new CommandResult
                    {
                        CommandId = "cmd-ok",
                        AgentId = "pc-1",
                        Success = true,
                        Message = "완료",
                        CompletedAt = DateTimeOffset.UtcNow,
                    },
                    new CommandResult
                    {
                        CommandId = "cmd-unknown",
                        AgentId = "pc-1",
                        Success = false,
                        Message = "알 수 없음",
                        CompletedAt = DateTimeOffset.UtcNow,
                    },
                ],
            };

            var result = await ProcessAsync(request, store);

            Assert.Equal(Ok, result.StatusCode);
            var response = Assert.IsType<AgentCheckInResponse>(result.Response);
            Assert.Equal(2, response.CommandResultOutcomes.Count);
            Assert.Contains(response.CommandResultOutcomes,
                o => o.CommandId == "cmd-ok" && o.Accepted);
            Assert.Contains(response.CommandResultOutcomes,
                o => o.CommandId == "cmd-unknown" && !o.Accepted);
            Assert.Equal(TimeSpan.Zero, response.ServerTime.Offset);

            var audit = await store.GetAuditAsync(100);
            Assert.Contains(audit, a => a.Action == "command-ok" && a.Target.Contains("cmd-ok"));
            Assert.Contains(audit,
                a => a.Action == "command-result-rejected" && a.Target.Contains("cmd-unknown"));
        });
    }

    [Fact]
    public async Task CheckIn_CapsRejectedAuditRows_ButKeepsAllOutcomeIds()
    {
        await WithStoreAsync(async (store, _) =>
        {
            var rejectedIds = Enumerable.Range(0, 25)
                .Select(i => $"unknown-{i:D2}")
                .ToList();
            var request = new AgentCheckInRequest
            {
                Agent = new AgentInfo { AgentId = "pc-1", MachineName = "PC-01" },
                Telemetry = new AgentTelemetry { AgentId = "pc-1" },
                CommandResults = rejectedIds.Select(id => new CommandResult
                {
                    CommandId = id,
                    AgentId = "pc-1",
                    Message = "거부",
                    CompletedAt = DateTimeOffset.UtcNow,
                }).ToList(),
            };

            var result = await ProcessAsync(request, store);

            Assert.Equal(Ok, result.StatusCode);
            var response = Assert.IsType<AgentCheckInResponse>(result.Response);
            Assert.Equal(rejectedIds.Count, response.CommandResultOutcomes.Count);
            Assert.All(response.CommandResultOutcomes, o => Assert.False(o.Accepted));
            foreach (var id in rejectedIds)
            {
                Assert.Contains(response.CommandResultOutcomes, o => o.CommandId == id);
            }

            var audit = await store.GetAuditAsync(500);
            Assert.Equal(20, audit.Count(a => a.Action == "command-result-rejected"));
            Assert.Contains(audit, a => a.Action == "command-result-audit-truncated");
        });
    }

    [Fact]
    public async Task CheckIn_StillRejectsForeignCommandResults()
    {
        await WithStoreAsync(async (store, _) =>
        {
            var request = new AgentCheckInRequest
            {
                Agent = new AgentInfo { AgentId = "pc-1", MachineName = "PC-01" },
                Telemetry = new AgentTelemetry { AgentId = "pc-1" },
                CommandResults =
                [
                    new CommandResult
                    {
                        CommandId = "cmd-foreign",
                        AgentId = "pc-other",
                        CompletedAt = DateTimeOffset.UtcNow,
                    },
                ],
            };

            var result = await ProcessAsync(request, store);

            Assert.Equal(BadRequest, result.StatusCode);
            Assert.Null(result.Response);
        });
    }

    // ---------------- B. 관리자 토큰 외부 변경 감지 ----------------

    [Fact]
    public void AdminTokenStore_NoticesExternallyOverwrittenOverrideFile()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "UpdateAgentAudit25Tokens", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var overridePath = Path.Combine(root, "runtime-admin-token.json");
        try
        {
            var options = new TestOptionsMonitor(new LabManagerOptions
            {
                AdminToken = UpdateAgentConstants.SampleAdminToken,
                AgentToken = UpdateAgentConstants.SampleAgentToken,
            });
            var store = new AdminTokenStore(
                options, overridePath, NullLogger<AdminTokenStore>.Instance);

            Assert.False(store.HasRuntimeOverride);

            WriteTokenFile(overridePath, "external-admin-token-0001");
            Assert.Equal("external-admin-token-0001", store.CurrentToken);
            Assert.True(store.HasRuntimeOverride);

            // 같은 인스턴스에서 외부 덮어쓰기를 다시 감지해야 한다.
            WriteTokenFile(overridePath, "external-admin-token-0002-longer");
            Assert.Equal("external-admin-token-0002-longer", store.CurrentToken);
            Assert.True(store.HasRuntimeOverride);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // 파일 잠금이 남으면 정리 실패는 테스트 결과에 영향을 주지 않는다.
            }
        }
    }

    // ---------------- C/D. UTC 시각과 서버 완료 시각 ----------------

    [Fact]
    public async Task Commands_CompareExpiryInUtc_RegardlessOfStoredOffset()
    {
        await WithStoreAsync(async (store, databasePath) =>
        {
            var now = DateTimeOffset.UtcNow;
            await InsertCommandAsync(databasePath, "cmd-future", now.AddMinutes(10));
            await InsertCommandAsync(databasePath, "cmd-past", now.AddMinutes(-10));

            var pending = await store.TakePendingCommandsAsync("pc-1", 10);

            Assert.Contains(pending, c => c.CommandId == "cmd-future");
            Assert.DoesNotContain(pending, c => c.CommandId == "cmd-past");

            // 들어온 값이 +09:00이어도 저장 문자열은 UTC여야 한다.
            var offset = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.FromHours(9));
            await store.AddCommandAsync(Command("cmd-offset", "pc-1", offset.AddHours(1)) with
            {
                CreatedAt = offset,
            });

            var expires = await ReadAsync(databasePath,
                "SELECT expires_at FROM commands WHERE command_id = 'cmd-offset'");
            Assert.Equal(offset.AddHours(1).ToUniversalTime().ToString("O"), expires);
        });
    }

    [Fact]
    public async Task CompleteCommandResults_UsesServerUtcTimestamp()
    {
        await WithStoreAsync(async (store, databasePath) =>
        {
            await store.AddCommandAsync(Command("cmd-complete", "pc-1"));
            var agentReported = DateTimeOffset.UtcNow.AddDays(-30);
            var before = DateTimeOffset.UtcNow;

            var accepted = await store.CompleteCommandResultsAsync("pc-1",
            [
                new CommandResult
                {
                    CommandId = "cmd-complete",
                    AgentId = "pc-1",
                    Success = true,
                    Message = "완료",
                    CompletedAt = agentReported,
                },
            ]);

            Assert.True(accepted.Contains("cmd-complete"));
            var stored = await store.GetCommandAsync("cmd-complete");
            Assert.NotNull(stored?.CompletedAt);
            Assert.NotEqual(agentReported, stored!.CompletedAt);
            Assert.InRange(stored.CompletedAt!.Value, before.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1));
            Assert.Equal(TimeSpan.Zero, stored.CompletedAt.Value.Offset);

            var raw = await ReadAsync(databasePath,
                "SELECT completed_at FROM commands WHERE command_id = 'cmd-complete'");
            Assert.EndsWith("+00:00", raw);
        });
    }

    // ---------------- F. 게이트 상한/보존 ----------------

    [Fact]
    public async Task CheckinGates_DoNotExceedCap_UnderConcurrentNewAgents()
    {
        var gates = new CheckinGateRegistry();
        var leases = await Task.WhenAll(Enumerable.Range(0, 220)
            .Select(i => gates.TryAcquireAsync($"pc-{i}")));

        var acquired = leases.Where(l => l is not null).ToList();
        Assert.Equal(200, acquired.Count);
        foreach (var lease in acquired)
        {
            lease!.Dispose();
        }
    }

    [Fact]
    public async Task Retention_RemovesOldRows_AndKeepsActiveJobs()
    {
        await WithStoreAsync(async (store, databasePath) =>
        {
            await store.AddCommandAsync(Command("cmd-old", "pc-1"));
            await store.AddAuditAsync("teacher", "old", "pc-1", "오래된 감사");
            await store.SaveJobSummaryAsync(new JobSummary
            {
                JobId = "job-active",
                AgentId = "pc-1",
                State = UpdateJobState.Installing,
                StartedAt = DateTimeOffset.UtcNow.AddDays(-40),
                UpdatedAt = DateTimeOffset.UtcNow,
            }, "PC-01");

            var old = UtcTimestamps.Format(DateTimeOffset.UtcNow.AddDays(-60));
            await ExecuteAsync(databasePath, """
                UPDATE commands SET created_at = $old WHERE command_id = 'cmd-old';
                UPDATE audit SET ts = $old;
                """, ("$old", old));

            var removed = await store.CleanupRetentionAsync();

            Assert.Equal(2, removed);
            Assert.Null(await store.GetCommandAsync("cmd-old"));
            Assert.Empty(await store.GetAuditAsync(100));
            Assert.NotNull(await store.GetJobAsync("job-active"));
        });
    }

    // ---------------- G. 외부 링크 정화 ----------------

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("file:///C:/temp/app.exe")]
    [InlineData("https://evil.example.test/app.exe")]
    [InlineData("not a url")]
    public void ExternalUrl_AllowsOnlyHttpOrHttpsGitHubHosts(string value)
    {
        Assert.False(GitHubReleaseChecker.IsAllowedExternalUrl(value, requireGitHubHost: true));
        Assert.True(GitHubReleaseChecker.IsAllowedExternalUrl(
            "https://github.com/ahbiyout-all/WinAutoUpdate/releases", requireGitHubHost: true));
    }

    [Fact]
    public async Task CheckAsync_StrictMode_FallsBackFromUnsafeReleaseAndAssetUrls()
    {
        const string json = """
        {
          "tag_name": "v9.9.9",
          "html_url": "javascript:alert(1)",
          "assets": [
            { "name": "UpdateAgent.Agent-Setup-9.9.9-x64.exe",
              "browser_download_url": "https://evil.example.test/app.exe", "size": 10 }
          ]
        }
        """;
        using var http = new HttpClient(new StubHandler(json, HttpStatusCode.OK));

        var info = await new GitHubReleaseChecker(http, strictUrlValidation: true)
            .CheckAsync(UpdateProduct.Agent);

        Assert.True(info.CheckSucceeded);
        Assert.Equal(UpdateAgentConstants.GitHubReleasesUrl, info.ReleaseUrl);
        Assert.Equal(UpdateAgentConstants.GitHubReleasesUrl, info.AssetUrl);
        Assert.Equal(TimeSpan.Zero, info.CheckedAt.Offset);
    }

    // ---------------- helpers ----------------

    private static Task<CheckInProcessingResult> ProcessAsync(
        AgentCheckInRequest request,
        LabStore store) =>
        AgentCheckInHandler.ProcessAsync(
            request,
            store,
            new AgentPolicyService(store),
            new LiveEventBus(),
            new CheckinGateRegistry(),
            CancellationToken.None);

    private static AgentCommand Command(
        string id,
        string agentId,
        DateTimeOffset? expiresAt = null) => new()
    {
        CommandId = id,
        AgentId = agentId,
        Type = CommandType.UnlockInput,
        Status = CommandStatus.Pending,
        PayloadJson = "{}",
        IssuedBy = "teacher",
        CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        ExpiresAt = expiresAt ?? DateTimeOffset.UtcNow.AddHours(1),
    };

    private static async Task WithStoreAsync(Func<LabStore, string, Task> action)
    {
        var root = Path.Combine(
            Path.GetTempPath(), "UpdateAgentAudit25", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "lab.db");
        try
        {
            var store = new LabStore(databasePath);
            await store.InitializeAsync();
            await action(store, databasePath);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // SQLite 풀 때문에 삭제가 실패하면 다음 테스트에 영향을 주지 않는다.
            }
        }
    }

    private static string ConnectionString(string databasePath) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();

    private static async Task InsertCommandAsync(
        string databasePath,
        string commandId,
        DateTimeOffset expiresAt)
    {
        await using var connection = new SqliteConnection(ConnectionString(databasePath));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO commands(command_id, agent_id, type, status, payload_json,
                                 issued_by, created_at, expires_at)
            VALUES($id, $agent, $type, $status, '{}', 'teacher', $created, $expires);
            """;
        var now = DateTimeOffset.UtcNow;
        command.Parameters.AddWithValue("$id", commandId);
        command.Parameters.AddWithValue("$agent", "pc-1");
        command.Parameters.AddWithValue("$type", (int)CommandType.UnlockInput);
        command.Parameters.AddWithValue("$status", (int)CommandStatus.Pending);
        command.Parameters.AddWithValue("$created", now.ToString("O"));
        command.Parameters.AddWithValue("$expires", expiresAt.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteAsync(
        string databasePath,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqliteConnection(ConnectionString(databasePath));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string?> ReadAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection(ConnectionString(databasePath));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync() as string;
    }

    private static void WriteTokenFile(string path, string token)
    {
        File.WriteAllText(
            path,
            $$"""{"AdminToken":"{{token}}"}""",
            new UTF8Encoding(false));
    }

    private sealed class TestOptionsMonitor : IOptionsMonitor<LabManagerOptions>
    {
        private readonly LabManagerOptions _value;

        public TestOptionsMonitor(LabManagerOptions value) => _value = value;

        public LabManagerOptions CurrentValue => _value;

        public LabManagerOptions Get(string? name) => _value;

        public IDisposable OnChange(Action<LabManagerOptions, string?> listener) => new NoopDisposable();

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _content;
        private readonly HttpStatusCode _status;

        public StubHandler(string content, HttpStatusCode status)
        {
            _content = content;
            _status = status;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_content, Encoding.UTF8, "application/json"),
            });
    }
}
