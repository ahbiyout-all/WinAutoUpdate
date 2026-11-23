using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UpdateAgent.LabManager.Auth;
using UpdateAgent.LabManager.Configuration;
using UpdateAgent.Shared;

namespace UpdateAgent.Tests;

public sealed class AdminTokenStoreTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly string _overridePath;

    public AdminTokenStoreTests()
    {
        _testDirectory = Path.Combine(
            Path.GetTempPath(), "updateagent-admin-token-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDirectory);
        _overridePath = Path.Combine(_testDirectory, "runtime-admin-token.json");
    }

    [Fact]
    public void CurrentToken_UsesConfiguredToken_WhenNoOverrideExists()
    {
        var store = CreateStore(UpdateAgentConstants.SampleAdminToken);

        Assert.Equal(UpdateAgentConstants.SampleAdminToken, store.CurrentToken);
        Assert.False(store.HasRuntimeOverride);
        Assert.True(store.IsUsingPlaceholder);
    }

    [Fact]
    public async Task SetTokenAsync_AppliesImmediately_AndPersistsAcrossRestart()
    {
        var store = CreateStore(UpdateAgentConstants.SampleAdminToken);

        await store.SetTokenAsync("brand-new-admin-secret");

        Assert.Equal("brand-new-admin-secret", store.CurrentToken);
        Assert.True(store.HasRuntimeOverride);
        Assert.False(store.IsUsingPlaceholder);
        Assert.True(File.Exists(_overridePath));

        var restarted = CreateStore(UpdateAgentConstants.SampleAdminToken);
        Assert.Equal("brand-new-admin-secret", restarted.CurrentToken);
        Assert.True(restarted.HasRuntimeOverride);
    }

    [Fact]
    public async Task SetTokenAsync_TrimsWhitespace()
    {
        var store = CreateStore(UpdateAgentConstants.SampleAdminToken);

        await store.SetTokenAsync("  trimmed-admin-token  ");

        Assert.Equal("trimmed-admin-token", store.CurrentToken);
    }

    [Fact]
    public async Task SetTokenAsync_RejectsShortTokens()
    {
        var store = CreateStore(UpdateAgentConstants.SampleAdminToken);

        var shortToken = await Assert.ThrowsAsync<ArgumentException>(
            () => store.SetTokenAsync("short"));
        Assert.Contains("8자", shortToken.Message);
        Assert.False(File.Exists(_overridePath));
    }

    [Fact]
    public async Task SetTokenAsync_RejectsSampleTokens()
    {
        var store = CreateStore(UpdateAgentConstants.SampleAdminToken);

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.SetTokenAsync(UpdateAgentConstants.SampleAdminToken));
        Assert.False(File.Exists(_overridePath));
    }

    [Fact]
    public void LoadOverrideToken_IgnoresPlaceholderTokenInFile()
    {
        File.WriteAllText(
            _overridePath,
            "{\"AdminToken\":\"" + UpdateAgentConstants.SampleAdminToken + "\"}",
            new UTF8Encoding(false));
        var store = CreateStore(UpdateAgentConstants.SampleAdminToken);

        Assert.Equal(UpdateAgentConstants.SampleAdminToken, store.CurrentToken);
        Assert.False(store.HasRuntimeOverride);
    }

    [Fact]
    public void LoadOverrideToken_IgnoresMalformedFile()
    {
        File.WriteAllText(_overridePath, "not json", new UTF8Encoding(false));
        var store = CreateStore(UpdateAgentConstants.SampleAdminToken);

        Assert.Equal(UpdateAgentConstants.SampleAdminToken, store.CurrentToken);
        Assert.False(store.HasRuntimeOverride);
    }

    [Fact]
    public void AllowDefaultTokens_PermitsSampleOverrideInTests()
    {
        File.WriteAllText(
            _overridePath,
            "{\"AdminToken\":\"" + UpdateAgentConstants.SampleAdminToken + "\"}",
            new UTF8Encoding(false));
        var store = CreateStore(UpdateAgentConstants.SampleAdminToken, allowDefaultTokens: true);

        Assert.Equal(UpdateAgentConstants.SampleAdminToken, store.CurrentToken);
        Assert.True(store.HasRuntimeOverride);
    }

    // 실제 배포 경로(icacls ACL 제한)에서도 재시작 후 override를 읽을 수 있어야 한다.
    // 현재 사용자 SID가 ACL에서 빠지면 UAC 필터 토큰으로 읽지 못해 이 테스트가 실패한다.
    [Fact]
    public async Task SetTokenAsync_WithAclRestriction_PersistsAcrossRestart()
    {
        var store = CreateStore(UpdateAgentConstants.SampleAdminToken, restrictAcl: true);

        await store.SetTokenAsync("acl-restricted-admin-secret");

        var restarted = CreateStore(UpdateAgentConstants.SampleAdminToken, restrictAcl: true);
        Assert.Equal("acl-restricted-admin-secret", restarted.CurrentToken);
        Assert.True(restarted.HasRuntimeOverride);
    }

    private AdminTokenStore CreateStore(
        string configuredToken,
        bool allowDefaultTokens = false,
        bool restrictAcl = false)
    {
        var options = new LabManagerOptions
        {
            AdminToken = configuredToken,
            AgentToken = UpdateAgentConstants.SampleAgentToken,
            AllowDefaultTokens = allowDefaultTokens,
        };
        var monitor = new TestOptionsMonitor(options);
        return new AdminTokenStore(
            monitor, _overridePath, NullLogger<AdminTokenStore>.Instance, restrictAcl);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory)) Directory.Delete(_testDirectory, recursive: true);
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
}
