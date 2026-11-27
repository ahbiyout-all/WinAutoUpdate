using Microsoft.Data.Sqlite;
using UpdateAgent.LabManager.Live;
using UpdateAgent.LabManager.Services;
using UpdateAgent.LabManager.Storage;
using UpdateAgent.Shared.Models;
using UpdateAgent.Shared.Serialization;

namespace UpdateAgent.Tests;

/// <summary>2차 50회 자아성찰에서 확인한 LabManager 결함의 회귀 테스트.</summary>
public sealed class Audit50Round2LabManagerTests
{
    [Fact]
    public async Task DuplicatePolicyKeyCaseVariants_DoNotCrashSummaries()
    {
        await WithStoreAsync(async (store, _) =>
        {
            await store.UpsertAgentAsync(
                new AgentInfo { AgentId = "pc-1", MachineName = "PC-01" },
                new AgentTelemetry { AgentId = "pc-1" });
            var policy = UpdateAgentJson.Serialize(UpdatePolicy.Default);
            await store.SetSettingAsync("agent-policy:pc-1", policy);
            await store.SetSettingAsync("agent-policy:PC-1", policy);

            var summaries = await new AgentPolicyService(store).GetSummariesAsync();

            var summary = Assert.Single(summaries);
            Assert.True(summary.HasOverride);
        });
    }

    [Fact]
    public async Task MixedCaseAgentId_StillReceivesCommandsAndPolicy()
    {
        await WithStoreAsync(async (store, _) =>
        {
            await store.UpsertAgentAsync(
                new AgentInfo { AgentId = "pc-1", MachineName = "PC-01" },
                new AgentTelemetry { AgentId = "pc-1" });
            await store.AddCommandAsync(new AgentCommand
            {
                CommandId = "cmd-mixed-case",
                AgentId = "PC-1",
                Type = CommandType.UnlockInput,
            });

            var commands = await store.TakePendingCommandsAsync("pc-1", 20);
            var policy = await new AgentPolicyService(store).SaveAsync(
                "PC-1", UpdatePolicy.Default, null);
            var effective = await new AgentPolicyService(store).GetEffectiveAsync("pc-1");

            Assert.Single(commands);
            Assert.Equal("cmd-mixed-case", commands[0].CommandId);
            Assert.True(policy.HasOverride);
            Assert.True(effective.HasOverride);
        });
    }

    [Fact]
    public async Task UpsertAgent_CaseVariantReusesSameRow()
    {
        await WithStoreAsync(async (store, _) =>
        {
            await store.UpsertAgentAsync(
                new AgentInfo { AgentId = "pc-1", MachineName = "PC-01" },
                new AgentTelemetry { AgentId = "pc-1" });
            await store.UpsertAgentAsync(
                new AgentInfo { AgentId = "PC-1", MachineName = "PC-01" },
                new AgentTelemetry { AgentId = "PC-1" });

            var agents = await store.GetAgentsAsync();
            var agent = Assert.Single(agents);
            Assert.Equal("pc-1", agent.Agent.AgentId);
            Assert.Equal(1, await store.CountAgentsAsync());
        });
    }

