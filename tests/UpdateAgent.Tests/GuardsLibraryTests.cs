using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UpdateAgent.Guards;
using UpdateAgent.LabManager.Auth;
using UpdateAgent.LabManager.Configuration;
using UpdateAgent.LabManager.Desktop.Services;
using UpdateAgent.Shared;

namespace UpdateAgent.Tests;

/// <summary>순수 창작 Guard DLL의 규칙과 기존 호출부의 회귀를 함께 검증한다.</summary>
public sealed class GuardsLibraryTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly string _overridePath;

    public GuardsLibraryTests()
    {
        _testDirectory = Path.Combine(
            Path.GetTempPath(), "updateagent-guards-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDirectory);
        _overridePath = Path.Combine(_testDirectory, "runtime-admin-token.json");
    }

    [Fact]
    public void TokenGuard_Normalize_RemovesBomAndWhitespace()
    {
        Assert.Equal("real-token", TokenGuard.Normalize("\uFEFF  real-token \t"));
        Assert.Equal(string.Empty, TokenGuard.Normalize(null));
    }

    [Fact]
    public void TokenGuard_IsPlaceholder_CoversPublishedDefaults()
    {
        Assert.True(TokenGuard.IsPlaceholder(null));
        Assert.True(TokenGuard.IsPlaceholder("change-me-agent-token"));
        Assert.True(TokenGuard.IsPlaceholder("SAMPLE-admin-token-REPLACE-BEFORE-PILOT"));
        Assert.False(TokenGuard.IsPlaceholder("teacher-specific-secret-42"));
    }

    [Fact]
    public void TokenGuard_IsAcceptable_EnforcesLengthAndPlaceholder()
    {
        Assert.False(TokenGuard.IsAcceptable("short"));
        Assert.False(TokenGuard.IsAcceptable("change-me-admin-token"));
        Assert.True(TokenGuard.IsAcceptable("teacher-specific-secret-42"));
        Assert.True(TokenGuard.IsAcceptable(UpdateAgentConstants.SampleAdminToken, allowPlaceholder: true));
    }

    [Fact]
    public void TokenGuard_FixedTimeEquals_RejectsEmptyAndNormalizesInputs()
    {
        Assert.False(TokenGuard.FixedTimeEquals(null, string.Empty));
        Assert.False(TokenGuard.FixedTimeEquals("same-value", "other-value"));
        Assert.True(TokenGuard.FixedTimeEquals("\uFEFF same-value ", "same-value"));
        Assert.True(TokenGuard.AreDistinct("admin-secret", "agent-secret"));
        Assert.False(TokenGuard.AreDistinct("shared-secret", "shared-secret"));
    }

    [Fact]
    public void UrlGuard_IsAllowedExternalUrl_BlocksUnsafeSchemesAndUserInfo()
    {
        Assert.True(UrlGuard.IsAllowedExternalUrl("https://github.com/org/repo", requireGitHubHost: true));
        Assert.True(UrlGuard.IsAllowedExternalUrl("https://api.github.com/repos/org/repo/releases/latest", requireGitHubHost: true));
        Assert.False(UrlGuard.IsAllowedExternalUrl("https://example.test/app.exe", requireGitHubHost: true));
        Assert.False(UrlGuard.IsAllowedExternalUrl("file:///C:/Windows/System32/cmd.exe"));
        Assert.False(UrlGuard.IsAllowedExternalUrl("javascript:alert(1)"));
        Assert.False(UrlGuard.IsAllowedExternalUrl("https://user:password@github.com/org/repo", requireGitHubHost: true));
    }

    [Fact]
    public void UrlGuard_IsSafeBrowserUrl_AllowsLocalHttpAndRemoteHttpsOnly()
    {
        Assert.True(UrlGuard.IsSafeBrowserUrl("http://localhost:5080/"));
        Assert.True(UrlGuard.IsSafeBrowserUrl("http://127.0.0.1:5080/monitor.html"));
        Assert.True(UrlGuard.IsSafeBrowserUrl("https://lab.example.com:8443/ua/"));
        Assert.False(UrlGuard.IsSafeBrowserUrl("http://lab.example.com:5080/"));
        Assert.False(UrlGuard.IsSafeBrowserUrl("file:///C:/Windows/notepad.exe"));
    }

    [Fact]
    public void UrlGuard_NormalizeBrowserUrl_MapsLocalhostToLoopbackIp()
    {
        Assert.Equal("http://127.0.0.1:5080/", UrlGuard.NormalizeBrowserUrl("http://localhost:5080"));
        Assert.Equal(
            "https://lab.example.com:8443/ua/",
            UrlGuard.NormalizeBrowserUrl("https://lab.example.com:8443/ua/"));
        Assert.Null(UrlGuard.NormalizeBrowserUrl("file:///C:/Windows/notepad.exe"));
    }

    [Fact]
    public void UtcClock_Parse_AssumesUtcWhenOffsetIsMissing()
    {
        var parsed = UtcClock.Parse("2026-11-24T10:30:00");
        Assert.Equal(TimeSpan.Zero, parsed.Offset);
        Assert.Equal("2026-11-24T10:30:00.0000000+00:00", UtcClock.Format(parsed));
    }

    [Fact]
    public async Task AdminTokenStore_SetTokenAsync_RejectsAgentTokenCollision()
    {
        var store = CreateStore(UpdateAgentConstants.SampleAdminToken, agentToken: "shared-agent-secret-42");

        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => store.SetTokenAsync("shared-agent-secret-42"));

        Assert.Contains("에이전트 토큰과 달라야", error.Message);
        Assert.False(File.Exists(_overridePath));
    }

    [Fact]
    public void AdminTokenStore_LoadOverrideToken_IgnoresAgentTokenCollision()
    {
        WriteOverride("shared-agent-secret-42");
        var store = CreateStore(UpdateAgentConstants.SampleAdminToken, agentToken: "shared-agent-secret-42");

        Assert.Equal(UpdateAgentConstants.SampleAdminToken, store.CurrentToken);
        Assert.False(store.HasRuntimeOverride);
    }

    [Fact]
    public void AdminTokenStore_LoadOverrideToken_IgnoresShortToken()
    {
        WriteOverride("short");
        var store = CreateStore(UpdateAgentConstants.SampleAdminToken);

        Assert.Equal(UpdateAgentConstants.SampleAdminToken, store.CurrentToken);
        Assert.False(store.HasRuntimeOverride);
    }

    [Fact]
    public void AdminTokenStore_RefreshOverride_DetectsSameLengthSameTimestampRewrite()
    {
        WriteOverride("override-token-first01");
        var stamp = File.GetLastWriteTimeUtc(_overridePath);
        var store = CreateStore(UpdateAgentConstants.SampleAdminToken);
        Assert.Equal("override-token-first01", store.CurrentToken);

        WriteOverride("override-token-second2");
        File.SetLastWriteTimeUtc(_overridePath, stamp);

        Assert.Equal("override-token-second2", store.CurrentToken);
    }

    [Fact]
    public void BrowserLauncher_BuildStartInfo_RejectsUnsafeUrl()
    {
        Assert.Throws<ArgumentException>(() => BrowserLauncher.BuildStartInfo(
            @"C:\Fake\msedge.exe", "file:///C:/Windows/notepad.exe", 1280, 800));
    }

    [Fact]
    public void BrowserLauncher_OpenDashboard_RejectsUnsafeUrlWithoutStartingProcess()
    {
        Assert.False(BrowserLauncher.OpenDashboard("javascript:alert(1)", 1280, 800));
    }

    private void WriteOverride(string token)
    {
        File.WriteAllText(
            _overridePath,
            "{\"AdminToken\":\"" + token + "\"}",
            new UTF8Encoding(false));
    }

    private AdminTokenStore CreateStore(
        string configuredToken,
        string? agentToken = null,
        bool allowDefaultTokens = false)
    {
        var options = new LabManagerOptions
        {
            AdminToken = configuredToken,
            AgentToken = agentToken ?? UpdateAgentConstants.SampleAgentToken,
            AllowDefaultTokens = allowDefaultTokens,
        };
        return new AdminTokenStore(
            new TestOptionsMonitor(options), _overridePath, NullLogger<AdminTokenStore>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory)) Directory.Delete(_testDirectory, recursive: true);
    }

    private sealed class TestOptionsMonitor : IOptionsMonitor<LabManagerOptions>
    {
        private readonly LabManagerOptions _value;

        public TestOptionsMonitor(LabManagerOptions value) => _value = value;

        public LabManagerOptions CurrentValue => _value;

        public LabManagerOptions Get(string? name) => _value;

        public IDisposable OnChange(Action<LabManagerOptions, string?> listener) => new NoopDisposable();

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }
}
