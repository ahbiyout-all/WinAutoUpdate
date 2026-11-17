using System.Net;
using System.Text;
using UpdateAgent.Shared;
using UpdateAgent.Shared.Updates;

namespace UpdateAgent.Tests;

public sealed class GitHubReleaseCheckerTests
{
    [Theory]
    [InlineData("v0.1.0", "0.1.0", false)]
    [InlineData("0.2.0", "0.1.0", true)]
    [InlineData("v0.1.0-beta", "0.1.0", false)]
    [InlineData("0.10.0", "0.9.0", true)]
    [InlineData("1.0.0", "0.99.99", true)]
    [InlineData("release-2026", "0.1.0", false)]
    public void IsNewer_ComparesVersionsNumerically(string candidate, string current, bool expected)
    {
        Assert.Equal(expected, GitHubReleaseChecker.IsNewer(candidate, current));
    }

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData(" V1.2.3 ", "1.2.3")]
    [InlineData("1.2.3", "1.2.3")]
    public void NormalizeVersion_StripsPrefixAndWhitespace(string value, string expected)
    {
        Assert.Equal(expected, GitHubReleaseChecker.NormalizeVersion(value));
    }

    [Fact]
    public void LatestReleaseApiUrl_UsesTheCanonicalRepository()
    {
        Assert.Equal(
            "https://api.github.com/repos/ahbiyout-all/WinAutoUpdate/releases/latest",
            UpdateAgentConstants.LatestReleaseApiUrl);
    }

    [Fact]
    public async Task CheckAsync_RequestsTheExactLatestReleaseEndpoint()
    {
        using var handler = new StubHandler("{}", HttpStatusCode.NotFound);
        using var http = new HttpClient(handler);

        await new GitHubReleaseChecker(http).CheckAsync();

        Assert.Equal(
            "https://api.github.com/repos/ahbiyout-all/WinAutoUpdate/releases/latest",
            handler.RequestUri?.ToString());
    }

    [Fact]
    public async Task CheckAsync_ParsesReleaseAndSelectsMsiAsset()
    {
        const string json = """
        {
          "tag_name": "v9.9.9",
          "name": "UpdateAgent 9.9.9",
          "body": "릴리스 노트",
          "html_url": "https://github.com/ahbiyout-all/WinAutoUpdate/releases/tag/v9.9.9",
          "published_at": "2026-11-16T00:00:00Z",
          "assets": [
            { "name": "notes.txt", "browser_download_url": "https://example.test/notes.txt", "size": 10 },
            { "name": "UpdateAgent.Agent-9.9.9-x64.msi", "browser_download_url": "https://example.test/app.msi", "size": 1234 }
          ]
        }
        """;

        using var http = new HttpClient(new StubHandler(json, HttpStatusCode.OK));
        var info = await new GitHubReleaseChecker(http).CheckAsync();

        Assert.True(info.CheckSucceeded);
        Assert.True(info.UpdateAvailable);
        Assert.Equal("9.9.9", info.LatestVersion);
        Assert.Equal("UpdateAgent 9.9.9", info.ReleaseName);
        Assert.Equal("릴리스 노트", info.ReleaseNotes);
        Assert.Equal("UpdateAgent.Agent-9.9.9-x64.msi", info.AssetName);
        Assert.Equal("https://example.test/app.msi", info.AssetUrl);
        Assert.Equal(1234, info.AssetSizeBytes);
        Assert.Null(info.Error);
    }

    [Fact]
    public async Task CheckAsync_PrefersInnoSetupExeOverMsi()
    {
        const string json = """
        {
          "tag_name": "v9.9.9",
          "assets": [
            { "name": "UpdateAgent.Agent-9.9.9-x64.msi", "browser_download_url": "https://example.test/app.msi", "size": 10 },
            { "name": "UpdateAgent.Agent-Setup-9.9.9-x64.exe", "browser_download_url": "https://example.test/app-setup.exe", "size": 20 }
          ]
        }
        """;

        using var http = new HttpClient(new StubHandler(json, HttpStatusCode.OK));
        var info = await new GitHubReleaseChecker(http).CheckAsync();

        Assert.True(info.UpdateAvailable);
        Assert.Equal("UpdateAgent.Agent-Setup-9.9.9-x64.exe", info.AssetName);
        Assert.Equal("https://example.test/app-setup.exe", info.AssetUrl);
    }

    [Fact]
    public async Task CheckAsync_ReportsFailureOnHttpError()
    {
        using var http = new HttpClient(new StubHandler("{}", HttpStatusCode.NotFound));
        var info = await new GitHubReleaseChecker(http).CheckAsync();

        Assert.False(info.CheckSucceeded);
        Assert.False(info.UpdateAvailable);
        Assert.Contains("404", info.Error);
    }

    [Fact]
    public async Task CheckAsync_ReportsFailureOnInvalidJson()
    {
        using var http = new HttpClient(new StubHandler("{not-json", HttpStatusCode.OK));
        var info = await new GitHubReleaseChecker(http).CheckAsync();

        Assert.False(info.CheckSucceeded);
        Assert.NotNull(info.Error);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _content;
        private readonly HttpStatusCode _status;

        public StubHandler(string content, HttpStatusCode status)
        {
            _content = content;
            _status = status;
        }

        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            var response = new HttpResponseMessage(_status)
            {
                Content = new StringContent(_content, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }
}
