using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UpdateAgent.LabManager.Auth;
using UpdateAgent.LabManager.Configuration;
using UpdateAgent.LabManager.Services;
using UpdateAgent.Shared;

namespace UpdateAgent.Tests;

public sealed class LabManagerServiceTests
{
    [Fact]
    public async Task TokenAuthMiddleware_AcceptsTheAgentToken()
    {
        var options = new LabManagerOptions
        {
            AdminToken = "admin-secret",
            AgentToken = "agent-secret",
        };
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/agents/checkin";
        context.Request.Headers[UpdateAgentConstants.AgentTokenHeader] = "agent-secret";

        var middleware = CreateMiddleware(options, context);
        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status204NoContent, context.Response.StatusCode);
    }

    [Fact]
    public async Task TokenAuthMiddleware_RejectsAMissingAdminToken()
    {
        var options = new LabManagerOptions
        {
            AdminToken = "admin-secret",
            AgentToken = "agent-secret",
        };
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/admin/update";

        var middleware = CreateMiddleware(options, context);
        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task UpdateCheckService_CachesSuccessfulChecksUnlessForced()
    {
        const string json = """
        {
          "tag_name": "v9.9.9",
          "assets": [
            { "name": "UpdateAgent.Agent-Setup-9.9.9-x64.exe", "browser_download_url": "https://example.test/setup.exe", "size": 10 }
          ]
        }
        """;

        using var handler = new CountingHandler(json);
        using var http = new HttpClient(handler);
        var factory = new SingleClientFactory(http);
        var service = new UpdateCheckService(factory, NullLogger<UpdateCheckService>.Instance);

        var first = await service.GetAsync(force: false, CancellationToken.None);
        var second = await service.GetAsync(force: false, CancellationToken.None);
        var forced = await service.GetAsync(force: true, CancellationToken.None);

        Assert.True(first.CheckSucceeded);
        Assert.Same(first, second);
        Assert.NotSame(first, forced);
        Assert.Equal(2, factory.CreateCount);
        Assert.Equal(2, handler.RequestCount);
    }

    private static TokenAuthMiddleware CreateMiddleware(LabManagerOptions options, HttpContext context)
    {
        return new TokenAuthMiddleware(
            _ =>
            {
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return Task.CompletedTask;
            },
            new TestOptionsMonitor(options),
            NullLogger<TokenAuthMiddleware>.Instance);
    }

    private sealed class TestOptionsMonitor : IOptionsMonitor<LabManagerOptions>
    {
        private readonly LabManagerOptions _value;

        public TestOptionsMonitor(LabManagerOptions value)
        {
            _value = value;
        }

        public LabManagerOptions CurrentValue => _value;

        public LabManagerOptions Get(string? name) => _value;

        public IDisposable OnChange(Action<LabManagerOptions, string?> listener) => new NoopDisposable();
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public SingleClientFactory(HttpClient client)
        {
            _client = client;
        }

        public int CreateCount { get; private set; }

        public HttpClient CreateClient(string name)
        {
            CreateCount++;
            return _client;
        }
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly string _json;

        public CountingHandler(string json)
        {
            _json = json;
        }

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
