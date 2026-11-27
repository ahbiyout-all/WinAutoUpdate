using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using UpdateAgent.Core.Comms;
using UpdateAgent.Core.Maestro;
using UpdateAgent.Core.Orchestration;
using UpdateAgent.Core.Preflight;
using UpdateAgent.Core.Reboot;
using UpdateAgent.Core.Sessions;
using UpdateAgent.Core.Storage;
using UpdateAgent.Core.Windows;
using UpdateAgent.Core.Windows.Update;
using UpdateAgent.Service.Commands;
using UpdateAgent.Service.Configuration;
using UpdateAgent.Shared.Models;
using UpdateAgent.Shared.Serialization;

namespace UpdateAgent.Tests;

/// <summary>50회차 2차 감사에서 확인한 런타임 결함의 회귀 테스트.</summary>
public sealed class Audit50Round2RuntimeTests
{
    private static readonly OsInfo ClientWindows11 =
        WindowsOsInfoProvider.Create("Windows 11 Pro", "Professional", "25H2", 26200);

    // 1. CLI: --pause-on-user는 기존에 파싱만 되고 정책에는 반영되지 않았다.
    [Fact]
    public void CliPauseOnUserFlag_EnablesPauseOnUserLogon()
    {
        Assert.True(BuildCliPolicy("pause-on-user").PauseOnUserLogon);
    }

    [Fact]
    public void CliDefaultAndNoUserPause_KeepPauseDisabled()
    {
        Assert.False(BuildCliPolicy().PauseOnUserLogon);
        Assert.False(BuildCliPolicy("no-user-pause").PauseOnUserLogon);
    }

