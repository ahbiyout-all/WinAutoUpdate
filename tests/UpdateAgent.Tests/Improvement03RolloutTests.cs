using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using UpdateAgent.LabManager.Live;
using UpdateAgent.LabManager.Models;
using UpdateAgent.LabManager.Services;
using UpdateAgent.LabManager.Storage;
using UpdateAgent.Shared.Models;
using UpdateAgent.Shared.Serialization;

namespace UpdateAgent.Tests;

/// <summary>
/// 개선 3(단계적 롤아웃/유지보수 창)의 동작 회귀 테스트.
/// 기본 경로가 즉시 시작인지, 예약 상태가 재시작 후에도 중복 없이 전송되는지 확인한다.
/// </summary>
public sealed class Improvement03RolloutTests
{
    private static readonly DateTimeOffset LocalNoon =
        new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset UtcNoon =
        new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void UpdatePolicy_DefaultsKeepImmediateStartAndOldJsonStillDeserializes()
    {
        var policy = UpdatePolicy.Default;
        Assert.Equal(0, policy.RolloutBatchSize);
        Assert.Equal(0, policy.RolloutIntervalSeconds);
        Assert.Null(policy.MaintenanceWindowStart);
        Assert.Null(policy.MaintenanceWindowEnd);

        const string legacyJson = """
            {"includeSecurity":true,"includeCritical":true,"maxRebootCount":3}
            """;
        var legacy = UpdateAgentJson.Deserialize<UpdatePolicy>(legacyJson);
        Assert.NotNull(legacy);
        Assert.Equal(0, legacy!.RolloutBatchSize);
        Assert.Null(legacy.MaintenanceWindowStart);

        var roundTrip = UpdateAgentJson.Deserialize<UpdatePolicy>(
            UpdateAgentJson.Serialize(new UpdatePolicy
            {
                RolloutBatchSize = 5,
                RolloutIntervalSeconds = 30,
                MaintenanceWindowStart = "22:00",
                MaintenanceWindowEnd = "06:00",
            }));
        Assert.NotNull(roundTrip);
        Assert.Equal(5, roundTrip!.RolloutBatchSize);
        Assert.Equal(30, roundTrip.RolloutIntervalSeconds);
        Assert.Equal("22:00", roundTrip.MaintenanceWindowStart);
        Assert.Equal("06:00", roundTrip.MaintenanceWindowEnd);
    }

    [Theory]
    [InlineData(-1, 0, null, null)]
    [InlineData(0, -1, null, null)]
    [InlineData(0, 0, "09:00", null)]
    [InlineData(0, 0, null, "17:00")]
    [InlineData(0, 0, "9:00", "17:00")]
    [InlineData(0, 0, "09:00", "09:00")]
    [InlineData(0, 0, "25:00", "01:00")]
    [InlineData(0, 0, "09:60", "17:00")]
    public void RolloutConfiguration_RejectsInvalidValues(
        int batchSize,
        int interval,
        string? start,
        string? end)
    {
        var policy = new UpdatePolicy
        {
            RolloutBatchSize = batchSize,
            RolloutIntervalSeconds = interval,
            MaintenanceWindowStart = start,
            MaintenanceWindowEnd = end,
        };

        Assert.False(RolloutConfiguration.TryCreate(policy, out _, out var errors));
        Assert.NotEmpty(errors);
        Assert.All(errors, message => Assert.Contains(" ", message));
    }

    [Fact]
    public void RolloutConfiguration_HandlesNormalAndMidnightCrossingWindows()
    {
        var normal = CreateConfiguration("09:00", "17:00");
        Assert.False(normal.CrossesMidnight);
        Assert.True(normal.IsWithinWindow(At(9, 0)));
        Assert.True(normal.IsWithinWindow(At(16, 59)));
        Assert.False(normal.IsWithinWindow(At(8, 59)));
        Assert.False(normal.IsWithinWindow(At(17, 0)));

        var midnight = CreateConfiguration("22:00", "06:00");
        Assert.True(midnight.CrossesMidnight);
        Assert.True(midnight.IsWithinWindow(At(22, 0)));
        Assert.True(midnight.IsWithinWindow(At(23, 59)));
        Assert.True(midnight.IsWithinWindow(At(0, 0)));
        Assert.True(midnight.IsWithinWindow(At(5, 59)));
        Assert.False(midnight.IsWithinWindow(At(6, 0)));
        Assert.False(midnight.IsWithinWindow(At(21, 59)));

        Assert.True(RolloutConfiguration.TryCreate(
            UpdatePolicy.Default, out var none, out var errors));
        Assert.Empty(errors);
        Assert.False(none.HasWindow);
        Assert.True(none.IsWithinWindow(At(3, 0)));
    }

