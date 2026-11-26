using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using UpdateAgent.Core.Maestro;
using UpdateAgent.Core.Orchestration;
using UpdateAgent.Core.Preflight;
using UpdateAgent.Core.Reboot;
using UpdateAgent.Core.Sessions;
using UpdateAgent.Core.Storage;
using UpdateAgent.Core.Windows;
using UpdateAgent.Core.Windows.Update;
using UpdateAgent.Shared;
using UpdateAgent.Shared.Models;
using UpdateAgent.Shared.Serialization;
using UpdateAgent.Shared.Updates;

namespace UpdateAgent.Tests;

/// <summary>자아성찰 50회에서 확인한 Core/Shared 결함의 회귀 테스트.</summary>
public sealed class Audit50CoreTests
{
    private static readonly OsInfo Windows11 =
        WindowsOsInfoProvider.Create("Windows 11 Pro", "Professional", "25H2", 26200);

    [Fact]
    public async Task Resume_RestoresDryRunFlag()
    {
        var store = new FakeStore { Detail = Detail(state: UpdateJobState.Installing, dryRun: true) };
        await using var runner = CreateRunner(store);
        await runner.InitializeAsync();

        var resumed = await runner.ResumeIfNeededAsync();

        Assert.NotNull(resumed);
        Assert.True(resumed!.IsDryRun);
        Assert.NotNull(runner.Current);
        Assert.True(runner.Current!.Summary.IsDryRun);
    }

    [Fact]
    public async Task DryRun_DoesNotScheduleRebootFromRealWuStatus()
    {
        var wua = new FakeWua
        {
            StatusHandler = _ => Task.FromResult(new WuaSystemStatus { RebootRequired = true }),
        };
        var reboot = new FakeReboot();
        await using var runner = CreateRunner(new FakeStore(), wua, reboot: reboot);
        await runner.InitializeAsync();

        var started = await runner.StartAsync(
            UpdatePolicy.Default with { RequireMaestroManualConfirmation = false },
            maestroConfirmed: false);

        Assert.True(started.Started);
        await WaitUntilAsync(() =>
            runner.Current?.Summary.IsTerminal == true ||
            runner.Current?.Summary.State == UpdateJobState.Rebooting ||
            reboot.RebootCount > 0);
        Assert.Equal(0, reboot.RebootCount);
        Assert.Equal(UpdateJobState.Completed, runner.Current!.Summary.State);
    }

    [Fact]
    public async Task DryRun_DoesNotScheduleShutdownAfterCompletion()
    {
        var reboot = new FakeReboot();
        await using var runner = CreateRunner(new FakeStore(), reboot: reboot);
        await runner.InitializeAsync();

        await runner.StartAsync(
            UpdatePolicy.Default with
            {
                RequireMaestroManualConfirmation = false,
                ShutdownAfterComplete = true,
            },
            maestroConfirmed: false);

        await WaitUntilAsync(() => runner.Current?.Summary.IsTerminal == true);
        Assert.Equal(0, reboot.ShutdownCount);
        Assert.Equal(UpdateJobState.Completed, runner.Current!.Summary.State);
    }

    [Fact]
    public async Task ProgressSubscriberFailure_DoesNotFailCompletedJob()
    {
        await using var runner = CreateRunner(new FakeStore());
        runner.ProgressChanged += (_, _) => throw new InvalidOperationException("UI 구독자 오류");
        await runner.InitializeAsync();

        await runner.StartAsync(
            UpdatePolicy.Default with { RequireMaestroManualConfirmation = false },
            maestroConfirmed: false);

        await WaitUntilAsync(() => runner.Current?.Summary.IsTerminal == true);
        Assert.Equal(UpdateJobState.Completed, runner.Current!.Summary.State);
    }

    [Fact]
    public async Task Resume_VerificationFailure_AlsoUpdatesCurrentSummary()
    {
        var store = new FakeStore { Detail = Detail(state: UpdateJobState.Installing, rebootCount: 1) };
        var maestro = new FakeMaestro
        {
            Verification = MarkerVerificationResult.Failed("복원 마커 검증 실패"),
        };
        await using var runner = CreateRunner(store, maestro: maestro);
        await runner.InitializeAsync();

        var resumed = await runner.ResumeIfNeededAsync();

        Assert.NotNull(resumed);
        Assert.Equal(UpdateJobState.Failed, resumed!.State);
        Assert.NotNull(runner.Current);
        Assert.Equal(UpdateJobState.Failed, runner.Current!.Summary.State);
    }

