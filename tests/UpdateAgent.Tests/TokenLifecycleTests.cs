using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UpdateAgent.LabManager.Auth;
using UpdateAgent.LabManager.Configuration;
using UpdateAgent.Shared;

namespace UpdateAgent.Tests;

public sealed class TokenLifecycleTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly string _overridePath;
    private readonly string _auditPath;

    public TokenLifecycleTests()
    {
        _testDirectory = Path.Combine(
            Path.GetTempPath(), "updateagent-token-lifecycle-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDirectory);
        _overridePath = Path.Combine(_testDirectory, "runtime-admin-token.json");
        _auditPath = Path.Combine(_testDirectory, "runtime-admin-token.audit.log");
    }

    [Fact]
    public async Task Changed_RecordsFingerprintButNeverRawToken()
    {
        const string token = "lifecycle-admin-secret-1234";
        var store = CreateStore();

        await store.SetTokenAsync(token);

        var line = Assert.Single(ReadAudit());
        Assert.Contains("\tchanged\t", line, StringComparison.Ordinal);
        var fingerprint = TokenAuditLog.Fingerprint(token);
        Assert.Matches("^[0-9a-f]{12}$", fingerprint);
        Assert.EndsWith("\t" + fingerprint, line);
        Assert.DoesNotContain(token, line, StringComparison.Ordinal);

        var status = store.GetStatus();
        Assert.NotNull(status.LastChangedAt);
        Assert.False(status.RotationDue);
        Assert.True(status.AuditAvailable);
    }

    [Fact]
    public async Task Rejected_RecordsEventWithoutFingerprintOrToken()
    {
        const string shortToken = "short";
        const string agentToken = "lifecycle-agent-token";
        var store = CreateStore(agentToken: agentToken);

        await Assert.ThrowsAsync<ArgumentException>(() => store.SetTokenAsync(shortToken));
        await Assert.ThrowsAsync<ArgumentException>(
            () => store.SetTokenAsync(UpdateAgentConstants.SampleAdminToken));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SetTokenAsync(agentToken));

        var audit = ReadAudit();
        Assert.Equal(
            3,
            audit.Count(line => line.EndsWith("\trejected", StringComparison.Ordinal)));
        Assert.All(audit, line => Assert.Equal(2, line.Split('\t').Length));

        var allLines = string.Join("\n", audit);
        foreach (var rejectedToken in new[] { shortToken, UpdateAgentConstants.SampleAdminToken, agentToken })
            Assert.DoesNotContain(rejectedToken, allLines, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RotationDue_UsesInjectedClock()
    {
        var now = DateTimeOffset.Parse(
            "2026-01-01T00:00:00Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        var store = CreateStore(clock: () => now);

        await store.SetTokenAsync("rotation-admin-secret");

        Assert.Equal(now, store.GetStatus().LastChangedAt);
        Assert.False(store.GetStatus().RotationDue);

        now = now.AddDays(AdminTokenStore.RotationAfterDays);
        Assert.False(store.GetStatus().RotationDue);

        now = now.AddTicks(1);
        Assert.True(store.GetStatus().RotationDue);
    }

    [Fact]
    public async Task WriteFailure_RestoresPreviousTokenAndRecordsRolledBack()
    {
        const string previous = "previous-admin-secret";
        const string replacement = "replacement-admin-secret";
        var failedOnce = false;
        Action<string, string, bool> move = (source, destination, overwrite) =>
        {
            if (!failedOnce && File.ReadAllText(source).Contains(replacement, StringComparison.Ordinal))
            {
                failedOnce = true;
                throw new IOException("모의 이동 실패");
            }
            File.Move(source, destination, overwrite);
        };
        var store = CreateStore(moveFile: move);

        await store.SetTokenAsync(previous);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SetTokenAsync(replacement));

        Assert.True(failedOnce);
        Assert.Equal(previous, store.CurrentToken);
        Assert.True(store.HasRuntimeOverride);
        Assert.Equal(previous, ReadPersistedToken());
        Assert.Contains(
            ReadAudit(),
            line => line.Contains(
                "\trolled-back\t" + TokenAuditLog.Fingerprint(previous), StringComparison.Ordinal));
        Assert.DoesNotContain(replacement, string.Join("\n", ReadAudit()), StringComparison.Ordinal);
    }

    [Fact]
    public void Trim_KeepsNewestEventWithinSizeLimit()
    {
        var auditLog = new TokenAuditLog(_auditPath, NullLogger.Instance);
        var oldLines = new List<string>();
        for (var index = 0; index < 5500; index++)
            oldLines.Add("2026-01-01T00:00:00.0000000Z\tloaded\t" + index.ToString("x12"));
        File.WriteAllText(_auditPath, string.Join("\n", oldLines) + "\n", new UTF8Encoding(false));

        var fingerprint = TokenAuditLog.Fingerprint("trim-marker-token");
        auditLog.Record(TokenAuditLog.Changed, fingerprint);

        var remaining = File.ReadAllLines(_auditPath);
        Assert.True(new FileInfo(_auditPath).Length <= TokenAuditLog.MaxFileBytes);
        Assert.Contains(remaining, line => line.EndsWith("\t" + fingerprint, StringComparison.Ordinal));
        Assert.DoesNotContain(
            remaining,
            line => line.EndsWith("\t000000000000", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AuditFailure_FailsClosedWithoutTokenInException()
    {
        // 디렉터리를 감사 로그 경로로 지정해 기록 실패를 재현한다.
        var auditLog = new TokenAuditLog(_testDirectory, NullLogger.Instance);
        var store = CreateStore(auditLog: auditLog);

        const string token = "audit-failure-admin-secret";
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.SetTokenAsync(token));

        Assert.DoesNotContain(token, error.Message, StringComparison.Ordinal);
        Assert.Equal(string.Empty, store.CurrentToken);
        Assert.False(store.GetStatus().AuditAvailable);
    }

    [Fact]
    public void RestrictedAuditLog_RemainsReadable()
    {
        if (!OperatingSystem.IsWindows()) return;

        var auditLog = new TokenAuditLog(_auditPath, NullLogger.Instance, restrictAcl: true);
        auditLog.Record(TokenAuditLog.Loaded, TokenAuditLog.Fingerprint("acl-audit-token"));

        Assert.Contains("\tloaded\t", File.ReadAllText(_auditPath), StringComparison.Ordinal);
    }

    private AdminTokenStore CreateStore(
        string? configuredToken = null,
        string agentToken = UpdateAgentConstants.SampleAgentToken,
        Func<DateTimeOffset>? clock = null,
        TokenAuditLog? auditLog = null,
        Action<string, string, bool>? moveFile = null)
    {
        var options = new LabManagerOptions
        {
            AdminToken = configuredToken ?? UpdateAgentConstants.SampleAdminToken,
            AgentToken = agentToken,
            AllowDefaultTokens = false,
        };
        var monitor = new TestOptionsMonitor(options);
        return new AdminTokenStore(
            monitor,
            _overridePath,
            NullLogger<AdminTokenStore>.Instance,
            restrictAcl: false,
            auditLog: auditLog,
            clock: clock,
            moveFile: moveFile);
    }

    private List<string> ReadAudit() => File.Exists(_auditPath)
        ? File.ReadAllLines(_auditPath).Where(line => line.Length > 0).ToList()
        : new List<string>();

    private string ReadPersistedToken()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(_overridePath));
        return document.RootElement.GetProperty("AdminToken").GetString() ?? string.Empty;
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
