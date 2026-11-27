using Microsoft.Extensions.Logging.Abstractions;
using UpdateAgent.Core.Maestro;
using UpdateAgent.Core.Orchestration;
using UpdateAgent.Core.Preflight;
using UpdateAgent.Core.Reboot;
using UpdateAgent.Core.Sessions;
using UpdateAgent.Core.Storage;
using UpdateAgent.Core.Windows;
using UpdateAgent.Core.Windows.Update;
using UpdateAgent.Guards;
using UpdateAgent.Shared.Models;

namespace UpdateAgent.Tests;

/// <summary>3차 감사에서 확인한 Core 일시중지 시간 계산 결함의 회귀 테스트.</summary>
public sealed class Audit50Round3CoreTests
{
    private static readonly OsInfo Windows11 =
        WindowsOsInfoProvider.Create("Windows 11 Pro", "Professional", "25H2", 26200);

    [Fact]
    public async Task MaxPauseHours_MaxIntKeepsJobPausedInsteadOfFailing()
    {
        var sessions = new FakeSessions { Result = UserSessionProbeResult.Failed("WTS 확인 실패") };
        await using var runner = CreateRunner(new FakeStore(), sessions: sessions, dryRun: true);
        await runner.InitializeAsync();

        var result = await runner.StartAsync(
            UpdatePolicy.Default with
            {
                RequireMaestroManualConfirmation = false,
                PauseOnUserLogon = true,
                MaxPauseHours = int.MaxValue,
            },
            maestroConfirmed: false);

        Assert.True(result.Started);
        await WaitUntilAsync(() =>
            runner.Current?.Summary.State == UpdateJobState.Paused &&
            runner.Current.Summary.PauseReason == PauseReason.UserLoggedOn);
        await Task.Delay(100);

        Assert.Equal(UpdateJobState.Paused, runner.Current!.Summary.State);
        Assert.Equal(PauseReason.UserLoggedOn, runner.Current.Summary.PauseReason);
        Assert.Null(runner.Current.Summary.LastError);
    }

    [Fact]
    public async Task ResumeOfPausedJob_DoesNotExtendPauseUntilByAnotherMaxPauseHours()
    {
        var pauseStart = UtcClock.Now.AddMinutes(-30);
        var pauseUntil = pauseStart.AddHours(1);
        var store = new FakeStore
        {
            Detail = Detail(
                state: UpdateJobState.Paused,
                pauseReason: PauseReason.UserLoggedOn,
                pauseUntil: pauseUntil,
                policy: UpdatePolicy.Default with
                {
                    RequireMaestroManualConfirmation = false,
                    PauseOnUserLogon = true,
                    MaxPauseHours = 1,
                }),
        };
        var sessions = new FakeSessions { Result = UserSessionProbeResult.Failed("WTS 확인 실패") };
        await using var runner = CreateRunner(store, sessions: sessions, dryRun: true);
        await runner.InitializeAsync();

        await runner.ResumeIfNeededAsync();
        await WaitUntilAsync(() => store.Saved.Count >= 2 && store.Saved[^1].Summary.PauseUntil.HasValue);

        var actual = store.Saved[^1].Summary.PauseUntil!.Value;
        Assert.InRange(actual, pauseUntil.AddMinutes(-1), pauseUntil.AddMinutes(1));
    }

    private static UpdateJobRunner CreateRunner(
        IJobStore store,
        IUserSessionProbe? sessions = null,
        bool dryRun = true) => new(
            new FakeWua(),
            store,
            new FakePreflight(),
            new FakeMaestro(),
            new FakeReboot(),
            sessions ?? new FakeSessions(),
            NullJobProgressSink.Instance,
            NullLogger<UpdateJobRunner>.Instance,
            new UpdateJobRunnerOptions
            {
                AgentId = "agent-round3",
                AgentName = "Round3",
                DryRun = dryRun,
                OsOverride = Windows11,
            });

    private static JobDetail Detail(
        UpdateJobState state = UpdateJobState.Pending,
        UpdatePolicy? policy = null,
        PauseReason pauseReason = PauseReason.None,
        DateTimeOffset? pauseUntil = null) => new()
    {
        Summary = new JobSummary
        {
            JobId = "job-round3",
            AgentId = "agent-round3",
            State = state,
            PauseReason = pauseReason,
            PauseUntil = pauseUntil,
            StartedAt = UtcClock.Now.AddMinutes(-1),
            UpdatedAt = UtcClock.Now,
        },
        Policy = policy ?? UpdatePolicy.Default,
        Items = [new JobItem { UpdateId = "wu-round3", State = UpdateItemState.Pending }],
    };

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = UtcClock.Now + (timeout ?? TimeSpan.FromSeconds(5));
        while (UtcClock.Now < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }

        throw new TimeoutException("조건이 제한 시간 내에 충족되지 않았습니다.");
    }

    private sealed class FakeWua : IWuaClient
    {
        public Task<WuaScanResult> ScanAsync(bool includeDrivers, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WuaScanResult());

        public Task<WuaOperationResult> DownloadAsync(
            WuaUpdate update, CancellationToken cancellationToken = default) =>
            Task.FromResult(WuaOperationResult.Ok());

        public Task<WuaInstallResult> InstallAsync(
            WuaUpdate update, CancellationToken cancellationToken = default) =>
            Task.FromResult(WuaInstallResult.Ok(false));

        public Task<WuaSystemStatus> GetSystemStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new WuaSystemStatus());

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
        public string Name => "Round3";

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
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new RestoreMarker
            {
                JobId = jobId,
                AgentId = agentId,
                Nonce = Guid.NewGuid().ToString("N"),
                BootCount = bootCount,
                Phase = phase,
            });

        public Task<MarkerVerificationResult> VerifyAfterRebootAsync(
            string jobId, CancellationToken cancellationToken = default) =>
            Task.FromResult(MarkerVerificationResult.Ok(
                new RestoreMarker
                {
                    JobId = jobId,
                    AgentId = "agent-round3",
                    Nonce = "round3",
                }));
    }

    private sealed class FakeReboot : IRebootManager
    {
        public Task ScheduleRebootAsync(
            int delaySeconds, string reason, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task CancelRebootAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ScheduleShutdownAsync(
            int delaySeconds, string reason, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public bool IsRebootPending() => false;

        public RebootPendingProbeResult ProbeRebootPending() => RebootPendingProbeResult.Ok(false);
    }

    private sealed class FakeSessions : IUserSessionProbe
    {
        public UserSessionProbeResult Result { get; set; } = UserSessionProbeResult.Success([]);

        public IReadOnlyList<InteractiveSession> GetInteractiveSessions() => Result.Sessions;

        public bool HasInteractiveUser() => Result.ProbeFailed || Result.Sessions.Count > 0;

        public UserSessionProbeResult Probe() => Result;
    }
}
