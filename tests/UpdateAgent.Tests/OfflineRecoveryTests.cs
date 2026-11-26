using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UpdateAgent.Service;
using UpdateAgent.Service.Comms;
using UpdateAgent.Service.Configuration;
using UpdateAgent.Shared.Comms;
using UpdateAgent.Shared.Models;

namespace UpdateAgent.Tests;

public sealed class OfflineRecoveryTests
{
    [Fact]
    public void CheckInRetryPolicy_UsesExponentialBackoffAndCap()
    {
        Assert.Equal(30, CheckInRetryPolicy.NextDelaySeconds(0, 30, 300));
        Assert.Equal(60, CheckInRetryPolicy.NextDelaySeconds(1, 30, 300));
        Assert.Equal(120, CheckInRetryPolicy.NextDelaySeconds(2, 30, 300));
        Assert.Equal(240, CheckInRetryPolicy.NextDelaySeconds(3, 30, 300));
        Assert.Equal(300, CheckInRetryPolicy.NextDelaySeconds(4, 30, 300));
        Assert.Equal(300, CheckInRetryPolicy.NextDelaySeconds(10, 30, 300));
    }

    [Fact]
    public void CheckInRetryPolicy_AppliesCustomCapAndClampsFailures()
    {
        Assert.Equal(30, CheckInRetryPolicy.NextDelaySeconds(0, 30, 90));
        Assert.Equal(60, CheckInRetryPolicy.NextDelaySeconds(1, 30, 90));
        Assert.Equal(90, CheckInRetryPolicy.NextDelaySeconds(2, 30, 90));
        Assert.Equal(90, CheckInRetryPolicy.NextDelaySeconds(3, 30, 90));
        Assert.Equal(90, CheckInRetryPolicy.NextDelaySeconds(int.MaxValue, 30, 90));
        Assert.Equal(30, CheckInRetryPolicy.NextDelaySeconds(-5, 30, 90));
    }

    [Fact]
    public void CheckInRetryPolicy_GuardsBaseAndMaxWithoutOverflow()
    {
        Assert.Equal(1, CheckInRetryPolicy.NextDelaySeconds(0, 0, 0));
        Assert.Equal(1, CheckInRetryPolicy.NextDelaySeconds(0, -5, -10));
        Assert.Equal(30, CheckInRetryPolicy.NextDelaySeconds(0, 30, 5));
        Assert.Equal(int.MaxValue, CheckInRetryPolicy.NextDelaySeconds(10, int.MaxValue, int.MaxValue));
    }

    [Fact]
    public void Worker_ResolveCheckInDelaySeconds_UsesPolicyWithDefaultCap()
    {
        Assert.Equal(30, Worker.ResolveCheckInDelaySeconds(0, 30));
        Assert.Equal(60, Worker.ResolveCheckInDelaySeconds(1, 30));
        Assert.Equal(120, Worker.ResolveCheckInDelaySeconds(2, 30));
        Assert.Equal(
            CheckInRetryPolicy.DefaultMaxDelaySeconds,
            Worker.ResolveCheckInDelaySeconds(10, 30));
        Assert.Equal(1, Worker.ResolveCheckInDelaySeconds(0, 0));
    }

    [Fact]
    public async Task LabManagerClient_TracksFailuresAndResetsAfterRecovery()
    {
        var handler = new SequenceHandler(
            Status(HttpStatusCode.ServiceUnavailable),
            Status(HttpStatusCode.ServiceUnavailable),
            OkJson);
        var client = CreateClient(handler);
        var request = NewRequest();

        Assert.Null(await client.CheckInAsync(request));
        Assert.Equal(1, client.ConsecutiveFailures);
        Assert.NotNull(client.LastFailureAt);
        Assert.Contains("503", client.LastError);
        var firstFailureAt = client.LastFailureAt!.Value;

        Assert.Null(await client.CheckInAsync(request));
        Assert.Equal(2, client.ConsecutiveFailures);
        Assert.True(client.LastFailureAt >= firstFailureAt);

        Assert.NotNull(await client.CheckInAsync(request));
        Assert.Equal(0, client.ConsecutiveFailures);
        Assert.Null(client.LastFailureAt);
        Assert.StartsWith("오프라인 복구:", client.LastError);
        Assert.Contains("2회", client.LastError);

        handler.Enqueue(OkJson);
        Assert.NotNull(await client.CheckInAsync(request));
        Assert.Equal(0, client.ConsecutiveFailures);
        Assert.Null(client.LastError);
    }

    [Fact]
    public async Task LabManagerClient_BoundsFailureCountAtTen()
    {
        var steps = Enumerable.Repeat(Status(HttpStatusCode.InternalServerError), 12)
            .Append(OkJson)
            .ToArray();
        var handler = new SequenceHandler(steps);
        var client = CreateClient(handler);

        for (var i = 0; i < 12; i++)
        {
            Assert.Null(await client.CheckInAsync(NewRequest()));
        }

        Assert.Equal(10, client.ConsecutiveFailures);

        Assert.NotNull(await client.CheckInAsync(NewRequest()));
        Assert.Equal(0, client.ConsecutiveFailures);
        Assert.Contains("이전 체크인 실패 10회", client.LastError);
    }