    [Fact]
    public async Task Resume_NormalizesCachedPolicyBeforeRunning()
    {
        var wua = new FakeWua
        {
            ScanHandler = (_, _) => Task.FromResult(new WuaScanResult
            {
                Updates =
                [
                    new WuaUpdate
                    {
                        Info = new UpdateItemInfo
                        {
                            UpdateId = "feature-1",
                            Title = "Windows 11 26H2 feature update",
                            Category = UpdateCategory.Upgrade,
                            IsFeatureUpdate = true,
                        },
                    },
                ],
            }),
        };
        var policy = UpdatePolicy.Default with
        {
            RequireMaestroManualConfirmation = false,
            IncludeFeatureUpdates = true,
            ApprovedFeatureRelease = "26H2",
        };
        var store = new FakeStore { Detail = Detail(state: UpdateJobState.Pending, policy: policy) };
        await using var runner = CreateRunner(store, wua);
        await runner.InitializeAsync();

        await runner.ResumeIfNeededAsync();
        await WaitUntilAsync(() => runner.Current?.Summary.IsTerminal == true);

        Assert.False(runner.Current!.Policy.IncludeFeatureUpdates);
        Assert.Null(runner.Current.Policy.ApprovedFeatureRelease);
        Assert.Equal(0, runner.Current.Summary.InstalledItems);
    }

    [Fact]
    public void MaestroDetector_ProcessEvidenceMeansInstalled()
    {
        Assert.True(MaestroDetector.HasInstallationEvidence(0, 1, 0, 0));
        Assert.True(MaestroDetector.HasInstallationEvidence(0, 0, 1, 0));
        Assert.False(MaestroDetector.HasInstallationEvidence(0, 0, 0, 0));
    }

    [Fact]
    public void Preflight_DetectionFailureIsNotReportedAsNotInstalled()
    {
        var failed = WindowsPreflightChecker.EvaluateMaestro(
            MaestroDetectionResult.Failed, maestroConfirmed: true, UpdatePolicy.Default);

        Assert.Equal(PreflightStatus.Warn, failed.Status);
        Assert.Contains("확인할 수 없습니다", failed.Message);

        var notInstalled = WindowsPreflightChecker.EvaluateMaestro(
            MaestroDetectionResult.NotInstalled, maestroConfirmed: true, UpdatePolicy.Default);
        Assert.Equal(PreflightStatus.Pass, notInstalled.Status);
    }

