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
    public void TokenGuard_Normalize_RemovesLeadingBomAndOuterWhitespaceOnly()
    {
        Assert.Equal("real-token", TokenGuard.Normalize("\uFEFF  real-token \t"));
        Assert.Equal(string.Empty, TokenGuard.Normalize(null));
        // 내부 U+FEFF는 별도 문자이므로 제거하면 서로 다른 토큰이 같아진다.
        Assert.Equal("real\uFEFFtoken", TokenGuard.Normalize(" real\uFEFFtoken "));
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
    public void TokenGuard_FixedTimeEquals_RejectsEmptyAndPreservesCase()
    {
        Assert.False(TokenGuard.FixedTimeEquals(null, string.Empty));
        Assert.False(TokenGuard.FixedTimeEquals("same-value", "other-value"));
        Assert.False(TokenGuard.FixedTimeEquals("Same-Value", "same-value"));
        Assert.False(TokenGuard.FixedTimeEquals("real\uFEFFtoken", "realtoken"));
        Assert.True(TokenGuard.FixedTimeEquals("\uFEFF same-value ", "same-value"));
        Assert.True(TokenGuard.AreDistinct("admin-secret", "agent-secret"));
        Assert.False(TokenGuard.AreDistinct("shared-secret", "shared-secret"));
        Assert.False(TokenGuard.AreDistinct(string.Empty, string.Empty));
        Assert.False(TokenGuard.AreDistinct("admin-secret", string.Empty));
    }

    [Fact]
    public void UrlGuard_IsAllowedExternalUrl_BlocksUnsafeSchemesAndUserInfo()
    {
        Assert.True(UrlGuard.IsAllowedExternalUrl("https://github.com/org/repo", requireGitHubHost: true));
        Assert.True(UrlGuard.IsAllowedExternalUrl("https://api.github.com/repos/org/repo/releases/latest", requireGitHubHost: true));
        Assert.True(UrlGuard.IsAllowedExternalUrl("https://objects.githubusercontent.com/asset", requireGitHubHost: true));
        Assert.True(UrlGuard.IsAllowedExternalUrl("https://raw.githubusercontent.com/org/repo/main/file", requireGitHubHost: true));
        Assert.False(UrlGuard.IsAllowedExternalUrl("https://example.test/app.exe", requireGitHubHost: true));
        Assert.False(UrlGuard.IsAllowedExternalUrl("file:///C:/Windows/System32/cmd.exe"));
        Assert.False(UrlGuard.IsAllowedExternalUrl("javascript:alert(1)"));
        Assert.False(UrlGuard.IsAllowedExternalUrl("https://user:password@example.test/org/repo"));
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
    public void UrlGuard_IsLoopbackHost_CoversIpv4AndIpv6()
    {
        Assert.True(UrlGuard.IsLoopbackHost("localhost"));
        Assert.True(UrlGuard.IsLoopbackHost("127.0.0.2"));
        Assert.True(UrlGuard.IsLoopbackHost("[::1]"));
        Assert.False(UrlGuard.IsLoopbackHost("lab.example.com"));
        Assert.False(UrlGuard.IsLoopbackHost(null));
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
    public void UrlGuard_CombineBrowserUrl_PreservesPathQueryAndPort()
    {
        Assert.Equal(
            "http://127.0.0.1:5080/api/health",
            UrlGuard.CombineBrowserUrl("http://localhost:5080", "api/health"));
        Assert.Equal(
            "https://lab.example.com:8443/ua/monitor.html?tenant=1",
            UrlGuard.CombineBrowserUrl("https://lab.example.com:8443/ua/?tenant=1", "monitor.html"));
        Assert.Equal(
            "http://127.0.0.1:5080/",
            UrlGuard.CombineBrowserUrl("http://localhost:5080", "/"));
        Assert.Null(UrlGuard.CombineBrowserUrl("file:///C:/Windows/notepad.exe", "api/health"));
    }

    [Fact]
    public void UtcClock_Parse_AssumesUtcWhenOffsetIsMissing()
    {
        var parsed = UtcClock.Parse("2026-11-24T10:30:00");
        Assert.Equal(TimeSpan.Zero, parsed.Offset);
        Assert.Equal("2026-11-24T10:30:00.0000000+00:00", UtcClock.Format(parsed));
    }

    [Fact]
    public void UtcClock_Parse_ConvertsExplicitOffsetAndRejectsPartialValues()
    {
        var parsed = UtcClock.Parse("2026-11-24T19:30:00+09:00");
        Assert.Equal(TimeSpan.Zero, parsed.Offset);
        Assert.Equal("2026-11-24T10:30:00.0000000+00:00", UtcClock.Format(parsed));
        Assert.Throws<FormatException>(() => UtcClock.Parse("2026-11-24"));
        Assert.Throws<FormatException>(() => UtcClock.Parse("not-a-date"));
    }

    [Fact]
    public void VersionGuard_NormalizesTagAndIgnoresPrereleaseMetadata()
    {
        Assert.Equal("0.1.36", VersionGuard.Normalize(" v0.1.36 "));
        Assert.True(VersionGuard.TryParse("v0.1.36-rc.1+build.4", out var parsed));
        Assert.Equal(new Version(0, 1, 36), parsed);
        Assert.True(VersionGuard.IsNewer("v0.1.36", "0.1.35"));
        Assert.False(VersionGuard.IsNewer("v0.1.35", "0.1.35"));
        Assert.False(VersionGuard.IsNewer("nightly", "0.1.35"));
    }

    [Fact]
    public void TextGuard_ValidatesIdentifiersAndTruncatesAuditText()
    {
        Assert.True(TextGuard.IsSafeIdentifier("pc-01"));
        Assert.True(TextGuard.IsSafeIdentifier("job_123"));
        Assert.False(TextGuard.IsSafeIdentifier("pc/01"));
        Assert.False(TextGuard.IsSafeIdentifier(new string('a', TextGuard.MaxIdentifierLength + 1)));
        Assert.Equal("abc", TextGuard.Truncate("abcdef", 3));
        Assert.Equal(string.Empty, TextGuard.Truncate(null, 3));
    }

    [Fact]
    public void JobStateRules_UsesOneTerminalAndBusyDefinition()
    {
        Assert.True(JobStateRules.IsTerminal("Completed"));
        Assert.True(JobStateRules.IsTerminal("Failed"));
        Assert.True(JobStateRules.IsTerminal("Cancelled"));
        Assert.False(JobStateRules.IsTerminal("Paused"));
        Assert.True(JobStateRules.IsBusy("Busy"));
        Assert.True(JobStateRules.IsBusy("Rebooting"));
        Assert.True(JobStateRules.IsBusy("Paused"));
        Assert.True(JobStateRules.IsActive("Installing"));
        Assert.False(JobStateRules.IsActive("Completed"));
    }

    [Fact]
    public void JobIdGuard_CreatesUtcBasedCollisionResistantId()
    {
        var utc = new DateTimeOffset(2026, 11, 24, 10, 30, 0, TimeSpan.Zero);
        var id = JobIdGuard.Create(utc.ToOffset(TimeSpan.FromHours(9)), Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"));

        Assert.StartsWith("job-20261124-103000-001122334455", id, StringComparison.Ordinal);
        Assert.Equal(32, id.Length);
    }

    [Fact]
    public void ProgressGuard_ClampsRunAndDisplayValues()
    {
        Assert.Equal(100, ProgressGuard.Percent(true, 1, 2));
        Assert.Equal(0, ProgressGuard.Percent(false, 0, 0));
        Assert.Equal(99, ProgressGuard.Percent(false, 10, 10));
        Assert.Equal(50, ProgressGuard.Percent(false, 1, 2));
        Assert.Equal(100, ProgressGuard.Display(500));
        Assert.Equal(0, ProgressGuard.Display(-5));
    }

    [Fact]
    public void WindowSizeGuard_ClampsInvalidAndExtremeValues()
    {
        Assert.Equal((1280, 800), WindowSizeGuard.Normalize(1280.9, 800.9));
        Assert.Equal(
            (WindowSizeGuard.DefaultWidth, WindowSizeGuard.DefaultHeight),
            WindowSizeGuard.Normalize(double.NaN, double.PositiveInfinity));
        Assert.Equal(
            (WindowSizeGuard.MinWidth, WindowSizeGuard.MinHeight),
            WindowSizeGuard.Normalize(-100, -100));
        Assert.Equal(
            (WindowSizeGuard.MaxWidth, WindowSizeGuard.MaxHeight),
            WindowSizeGuard.Normalize(99999, 99999));
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
