using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using UpdateAgent.Core.Storage;
using UpdateAgent.Service;
using UpdateAgent.Service.Commands;
using UpdateAgent.Service.Configuration;
using UpdateAgent.Shared.Models;
using UpdateAgent.Shared.Serialization;

namespace UpdateAgent.Tests;

/// <summary>25회차 감사에서 확인한 에이전트 런타임 결함의 회귀 테스트.</summary>
public sealed class Audit25AgentRuntimeTests
{
    private const string AgentId = "pc-audit25";

    // A: 이전 서버 응답에는 결과 판정 필드가 없다. 제출한 전체 ID를 acknowledgement해야 한다.
    [Fact]
    public void AcknowledgeIds_OldServerResponseWithoutOutcomes_UsesAllSubmittedIds()
    {
        const string legacyJson = """
            {"serverTime":"2026-01-01T00:00:00+00:00","commands":[]}
            """;
        var response = UpdateAgentJson.Deserialize<AgentCheckInResponse>(legacyJson);

        Assert.NotNull(response);
        Assert.Empty(response!.CommandResultOutcomes);

        var ids = Worker.ResolveAcknowledgedCommandIds(Results("cmd-1", "cmd-2"), response);

        Assert.Equal(new[] { "cmd-1", "cmd-2" }, ids);
    }

    // A: 새 서버 응답은 수락/거부 판정을 모두 포함한다. 거부된 ID도 큐에서 제거해야 한다.
    [Fact]
    public void AcknowledgeIds_NewServerResponse_IncludesAcceptedAndRejectedIds()
    {
        var response = new AgentCheckInResponse
        {
            CommandResultOutcomes =
            [
                new CommandResultOutcome { CommandId = "cmd-accepted", Accepted = true },
                new CommandResultOutcome { CommandId = "cmd-rejected", Accepted = false },
            ],
        };
        var json = UpdateAgentJson.Serialize(response);
        var parsed = UpdateAgentJson.Deserialize<AgentCheckInResponse>(json);

        Assert.NotNull(parsed);
        Assert.Equal(2, parsed!.CommandResultOutcomes.Count);

        var ids = Worker.ResolveAcknowledgedCommandIds(
            Results("cmd-accepted", "cmd-rejected", "cmd-not-reported"), parsed);

        Assert.Equal(new[] { "cmd-accepted", "cmd-rejected" }, ids);
    }

    [Fact]
    public async Task RejectedResults_AreAcknowledgedTogetherWithAcceptedResults()
    {
        await WithStoreAsync(async (store, dbPath) =>
        {
            var executor = CreateExecutor(store);
            await executor.ExecuteAsync([Ping("cmd-accepted"), Ping("cmd-rejected")]);
            Assert.Equal(2, executor.SnapshotResults().Count);

            var response = new AgentCheckInResponse
            {
                CommandResultOutcomes =
                [
                    new CommandResultOutcome { CommandId = "cmd-accepted", Accepted = true },
                    new CommandResultOutcome { CommandId = "cmd-rejected", Accepted = false },
                ],
            };
            var ids = Worker.ResolveAcknowledgedCommandIds(executor.SnapshotResults(), response);
            await executor.AcknowledgeResultsAsync(ids);

            Assert.Empty(executor.SnapshotResults());

            var restarted = CreateExecutor(new SqliteJobStore(dbPath));
            await restarted.InitializeAsync();
            Assert.Empty(restarted.SnapshotResults());
        });
    }

    // B: 재부팅으로 프로세스가 바뀌어도 아직 서버에 전달되지 않은 결과가 남아야 한다.
    [Fact]
    public async Task PendingResults_SurviveRestart_AndAcknowledgementIsPersisted()
    {
        await WithStoreAsync(async (store, dbPath) =>
        {
            var executor = CreateExecutor(store);
            await executor.InitializeAsync();
            await executor.ExecuteAsync([Ping("cmd-1"), Ping("cmd-2")]);
            Assert.Equal(2, executor.SnapshotResults().Count);

            var restarted = CreateExecutor(new SqliteJobStore(dbPath));
            await restarted.InitializeAsync();
            var restored = restarted.SnapshotResults();
            Assert.Equal(2, restored.Count);
            Assert.Contains(restored, r => r.CommandId == "cmd-1");
            Assert.Contains(restored, r => r.CommandId == "cmd-2");

            await restarted.AcknowledgeResultsAsync(["cmd-1"]);
            Assert.Single(restarted.SnapshotResults());

            var restartedAgain = CreateExecutor(new SqliteJobStore(dbPath));
            await restartedAgain.InitializeAsync();
            var remaining = Assert.Single(restartedAgain.SnapshotResults());
            Assert.Equal("cmd-2", remaining.CommandId);
        });
    }

    // B: 저장소 장애로 writes가 실패해도 메모리 큐의 결과를 잃지 않고 다음 변경 때 다시 저장한다.
    [Fact]
    public async Task PendingResults_AreNotLostWhenSaveFails()
    {
        await WithStoreAsync(async (store, dbPath) =>
        {
            var failing = new FailingWriteStore(store);
            var executor = CreateExecutor(failing);
            await executor.ExecuteAsync([Ping("cmd-save-fail")]);
            Assert.Single(executor.SnapshotResults());

            failing.FailWrites = false;
            await executor.ExecuteAsync([Ping("cmd-save-retry")]);

            var restarted = CreateExecutor(new SqliteJobStore(dbPath));
            await restarted.InitializeAsync();
            var restored = restarted.SnapshotResults();
            Assert.Equal(2, restored.Count);
            Assert.Contains(restored, r => r.CommandId == "cmd-save-fail");
            Assert.Contains(restored, r => r.CommandId == "cmd-save-retry");
        });
    }