    [Fact]
    public async Task Initialize_MergesLegacyCaseVariantRows()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "UpdateAgentAudit50Round2Legacy", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var dbPath = Path.Combine(root, "lab.db");
        try
        {
            var infoJson = UpdateAgentJson.Serialize(
                new AgentInfo { AgentId = "pc-1", MachineName = "PC-01" });
            var telemetryJson = UpdateAgentJson.Serialize(
                new AgentTelemetry { AgentId = "pc-1", State = AgentState.Idle });
            var policyJson = UpdateAgentJson.Serialize(UpdatePolicy.Default);

            await using (var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadWriteCreate"))
            {
                await connection.OpenAsync();
                await using var create = connection.CreateCommand();
                create.CommandText = """
                    CREATE TABLE agents (
                        agent_id TEXT PRIMARY KEY, machine_name TEXT NOT NULL, state INTEGER NOT NULL,
                        last_seen TEXT NOT NULL, info_json TEXT NOT NULL, telemetry_json TEXT NOT NULL);
                    CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT NOT NULL, updated_at TEXT NOT NULL);
                    INSERT INTO agents VALUES ('pc-1', 'PC-01', 0, '2026-01-01T00:00:00Z', $info, $telemetry);
                    INSERT INTO agents VALUES ('PC-1', 'PC-01', 0, '2026-01-02T00:00:00Z', $info, $telemetry);
                    INSERT INTO settings VALUES ('agent-policy:pc-1', $policy, '2026-01-01T00:00:00Z');
                    INSERT INTO settings VALUES ('agent-policy:PC-1', $policy, '2026-01-02T00:00:00Z');
                    """;
                create.Parameters.AddWithValue("$info", infoJson);
                create.Parameters.AddWithValue("$telemetry", telemetryJson);
                create.Parameters.AddWithValue("$policy", policyJson);
                await create.ExecuteNonQueryAsync();
            }

            var store = new LabStore(dbPath);
            await store.InitializeAsync();

            var agents = await store.GetAgentsAsync();
            Assert.Single(agents);
            Assert.Equal("pc-1", agents[0].Agent.AgentId);
            Assert.Single(await store.GetSettingsByPrefixAsync("agent-policy:"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(root);
        }
    }

    [Fact]
    public async Task ActiveJobs_AreBoundedAndSnapshotReportsHiddenCount()
    {
        await WithStoreAsync(async (store, _) =>
        {
            var now = DateTimeOffset.UtcNow;
            for (var i = 0; i < LabStore.MaxActiveJobs + 12; i++)
            {
                await store.SaveJobSummaryAsync(new JobSummary
                {
                    JobId = $"job-{i:D4}",
                    AgentId = "pc-1",
                    State = UpdateJobState.Installing,
                    UpdatedAt = now.AddSeconds(i),
                }, "PC-01");
            }

            var active = await store.GetActiveJobsAsync();
            var total = await store.CountActiveJobsAsync();
            var snapshot = await LiveSnapshotBuilder.BuildAsync(store);

            Assert.Equal(LabStore.MaxActiveJobs + 12, total);
            Assert.Equal(LabStore.MaxActiveJobs, active.Count);
            Assert.Equal(12, snapshot.HiddenActiveJobCount);
        });
    }

    [Fact]
    public async Task CheckIn_RejectsNewJobAfterActiveCap()
    {
        await WithStoreAsync(async (store, _) =>
        {
            var now = DateTimeOffset.UtcNow;
            for (var i = 0; i < LabStore.MaxActiveJobs; i++)
            {
                await store.SaveJobSummaryAsync(new JobSummary
                {
                    JobId = $"job-{i:D4}",
                    AgentId = "pc-1",
                    State = UpdateJobState.Installing,
                    UpdatedAt = now.AddSeconds(i),
                }, "PC-01");
            }

            var request = new AgentCheckInRequest
            {
                Agent = new AgentInfo { AgentId = "PC-1", MachineName = "PC-01" },
                Telemetry = new AgentTelemetry
                {
                    AgentId = "PC-1",
                    CurrentJob = new JobSummary
                    {
                        JobId = "job-over-cap",
                        AgentId = "PC-1",
                        State = UpdateJobState.Installing,
                        UpdatedAt = now.AddMinutes(1),
                    },
                },
            };

            var result = await AgentCheckInHandler.ProcessAsync(
                request,
                store,
                new AgentPolicyService(store),
                new LiveEventBus(),
                new CheckinGateRegistry(),
                CancellationToken.None);

            Assert.Equal(200, result.StatusCode);
            Assert.Null(await store.GetJobAsync("job-over-cap"));
        });
    }

    private static async Task WithStoreAsync(Func<LabStore, string, Task> action)
    {
        var root = Path.Combine(
            Path.GetTempPath(), "UpdateAgentAudit50Round2LabManager", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new LabStore(Path.Combine(root, "lab.db"));
            await store.InitializeAsync();
            await action(store, root);
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
}