    [Fact]
    public async Task MaestroChecklist_UsesCaseInsensitiveJobIdsAfterReload()
    {
        var root = Path.Combine(Path.GetTempPath(), "UpdateAgentTests", Guid.NewGuid().ToString("N"));
        var original = Environment.GetEnvironmentVariable(UpdateAgentConstants.UnprotectedStatePathVariable);
        Environment.SetEnvironmentVariable(
            UpdateAgentConstants.UnprotectedStatePathVariable, Path.Combine(root, "stable"));
        try
        {
            var provider = new ManualMaestroProvider(root);
            await provider.ConfirmProtectionSuspendedAsync("Job-A", "teacher");

            var reloaded = await provider.GetChecklistAsync("job-a");
            Assert.True(reloaded.ProtectionSuspendedConfirmed);

            await provider.ConfirmProtectionSuspendedAsync("JOB-A", "teacher2");
            var json = await File.ReadAllTextAsync(Path.Combine(root, "maestro-checklist.json"));
            var saved = UpdateAgentJson.Deserialize<Dictionary<string, MaestroChecklist>>(json);
            Assert.NotNull(saved);
            Assert.Equal("teacher2", Assert.Single(saved!.Values).ConfirmedBy);
        }
        finally
        {
            Environment.SetEnvironmentVariable(UpdateAgentConstants.UnprotectedStatePathVariable, original);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentMarkerWrites_KeepMatchingNonces()
    {
        var root = Path.Combine(Path.GetTempPath(), "UpdateAgentTests", Guid.NewGuid().ToString("N"));
        var stableRoot = Path.Combine(root, "stable");
        var original = Environment.GetEnvironmentVariable(UpdateAgentConstants.UnprotectedStatePathVariable);
        Environment.SetEnvironmentVariable(UpdateAgentConstants.UnprotectedStatePathVariable, stableRoot);
        try
        {
            var provider = new ManualMaestroProvider(root);
            var markers = await Task.WhenAll(Enumerable.Range(0, 16).Select(i =>
                provider.WriteResumeMarkerAsync($"job-{i}", "agent-1", i, "before-reboot")));

            var primaryJson = await File.ReadAllTextAsync(Path.Combine(root, "resume-marker.json"));
            var stableJson = await File.ReadAllTextAsync(Path.Combine(stableRoot, "stable-resume-marker.json"));
            var primary = UpdateAgentJson.Deserialize<RestoreMarker>(primaryJson);
            var stable = UpdateAgentJson.Deserialize<RestoreMarker>(stableJson);

            Assert.NotNull(primary);
            Assert.NotNull(stable);
            Assert.Equal(primary!.Nonce, stable!.Nonce);
            Assert.Contains(markers, m => m.Nonce == primary.Nonce);
        }
        finally
        {
            Environment.SetEnvironmentVariable(UpdateAgentConstants.UnprotectedStatePathVariable, original);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SqliteJobStore_AcceptsBareFileName()
    {
        var fileName = $"audit50-{Guid.NewGuid():N}.db";
        try
        {
            var store = new SqliteJobStore(fileName);
            await store.InitializeAsync();

            Assert.Null(await store.GetActiveJobAsync("agent-1"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { fileName, fileName + "-wal", fileName + "-shm" })
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task ReleaseWithEmptyVersion_IsReportedAsFailure()
    {
        using var http = new HttpClient(new StubHandler("{\"tag_name\":\"v\"}"));
        var info = await new GitHubReleaseChecker(http).CheckAsync(UpdateProduct.LabManager);

        Assert.False(info.CheckSucceeded);
        Assert.False(info.UpdateAvailable);
        Assert.Equal("", info.LatestVersion);
        Assert.NotNull(info.Error);
    }

    [Fact]
    public async Task Scanner_DoesNotSelectUnapprovedFeatureUpdates()
    {
        var wua = new FakeWua
        {
            ScanHandler = (_, _) => Task.FromResult(new WuaScanResult
            {
                Updates =
                [
                    new WuaUpdate
                    {
                        Info = new UpdateItemInfo
                        {
                            UpdateId = "feature-1",
                            Title = "Windows 11 26H2 feature update",
                            Category = UpdateCategory.Upgrade,
                            IsFeatureUpdate = true,
                        },
                    },
                ],
            }),
        };
        var policy = UpdatePolicy.Default with
        {
            IncludeFeatureUpdates = true,
            ApprovedFeatureRelease = "26H2",
        };

        var report = await new UpdateScanner(wua, Windows11).ScanAsync(policy);

        Assert.Empty(report.Selected);
        Assert.Single(report.Excluded);
    }

    private static JobDetail Detail(
        string jobId = "job-1",
        UpdateJobState state = UpdateJobState.Installing,
        bool dryRun = true,
        UpdatePolicy? policy = null,
        int rebootCount = 0) => new()
    {
        Summary = new JobSummary
        {
            JobId = jobId,
            AgentId = "agent-1",
            AgentName = "PC-TEST",
            State = state,
            IsDryRun = dryRun,
            RebootCount = rebootCount,
            UpdatedAt = DateTimeOffset.UtcNow,
        },
        Policy = policy ?? UpdatePolicy.Default,
    };

    private static UpdateJobRunner CreateRunner(
        FakeStore store,
        FakeWua? wua = null,
        IRestorationProvider? maestro = null,
        FakeReboot? reboot = null,
        UpdateJobRunnerOptions? options = null) => new(
            wua ?? new FakeWua(),
            store,
            new FakePreflight(),
            maestro ?? new FakeMaestro(),
            reboot ?? new FakeReboot(),
            new FakeSessions(),
            NullJobProgressSink.Instance,
            NullLogger<UpdateJobRunner>.Instance,
            options ?? new UpdateJobRunnerOptions
            {
                AgentId = "agent-1",
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
        public JobDetail? Detail { get; set; }

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveJobAsync(JobDetail detail, CancellationToken cancellationToken = default)
        {
            Detail = detail;
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
        public string Name => "Audit50";

        public MarkerVerificationResult Verification { get; init; } = MarkerVerificationResult.Ok(
            new RestoreMarker
            {
                JobId = "job-1",
                AgentId = "agent-1",
                Nonce = "audit50",
            });

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
            Task.FromResult(Verification);
    }

    private sealed class FakeReboot : IRebootManager
    {
        public int RebootCount;
        public int ShutdownCount;
        public int CancelCount;

        public Task ScheduleRebootAsync(
            int delaySeconds, string reason, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref RebootCount);
            return Task.CompletedTask;
        }

        public Task CancelRebootAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref CancelCount);
            return Task.CompletedTask;
        }

        public Task ScheduleShutdownAsync(
            int delaySeconds, string reason, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref ShutdownCount);
            return Task.CompletedTask;
        }

        public bool IsRebootPending() => false;
    }

    private sealed class FakeSessions : IUserSessionProbe
    {
        public IReadOnlyList<InteractiveSession> GetInteractiveSessions() => [];

        public bool HasInteractiveUser() => false;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _json;

        public StubHandler(string json) => _json = json;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json"),
            });
    }
}