    [Fact]
    public async Task DefaultBehavior_StartsAllTargetsImmediatelyWithoutWindowOrSchedule()
    {
        await WithStoreAsync(async (store, _) =>
        {
            var starts = CreateStartService(store);
            await AddAgentAsync(store, "pc-1");
            await AddAgentAsync(store, "pc-2");

            var result = await starts.StartAsync(Request(["pc-1", "pc-2"]), LocalNoon, UtcNoon, default);

            Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
            Assert.Equal(2, result.Response!.JobIds.Count);
            Assert.Equal(1, result.Response.TotalBatches);
            Assert.Equal(1, result.Response.DispatchedBatches);
            Assert.Equal(0, result.Response.PendingBatches);
            Assert.Empty(result.Response.Warnings);

            var commands = await store.GetCommandsAsync();
            Assert.Equal(2, commands.Count);
            Assert.All(commands, command => Assert.Equal(CommandType.StartJob, command.Type));
            foreach (var jobId in result.Response.JobIds)
                Assert.Null(await store.GetRolloutScheduleAsync(jobId));
            Assert.Equal(2, (await store.GetAuditAsync()).Count);
        });
    }

    [Fact]
    public async Task OutsideMaintenanceWindow_RejectsBeforeAnySideEffect()
    {
        await WithStoreAsync(async (store, _) =>
        {
            var starts = CreateStartService(store);
            await AddAgentAsync(store, "pc-1");
            await AddAgentAsync(store, "pc-2");
            var policy = new UpdatePolicy
            {
                MaintenanceWindowStart = "09:00",
                MaintenanceWindowEnd = "17:00",
                RolloutBatchSize = 2,
            };

            var result = await starts.StartAsync(
                Request(["pc-1", "pc-2"], policy), At(18, 0), UtcNoon, default);

            Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
            Assert.Contains("유지보수", result.Errors[0]);
            Assert.Contains("09:00~17:00", result.Errors[0]);
            Assert.Empty(result.Response?.JobIds ?? []);
            Assert.Empty(await store.GetCommandsAsync());
            Assert.Empty(await store.GetJobsAsync());
            Assert.Empty(await store.GetAuditAsync());
        });
    }

    [Fact]
    public async Task InvalidConfiguration_RejectsBeforeAnySideEffect()
    {
        await WithStoreAsync(async (store, _) =>
        {
            var starts = CreateStartService(store);
            await AddAgentAsync(store, "pc-1");
            await AddAgentAsync(store, "pc-2");
            var policy = new UpdatePolicy { RolloutBatchSize = -1 };

            var result = await starts.StartAsync(
                Request(["pc-1", "pc-2"], policy), LocalNoon, UtcNoon, default);

            Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
            Assert.Contains("배치 크기", result.Errors[0]);
            Assert.Empty(await store.GetCommandsAsync());
            Assert.Empty(await store.GetJobsAsync());
            Assert.Empty(await store.GetAuditAsync());
        });
    }

