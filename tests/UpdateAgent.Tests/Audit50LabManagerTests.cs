using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UpdateAgent.LabManager.Auth;
using UpdateAgent.LabManager.Configuration;
using UpdateAgent.LabManager.Live;
using UpdateAgent.LabManager.Models;
using UpdateAgent.LabManager.Services;
using UpdateAgent.LabManager.Storage;
using UpdateAgent.Shared;
using UpdateAgent.Shared.Models;
using UpdateAgent.Shared.Serialization;

namespace UpdateAgent.Tests;

/// <summary>50차 자아성찰에서 확인한 LabManager 결함의 회귀 테스트.</summary>
public sealed class Audit50LabManagerTests
{
    [Fact]
    public async Task SaveJobSummary_UpdatesMachineNameAfterRename()
    {
        await WithStoreAsync(async (store, _) =>
        {
            var now = DateTimeOffset.UtcNow;
            var summary = new JobSummary
            {
                JobId = "job-rename",
                AgentId = "agent-1",
                State = UpdateJobState.Installing,
                StartedAt = now,
                UpdatedAt = now,
            };
            await store.SaveJobSummaryAsync(summary, "OLD-PC");
            await store.SaveJobSummaryAsync(summary with { UpdatedAt = now.AddMinutes(1) }, "NEW-PC");

            var stored = await store.GetJobAsync("job-rename");
            Assert.NotNull(stored);
            Assert.Equal("NEW-PC", stored!.MachineName);
        });
    }

    [Fact]
    public void Normalize_KeepsServerOwnedJobFlagsFromStoredSummary()
    {
        var now = DateTimeOffset.UtcNow;
        var stored = new JobSummary
        {
            JobId = "job-flags",
            AgentId = "pc-1",
            State = UpdateJobState.Installing,
            IsDryRun = true,
            RequiresMaestroAttention = true,
            MaxRebootCount = 3,
            UpdatedAt = now.AddMinutes(-1),
        };
        var incoming = new JobSummary
        {
            JobId = "job-flags",
            AgentId = "pc-1",
            State = UpdateJobState.Installing,
            IsDryRun = false,
            RequiresMaestroAttention = false,
            MaxRebootCount = 9,
            UpdatedAt = now.AddDays(1),
        };

        var normalized = JobStateGuard.Normalize(incoming, now, stored);

        Assert.True(normalized.IsDryRun);
        Assert.True(normalized.RequiresMaestroAttention);
        Assert.Equal(3, normalized.MaxRebootCount);
        Assert.Equal(now, normalized.UpdatedAt);
        Assert.Equal(now, normalized.ReportedAt);
    }

    [Fact]
    public async Task CheckIn_OversizedResultBatch_StillReturnsServerPolicy()
    {
        await WithStoreAsync(async (store, _) =>
        {
            var results = Enumerable.Range(0, 101)
                .Select(i => new CommandResult
                {
                    CommandId = $"cmd-{i:D3}",
                    AgentId = "pc-1",
                    Message = "완료",
                    CompletedAt = DateTimeOffset.UtcNow,
                })
                .ToList();
            var request = new AgentCheckInRequest
            {
                Agent = new AgentInfo { AgentId = "pc-1", MachineName = "PC-01" },
                Telemetry = new AgentTelemetry { AgentId = "pc-1" },
                CommandResults = results,
            };

            var result = await ProcessAsync(request, store);

            Assert.Equal(200, result.StatusCode);
            var response = Assert.IsType<AgentCheckInResponse>(result.Response);
            Assert.NotNull(response.Policy);
            Assert.True(response.MaestroManualConfirmationRequired);
            Assert.Equal(results.Count, response.CommandResultOutcomes.Count);
        });
    }

    [Fact]
    public async Task LiveSnapshot_MarksAgentWithActiveJobMissingFromTelemetry()
    {
        await WithStoreAsync(async (store, _) =>
        {
            await store.UpsertAgentAsync(
                new AgentInfo { AgentId = "agent-1", MachineName = "PC-01" },
                new AgentTelemetry { AgentId = "agent-1", State = AgentState.Idle });
            await store.SaveJobSummaryAsync(
                new JobSummary
                {
                    JobId = "job-active",
                    AgentId = "agent-1",
                    State = UpdateJobState.Installing,
                    UpdatedAt = DateTimeOffset.UtcNow,
                },
                "PC-01");

            var snapshot = await LiveSnapshotBuilder.BuildAsync(store);

            var agent = Assert.Single(snapshot.Agents);
            Assert.True(agent.HasActiveJob);
        });
    }

