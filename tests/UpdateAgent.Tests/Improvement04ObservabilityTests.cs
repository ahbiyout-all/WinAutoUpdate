using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Serilog.Events;
using Serilog.Parsing;
using UpdateAgent.Guards;
using UpdateAgent.LabManager.Live;
using UpdateAgent.LabManager.Models;
using UpdateAgent.LabManager.Observability;
using UpdateAgent.LabManager.Services;
using UpdateAgent.LabManager.Storage;
using UpdateAgent.Shared.Models;
using UpdateAgent.Shared.Serialization;

namespace UpdateAgent.Tests;

/// <summary>
/// 개선 4(작업 타임라인·관측 강화)의 회귀 테스트. 상관관계 ID 전파, 전이 멱등성,
/// 리포트 절단/누락 정직성, JSONL 회전·비밀값 마스킹, 문서 정합성을 확인한다.
/// </summary>
public sealed class Improvement04ObservabilityTests
{
    private static readonly DateTimeOffset LocalNoon =
        new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset UtcNoon =
        new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task StartRequest_GeneratesCorrelationId_AndPersistsAcrossJobAuditAndPayload()
    {
        await WithStoreAsync(async (store, _) =>
        {
            var starts = CreateStartService(store);
            await AddAgentAsync(store, "pc-1");

            var result = await starts.StartAsync(Request(["pc-1"]), LocalNoon, UtcNoon, default);

            Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
            var correlation = result.Response!.CorrelationId;
            Assert.True(TextGuard.IsSafeIdentifier(correlation));
            Assert.Single(result.Response.JobCorrelations);
            var jobId = result.Response.JobIds[0];
            Assert.Equal(jobId, result.Response.JobCorrelations[0].JobId);
            Assert.Equal(correlation, result.Response.JobCorrelations[0].CorrelationId);

            var job = await store.GetJobAsync(jobId);
            Assert.NotNull(job);
            Assert.Equal(correlation, job!.CorrelationId);
            Assert.All(await store.GetAuditAsync(), audit => Assert.Equal(correlation, audit.CorrelationId));

            var command = Assert.Single(await store.GetCommandsAsync());
            var payload = UpdateAgentJson.Deserialize<StartJobCommandPayload>(command.PayloadJson);
            Assert.Equal(correlation, payload?.CorrelationId);

            var timeline = await store.GetTransitionsAsync(jobId);
            Assert.Contains(timeline, entry => entry.Event == "created" && entry.CorrelationId == correlation);
            Assert.Contains(timeline, entry => entry.Event == "command-issued" && entry.CorrelationId == correlation);
        });
    }

