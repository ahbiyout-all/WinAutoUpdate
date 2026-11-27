using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UpdateAgent.Core.Maestro;
using UpdateAgent.Core.Orchestration;
using UpdateAgent.Core.Preflight;
using UpdateAgent.Core.Reboot;
using UpdateAgent.Core.Sessions;
using UpdateAgent.Core.Storage;
using UpdateAgent.Core.Windows;
using UpdateAgent.Core.Windows.Update;
using UpdateAgent.Guards;
using UpdateAgent.Service;
using UpdateAgent.Service.Commands;
using UpdateAgent.Service.Comms;
using UpdateAgent.Service.Configuration;
using UpdateAgent.Service.Sessions;
using UpdateAgent.Shared;
using UpdateAgent.Shared.Models;
using UpdateAgent.Shared.Serialization;

namespace UpdateAgent.Tests;

/// <summary>2차 감사에서 확인한 Core/Shared/Service 결함의 회귀 테스트.</summary>
public sealed class Audit50Round2CoreTests
{
    private static readonly OsInfo Windows11 =
        WindowsOsInfoProvider.Create("Windows 11 Pro", "Professional", "25H2", 26200);

    [Fact]
    public async Task Resume_RequeuesDownloadingAndInstallingItemsBeforeRunning()
    {
        var store = new FakeStore
        {
            Detail = Detail(state: UpdateJobState.Installing) with
            {
                Items =
                [
                    Item("wu-downloading", UpdateItemState.Downloading),
                    Item("wu-installing", UpdateItemState.Installing),
                ],
            },
        };
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wua = new FakeWua
        {
            ScanHandler = async (_, token) =>
            {
                await release.Task.WaitAsync(token);
                return new WuaScanResult();
            },
        };
        await using var runner = CreateRunner(store, wua);
        await runner.InitializeAsync();

        try
        {
            var resumed = await runner.ResumeIfNeededAsync();

            Assert.NotNull(resumed);
            Assert.Equal(UpdateJobState.Pending, resumed!.State);
            var firstSave = store.Saved[0];
            Assert.All(firstSave.Items, item => Assert.Equal(UpdateItemState.Pending, item.State));
            Assert.All(firstSave.Items, item => Assert.Null(item.CompletedAt));
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public void MergeItems_DisappearingInstallingItemIsNeverReportedAsInstalled()
    {
        var store = new FakeStore();
        var runner = CreateRunner(store);
        var detail = Detail(state: UpdateJobState.Installing) with
        {
            Items = [Item("wu-vanished", UpdateItemState.Installing)],
        };

        var contextType = typeof(UpdateJobRunner).GetNestedType("JobContext", BindingFlags.NonPublic)!;
        var context = contextType
            .GetMethod("FromDetail", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, [detail]);
        var merge = typeof(UpdateJobRunner).GetMethod("MergeItems", BindingFlags.NonPublic | BindingFlags.Instance)!;
        merge.Invoke(runner, [context, Array.Empty<UpdateItemInfo>()]);

        var items = (List<JobItem>)contextType.GetProperty("Items")!.GetValue(context)!;
        var item = Assert.Single(items);
        Assert.NotEqual(UpdateItemState.Installed, item.State);
        Assert.Contains("확인할 수 없습니다", item.Error);
    }

    [Fact]
    public async Task PassesWithoutProgress_SurvivesJsonStoreAndRunnerRoundTrip()
    {
        var detail = Detail(passesWithoutProgress: 3) with
        {
            Items = [Item("wu-1", UpdateItemState.Installed)],
        };

        var restored = UpdateAgentJson.Deserialize<JobDetail>(UpdateAgentJson.Serialize(detail));
        Assert.NotNull(restored);
        Assert.Equal(3, restored!.Summary.PassesWithoutProgress);

        var root = Path.Combine(Path.GetTempPath(), "UpdateAgentRound2", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "round2.db");
        try
        {
            var store = new SqliteJobStore(database);
            await store.InitializeAsync();
            await store.SaveJobAsync(detail);
            var fromStore = await store.GetJobAsync(detail.Summary.JobId);
            Assert.NotNull(fromStore);
            Assert.Equal(3, fromStore!.Summary.PassesWithoutProgress);

            var runnerStore = new FakeStore { Detail = detail };
            await using var runner = CreateRunner(runnerStore);
            await runner.InitializeAsync();
            var resumed = await runner.ResumeIfNeededAsync();

            Assert.NotNull(resumed);
            Assert.Equal(3, resumed!.PassesWithoutProgress);
            Assert.Equal(3, runner.Current!.Summary.PassesWithoutProgress);
            await WaitUntilAsync(() => runner.Current.Summary.IsTerminal == true);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MaxRuntimeHours_MaxIntDoesNotFailJob()
    {
        await using var runner = CreateRunner(new FakeStore());
        await runner.InitializeAsync();

        await runner.StartAsync(
            UpdatePolicy.Default with
            {
                RequireMaestroManualConfirmation = false,
                MaxRuntimeHours = int.MaxValue,
            },
            maestroConfirmed: false);
        await WaitUntilAsync(() => runner.Current?.Summary.IsTerminal == true);

        Assert.Equal(UpdateJobState.Completed, runner.Current!.Summary.State);
    }

    [Fact]
    public async Task RuntimeLimit_ElapsedBeyondPolicyStillFails()
    {
        var store = new FakeStore
        {
            Detail = Detail(
                state: UpdateJobState.Pending,
                policy: UpdatePolicy.Default with { MaxRuntimeHours = 1, RequireMaestroManualConfirmation = false },
                startedAt: UtcClock.Now.AddHours(-2)),
        };
        await using var runner = CreateRunner(store);
        await runner.InitializeAsync();

        await runner.ResumeIfNeededAsync();
        await WaitUntilAsync(() => runner.Current?.Summary.IsTerminal == true);

        Assert.Equal(UpdateJobState.Failed, runner.Current!.Summary.State);
        Assert.Contains("작업 제한 시간", runner.Current.Summary.LastError);
    }

    [Fact]
    public async Task Maestro_CorruptChecklistFailsClosedWithoutOverwritingFile()
    {
        var paths = CreateMaestroPaths();
        try
        {
            var provider = new ManualMaestroProvider(paths.Root);
            await provider.ConfirmProtectionSuspendedAsync("job-1", "teacher");
            await provider.ConfirmProtectionSuspendedAsync("job-2", "teacher");
            var checklistPath = Path.Combine(paths.Root, "maestro-checklist.json");
            var validJson = await File.ReadAllTextAsync(checklistPath);
            await File.WriteAllTextAsync(checklistPath, "{not-json");

            await Assert.ThrowsAnyAsync<Exception>(() =>
                provider.ConfirmProtectionSuspendedAsync("job-3", "teacher"));
            Assert.Equal("{not-json", await File.ReadAllTextAsync(checklistPath));
            await Assert.ThrowsAnyAsync<Exception>(() => provider.GetChecklistAsync("job-1"));

            // 정상 파일에서는 다른 작업의 확인이 보존된다.
            await File.WriteAllTextAsync(checklistPath, validJson);
            await provider.ConfirmProtectionSuspendedAsync("job-3", "teacher");
            var all = UpdateAgentJson.Deserialize<Dictionary<string, MaestroChecklist>>(
                await File.ReadAllTextAsync(checklistPath));
            Assert.NotNull(all);
            Assert.True(all!["job-2"].ProtectionSuspendedConfirmed);
            Assert.True(all["job-3"].ProtectionSuspendedConfirmed);
        }
        finally
        {
            CleanupMaestroPaths(paths);
        }
    }

    [Fact]
    public async Task Maestro_SecondaryMarkerWriteFailurePropagates()
    {
        var paths = CreateMaestroPaths();
        try
        {
            // 파일 경로에 디렉터리를 만들어 쓰기 실패를 강제한다.
            Directory.CreateDirectory(Path.Combine(paths.StableRoot, "stable-resume-marker.json"));
            var provider = new ManualMaestroProvider(paths.Root);

            await Assert.ThrowsAnyAsync<Exception>(() =>
                provider.WriteResumeMarkerAsync("job-1", "agent-1", 1, "before-reboot"));
        }
        finally
        {
            CleanupMaestroPaths(paths);
        }
    }

    [Fact]
    public async Task Maestro_CorruptSecondaryMarkerFailsVerification()
    {
        var paths = CreateMaestroPaths();
        try
        {
            var provider = new ManualMaestroProvider(paths.Root);
            await provider.WriteResumeMarkerAsync("job-1", "agent-1", 1, "before-reboot");
            await File.WriteAllTextAsync(
                Path.Combine(paths.StableRoot, "stable-resume-marker.json"), "{not-json");

            var result = await provider.VerifyAfterRebootAsync("job-1");

            Assert.False(result.Verified);
            Assert.Contains("보조 마커", result.Message);
        }
        finally
        {
            CleanupMaestroPaths(paths);
        }
    }

    [Fact]
    public void RebootPendingProbe_ReportsFailureWithoutBreakingLegacyCallers()
    {
        var failed = RebootPendingProbeResult.Failed(
            "재부팅 대기 상태를 확인할 수 없습니다: 레지스트리 접근 실패");
        Assert.Null(failed.Pending);
        Assert.True(failed.ProbeFailed);
        Assert.Contains("확인할 수 없습니다", failed.Error);

        var ok = RebootPendingProbeResult.Ok(true);
        Assert.True(ok.Pending);
        Assert.False(ok.ProbeFailed);

        IRebootManager legacy = new LegacyRebootManager();
        Assert.False(legacy.IsRebootPending());
        var probe = legacy.ProbeRebootPending();
        Assert.False(probe.ProbeFailed);
        Assert.False(probe.Pending);
    }

    [Fact]
    public void UserSessionProbeResult_DistinguishesFailureFromNoSessions()
    {
        var failed = UserSessionProbeResult.Failed("WTS 조회 실패");
        Assert.True(failed.ProbeFailed);
        Assert.Empty(failed.Sessions);

        IUserSessionProbe legacy = new LegacySessionProbe();
        var probe = legacy.Probe();
        Assert.False(probe.ProbeFailed);
        Assert.Empty(probe.Sessions);
    }

    [Fact]
    public async Task Runner_PauseOnUserLogon_FailsClosedWhenSessionProbeFails()
    {
        var sessions = new FakeSessions
        {
            Result = UserSessionProbeResult.Failed("WTS 세션을 확인할 수 없습니다"),
        };
        var wua = new FakeWua
        {
            ScanHandler = (_, _) => Task.FromResult(new WuaScanResult
            {
                Updates = [Update("wu-1")],
            }),
        };
        var policy = UpdatePolicy.Default with
        {
            RequireMaestroManualConfirmation = false,
            PauseOnUserLogon = true,
        };
        await using var runner = CreateRunner(new FakeStore(), wua, sessions: sessions);
        await runner.InitializeAsync();

        await runner.StartAsync(policy, maestroConfirmed: false);
        await WaitUntilAsync(() => runner.Current?.Summary.State == UpdateJobState.Paused
            && runner.Current.Summary.PauseReason == PauseReason.UserLoggedOn);
        await Task.Delay(200);

        Assert.Equal(UpdateJobState.Paused, runner.Current!.Summary.State);
        Assert.Equal(PauseReason.UserLoggedOn, runner.Current.Summary.PauseReason);

        sessions.Result = UserSessionProbeResult.Success([]);
        await WaitUntilAsync(() => runner.Current!.Summary.IsTerminal == true, TimeSpan.FromSeconds(15));
        Assert.Equal(UpdateJobState.Completed, runner.Current.Summary.State);
    }

    [Fact]
    public async Task NonUserPauseReason_StaysPausedUntilExplicitResume()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scanCount = 0;
        var wua = new FakeWua
        {
            ScanHandler = async (_, token) =>
            {
                if (Interlocked.Increment(ref scanCount) == 1)
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(token);
                    return new WuaScanResult { Updates = [Update("wu-1")] };
                }

                return new WuaScanResult();
            },
        };
        await using var runner = CreateRunner(new FakeStore(), wua);
        await runner.InitializeAsync();
        await runner.StartAsync(NonManualPolicy(), maestroConfirmed: false);

        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await runner.PauseAsync(PauseReason.WuBusy);
            release.TrySetResult();
            await WaitUntilAsync(() => runner.Current?.Summary.State == UpdateJobState.Paused
                && runner.Current.Summary.PauseReason == PauseReason.WuBusy);
            await Task.Delay(200);

            Assert.Equal(UpdateJobState.Paused, runner.Current!.Summary.State);
            Assert.Equal(PauseReason.WuBusy, runner.Current.Summary.PauseReason);

            await runner.ResumeFromPauseAsync();
            await WaitUntilAsync(() => runner.Current?.Summary.IsTerminal == true, TimeSpan.FromSeconds(15));
            Assert.Equal(UpdateJobState.Completed, runner.Current.Summary.State);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task DisposeAsync_LeavesJobResumableInsteadOfTeacherCancelled()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new FakeStore();
        var wua = new FakeWua
        {
            ScanHandler = async (_, token) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                return new WuaScanResult();
            },
        };
        var runner = CreateRunner(store, wua);
        await runner.InitializeAsync();
        await runner.StartAsync(NonManualPolicy(), maestroConfirmed: false);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await runner.DisposeAsync();
        release.TrySetResult();

        var last = store.Saved[^1].Summary;
        Assert.Contains(last.State, new[] { UpdateJobState.Pending, UpdateJobState.Paused });
        Assert.DoesNotContain("교사가 작업을 취소했습니다.", last.Message);
        Assert.Contains("다시 시작하면", last.Message);
    }

    [Theory]
    [InlineData(0, 900, 300)]
    [InlineData(3, 1000, 300)]
    [InlineData(0, 45, 45)]
    [InlineData(2, 60, 240)]
    public void CheckInDelay_CapsBaseAtPolicyMaximum(
        int failures,
        int pollSeconds,
        int expected)
    {
        Assert.Equal(expected, Worker.ResolveCheckInDelaySeconds(failures, pollSeconds));
    }

    [Theory]
    [InlineData(300, true)]
    [InlineData(301, false)]
    [InlineData(1200, false)]
    public void Validator_CapsPollIntervalAt300Seconds(int pollSeconds, bool valid)
    {
        var options = new AgentOptions
        {
            LabManagerUrl = "http://localhost:5080",
            AgentToken = "audit-round2-token",
            PollIntervalSeconds = pollSeconds,
        };

        Assert.Equal(valid, new AgentOptionsValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void TelemetryLastError_PrefersOfflineRecoveryMessage()
    {
        const string recovery = "오프라인 복구: 이전 체크인 실패 3회";

        Assert.Equal(recovery, Worker.ResolveTelemetryLastError("이전 작업 오류", recovery));
        Assert.Equal("이전 작업 오류", Worker.ResolveTelemetryLastError("이전 작업 오류", "  "));
        Assert.Null(Worker.ResolveTelemetryLastError(null, null));
    }

    [Fact]
    public async Task Worker_TelemetryMarksProbeFailuresAndKeepsRecoveryMessage()
    {
        const string recovery = "오프라인 복구: 이전 체크인 실패 1회";
        var store = new FakeStore();
        var runner = CreateRunner(store);
        SetField(runner, "_os", Windows11);
        SetField(runner, "_current", Detail(state: UpdateJobState.Pending) with
        {
            Summary = Detail(state: UpdateJobState.Pending).Summary with { LastError = "오래된 작업 오류" },
        });

        var labManager = new LabManagerClient(null!, StubOptions(new AgentOptions()), NullLogger<LabManagerClient>.Instance);
        SetMember(labManager, "LastError", recovery);
        var commands = new CommandExecutor(
            null!, null!, null!, store, new ServerPolicyProvider(store), NullLogger<CommandExecutor>.Instance);
        var sessions = new FakeSessions { Result = UserSessionProbeResult.Failed("WTS 조회 실패") };
        var reboot = new FakeReboot { Probe = RebootPendingProbeResult.Failed("레지스트리 접근 실패") };
        var worker = new Worker(
            runner,
            new FakeWua(),
            store,
            new FakeMaestro(),
            sessions,
            reboot,
            null!,
            null!,
            null!,
            null!,
            labManager,
            commands,
            StubOptions(new AgentOptions()),
            NullLogger<Worker>.Instance);

        var method = typeof(Worker).GetMethod("BuildCheckInAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var task = (Task<AgentCheckInRequest>)method.Invoke(worker, [CancellationToken.None])!;
        var request = await task;

        Assert.True(request.Telemetry.UserSessionProbeFailed);
        Assert.False(request.Telemetry.UserLoggedOn);
        Assert.True(request.Telemetry.RebootPendingProbeFailed);
        Assert.False(request.Telemetry.RebootPending);
        Assert.Equal(recovery, request.Telemetry.LastError);
    }

    [Fact]
    public void SessionWatcher_ProbeFailureKeepsExistingHelperState()
    {
        var watcher = new SessionWatcherService(
            new FakeSessions { Result = UserSessionProbeResult.Failed("WTS 조회 실패") },
            new SessionLauncher(),
            StubOptions(new AgentOptions { OverlayEnabled = true }),
            NullLogger<SessionWatcherService>.Instance);
        var launched = new Dictionary<int, DateTimeOffset> { [7] = DateTimeOffset.Now };
        SetField(watcher, "_launched", launched);

        var method = typeof(SessionWatcherService)
            .GetMethod("EnsureHelpers", BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(watcher, null);

        Assert.True(launched.ContainsKey(7));
    }

    [Fact]
    public void Preflight_WuaStatusUsesIsBusyAndFailsClosedOnLastError()
    {
        var busy = WindowsPreflightChecker.EvaluateWuaStatus(
            new WuaSystemStatus { IsWuServiceRunning = true });
        Assert.Equal(PreflightStatus.Block, busy.Status);
        Assert.Contains("진행 중", busy.Message);

        var unknown = WindowsPreflightChecker.EvaluateWuaStatus(
            new WuaSystemStatus { LastError = "WUA 상태 조회 실패" });
        Assert.Equal(PreflightStatus.Block, unknown.Status);
        Assert.Contains("확인할 수 없습니다", unknown.Message);
        Assert.DoesNotContain("다른 설치 작업이 없습니다.", unknown.Message);

        var reboot = WindowsPreflightChecker.EvaluateRebootStatus(
            new WuaSystemStatus { LastError = "WUA 상태 조회 실패" });
        Assert.Equal(PreflightStatus.Warn, reboot.Status);
        Assert.Contains("확인할 수 없습니다", reboot.Message);
    }

    private static UpdatePolicy NonManualPolicy() =>
        UpdatePolicy.Default with { RequireMaestroManualConfirmation = false };

    private static WuaUpdate Update(string id) => new()
    {
        Info = new UpdateItemInfo
        {
            UpdateId = id,
            Title = $"Round2 update {id}",
            Category = UpdateCategory.Security,
        },
    };

    private static JobItem Item(string id, UpdateItemState state) => new()
    {
        UpdateId = id,
        KbArticleIds = id,
        State = state,
    };

    private static JobDetail Detail(
        string jobId = "job-round2",
        UpdateJobState state = UpdateJobState.Pending,
        UpdatePolicy? policy = null,
        DateTimeOffset? startedAt = null,
        int passesWithoutProgress = 0) => new()
    {
        Summary = new JobSummary
        {
            JobId = jobId,
            AgentId = "agent-round2",
            AgentName = "PC-ROUND2",
            State = state,
            IsDryRun = true,
            StartedAt = startedAt ?? UtcClock.Now,
            UpdatedAt = UtcClock.Now,
            PassesWithoutProgress = passesWithoutProgress,
        },
        Policy = policy ?? UpdatePolicy.Default,
    };

    private static UpdateJobRunner CreateRunner(
        FakeStore store,
        FakeWua? wua = null,
        FakeMaestro? maestro = null,
        FakeReboot? reboot = null,
        FakeSessions? sessions = null) => new(
            wua ?? new FakeWua(),
            store,
            new FakePreflight(),
            maestro ?? new FakeMaestro(),
            reboot ?? new FakeReboot(),
            sessions ?? new FakeSessions(),
            NullJobProgressSink.Instance,
            NullLogger<UpdateJobRunner>.Instance,
            new UpdateJobRunnerOptions
            {
                AgentId = "agent-round2",
                OsOverride = Windows11,
                DryRun = true,
            });

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }

        throw new TimeoutException("작업이 제한 시간 내에 종료되지 않았습니다.");
    }

    private static (string Root, string StableRoot, string? Original) CreateMaestroPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "UpdateAgentRound2", Guid.NewGuid().ToString("N"));
        var stableRoot = Path.Combine(root, "stable");
        var original = Environment.GetEnvironmentVariable(UpdateAgentConstants.UnprotectedStatePathVariable);
        Environment.SetEnvironmentVariable(
            UpdateAgentConstants.UnprotectedStatePathVariable, stableRoot);
        return (root, stableRoot, original);
    }

    private static void CleanupMaestroPaths((string Root, string StableRoot, string? Original) paths)
    {
        Environment.SetEnvironmentVariable(UpdateAgentConstants.UnprotectedStatePathVariable, paths.Original);
        if (Directory.Exists(paths.Root)) Directory.Delete(paths.Root, recursive: true);
    }

    private static void SetField(object target, string name, object? value) => target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
        .SetValue(target, value);

    private static void SetMember(object target, string name, object? value)
    {
        var type = target.GetType();
        var field = type.GetField($"<{name}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field is not null)
        {
            field.SetValue(target, value);
            return;
        }

        type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public)!.SetValue(target, value);
    }

    private static IOptionsMonitor<T> StubOptions<T>(T value) => new StubOptionsMonitor<T>(value);

    private sealed class FakeWua : IWuaClient
    {
        public Func<bool, CancellationToken, Task<WuaScanResult>>? ScanHandler { get; init; }
        public Func<CancellationToken, Task<WuaSystemStatus>>? StatusHandler { get; init; }

        public Task<WuaScanResult> ScanAsync(bool includeDrivers, CancellationToken cancellationToken = default) =>
            ScanHandler?.Invoke(includeDrivers, cancellationToken) ?? Task.FromResult(new WuaScanResult());

        public Task<WuaOperationResult> DownloadAsync(
            WuaUpdate update, CancellationToken cancellationToken = default) =>
            Task.FromResult(WuaOperationResult.Ok());

        public Task<WuaInstallResult> InstallAsync(
            WuaUpdate update, CancellationToken cancellationToken = default) =>
            Task.FromResult(WuaInstallResult.Ok(false));

        public Task<WuaSystemStatus> GetSystemStatusAsync(CancellationToken cancellationToken = default) =>
            StatusHandler?.Invoke(cancellationToken) ?? Task.FromResult(new WuaSystemStatus());

        public Task<bool> IsBusyAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class FakeStore : IJobStore
    {
        private readonly object _gate = new();

        public JobDetail? Detail { get; set; }
        public List<JobDetail> Saved { get; } = [];

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveJobAsync(JobDetail detail, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                Detail = detail;
                Saved.Add(detail);
            }

            return Task.CompletedTask;
        }

        public Task<JobDetail?> GetJobAsync(string jobId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Detail);

        public Task<JobDetail?> GetActiveJobAsync(string agentId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Detail is { Summary.IsTerminal: false } ? Detail : null);

        public Task<IReadOnlyList<JobSummary>> GetRecentJobsAsync(
            string agentId, int limit = 20, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<JobSummary>>([]);

        public Task AppendEventAsync(
            string jobId, string level, string message, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<JobEventRecord>> GetEventsAsync(
            string jobId, int limit = 200, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<JobEventRecord>>([]);

        public Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task SetSettingAsync(
            string key, string value, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FakePreflight : IPreflightChecker
    {
        public Task<PreflightResult> CheckAsync(
            UpdatePolicy policy,
            OsInfo os,
            MaestroDetectionResult maestro,
            bool maestroConfirmed,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new PreflightResult
            {
                Checks = [PreflightCheck.Pass("테스트", "통과")],
            });
    }

    private sealed class FakeMaestro : IRestorationProvider
    {
        public string Name => "Round2";

        public Task<MaestroDetectionResult> DetectAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(MaestroDetectionResult.NotInstalled);

        public Task<MaestroChecklist> GetChecklistAsync(
            string jobId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MaestroChecklist { JobId = jobId });

        public Task<MaestroChecklist> ConfirmProtectionSuspendedAsync(
            string jobId, string confirmedBy, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MaestroChecklist { JobId = jobId });

        public Task<MaestroChecklist> ConfirmRestoredAsync(
            string jobId,
            bool stateSaved,
            bool protectionRestored,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new MaestroChecklist { JobId = jobId });

        public Task<RestoreMarker> WriteResumeMarkerAsync(
            string jobId,
            string agentId,
            int bootCount,
            string phase,
            CancellationToken cancellationToken = default) => Task.FromResult(new RestoreMarker
            {
                JobId = jobId,
                AgentId = agentId,
                BootCount = bootCount,
                Phase = phase,
                Nonce = Guid.NewGuid().ToString("N"),
            });

        public Task<MarkerVerificationResult> VerifyAfterRebootAsync(
            string jobId, CancellationToken cancellationToken = default) =>
            Task.FromResult(MarkerVerificationResult.Ok(new RestoreMarker
            {
                JobId = jobId,
                AgentId = "agent-round2",
                Nonce = "round2",
            }));
    }

    private sealed class FakeReboot : IRebootManager
    {
        public RebootPendingProbeResult Probe { get; init; } = RebootPendingProbeResult.Ok(false);
        public int RebootCount;
        public int ShutdownCount;

        public Task ScheduleRebootAsync(
            int delaySeconds, string reason, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref RebootCount);
            return Task.CompletedTask;
        }

        public Task CancelRebootAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ScheduleShutdownAsync(
            int delaySeconds, string reason, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref ShutdownCount);
            return Task.CompletedTask;
        }

        public bool IsRebootPending() => Probe.Pending ?? false;

        public RebootPendingProbeResult ProbeRebootPending() => Probe;
    }

    private sealed class FakeSessions : IUserSessionProbe
    {
        public UserSessionProbeResult Result { get; set; } = UserSessionProbeResult.Success([]);

        public IReadOnlyList<InteractiveSession> GetInteractiveSessions() => Result.Sessions;

        public bool HasInteractiveUser() => Result.ProbeFailed || Result.Sessions.Count > 0;

        public UserSessionProbeResult Probe() => Result;
    }

    private sealed class LegacyRebootManager : IRebootManager
    {
        public Task ScheduleRebootAsync(
            int delaySeconds, string reason, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task CancelRebootAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ScheduleShutdownAsync(
            int delaySeconds, string reason, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public bool IsRebootPending() => false;
    }

    private sealed class LegacySessionProbe : IUserSessionProbe
    {
        public IReadOnlyList<InteractiveSession> GetInteractiveSessions() => [];

        public bool HasInteractiveUser() => false;
    }

    private sealed class StubOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public T GetCurrentValue(string? name) => value;
        public IDisposable OnChange(Action<T, string?> listener) => new NoopDisposable();

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }
}