    [Fact]
    public async Task PolicySummaries_MatchAgentIdCaseInsensitively()
    {
        await WithStoreAsync(async (store, _) =>
        {
            await store.UpsertAgentAsync(
                new AgentInfo { AgentId = "pc-1", MachineName = "PC-01" },
                new AgentTelemetry { AgentId = "pc-1" });
            await store.SetSettingAsync(
                "agent-policy:PC-1", UpdateAgentJson.Serialize(UpdatePolicy.Default));

            var summaries = await new AgentPolicyService(store).GetSummariesAsync();

            var summary = Assert.Single(summaries);
            Assert.True(summary.HasOverride);
            Assert.Equal("PC별 설정", summary.Source);
        });
    }

    [Fact]
    public async Task AdminTokenStore_RechecksCollisionAfterAgentTokenReload()
    {
        await WithTokenStore((options, store, overridePath) =>
        {
            WriteTokenFile(overridePath, "runtime-admin-token-001");
            Assert.Equal("runtime-admin-token-001", store.CurrentToken);

            options.CurrentValue = new LabManagerOptions
            {
                AdminToken = UpdateAgentConstants.SampleAdminToken,
                AgentToken = "runtime-admin-token-001",
            };

            Assert.Equal(string.Empty, store.CurrentToken);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void StartJobGate_TreatsTelemetryMaestroDetectionFailureAsManualConfirmationRequired()
    {
        var agent = new AgentRecord
        {
            Agent = new AgentInfo { AgentId = "pc-1", MachineName = "PC-01" },
            Telemetry = new AgentTelemetry
            {
                AgentId = "pc-1",
                Maestro = MaestroDetectionResult.Failed,
            },
            Online = true,
            LastSeen = DateTimeOffset.UtcNow,
        };

        Assert.True(AgentPolicyService.RequiresMaestroConfirmation(agent));
    }

    [Fact]
    public async Task WakeOnLan_RejectsMacTextWithNonSeparatorJunk()
    {
        var options = new MutableOptionsMonitor(new LabManagerOptions());
        var service = new WakeOnLanService(options, NullLogger<WakeOnLanService>.Instance);

        var results = await service.WakeAsync(["zz11:22:33:44:55:66xx"], null);

        var result = Assert.Single(results);
        Assert.False(result.Sent);
        Assert.Contains("형식이", result.Message);
    }

    [Fact]
    public void StartJobsRequest_HasTargetsHandlesNullAndEmptyLists()
    {
        Assert.False(new StartJobsRequest { AgentIds = null! }.HasTargets);
        Assert.False(new StartJobsRequest().HasTargets);
        Assert.True(new StartJobsRequest { AgentIds = ["pc-1"] }.HasTargets);
    }

    [Fact]
    public async Task AdminTokenStatus_ReflectsActualStoreState()
    {
        await WithTokenStore(async (options, store, _) =>
        {
            var before = store.GetStatus();
            Assert.True(before.IsPlaceholder);
            Assert.False(before.HasRuntimeOverride);

            await store.SetTokenAsync("admin-token-12345");

            var after = store.GetStatus();
            Assert.False(after.IsPlaceholder);
            Assert.True(after.HasRuntimeOverride);
        });
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

    private static async Task WithStoreAsync(Func<LabStore, string, Task> action)
    {
        var root = Path.Combine(
            Path.GetTempPath(), "UpdateAgentAudit50LabManager", Guid.NewGuid().ToString("N"));
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

    private static async Task WithTokenStore(
        Func<MutableOptionsMonitor, AdminTokenStore, string, Task> action)
    {
        var root = Path.Combine(
            Path.GetTempPath(), "UpdateAgentAudit50Tokens", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var overridePath = Path.Combine(root, "runtime-admin-token.json");
        var options = new MutableOptionsMonitor(new LabManagerOptions
        {
            AdminToken = UpdateAgentConstants.SampleAdminToken,
            AgentToken = UpdateAgentConstants.SampleAgentToken,
        });
        var store = new AdminTokenStore(options, overridePath, NullLogger<AdminTokenStore>.Instance);
        try
        {
            await action(options, store, overridePath);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void WriteTokenFile(string path, string token) =>
        File.WriteAllText(
            path,
            $$"""{"AdminToken":"{{token}}"}""",
            new UTF8Encoding(false));

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // 파일 잠금이 남아도 다음 테스트에는 영향을 주지 않는다.
        }
    }

    private sealed class MutableOptionsMonitor : IOptionsMonitor<LabManagerOptions>
    {
        public MutableOptionsMonitor(LabManagerOptions value) => CurrentValue = value;

        public LabManagerOptions CurrentValue { get; set; }

        public LabManagerOptions Get(string? name) => CurrentValue;

        public IDisposable OnChange(Action<LabManagerOptions, string?> listener) => new NoopDisposable();

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