    // B: 재시작 후에도 대기 결과 상한(100건)이 유지된다.
    [Fact]
    public async Task PendingResults_AreBoundedAfterRestart()
    {
        await WithStoreAsync(async (store, dbPath) =>
        {
            var commands = Enumerable.Range(0, 105).Select(i => Ping($"cmd-{i}")).ToList();
            var executor = CreateExecutor(store);
            await executor.ExecuteAsync(commands);
            Assert.Equal(100, executor.SnapshotResults().Count);

            var restarted = CreateExecutor(new SqliteJobStore(dbPath));
            await restarted.InitializeAsync();
            var restored = restarted.SnapshotResults();

            Assert.Equal(100, restored.Count);
            Assert.DoesNotContain(restored, r => r.CommandId == "cmd-0");
            Assert.Contains(restored, r => r.CommandId == "cmd-104");
        });
    }

    // C: 원격 ApplyPolicy가 Maestro 수동 확인 게이트를 끄면 안 된다.
    [Fact]
    public async Task ApplyPolicy_ForcesMaestroConfirmationInCachedPolicy()
    {
        await WithStoreAsync(async (store, _) =>
        {
            var executor = CreateExecutor(store);
            var command = new AgentCommand
            {
                CommandId = "cmd-policy",
                AgentId = AgentId,
                Type = CommandType.ApplyPolicy,
                PayloadJson = UpdateAgentJson.Serialize(new UpdatePolicy
                {
                    RequireMaestroManualConfirmation = false,
                    IncludeFeatureUpdates = true,
                    ApprovedFeatureRelease = "26H2",
                }),
            };

            await executor.ExecuteAsync([command]);

            var result = Assert.Single(executor.SnapshotResults());
            Assert.True(result.Success);

            var json = await store.GetSettingAsync(ServerPolicyProvider.SettingKey);
            Assert.NotNull(json);
            var cached = UpdateAgentJson.Deserialize<UpdatePolicy>(json!);
            Assert.NotNull(cached);
            Assert.True(cached!.RequireMaestroManualConfirmation);
            Assert.False(cached.IncludeFeatureUpdates);
            Assert.Null(cached.ApprovedFeatureRelease);
        });
    }

    private static CommandExecutor CreateExecutor(IJobStore store) =>
        // Ping/ApplyPolicy 경로에서는 runner/reboot/overlay 의존성을 사용하지 않는다.
        new(null!, null!, null!, store, new ServerPolicyProvider(store),
            NullLogger<CommandExecutor>.Instance);

    private static AgentCommand Ping(string commandId) => new()
    {
        CommandId = commandId,
        AgentId = AgentId,
        Type = CommandType.Ping,
        ExpiresAt = DateTimeOffset.Now.AddHours(1),
    };

    private static IReadOnlyList<CommandResult> Results(params string[] commandIds) =>
        commandIds.Select(id => new CommandResult { CommandId = id, AgentId = AgentId }).ToList();

    private static async Task WithStoreAsync(Func<SqliteJobStore, string, Task> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "UpdateAgentAudit25", Guid.NewGuid().ToString("N"));
        try
        {
            var databasePath = Path.Combine(root, "agent.db");
            var store = new SqliteJobStore(databasePath);
            await store.InitializeAsync();
            await action(store, databasePath);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FailingWriteStore(IJobStore inner) : IJobStore
    {
        public bool FailWrites { get; set; } = true;

        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            inner.InitializeAsync(cancellationToken);

        public Task SaveJobAsync(JobDetail detail, CancellationToken cancellationToken = default) =>
            inner.SaveJobAsync(detail, cancellationToken);

        public Task<JobDetail?> GetJobAsync(string jobId, CancellationToken cancellationToken = default) =>
            inner.GetJobAsync(jobId, cancellationToken);

        public Task<JobDetail?> GetActiveJobAsync(string agentId, CancellationToken cancellationToken = default) =>
            inner.GetActiveJobAsync(agentId, cancellationToken);

        public Task<IReadOnlyList<JobSummary>> GetRecentJobsAsync(
            string agentId, int limit = 20, CancellationToken cancellationToken = default) =>
            inner.GetRecentJobsAsync(agentId, limit, cancellationToken);

        public Task AppendEventAsync(
            string jobId, string level, string message, CancellationToken cancellationToken = default) =>
            inner.AppendEventAsync(jobId, level, message, cancellationToken);

        public Task<IReadOnlyList<JobEventRecord>> GetEventsAsync(
            string jobId, int limit = 200, CancellationToken cancellationToken = default) =>
            inner.GetEventsAsync(jobId, limit, cancellationToken);

        public Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default) =>
            inner.GetSettingAsync(key, cancellationToken);

        public async Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            if (FailWrites && key == "pending-command-results")
            {
                throw new IOException("테스트 저장소 쓰기 실패");
            }
            await inner.SetSettingAsync(key, value, cancellationToken);
        }
    }
}