    [Fact]
    public async Task LabManagerClient_RedactsConfiguredTokenFromErrorAndLogs()
    {
        const string token = "dummy-agent-token-secret-42";
        var logger = new CollectingLogger();
        var handler = new SequenceHandler(
            _ => throw new HttpRequestException($"연결 실패: token={token}"),
            OkJson);
        var client = CreateClient(handler, token, logger);

        Assert.Null(await client.CheckInAsync(NewRequest()));
        Assert.Equal(1, client.ConsecutiveFailures);
        Assert.NotNull(client.LastFailureAt);
        Assert.NotNull(client.LastError);
        Assert.Contains("[redacted]", client.LastError);
        Assert.DoesNotContain(token, client.LastError);
        Assert.All(logger.Messages, message => Assert.DoesNotContain(token, message));

        Assert.NotNull(await client.CheckInAsync(NewRequest()));
        Assert.StartsWith("오프라인 복구:", client.LastError);
        Assert.DoesNotContain(token, client.LastError);
        Assert.All(logger.Messages, message => Assert.DoesNotContain(token, message));
    }

    [Fact]
    public async Task LabManagerClient_RedactsUrlCredentialsFromErrorAndLogs()
    {
        var logger = new CollectingLogger();
        var handler = new SequenceHandler(
            _ => throw new HttpRequestException("요청 실패: http://lab-user:lab-pass@labmanager/api"),
            OkJson);
        var client = CreateClient(handler, logger: logger);

        Assert.Null(await client.CheckInAsync(NewRequest()));
        Assert.NotNull(client.LastError);
        Assert.Contains("http://[redacted]@labmanager/api", client.LastError);
        Assert.DoesNotContain("lab-pass", client.LastError);
        Assert.All(logger.Messages, message => Assert.DoesNotContain("lab-pass", message));
    }

    [Fact]
    public async Task LabManagerClient_DoesNotCountCallerCancellationAsFailure()
    {
        var handler = new SequenceHandler(_ => throw new OperationCanceledException());
        var client = CreateClient(handler);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.CheckInAsync(NewRequest(), cts.Token));

        Assert.Equal(0, client.ConsecutiveFailures);
        Assert.Null(client.LastFailureAt);
    }

    private static AgentCheckInRequest NewRequest() => new()
    {
        Agent = new AgentInfo { AgentId = "test-agent" },
        Telemetry = new AgentTelemetry { AgentId = "test-agent" },
    };

    private static LabManagerClient CreateClient(
        HttpMessageHandler handler,
        string token = "dummy-agent-token-for-tests",
        ILogger<LabManagerClient>? logger = null)
    {
        var options = new AgentOptions
        {
            LabManagerUrl = "http://localhost:5080",
            AgentToken = token,
            PollIntervalSeconds = 30,
        };
        var factory = new SingleClientFactory(new HttpClient(handler));
        return new LabManagerClient(
            factory,
            new TestOptionsMonitor(options),
            logger ?? NullLogger<LabManagerClient>.Instance);
    }

    private static Func<CancellationToken, HttpResponseMessage> Status(HttpStatusCode code) =>
        _ => new HttpResponseMessage(code);

    private static HttpResponseMessage OkJson(CancellationToken _) => new(HttpStatusCode.OK)
    {
        Content = new StringContent("{}", Encoding.UTF8, "application/json"),
    };

    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly Queue<Func<CancellationToken, HttpResponseMessage>> _steps;

        public SequenceHandler(params Func<CancellationToken, HttpResponseMessage>[] steps)
        {
            _steps = new Queue<Func<CancellationToken, HttpResponseMessage>>(steps);
        }

        public void Enqueue(Func<CancellationToken, HttpResponseMessage> step) => _steps.Enqueue(step);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (_steps.Count == 0) throw new InvalidOperationException("예약된 응답이 없습니다.");
            return Task.FromResult(_steps.Dequeue()(cancellationToken));
        }
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public SingleClientFactory(HttpClient client)
        {
            _client = client;
        }

        public HttpClient CreateClient(string name) => _client;
    }

    private sealed class TestOptionsMonitor : IOptionsMonitor<AgentOptions>
    {
        private readonly AgentOptions _value;

        public TestOptionsMonitor(AgentOptions value)
        {
            _value = value;
        }

        public AgentOptions CurrentValue => _value;

        public AgentOptions Get(string? name) => _value;

        public IDisposable OnChange(Action<AgentOptions, string?> listener) => new NoopDisposable();
    }

    private sealed class CollectingLogger : ILogger<LabManagerClient>
    {
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => new NoopDisposable();

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