    [Fact]
    public async Task InsideWindow_BatchesTargetsInRequestOrderAndPersistsSchedules()
    {
        await WithStoreAsync(async (store, _) =>
        {
            var starts = CreateStartService(store);
            var ids = Enumerable.Range(1, 5).Select(i => $"pc-{i}").ToList();
            foreach (var id in ids) await AddAgentAsync(store, id);
            var policy = new UpdatePolicy
            {
                RolloutBatchSize = 2,
                RolloutIntervalSeconds = 30,
                MaintenanceWindowStart = "00:00",
                MaintenanceWindowEnd = "23:59",
            };

            var result = await starts.StartAsync(Request(ids, policy), LocalNoon, UtcNoon, default);

            Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
            Assert.Equal(5, result.Response!.JobIds.Count);
            Assert.Equal(3, result.Response.TotalBatches);
            Assert.Equal(1, result.Response.DispatchedBatches);
            Assert.Equal(2, result.Response.PendingBatches);
            Assert.Contains(result.Response.Warnings, warning => warning.Contains("2/3개 배치"));

            var commands = await store.GetCommandsAsync();
            Assert.Equal(2, commands.Count); // 배치 1의 두 대가 즉시 시작된다.
            Assert.All(commands, command => Assert.Equal(CommandType.StartJob, command.Type));
            Assert.All(commands, command =>
                Assert.DoesNotContain("oken", command.PayloadJson, StringComparison.OrdinalIgnoreCase));
            var firstPayload = UpdateAgentJson.Deserialize<StartJobCommandPayload>(commands[0].PayloadJson);
            Assert.Equal(result.Response.JobIds[0], firstPayload?.JobId);

            var expectedBatches = new[] { 1, 1, 2, 2, 3 };
            var expectedOffsets = new[] { 0, 0, 30, 30, 60 };
            for (var i = 0; i < ids.Count; i++)
            {
                var schedule = await store.GetRolloutScheduleAsync(result.Response.JobIds[i]);
                Assert.NotNull(schedule);
                Assert.Equal(expectedBatches[i], schedule!.BatchIndex);
                Assert.Equal(3, schedule.BatchCount);
                Assert.Equal(30, schedule.IntervalSeconds);
                Assert.Equal(UtcNoon.AddSeconds(expectedOffsets[i]), schedule.DueAt);
                if (i < 2)
                {
                    Assert.NotNull(schedule.DispatchedAt);
                    Assert.Null(schedule.CancelledAt);
                }
                else
                {
                    Assert.Null(schedule.DispatchedAt);
                }
            }
        });
    }

    [Fact]
    public async Task Dispatch_IsIdempotentAcrossDuplicateTicksAndRestart()
    {
        await WithStoreAsync(async (store, root) =>
        {
            var starts = CreateStartService(store);
            var ids = Enumerable.Range(1, 3).Select(i => $"pc-{i}").ToList();
            foreach (var id in ids) await AddAgentAsync(store, id);
            var policy = new UpdatePolicy { RolloutBatchSize = 2, RolloutIntervalSeconds = 10 };

            var result = await starts.StartAsync(Request(ids, policy), LocalNoon, UtcNoon, default);
            Assert.Equal(2, (await store.GetCommandsAsync()).Count); // 배치 1 두 대

            Assert.Empty(await store.DispatchDueRolloutJobsAsync(UtcNoon, cancellationToken: default));
            var due = UtcNoon.AddSeconds(10);
            var first = await store.DispatchDueRolloutJobsAsync(due, cancellationToken: default);
            Assert.Equal([result.Response!.JobIds[2]], first);
            Assert.Equal(3, (await store.GetCommandsAsync()).Count);

            Assert.Empty(await store.DispatchDueRolloutJobsAsync(due, cancellationToken: default));
            Assert.Empty(await store.DispatchDueRolloutJobsAsync(due, cancellationToken: default));
            Assert.Equal(3, (await store.GetCommandsAsync()).Count);

            // 같은 DB를 새 LabStore로 열어도 dispatched_at이 중복 발급을 막는다.
            var reopened = new LabStore(Path.Combine(root, "lab.db"));
            await reopened.InitializeAsync();
            Assert.Empty(await reopened.DispatchDueRolloutJobsAsync(
                UtcNoon.AddDays(1), cancellationToken: default));
            Assert.Equal(3, (await reopened.GetCommandsAsync()).Count);
        });
    }

    [Fact]
    public async Task SaveRolloutSchedule_WithNullFirstCommand_PersistsPendingBatch()
    {
        await WithStoreAsync(async (store, _) =>
        {
            const string jobId = "job-null-first";
            await store.SaveRolloutScheduleAsync(new JobRolloutSchedule
            {
                JobId = jobId,
                AgentId = "pc-1",
                MachineName = "PC-1",
                BatchIndex = 2,
                BatchCount = 2,
                IntervalSeconds = 30,
                DueAt = UtcNoon.AddSeconds(30),
                PayloadJson = "{}",
            }, null);

            var schedule = await store.GetRolloutScheduleAsync(jobId);

            Assert.NotNull(schedule);
            Assert.Null(schedule!.DispatchedAt);
            Assert.Null(schedule.CancelledAt);
            Assert.Empty(await store.GetCommandsAsync());
        });
    }

