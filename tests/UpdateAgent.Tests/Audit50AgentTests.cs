using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UpdateAgent.Core.Comms;
using UpdateAgent.Core.Storage;
using UpdateAgent.Guards;
using UpdateAgent.Service;
using UpdateAgent.Service.Commands;
using UpdateAgent.Service.Configuration;
using UpdateAgent.Service.Overlay;
using UpdateAgent.SessionHelper;
using UpdateAgent.Shared.Models;

namespace UpdateAgent.Tests;

/// <summary>50회차 에이전트 감사에서 확인한 결함의 회귀 테스트.</summary>
public sealed class Audit50AgentTests
{
    [Fact]
    public async Task ExpiredCommand_IsAcknowledgedOnlyOnce_WhenServerResendsIt()
    {
        var store = new MemoryJobStore();
        var executor = CreateExecutor(store);
        var expired = new AgentCommand
        {
            CommandId = "cmd-expired",
            AgentId = "pc-audit50",
            Type = CommandType.Ping,
            ExpiresAt = DateTimeOffset.Now.AddMinutes(-1),
        };

        await executor.ExecuteAsync([expired]);
        await executor.ExecuteAsync([expired]);

        Assert.Single(executor.SnapshotResults());
    }

    [Fact]
    public async Task HideOverlay_WithEmptyJobId_StillSuppressesLastJob()
    {
        var sink = new OverlayJobProgressSink(
            new OverlayPipeServer($"audit50-{Guid.NewGuid():N}"),
            new StubOptionsMonitor<AgentOptions>(new AgentOptions()),
            NullLogger<OverlayJobProgressSink>.Instance);
        SetField(sink, "_lastSummary", new JobSummary { JobId = "job-last" });

        // LocalUpdateTrigger는 ID가 없을 때 null 대신 빈 문자열을 넘긴다.
        await sink.HideAsync("긴급 중단", string.Empty);

        Assert.Equal("job-last", GetField(sink, "_suppressedJobId"));
    }

    [Theory]
    [InlineData("ftp://teacher:21")]
    [InlineData("http://")]
    [InlineData("http://user:pass@teacher:5080")]
    public void Validator_RejectsInvalidLabManagerUrl(string url)
    {
        var options = new AgentOptions
        {
            LabManagerUrl = url,
            AgentToken = "audit50-real-token",
        };

        Assert.False(new AgentOptionsValidator().Validate(null, options).Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public void Validator_RejectsOutOfRangeCommandLimit(int limit)
    {
        var options = new AgentOptions
        {
            LabManagerUrl = "http://localhost:5080",
            AgentToken = "audit50-real-token",
            MaxCommandsPerCheckIn = limit,
        };

        Assert.False(new AgentOptionsValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void Validator_AcceptsValidConfiguration()
    {
        var options = new AgentOptions
        {
            LabManagerUrl = "http://localhost:5080",
            AgentToken = "audit50-real-token",
        };

        Assert.True(new AgentOptionsValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void Overlay_MarksDryRunJobAsSimulation()
    {
        var viewModel = new OverlayViewModel();

        viewModel.Apply(new JobSummary
        {
            JobId = "job-dry-run",
            State = UpdateJobState.Completed,
            Phase = "업데이트 완료",
            IsDryRun = true,
        });

        Assert.Contains("DRY RUN", viewModel.Phase);
    }

    [Fact]
    public void Overlay_DryRunJobDoesNotWarnAboutReboot()
    {
        var viewModel = new OverlayViewModel();

        viewModel.Apply(new JobSummary
        {
            JobId = "job-dry-reboot",
            State = UpdateJobState.PendingReboot,
            IsDryRun = true,
        });

        Assert.Equal(System.Windows.Visibility.Collapsed, viewModel.RebootVisibility);
        Assert.Equal("", viewModel.RebootText);
    }

    [Fact]
    public void HideNotice_IsAppliedEvenWhenOverlayIsHidden()
    {
        var viewModel = new OverlayViewModel();

        var hasNotice = MainWindow.ApplyHideNotice(viewModel, "긴급 중단 안내");

        Assert.True(hasNotice);
        Assert.Equal("긴급 중단 안내", viewModel.NoticeText);
        Assert.False(MainWindow.ApplyHideNotice(viewModel, "   "));
    }

    [Fact]
    public void IdentifierGuard_AllowsAsciiIdentifiersOnly()
    {
        Assert.True(TextGuard.IsSafeIdentifier("job-AUDIT50_01"));
        Assert.False(TextGuard.IsSafeIdentifier("job-감사50"));
        Assert.False(TextGuard.IsSafeIdentifier("job-１２３"));
    }

    [Fact]
    public void UrlGuard_AcceptsBracketedIpv6Loopback()
    {
        Assert.True(UrlGuard.IsLoopbackHost("[::1]"));
        Assert.True(UrlGuard.IsSafeBrowserUrl("http://[::1]:5080"));
    }

    [Theory]
    [InlineData("short")]
    [InlineData("1234567")]
    public void Validator_RejectsTooShortAgentToken(string token)
    {
        var options = new AgentOptions
        {
            LabManagerUrl = "http://localhost:5080",
            AgentToken = token,
        };

        var result = new AgentOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("AgentToken", result.FailureMessage);
    }

    private static CommandExecutor CreateExecutor(IJobStore store) =>
        // Ping/ApplyPolicy/만료 경로에서는 runner/reboot/overlay 의존성을 사용하지 않는다.
        new(null!, null!, null!, store, new ServerPolicyProvider(store),
            NullLogger<CommandExecutor>.Instance);

    private static object? GetField(object target, string name) => target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(target);

    private static void SetField(object target, string name, object? value) => target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private sealed class MemoryJobStore : IJobStore
    {
        private readonly Dictionary<string, string> _settings = new(StringComparer.Ordinal);

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveJobAsync(JobDetail detail, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<JobDetail?> GetJobAsync(string jobId, CancellationToken cancellationToken = default) =>
            Task.FromResult<JobDetail?>(null);
        public Task<JobDetail?> GetActiveJobAsync(string agentId, CancellationToken cancellationToken = default) =>
            Task.FromResult<JobDetail?>(null);
        public Task<IReadOnlyList<JobSummary>> GetRecentJobsAsync(
            string agentId, int limit = 20, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<JobSummary>>([]);
        public Task AppendEventAsync(string jobId, string level, string message, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task<IReadOnlyList<JobEventRecord>> GetEventsAsync(
            string jobId, int limit = 200, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<JobEventRecord>>([]);

        public Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(_settings.TryGetValue(key, out var value) ? value : null);

        public Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            _settings[key] = value;
            return Task.CompletedTask;
        }
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