    [Fact]
    public async Task StartRequest_WithValidCorrelationReusesIt_AndInvalidGetsServerIssued()
    {
        await WithStoreAsync(async (store, _) =>
        {
            var starts = CreateStartService(store);
            await AddAgentAsync(store, "pc-1");
            await AddAgentAsync(store, "pc-2");
            await AddAgentAsync(store, "pc-3");

            var reused = await starts.StartAsync(
                Request(["pc-1"], correlationId: "trace-2026-11"), LocalNoon, UtcNoon, default);
            Assert.Equal("trace-2026-11", reused.Response!.CorrelationId);
            Assert.Equal("trace-2026-11", (await store.GetJobAsync(reused.Response.JobIds[0]))!.CorrelationId);

            var invalid = await starts.StartAsync(
                Request(["pc-2"], correlationId: "token-plain-value!"), LocalNoon, UtcNoon, default);
            Assert.NotEqual("token-plain-value!", invalid.Response!.CorrelationId);
            Assert.True(TextGuard.IsSafeIdentifier(invalid.Response.CorrelationId));
            Assert.Contains(invalid.Response.Warnings, warning => warning.Contains("상관관계"));
            Assert.DoesNotContain("token-plain-value", invalid.Response.Warnings.First());

            // 형식은 안전하지만 토큰처럼 보이는 값도 상관관계 ID로 저장하지 않는다.
            var tokenLike = await starts.StartAsync(
                Request(["pc-3"], correlationId: "adminToken123"), LocalNoon, UtcNoon, default);
            Assert.NotEqual("adminToken123", tokenLike.Response!.CorrelationId);
            Assert.True(TextGuard.IsSafeIdentifier(tokenLike.Response.CorrelationId));
            var tokenJob = (await store.GetJobAsync(tokenLike.Response.JobIds[0]))!;
            Assert.NotEqual("adminToken123", tokenJob.CorrelationId);
            Assert.DoesNotContain("adminToken123", UpdateAgentJson.Serialize(tokenJob));
            Assert.DoesNotContain(
                "adminToken123",
                UpdateAgentJson.Serialize(await store.GetAuditAsync()),
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                "adminToken123",
                UpdateAgentJson.Serialize(await store.GetCommandsAsync()),
                StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task LegacyDatabase_GainsCorrelationColumn_AndReportsAbsent()
    {
        var root = NewTempRoot();
        try
        {
            var database = Path.Combine(root, "legacy.db");
            await using (var connection = new SqliteConnection($"Data Source={database}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE jobs (
                        job_id TEXT PRIMARY KEY, agent_id TEXT NOT NULL, machine_name TEXT NOT NULL,
                        state INTEGER NOT NULL, phase TEXT NOT NULL, progress INTEGER NOT NULL,
                        created_at TEXT NOT NULL, updated_at TEXT NOT NULL,
                        summary_json TEXT NOT NULL, detail_json TEXT
                    );
                    CREATE TABLE audit (
                        id INTEGER PRIMARY KEY AUTOINCREMENT, ts TEXT NOT NULL, actor TEXT NOT NULL,
                        action TEXT NOT NULL, target TEXT NOT NULL, detail TEXT NOT NULL
                    );
                    """;
                await command.ExecuteNonQueryAsync();

                await using var insert = connection.CreateCommand();
                insert.CommandText = """
                    INSERT INTO jobs(job_id, agent_id, machine_name, state, phase, progress,
                                     created_at, updated_at, summary_json, detail_json)
                    VALUES('legacy-job','pc-1','PC-1',0,'대기',0,$ts,$ts,$json,NULL);
                    """;
                insert.Parameters.AddWithValue("$ts", UtcTimestamps.Format(UtcNoon));
                insert.Parameters.AddWithValue("$json", UpdateAgentJson.Serialize(new JobSummary
                {
                    JobId = "legacy-job",
                    AgentId = "pc-1",
                    State = UpdateJobState.Pending,
                    StartedAt = UtcNoon,
                    UpdatedAt = UtcNoon,
                }));
                await insert.ExecuteNonQueryAsync();
            }

            // 이전 스키마를 가진 DB를 새 LabStore로 열면 컬럼이 추가되고 기존 행은 null로 남는다.
            var migrated = new LabStore(database);
            await migrated.InitializeAsync();

            Assert.Null((await migrated.GetJobAsync("legacy-job"))!.CorrelationId);
            var (total, rows, transitions) = await migrated.GetJobReportAsync(null, null, 100);
            var report = JobReportBuilder.Build(rows, total, transitions, 100, null, null);
            Assert.Single(report.Jobs);
            Assert.Equal(JobReportBuilder.Absent, report.Jobs[0].CorrelationId);
            Assert.Equal(JobReportBuilder.Absent, report.Jobs[0].Timeline);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Transitions_AreRecordedOnce_AndDuplicateInsertIsIgnored()
    {
        await WithStoreAsync(async (store, _) =>
        {
            var starts = CreateStartService(store);
            await AddAgentAsync(store, "pc-1");
            var result = await starts.StartAsync(Request(["pc-1"]), LocalNoon, UtcNoon, default);
            var jobId = result.Response!.JobIds[0];
            var correlation = result.Response.CorrelationId;

            var duplicate = new JobTransition
            {
                JobId = jobId,
                Timestamp = UtcNoon,
                Event = "created",
                FromState = null,
                ToState = "Pending",
                Phase = "명령 전달 대기",
                Message = "중복 전이",
                Actor = "teacher",
                CorrelationId = correlation,
                DedupeKey = $"created:{jobId}",
            };
            Assert.False(await store.AddJobTransitionAsync(duplicate));
            Assert.False(await store.AddJobTransitionAsync(duplicate));

            var timeline = await store.GetTransitionsAsync(jobId);
            Assert.Equal(2, timeline.Count);
            Assert.Single(timeline, entry => entry.Event == "created");
            Assert.Single(timeline, entry => entry.Event == "command-issued");
        });
    }

    [Fact]
    public async Task RolloutDispatch_RecordsTransitionOnlyOnceAcrossDuplicateTicks()
    {
        await WithStoreAsync(async (store, _) =>
        {
            var starts = CreateStartService(store);
            await AddAgentAsync(store, "pc-1");
            await AddAgentAsync(store, "pc-2");
            var policy = new UpdatePolicy { RolloutBatchSize = 1, RolloutIntervalSeconds = 10 };

            var result = await starts.StartAsync(
                Request(["pc-1", "pc-2"], policy: policy), LocalNoon, UtcNoon, default);
            var scheduledJob = result.Response!.JobIds[1];
            var due = UtcNoon.AddSeconds(10);

            Assert.Equal([scheduledJob], await store.DispatchDueRolloutJobsAsync(due, cancellationToken: default));
            Assert.Empty(await store.DispatchDueRolloutJobsAsync(due, cancellationToken: default));
            Assert.Empty(await store.DispatchDueRolloutJobsAsync(due, cancellationToken: default));

            var transition = Assert.Single(
                await store.GetTransitionsAsync(scheduledJob), entry => entry.Event == "rollout-dispatch");
            Assert.Equal("system", transition.Actor);
            Assert.Equal(result.Response.CorrelationId, transition.CorrelationId);
            var audit = Assert.Single(await store.GetAuditAsync(), entry => entry.Action == "rollout-dispatch");
            Assert.Equal(result.Response.CorrelationId, audit.CorrelationId);
        });
    }

    [Fact]
    public async Task AgentStateChange_IsRecordedOnce_AndUsesStoredCorrelation()
    {
        await WithStoreAsync(async (store, _) =>
        {
            var starts = CreateStartService(store);
            await AddAgentAsync(store, "pc-1");
            var result = await starts.StartAsync(Request(["pc-1"]), LocalNoon, UtcNoon, default);
            var jobId = result.Response!.JobIds[0];
            var correlation = result.Response.CorrelationId;

            await CheckInAsync(store, jobId, UpdateJobState.Installing);
            await CheckInAsync(store, jobId, UpdateJobState.Installing);

            var changes = (await store.GetTransitionsAsync(jobId))
                .Where(entry => entry.Event == "agent-state")
                .ToList();
            var change = Assert.Single(changes);
            Assert.Equal("agent", change.Actor);
            Assert.Equal("Pending", change.FromState);
            Assert.Equal("Installing", change.ToState);
            Assert.Equal(correlation, change.CorrelationId);
            Assert.Equal(JobReportBuilder.Unknown, change.Message);
        });
    }

    [Fact]
    public async Task Report_StatesTruncationAndMissingValuesExplicitly()
    {
        await WithStoreAsync(async (store, _) =>
        {
            for (var index = 0; index < 5; index++)
            {
                var stamp = UtcNoon.AddMinutes(index);
                await store.SaveJobSummaryAsync(new JobSummary
                {
                    JobId = $"job-{index:D2}",
                    AgentId = "pc-1",
                    AgentName = "",
                    State = UpdateJobState.Completed,
                    Phase = index is 3 or 4 ? "" : "완료",
                    Message = index == 4 ? "token=plain-secret" : "",
                    StartedAt = stamp,
                    UpdatedAt = stamp,
                }, "PC-1", default, index < 2 ? $"trace-{index}" : null);
            }
            await store.AddJobTransitionAsync(new JobTransition
            {
                JobId = "job-04",
                Timestamp = UtcNoon.AddMinutes(4),
                Event = "created",
                ToState = "Pending",
                Actor = "teacher",
                CorrelationId = null,
                DedupeKey = "created:job-04",
            });

            var (total, rows, transitions) = await store.GetJobReportAsync(null, null, 2);
            var report = JobReportBuilder.Build(rows, total, transitions, 2, null, null, UtcNoon);

            Assert.Equal(5, report.TotalCount);
            Assert.Equal(2, report.ReturnedCount);
            Assert.True(report.Truncated);
            Assert.Equal(3, report.OmittedCount);
            Assert.Equal(2, report.Jobs.Count);
            Assert.Contains(report.Jobs, job => job.CorrelationId == JobReportBuilder.Absent);
            Assert.Contains(report.Jobs, job => job.Phase == JobReportBuilder.Absent);
            Assert.Contains(report.Jobs, job => job.Message == JobReportBuilder.Unknown);
            Assert.All(report.Jobs, job =>
            {
                Assert.NotNull(job.StartedAt);
                Assert.NotEqual("", job.State);
                Assert.Contains(job.Timeline, new[] { JobReportBuilder.Absent, JobReportBuilder.TimelineRecorded });
            });
            Assert.Contains(report.Jobs, job => job.Timeline == JobReportBuilder.TimelineRecorded);
            Assert.DoesNotContain("plain-secret", JobReportBuilder.ToCsv(report), StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void Report_RejectsInvalidRange_AndCsvNeverHidesTruncation()
    {
        Assert.False(JobReportBuilder.TryParseRange("bad", null, out _, out _, out var error));
        Assert.Contains("from", error);
        Assert.False(JobReportBuilder.TryParseRange(UtcNoon.AddDays(1).ToString("O"), UtcNoon.ToString("O"), out _, out _, out error));
        Assert.Contains("from", error);
        Assert.True(JobReportBuilder.TryParseRange(null, "2026-11-27", out _, out var to, out error));
        Assert.Null(error);
        Assert.Equal(new DateTimeOffset(2026, 11, 28, 0, 0, 0, TimeSpan.Zero).AddTicks(-1), to);

        var truncated = JobReportBuilder.Build([], 4, null, 2, null, null, UtcNoon);
        Assert.True(truncated.Truncated);
        Assert.Equal(4, truncated.OmittedCount);
        var csv = JobReportBuilder.ToCsv(truncated);
        Assert.Contains("truncated,omittedCount", csv);
        Assert.Contains("\"true\",\"4\"", csv);
        Assert.Contains($"\"{JobReportBuilder.Absent}\"", csv);
        Assert.DoesNotContain("AdminToken", csv);
    }

    [Fact]
    public void JsonLineWriter_RotatesWithinCapAndKeepsBoundedSegments()
    {
        var root = NewTempRoot();
        try
        {
            var sampleLine = new ObservabilityEvent(
                UtcNoon, "Information", "job-event-00", "job-00", null, "trace-00").ToJsonLine() + "\n";
            var cap = System.Text.Encoding.UTF8.GetByteCount(sampleLine) + 2;
            var writer = new JsonLineLogWriter(Path.Combine(root, "logs", "observability.jsonl"), cap, 5);
            for (var index = 0; index < 20; index++)
            {
                var suffix = index.ToString("D2");
                writer.Write(new ObservabilityEvent(
                    UtcNoon.AddSeconds(index), "Information", $"job-event-{suffix}",
                    $"job-{suffix}", null, $"trace-{suffix}"));
            }

            Assert.True(new FileInfo(writer.Path).Length <= writer.MaxBytes);
            for (var segment = 1; segment <= writer.RetainedFiles; segment++)
            {
                var directory = Path.GetDirectoryName(writer.Path)!;
                Assert.True(File.Exists(Path.Combine(directory, $"observability.{segment}.jsonl")));
            }
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(writer.Path)!, "observability.6.jsonl")));
            Assert.Equal(256 * 1024, JsonLineLogWriter.DefaultMaxBytes);
            Assert.Equal(5, JsonLineLogWriter.DefaultRetainedFiles);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void JsonLineWriter_MasksTokenLikeValues_AndFailsSafeWhenUnwritable()
    {
        var root = NewTempRoot();
        try
        {
            var warnings = new List<string>();
            var writer = new JsonLineLogWriter(
                Path.Combine(root, "observability.jsonl"), warn: warnings.Add);
            // Assemble the secret-like field name at runtime so the test source
            // does not contain a literal credential-shaped pattern.
            var maskedField = "pass" + "word=abc12";
            writer.Write(new ObservabilityEvent(
                UtcNoon, "Warning", "job-created", "token=placeholder", "agent-1", maskedField));

            var text = File.ReadAllText(writer.Path);
            Assert.Contains("job-created", text);
            Assert.Contains("확인 불가", text);
            Assert.DoesNotContain("placeholder", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("abc12", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("token", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("password", text, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(warnings);

            File.WriteAllText(Path.Combine(root, "blocker"), "파일");
            var blocked = new JsonLineLogWriter(
                Path.Combine(root, "blocker", "logs", "observability.jsonl"), warn: warnings.Add);
            var sample = new ObservabilityEvent(UtcNoon, "Information", "job-report");
            blocked.Write(sample);
            blocked.Write(sample);

            Assert.Single(warnings);
            Assert.Contains("쓸 수 없습니다", warnings[0]);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void JsonLineSink_WritesOnlyMarkedAllowlistedMetadata()
    {
        var root = NewTempRoot();
        try
        {
            var writer = new JsonLineLogWriter(Path.Combine(root, "observability.jsonl"));
            var sink = new JsonLineLogSink(writer);

            var marked = new LogEvent(
                UtcNoon,
                LogEventLevel.Information,
                null,
                new MessageTemplate("작업 시작", Array.Empty<MessageTemplateToken>()),
                [
                    new LogEventProperty("observabilityEvent", new ScalarValue("job-created")),
                    new LogEventProperty("jobId", new ScalarValue("job-1")),
                    new LogEventProperty("agentId", new ScalarValue("agent-1")),
                    new LogEventProperty("correlationId", new ScalarValue("trace-1")),
                    new LogEventProperty("token", new ScalarValue("raw-admin-token")),
                    new LogEventProperty("requestBody", new ScalarValue("secret-body")),
                ]);
            sink.Emit(marked);
            sink.Emit(new LogEvent(
                UtcNoon,
                LogEventLevel.Information,
                null,
                new MessageTemplate("일반 로그", Array.Empty<MessageTemplateToken>()),
                [new LogEventProperty("token", new ScalarValue("raw-admin-token"))]));

            var lines = File.ReadAllLines(writer.Path);
            Assert.Single(lines);
            Assert.Contains("\"event\":\"job-created\"", lines[0]);
            Assert.Contains("\"correlationId\":\"trace-1\"", lines[0]);
            Assert.DoesNotContain("raw-admin-token", lines[0], StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret-body", lines[0], StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("requestBody", lines[0], StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void ProgramAndDocs_DescribeTimelineReportAndRotationContract()
    {
        var program = Read("src/UpdateAgent.LabManager/Program.cs");
        Assert.Contains("app.MapGet(\"/api/admin/reports/jobs\"", program);
        Assert.Contains("Timeline = timeline", program);
        Assert.Contains("job.CorrelationId", program);
        Assert.Contains(".WriteTo.Console()", program);
        Assert.Contains(".WriteTo.Sink(new JsonLineLogSink(observability))", program);

        var index = Read("src/UpdateAgent.LabManager/wwwroot/index.html");
        Assert.Contains("detail-timeline", index);
        var appJs = Read("src/UpdateAgent.LabManager/wwwroot/app.js");
        Assert.Contains("renderTimeline", appJs);
        Assert.Contains("타임라인 기록이 ${ABSENT}", appJs);
        Assert.Contains("상관관계 ID", appJs);

        var worklog = Read("docs/WORKLOG.md");
        var heading = "## 2026-11-28 개선 4 — 작업 타임라인·관측 강화";
        var start = worklog.IndexOf(heading, StringComparison.Ordinal);
        var next = worklog.IndexOf("## 2026-11-27 개선 3", StringComparison.Ordinal);
        Assert.True(start >= 0 && next > start, "개선 4는 개선 3 위에 있어야 합니다.");
        var section = worklog[start..next];
        var tableStart = section.IndexOf("| 회차 |", StringComparison.Ordinal);
        var tableEnd = section.IndexOf("\n\n", tableStart, StringComparison.Ordinal);
        Assert.True(tableStart >= 0 && tableEnd > tableStart);
        var table = section[tableStart..tableEnd];
        Assert.Equal(10, Regex.Matches(table, @"^\| \d+ \|", RegexOptions.Multiline).Count);

        var previous = -1;
        foreach (var dimension in new[]
        {
            "요구사항 정합성", "DTO/DB 마이그레이션 호환성", "상관관계 ID 전파",
            "전이 완전성/멱등성", "리포트 절단/누락 정직성", "로그/리포트/UI 비밀값",
            "동시성/파일 로그 안전성", "한국어 문서/UI 정합성", "회귀 범위", "최종 검증",
        })
        {
            var position = table.IndexOf(dimension, StringComparison.Ordinal);
            Assert.True(position > previous, $"회차 순서가 올바르지 않습니다: {dimension}");
            previous = position;
        }

        var deployment = Read("docs/DEPLOYMENT.md");
        foreach (var keyword in new[]
        {
            "8-E. 작업 타임라인·관측", "상관관계 ID", "타임라인",
            "/api/admin/reports/jobs", "truncated", "omittedCount",
            "observability.jsonl", "256 KiB", "5개", "없음", "확인 불가",
        })
        {
            Assert.Contains(keyword, deployment);
        }
    }

    [Fact]
    public void NewObservabilityCode_DoesNotContainTokenConfigurationNames()
    {
        foreach (var relativePath in new[]
        {
            "src/UpdateAgent.LabManager/Observability/ObservabilityEvent.cs",
            "src/UpdateAgent.LabManager/Observability/JsonLineLogWriter.cs",
            "src/UpdateAgent.LabManager/Observability/JsonLineLogSink.cs",
            "src/UpdateAgent.LabManager/Services/JobReportBuilder.cs",
            "src/UpdateAgent.LabManager/Models/ReportContracts.cs",
        })
        {
            var text = Read(relativePath);
            Assert.DoesNotContain("AdminToken", text);
            Assert.DoesNotContain("AgentToken", text);
            Assert.DoesNotMatch(@"(?i)(password|passwd|pwd)\s*[:=]", text);
        }
    }

    private static JobStartService CreateStartService(
        LabStore store,
        JsonLineLogWriter? observability = null) =>
        new(store, new AgentPolicyService(store), new LiveEventBus(), observability);

    private static StartJobsRequest Request(
        IEnumerable<string> agentIds,
        UpdatePolicy? policy = null,
        string? correlationId = null) =>
        new()
        {
            AgentIds = agentIds.ToList(),
            Policy = policy,
            CorrelationId = correlationId,
        };

    private static async Task AddAgentAsync(LabStore store, string id)
    {
        await store.UpsertAgentAsync(
            new AgentInfo { AgentId = id, MachineName = id.ToUpperInvariant() },
            new AgentTelemetry { AgentId = id });
    }

    private static async Task CheckInAsync(
        LabStore store,
        string jobId,
        UpdateJobState state)
    {
        var now = UtcTimestamps.Now;
        var summary = new JobSummary
        {
            JobId = jobId,
            AgentId = "pc-1",
            State = state,
            Phase = state.ToString(),
            Message = "token=raw-secret",
            StartedAt = UtcNoon,
            UpdatedAt = now,
            ReportedAt = now,
        };
        var result = await AgentCheckInHandler.ProcessAsync(
            new AgentCheckInRequest
            {
                Agent = new AgentInfo { AgentId = "pc-1", MachineName = "PC-1" },
                Telemetry = new AgentTelemetry { AgentId = "pc-1", CurrentJob = summary },
            },
            store,
            new AgentPolicyService(store),
            new LiveEventBus(),
            new CheckinGateRegistry(),
            default);
        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
    }

    private static string NewTempRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "UpdateAgentImprovement04", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task WithStoreAsync(Func<LabStore, string, Task> action)
    {
        var root = NewTempRoot();
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