    [Fact]
    public async Task FailedFirstBatch_DoesNotReportImmediateDispatch()
    {
        await WithStoreAsync(async (store, root) =>
        {
            var starts = CreateStartService(store);
            await AddAgentAsync(store, "pc-1");
            await AddAgentAsync(store, "pc-2");

            // 온라인 판정은 최근 check-in 시각(last_seen) 기준이므로 오래된 시각으로 바꾼다.
            await using (var connection = new SqliteConnection(
                $"Data Source={Path.Combine(root, "lab.db")}"))
            {
                await connection.OpenAsync();
                await using var update = connection.CreateCommand();
                update.CommandText = "UPDATE agents SET last_seen=$seen WHERE agent_id='pc-1';";
                update.Parameters.AddWithValue(
                    "$seen", UtcTimestamps.Format(UtcNoon.AddHours(-2)));
                await update.ExecuteNonQueryAsync();
            }

            var policy = new UpdatePolicy { RolloutBatchSize = 1, RolloutIntervalSeconds = 10 };
            var result = await starts.StartAsync(
                Request(["pc-1", "pc-2"], policy), LocalNoon, UtcNoon, default);

            Assert.Single(result.Response!.JobIds);
            Assert.Contains(result.Response.Errors, error => error.Contains("오프라인"));
            Assert.Equal(1, result.Response.TotalBatches);
            Assert.Equal(0, result.Response.DispatchedBatches);
            Assert.Equal(1, result.Response.PendingBatches);
            // 실제로 만들어진 배치가 1개뿐이므로 즉시 발급 오보도, 다중 배치 안내도 없다.
            Assert.DoesNotContain(
                "첫 배치는 즉시",
                string.Join(" ", result.Response.Warnings));
            Assert.DoesNotContain(
                "배치가 서버에 대기",
                string.Join(" ", result.Response.Warnings));
            Assert.Empty(await store.GetCommandsAsync());

            var schedule = await store.GetRolloutScheduleAsync(result.Response.JobIds[0]);
            Assert.NotNull(schedule);
            Assert.Equal(2, schedule!.BatchIndex);
            Assert.Null(schedule.DispatchedAt);
        });
    }

    [Fact]
    public async Task CancelledJobBeforeDispatch_IsSkippedAndOtherJobStillStarts()
    {
        await WithStoreAsync(async (store, _) =>
        {
            var starts = CreateStartService(store);
            var ids = Enumerable.Range(1, 4).Select(i => $"pc-{i}").ToList();
            foreach (var id in ids) await AddAgentAsync(store, id);
            var policy = new UpdatePolicy { RolloutBatchSize = 2, RolloutIntervalSeconds = 10 };

            var result = await starts.StartAsync(Request(ids, policy), LocalNoon, UtcNoon, default);
            var cancelledId = result.Response!.JobIds[2];
            Assert.True(await store.CancelRolloutAsync(cancelledId, UtcNoon, default));

            var dispatched = await store.DispatchDueRolloutJobsAsync(
                UtcNoon.AddSeconds(10), cancellationToken: default);

            Assert.Equal([result.Response.JobIds[3]], dispatched);
            Assert.Empty(await store.GetCommandsAsync("pc-3"));
            Assert.Single(await store.GetCommandsAsync("pc-4"));
            var cancelled = await store.GetRolloutScheduleAsync(cancelledId);
            Assert.NotNull(cancelled);
            Assert.NotNull(cancelled!.CancelledAt);
            Assert.Null(cancelled.DispatchedAt);
        });
    }

    [Fact]
    public async Task AlreadyDispatchedJob_CannotBeCancelledOutOfTheQueue()
    {
        await WithStoreAsync(async (store, _) =>
        {
            var starts = CreateStartService(store);
            await AddAgentAsync(store, "pc-1");
            await AddAgentAsync(store, "pc-2");
            var policy = new UpdatePolicy { RolloutBatchSize = 1, RolloutIntervalSeconds = 10 };

            var result = await starts.StartAsync(Request(["pc-1", "pc-2"], policy), LocalNoon, UtcNoon, default);
            var jobId = result.Response!.JobIds[0];

            Assert.False(await store.CancelRolloutAsync(jobId, UtcNoon, default));
            var schedule = await store.GetRolloutScheduleAsync(jobId);
            Assert.NotNull(schedule);
            Assert.NotNull(schedule!.DispatchedAt);
            Assert.Null(schedule.CancelledAt);
            Assert.Single(await store.GetCommandsAsync()); // 배치 2는 아직 예약 상태
        });
    }