    [Fact]
    public void CliConflictingPauseFlags_AreRejected()
    {
        var ex = Assert.Throws<TargetInvocationException>(
            () => BuildCliPolicy("pause-on-user", "no-user-pause"));

        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    // 2. 오버레이: 정체된 클라이언트가 다른 클라이언트 수신/제거를 막지 않는다.
    [Fact]
    public async Task Broadcast_TimedOutClientIsRemovedAndHealthyClientStillReceives()
    {
        var stalledId = Guid.NewGuid();
        var healthyId = Guid.NewGuid();
        var received = new List<string>();
        var removed = new List<Guid>();
        const string line = "{\"kind\":\"hide\"}";

        var stopwatch = Stopwatch.StartNew();
        await OverlayPipeServer.BroadcastWithTimeoutAsync(
            new[]
            {
                Pair(stalledId, (Func<string, CancellationToken, Task>)((_, _) =>
                    new TaskCompletionSource<bool>().Task)),
                Pair(healthyId, (Func<string, CancellationToken, Task>)((text, _) =>
                {
                    received.Add(text);
                    return Task.CompletedTask;
                })),
            },
            static (write, text, token) => write(text, token),
            removed.Add,
            line,
            TimeSpan.FromMilliseconds(80));

        stopwatch.Stop();
        Assert.Equal([line], received);
        Assert.Equal([stalledId], removed);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), "클라이언트 쓰기가 무한정 대기했습니다.");
    }

    [Fact]
    public async Task Broadcast_FailedWriteDoesNotStopRemainingClients()
    {
        var failedId = Guid.NewGuid();
        var healthyId = Guid.NewGuid();
        var received = new List<string>();
        var removed = new List<Guid>();

        await OverlayPipeServer.BroadcastWithTimeoutAsync(
            new[]
            {
                Pair(failedId, (Func<string, CancellationToken, Task>)((_, _) =>
                    throw new IOException("파이프 버퍼가 가득 찼습니다."))),
                Pair(healthyId, (Func<string, CancellationToken, Task>)((text, _) =>
                {
                    received.Add(text);
                    return Task.CompletedTask;
                })),
            },
            static (write, text, token) => write(text, token),
            removed.Add,
            "hide",
            TimeSpan.FromMilliseconds(80));

        Assert.Equal(["hide"], received);
        Assert.Equal([failedId], removed);
    }

    // 3. 학생용 상태 화면: 설정을 읽지 못하면 기본 URL을 사실처럼 보여주지 않는다.
    [Fact]
    public void AgentStatusReader_UnreadableConfig_ReturnsUnknown()
    {
        var root = NewTempDirectory();
        try
        {
            var missing = Path.Combine(root, "missing.json");
            var invalid = Path.Combine(root, "invalid.json");
            var noUrl = Path.Combine(root, "no-url.json");
            File.WriteAllText(invalid, "{ not json");
            File.WriteAllText(noUrl, "{\"Agent\":{}}");

            Assert.Equal("확인 불가", ReadLabManagerUrl(missing));
            Assert.Equal("확인 불가", ReadLabManagerUrl(missing, invalid));
            Assert.Equal("확인 불가", ReadLabManagerUrl(noUrl));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AgentStatusReader_ConfiguredUrlIsStillReported()
    {
        var root = NewTempDirectory();
        try
        {
            var config = Path.Combine(root, "agent.json");
            File.WriteAllText(config, "{\"Agent\":{\"LabManagerUrl\":\"http://teacher-pc:5090\"}}");

            Assert.Equal(
                "http://teacher-pc:5090",
                ReadLabManagerUrl(Path.Combine(root, "missing.json"), config));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // 4. 원격 StartJob: 신뢰할 수 없는 페이로드로 Maestro 확인 게이트를 끌 수 없다.
    [Fact]
    public async Task StartJob_PayloadCannotDisableMaestroConfirmationGate()
    {
        var store = new FakeStore();
        await using var runner = CreateRunner(store);
        await runner.InitializeAsync();
        var executor = CreateExecutor(runner, store);

        await executor.ExecuteAsync([StartJob(
            UpdatePolicy.Default with { RequireMaestroManualConfirmation = false },
            maestroConfirmed: false)]);

        var result = Assert.Single(executor.SnapshotResults());
        Assert.False(result.Success);
        Assert.Contains("Maestro", result.Message);
        Assert.Null(runner.Current);
    }

    [Fact]
    public async Task StartJob_ConfirmedPayloadUsesNormalizedPolicy()
    {
        var store = new FakeStore();
        await using var runner = CreateRunner(store);
        await runner.InitializeAsync();
        var executor = CreateExecutor(runner, store);
        var payloadPolicy = UpdatePolicy.Default with
        {
            RequireMaestroManualConfirmation = false,
            IncludeFeatureUpdates = true,
            ApprovedFeatureRelease = "26H2",
        };

        await executor.ExecuteAsync([StartJob(payloadPolicy, maestroConfirmed: true)]);

        Assert.True(Assert.Single(executor.SnapshotResults()).Success);
        Assert.NotNull(runner.Current);
        Assert.True(runner.Current!.Policy.RequireMaestroManualConfirmation);
        Assert.False(runner.Current.Policy.IncludeFeatureUpdates);
        Assert.Null(runner.Current.Policy.ApprovedFeatureRelease);
    }

    private static KeyValuePair<Guid, TClient> Pair<TClient>(Guid id, TClient client) => new(id, client);

    private static UpdatePolicy BuildCliPolicy(params string[] flags)
    {
        var assembly = LoadBuiltAssembly("UpdateAgent.Cli", "UpdateAgent.Cli.dll");
        var type = assembly.GetType("UpdateAgent.Cli.Program", throwOnError: true)!;
        var method = type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(m => m.Name == "BuildPolicy");
        var options = flags.ToDictionary(flag => flag, _ => (string?)null, StringComparer.OrdinalIgnoreCase);
        return (UpdatePolicy)method.Invoke(null, [options])!;
    }

    private static string ReadLabManagerUrl(params string[] paths)
    {
        var assembly = LoadBuiltAssembly("UpdateAgent.Agent.Desktop", "UpdateAgent.Agent.Desktop.dll");
        var type = assembly.GetType(
            "UpdateAgent.Agent.Desktop.Services.AgentStatusReader", throwOnError: true)!;
        var method = type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(m => m.Name == "ReadLabManagerUrl" && m.GetParameters().Length == 1);
        return (string)method.Invoke(null, [(IEnumerable<string>)paths])!;
    }

    private static Assembly LoadBuiltAssembly(string projectFolder, string fileName)
    {
        var root = FindRepositoryRoot();
        foreach (var configuration in new[] { "Release", "Debug" })
        {
            var path = Path.Combine(
                root, "src", projectFolder, "bin", configuration, "net10.0-windows", fileName);
            if (File.Exists(path)) return Assembly.LoadFrom(path);
        }

        throw new FileNotFoundException("빌드된 어셈블리를 찾을 수 없습니다.", fileName);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (directory.GetFiles("UpdateAgent.sln").Length > 0) return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("UpdateAgent.sln을 찾을 수 없습니다.");
    }

    private static string NewTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "UpdateAgentRound2", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static AgentCommand StartJob(UpdatePolicy policy, bool maestroConfirmed) => new()
    {
        CommandId = $"cmd-start-{Guid.NewGuid():N}",
        AgentId = "pc-audit50-r2",
        Type = CommandType.StartJob,
        ExpiresAt = DateTimeOffset.Now.AddHours(1),
        PayloadJson = UpdateAgentJson.Serialize(new StartJobCommandPayload
        {
            Policy = policy,
            MaestroConfirmed = maestroConfirmed,
        }),
    };

    private static CommandExecutor CreateExecutor(UpdateJobRunner runner, IJobStore store) =>
        new(runner, null!, null!, store, new ServerPolicyProvider(store),
            NullLogger<CommandExecutor>.Instance);

    private static UpdateJobRunner CreateRunner(IJobStore store) => new(
        new FakeWua(),
        store,
        new FakePreflight(),
        new FakeMaestro(),
        new FakeReboot(),
        new FakeSessions(),
        NullJobProgressSink.Instance,
        NullLogger<UpdateJobRunner>.Instance,
        new UpdateJobRunnerOptions
        {
            AgentId = "agent-audit50-r2",
            OsOverride = ClientWindows11,
            DryRun = true,
        });

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
        public JobDetail? Detail { get; private set; }

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
            Task.FromResult(MarkerVerificationResult.Failed("테스트"));
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
    }

    private sealed class FakeSessions : IUserSessionProbe
    {
        public IReadOnlyList<InteractiveSession> GetInteractiveSessions() => [];

        public bool HasInteractiveUser() => false;
    }
}