    [Fact]
    public async Task MaestroConfirmation_StillRequiredUnderBatching()
    {
        await WithStoreAsync(async (store, _) =>
        {
            var starts = CreateStartService(store);
            await AddAgentAsync(store, "pc-1", maestro: true);
            var policy = new UpdatePolicy { RolloutBatchSize = 2, RolloutIntervalSeconds = 10 };

            var result = await starts.StartAsync(
                Request(["pc-1"], policy, maestroConfirmed: false), LocalNoon, UtcNoon, default);

            Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
            Assert.Contains(result.Response!.Errors, error => error.Contains("Maestro"));
            Assert.Empty(result.Response.JobIds);
            Assert.Empty(await store.GetCommandsAsync());
            Assert.Empty(await store.GetJobsAsync());
        });
    }

    [Fact]
    public async Task ActiveJobCap_StillAppliesToPendingBatchedJobs()
    {
        await WithStoreAsync(async (store, _) =>
        {
            var starts = CreateStartService(store);
            await AddAgentAsync(store, "pc-1");
            for (var i = 0; i < LabStore.MaxActiveJobs; i++)
            {
                await store.SaveJobSummaryAsync(new JobSummary
                {
                    JobId = $"job-cap-{i:D4}",
                    AgentId = "pc-1",
                    State = UpdateJobState.Installing,
                    StartedAt = UtcNoon,
                    UpdatedAt = UtcNoon.AddSeconds(i),
                }, "PC-1");
            }
            var policy = new UpdatePolicy { RolloutBatchSize = 2, RolloutIntervalSeconds = 10 };

            var result = await starts.StartAsync(
                Request(["pc-1"], policy, maestroConfirmed: true), LocalNoon, UtcNoon, default);

            Assert.Contains(result.Response!.Errors, error => error.Contains("상한"));
            Assert.Empty(result.Response.JobIds);
            Assert.Empty(await store.GetCommandsAsync());
        });
    }

    [Fact]
    public async Task DuplicateTargets_AreCollapsedBeforeBatching()
    {
        await WithStoreAsync(async (store, _) =>
        {
            var starts = CreateStartService(store);
            await AddAgentAsync(store, "pc-1");
            var policy = new UpdatePolicy { RolloutBatchSize = 1, RolloutIntervalSeconds = 5 };

            var result = await starts.StartAsync(
                Request(["pc-1", "PC-1"], policy), LocalNoon, UtcNoon, default);

            Assert.Single(result.Response!.JobIds);
            Assert.Equal(1, result.Response.TotalBatches);
            Assert.Single(await store.GetCommandsAsync());
            // 중복을 제거한 뒤에는 배치 경고가 나오지 않는다.
            Assert.DoesNotContain("배치로 예약", string.Join(" ", result.Response.Warnings));
        });
    }

    [Fact]
    public async Task HostedDispatcher_WithCancelledTokenDoesNotDispatch()
    {
        await WithStoreAsync(async (store, _) =>
        {
            await store.SaveJobSummaryAsync(new JobSummary
            {
                JobId = "job-hosted-cancel",
                AgentId = "pc-1",
                State = UpdateJobState.Pending,
                StartedAt = UtcNoon,
                UpdatedAt = UtcNoon,
            }, "PC-1");
            await store.SaveRolloutScheduleAsync(new JobRolloutSchedule
            {
                JobId = "job-hosted-cancel",
                AgentId = "pc-1",
                MachineName = "PC-1",
                BatchIndex = 2,
                BatchCount = 2,
                DueAt = UtcNoon.AddMinutes(-1),
            }, null);

            var dispatcher = new JobRolloutDispatcherService(
                store, new LiveEventBus(), NullLogger<JobRolloutDispatcherService>.Instance);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await dispatcher.StartAsync(cts.Token);
            await dispatcher.StopAsync(CancellationToken.None);

            Assert.Empty(await store.GetCommandsAsync());
        });
    }

    [Fact]
    public void Endpoint_WiresValidatedServiceAndHostedDispatcher()
    {
        var program = Read("src/UpdateAgent.LabManager/Program.cs");
        Assert.Contains("AddSingleton<JobStartService>()", program);
        Assert.Contains("AddHostedService<JobRolloutDispatcherService>()", program);
        Assert.Contains("await jobStarts.StartAsync(request, cancellationToken)", program);
        Assert.Contains("CancelRolloutAsync(jobId", program);
        Assert.Contains("RolloutConfiguration.TryCreate(policy", program);

        var start = program.IndexOf("app.MapPost(\"/api/admin/jobs/start\"", StringComparison.Ordinal);
        var end = program.IndexOf("app.MapPost(\"/api/admin/jobs/{jobId}/pause\"", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var endpoint = program[start..end];
        Assert.DoesNotContain("AddCommandAsync", endpoint);
        Assert.Contains("Results.BadRequest", endpoint);
        Assert.Contains("errors = result.Errors", endpoint);
    }

    [Fact]
    public void Docs_DescribeWindowBatchingRestartAndAbort()
    {
        var deployment = Read("docs/DEPLOYMENT.md");
        Assert.Contains("8-D. 단계적 롤아웃과 유지보수 창", deployment);
        foreach (var keyword in new[]
        {
            "RolloutBatchSize", "RolloutIntervalSeconds",
            "MaintenanceWindowStart", "MaintenanceWindowEnd",
            "자정", "재시작", "취소", "감사",
        })
        {
            Assert.Contains(keyword, deployment);
        }

        var worklog = Read("docs/WORKLOG.md");
        var heading = "## 2026-11-27 개선 3 — 단계적 롤아웃 / 유지보수 창";
        var start = worklog.IndexOf(heading, StringComparison.Ordinal);
        var next = worklog.IndexOf("## 2026-11-27 개선 2", StringComparison.Ordinal);
        Assert.True(start >= 0 && next > start, "개선 3은 개선 2 위에 있어야 합니다.");

        var section = worklog[start..next];
        var tableStart = section.IndexOf("| 회차 |", StringComparison.Ordinal);
        var tableEnd = section.IndexOf("\n\n", tableStart, StringComparison.Ordinal);
        Assert.True(tableStart >= 0 && tableEnd > tableStart);
        var table = section[tableStart..tableEnd];
        Assert.Equal(10, Regex.Matches(table, @"^\| \d+ \|", RegexOptions.Multiline).Count);

        var previous = -1;
        foreach (var dimension in new[]
        {
            "요구사항 정합성", "기존 정책/직렬화 호환성", "유지보수 창(자정 포함)",
            "배치 멱등성/재시작 안전성", "취소/중단 동작", "비밀값/감사 로그",
            "동시성/취소 토큰", "한국어 문서/메시지", "회귀 범위", "최종 검증",
        })
        {
            var index = table.IndexOf(dimension, StringComparison.Ordinal);
            Assert.True(index > previous, $"회차 순서가 올바르지 않습니다: {dimension}");
            previous = index;
        }
    }

    [Fact]
    public void NewRolloutCode_DoesNotContainTokenValues()
    {
        foreach (var relativePath in new[]
        {
            "src/UpdateAgent.LabManager/Services/RolloutConfiguration.cs",
            "src/UpdateAgent.LabManager/Services/JobStartService.cs",
            "src/UpdateAgent.LabManager/Services/JobRolloutDispatcherService.cs",
        })
        {
            var text = Read(relativePath);
            Assert.DoesNotContain("AdminToken", text);
            Assert.DoesNotContain("AgentToken", text);
            Assert.DoesNotMatch(@"(?i)(password|passwd|pwd)\s*[:=]", text);
        }
    }

    private static RolloutConfiguration CreateConfiguration(string start, string end)
    {
        Assert.True(RolloutConfiguration.TryCreate(
            new UpdatePolicy { MaintenanceWindowStart = start, MaintenanceWindowEnd = end },
            out var configuration,
            out var errors));
        Assert.Empty(errors);
        return configuration;
    }

    private static DateTimeOffset At(int hour, int minute) =>
        new(2026, 11, 27, hour, minute, 0, TimeSpan.Zero);

    private static JobStartService CreateStartService(LabStore store) =>
        new(store, new AgentPolicyService(store), new LiveEventBus());

    private static StartJobsRequest Request(
        IEnumerable<string> agentIds,
        UpdatePolicy? policy = null,
        bool maestroConfirmed = false) =>
        new()
        {
            AgentIds = agentIds.ToList(),
            Policy = policy,
            MaestroConfirmed = maestroConfirmed,
        };

    private static async Task AddAgentAsync(LabStore store, string id, bool maestro = false)
    {
        await store.UpsertAgentAsync(
            new AgentInfo
            {
                AgentId = id,
                MachineName = id.ToUpperInvariant(),
                MaestroInstalled = maestro,
            },
            new AgentTelemetry { AgentId = id });
    }

    private static async Task WithStoreAsync(Func<LabStore, string, Task> action)
    {
        var root = Path.Combine(
            Path.GetTempPath(), "UpdateAgentImprovement03", Guid.NewGuid().ToString("N"));
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
