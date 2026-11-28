using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UpdateAgent.Guards;
using UpdateAgent.LabManager.Auth;
using UpdateAgent.LabManager.Configuration;
using UpdateAgent.LabManager.Services;
using UpdateAgent.LabManager.Storage;
using UpdateAgent.Shared;
using UpdateAgent.Shared.Serialization;

namespace UpdateAgent.Tests;

/// <summary>
/// 개선 6(토큰 회전 스케줄러)의 회귀 테스트. 옵트인 기본값, 강한 토큰 생성,
/// 첫 줄만 쓰는 파일, 지문 전용 감사, 롤백/fail-closed, UI 재입력과 비밀값 비누출을 확인한다.
/// </summary>
public sealed class Improvement06TokenRotationTests : IDisposable
{
    private const string InitialToken = "initial-admin-token-123456";
    private const string StrongCandidate = "rotate-strong-candidate-987654";
    private const string DecoyToken = "decoy-admin-token-999999";

    private static readonly DateTimeOffset Now =
        new(2026, 11, 28, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root;
    private readonly string _overridePath;
    private readonly string _auditPath;
    private readonly MutableOptionsMonitor _options;
    private DateTimeOffset _now;

    public Improvement06TokenRotationTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(), "updateagent-improvement06", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _overridePath = Path.Combine(_root, "runtime-admin-token.json");
        _auditPath = Path.Combine(_root, "runtime-admin-token.audit.log");
        _now = Now;
        _options = new MutableOptionsMonitor(new LabManagerOptions
        {
            AdminToken = UpdateAgentConstants.SampleAdminToken,
            AgentToken = UpdateAgentConstants.SampleAgentToken,
            AllowDefaultTokens = false,
        });
    }

    [Fact]
    public async Task DefaultOptions_AreOptIn_AndServiceDoesNotRotateOnStartup()
    {
        var options = new LabManagerOptions();
        Assert.False(options.TokenRotation.Enabled);
        Assert.Equal(60, options.TokenRotation.CheckIntervalMinutes);
        Assert.Equal(TimeSpan.FromMinutes(5), TokenRotationOptions.ResolveInterval(0));
        Assert.Equal(TimeSpan.FromMinutes(60), TokenRotationOptions.ResolveInterval(60));
        Assert.Equal(TimeSpan.FromMinutes(1440), TokenRotationOptions.ResolveInterval(99999));

        var store = CreateStore();
        await store.SetTokenAsync(InitialToken);
        _now = Now.AddDays(AdminTokenStore.RotationAfterDays + 1);

        // 시작 직후에는 주기 대기만 한다. 조건이 이미 만족되어도 서비스 시작이 회전을 유발하지 않는다.
        using var cts = new CancellationTokenSource();
        var service = CreateService(store);
        await service.StartAsync(cts.Token);
        await Task.Delay(80, cts.Token);
        await service.StopAsync(cts.Token);

        Assert.Equal(InitialToken, store.CurrentToken);
        Assert.False(await service.CheckAndRotateAsync(cts.Token));
        Assert.Equal(InitialToken, store.CurrentToken);
        Assert.DoesNotContain("\ttoken-rotated\t", string.Join("\n", AuditLines()));
    }

    [Fact]
    public void DependencyInjection_ResolvesHostedService_WithoutManualIntervalRegistration()
    {
        var store = CreateStore();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOptionsMonitor<LabManagerOptions>>(_options);
        services.AddSingleton(store);
        services.AddHostedService<AdminTokenRotationService>();
        using var provider = services.BuildServiceProvider();

        var hosted = Assert.Single(
            provider.GetServices<IHostedService>().OfType<AdminTokenRotationService>());
        Assert.NotNull(hosted);
    }

    [Fact]
    public async Task DueAndEnabled_RotatesExactlyOnce_WithStrongDifferentToken()
    {
        _options.CurrentValue.TokenRotation.Enabled = true;
        var store = CreateStore();
        await store.SetTokenAsync(InitialToken);
        _now = Now.AddDays(AdminTokenStore.RotationAfterDays + 1);
        var service = CreateService(store);

        var results = await Task.WhenAll(
            service.CheckAndRotateAsync(default),
            service.CheckAndRotateAsync(default));

        Assert.Equal(1, results.Count(result => result));
        var rotated = store.CurrentToken;
        Assert.NotEqual(InitialToken, rotated);
        Assert.NotEqual(_options.CurrentValue.AgentToken, rotated);
        Assert.False(TokenGuard.IsPlaceholder(rotated));
        Assert.True(rotated.Length >= TokenGuard.MinimumLength);
        Assert.Matches("^[A-Za-z0-9_-]+$", rotated);
        Assert.Equal(rotated, ReadPersistedToken());

        var status = store.GetStatus();
        Assert.True(status.RotationEnabled);
        Assert.Equal(_now, status.LastChangedAt);
        Assert.False(status.RotationDue);
        Assert.Equal(1, AuditLines().Count(line => line.Contains("\ttoken-rotated\t", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task FirstLineOnlyFile_IsPreserved_AndTrailingLineIsIgnored()
    {
        var store = CreateStore();
        await store.SetTokenAsync(InitialToken);
        await store.RotateAsync(default);
        var rotated = store.CurrentToken;

        var text = File.ReadAllText(_overridePath);
        Assert.Single(File.ReadLines(_overridePath));
        Assert.DoesNotContain("\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", text, StringComparison.Ordinal);
        Assert.Equal(rotated, ReadPersistedToken());

        File.AppendAllText(
            _overridePath,
            "\n" + JsonSerializer.Serialize(new { AdminToken = DecoyToken }));
        var reloaded = CreateStore();
        Assert.Equal(rotated, reloaded.CurrentToken);
        Assert.DoesNotContain(DecoyToken, string.Join("\n", AuditLines()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentTokenCollision_IsRejected_WithoutWritingOrLeaking()
    {
        var agentToken = _options.CurrentValue.AgentToken;
        await AssertRotationRejected(() => agentToken, agentToken);
    }

    [Fact]
    public async Task CurrentTokenCollision_IsRejected_WithoutWritingOrLeaking()
    {
        await AssertRotationRejected(() => InitialToken, InitialToken);
    }

    [Fact]
    public async Task PlaceholderCandidate_IsRejected_WithoutWritingOrLeaking()
    {
        var placeholder = UpdateAgentConstants.SampleAdminToken;
        await AssertRotationRejected(() => placeholder, placeholder);
    }

    [Fact]
    public async Task PlaceholderCurrentToken_FailsClosedForRotation()
    {
        var store = CreateStore(
            configuredAdminToken: UpdateAgentConstants.SampleAdminToken,
            tokenGenerator: () => StrongCandidate);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.RotateAsync(default));

        Assert.DoesNotContain(StrongCandidate, error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(_overridePath));
        Assert.DoesNotContain("\ttoken-rotated\t", string.Join("\n", AuditLines()), StringComparison.Ordinal);
        Assert.Contains(AuditLines(), line => line.EndsWith("\trejected", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RotationWriteFailure_RollsBack_PreservingPreviousToken()
    {
        var failedOnce = false;
        Action<string, string, bool> moveFile = (source, destination, overwrite) =>
        {
            if (!failedOnce && File.ReadAllText(source).Contains(StrongCandidate, StringComparison.Ordinal))
            {
                failedOnce = true;
                throw new IOException("모의 이동 실패");
            }
            File.Move(source, destination, overwrite);
        };
        var store = CreateStore(moveFile: moveFile, tokenGenerator: () => StrongCandidate);
        await store.SetTokenAsync(InitialToken);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.RotateAsync(default));

        Assert.True(failedOnce);
        Assert.Equal(InitialToken, store.CurrentToken);
        Assert.Equal(InitialToken, ReadPersistedToken());
        Assert.DoesNotContain(StrongCandidate, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(StrongCandidate, string.Join("\n", AuditLines()), StringComparison.Ordinal);
        Assert.DoesNotContain("\ttoken-rotated\t", string.Join("\n", AuditLines()), StringComparison.Ordinal);
        Assert.Contains(
            AuditLines(),
            line => line.Contains(
                "\trolled-back\t" + TokenAuditLog.Fingerprint(InitialToken), StringComparison.Ordinal));
    }

    [Fact]
    public async Task RotationAuditFailure_FailsClosed_WithoutTokenInMessage()
    {
        // 디렉터리를 감사 로그 경로로 지정해 기록 실패를 재현한다.
        var brokenAudit = new TokenAuditLog(_root, NullLogger.Instance);
        var store = CreateStore(
            configuredAdminToken: InitialToken,
            auditLog: brokenAudit,
            tokenGenerator: () => StrongCandidate);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.RotateAsync(default));
        var status = store.GetStatus();

        Assert.DoesNotContain(StrongCandidate, error.Message, StringComparison.Ordinal);
        Assert.Equal(string.Empty, store.CurrentToken);
        Assert.False(status.AuditAvailable);
        Assert.False(status.HasRuntimeOverride);
        Assert.Null(status.LastChangedAt);
    }

    [Fact]
    public void Status_IsHonestForMissingData_AndNeverIncludesToken()
    {
        var store = CreateStore();
        var status = store.GetStatus();

        Assert.True(status.IsPlaceholder);
        Assert.False(status.HasRuntimeOverride);
        Assert.Null(status.LastChangedAt);
        Assert.False(status.RotationDue);
        Assert.False(status.RotationEnabled);

        var serialized = UpdateAgentJson.Serialize(status);
        Assert.DoesNotContain(_options.CurrentValue.AdminToken, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(_options.CurrentValue.AgentToken, serialized, StringComparison.Ordinal);

        _options.CurrentValue.TokenRotation.Enabled = true;
        Assert.True(store.GetStatus().RotationEnabled);
        Assert.Null(store.GetStatus().LastChangedAt);
    }

    [Fact]
    public async Task MissingRotationConfig_StaysDisabled_AndNeverThrows()
    {
        _options.CurrentValue.TokenRotation = null!;
        var store = CreateStore();
        await store.SetTokenAsync(InitialToken);
        _now = Now.AddDays(AdminTokenStore.RotationAfterDays + 1);
        var service = CreateService(store);

        Assert.False(store.GetStatus().RotationEnabled);
        Assert.False(await service.CheckAndRotateAsync(default));
        Assert.Equal(InitialToken, store.CurrentToken);
        Assert.DoesNotContain("\ttoken-rotated\t", string.Join("\n", AuditLines()));
    }

    [Fact]
    public async Task NoPlaintextToken_InStatusOrAuditResponses()
    {
        var store = CreateStore();
        await store.SetTokenAsync(InitialToken);
        await store.RotateAsync(default);
        var rotated = store.CurrentToken;

        var exposed = UpdateAgentJson.Serialize(new
        {
            status = store.GetStatus(),
            audit = AuditLines(),
        });
        foreach (var secret in new[] { InitialToken, rotated })
            Assert.DoesNotContain(secret, exposed, StringComparison.Ordinal);

        Assert.Contains(
            AuditLines(),
            line => line.EndsWith("\t" + TokenAuditLog.Fingerprint(rotated), StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoPlaintextToken_InApplicationLogs()
    {
        var logger = new CapturingLogger<AdminTokenStore>();
        var store = CreateStore(logger: logger);
        await store.SetTokenAsync(InitialToken);
        await store.RotateAsync(default);
        var rotated = store.CurrentToken;

        var logText = string.Join("\n", logger.Messages);
        Assert.NotEmpty(logger.Messages);
        Assert.Contains("길이: ", logText, StringComparison.Ordinal);
        foreach (var secret in new[] { InitialToken, rotated })
            Assert.DoesNotContain(secret, logText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LabAudit_RecordsTokenRotate_WithoutTokenValue()
    {
        var database = Path.Combine(_root, "lab.db");
        var store = new LabStore(database);
        await store.InitializeAsync();
        try
        {
            await store.AddAuditAsync(
                "teacher", "token-rotate", "admin", "관리자 토큰 회전", default);
            var audits = await store.GetAuditAsync();
            var entry = Assert.Single(audits, audit => audit.Action == "token-rotate");
            var serialized = UpdateAgentJson.Serialize(entry);
            Assert.DoesNotContain(StrongCandidate, serialized, StringComparison.Ordinal);
            Assert.DoesNotContain(InitialToken, serialized, StringComparison.Ordinal);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task UnauthenticatedRotation_IsRejectedByExistingAdminMiddleware()
    {
        var store = CreateStore();
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/api/admin/token/rotate";
        var nextCalled = false;
        var middleware = new TokenAuthMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            _options,
            store,
            NullLogger<TokenAuthMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task Cancellation_StopsRotationCheck_BeforeChangingToken()
    {
        _options.CurrentValue.TokenRotation.Enabled = true;
        var store = CreateStore();
        await store.SetTokenAsync(InitialToken);
        _now = Now.AddDays(AdminTokenStore.RotationAfterDays + 1);
        var service = CreateService(store);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.CheckAndRotateAsync(cts.Token));

        Assert.Equal(InitialToken, store.CurrentToken);
    }

    [Fact]
    public void TeacherUiDocsAndApi_ShowRotationFields_AndReentryFlow()
    {
        var app = Read("src/UpdateAgent.LabManager/wwwroot/app.js");
        var tokenDialog = Slice(app, "function setupTokenDialog", "function wireEvents");
        foreach (var marker in new[]
                 {
                     "자동 회전", "사용 안 함", "마지막 변경", "회전 필요",
                     "ABSENT", "UNKNOWN", "/api/admin/token/rotate",
                     "새 토큰은 교사 PC 설정 파일에서 확인 후 다시 입력해야 합니다.",
                     "localStorage.removeItem(STORAGE_KEY)",
                 })
        {
            Assert.Contains(marker, tokenDialog, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("console.log", tokenDialog, StringComparison.OrdinalIgnoreCase);
        var rotateHandler = Slice(tokenDialog, "const rotateServerToken", "$('#token-save')");
        Assert.DoesNotContain("newToken", rotateHandler, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("body:", rotateHandler, StringComparison.OrdinalIgnoreCase);

        var html = Read("src/UpdateAgent.LabManager/wwwroot/index.html");
        Assert.Contains("id=\"token-rotate\"", html);
        Assert.Contains("지금 회전", html);
        Assert.Contains("다시 입력해야 합니다", html);

        var program = Read("src/UpdateAgent.LabManager/Program.cs");
        Assert.Contains("AddHostedService<AdminTokenRotationService>()", program);
        Assert.Contains("\"/api/admin/token/rotate\"", program);
        var endpoint = Slice(program, "\"/api/admin/token/rotate\"", "// ---------------- 정책/감사");
        Assert.Contains("token-rotate", endpoint);
        Assert.Contains("GetStatus()", endpoint);
        Assert.DoesNotContain("NewToken", endpoint);

        var worklog = Read("docs/WORKLOG.md");
        var heading = "## 2026-11-28 개선 6 — 토큰 회전 스케줄러";
        var start = worklog.IndexOf(heading, StringComparison.Ordinal);
        var next = worklog.IndexOf("## 2026-11-28 개선 5", StringComparison.Ordinal);
        Assert.True(start >= 0 && next > start, "개선 6은 개선 5 위에 있어야 합니다.");
        var section = worklog[start..next];
        var tableStart = section.IndexOf("| 회차 |", StringComparison.Ordinal);
        var tableEnd = section.IndexOf("\n\n", tableStart, StringComparison.Ordinal);
        Assert.True(tableStart >= 0 && tableEnd > tableStart);
        var table = section[tableStart..tableEnd];
        Assert.Equal(10, Regex.Matches(table, @"^\| \d+ \|", RegexOptions.Multiline).Count);

        var previous = -1;
        foreach (var dimension in new[]
                 {
                     "요구사항 정합성", "하위 호환성", "토큰 생성 강도", "원자성·롤백·fail-closed",
                     "스케줄러 반복·취소", "API 인증·상태 정직성", "교사 UI 재입력",
                     "한국어 문서/UI 정합성", "회귀 범위", "최종 검증",
                 })
        {
            var position = table.IndexOf(dimension, StringComparison.Ordinal);
            Assert.True(position > previous, $"회차 순서가 올바르지 않습니다: {dimension}");
            previous = position;
        }

        var deployment = Read("docs/DEPLOYMENT.md");
        foreach (var keyword in new[]
                 {
                     "8-G", "옵트인", "기본값이 사용 안 함", "90일", "runtime-admin-token.json",
                     "첫 줄", "다시 입력", "token-rotated", "token-rotate", "rolled-back",
                     "없음", "확인 불가", "토큰 원문",
                 })
        {
            Assert.Contains(keyword, deployment, StringComparison.Ordinal);
        }
    }

    private async Task AssertRotationRejected(Func<string> generator, string rejectedValue)
    {
        var store = CreateStore(tokenGenerator: generator);
        await store.SetTokenAsync(InitialToken);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.RotateAsync(default));
        var audit = string.Join("\n", AuditLines());

        Assert.DoesNotContain(rejectedValue, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(rejectedValue, audit, StringComparison.Ordinal);
        Assert.Equal(InitialToken, store.CurrentToken);
        Assert.Equal(InitialToken, ReadPersistedToken());
        Assert.DoesNotContain("\ttoken-rotated\t", audit, StringComparison.Ordinal);
        Assert.Contains(AuditLines(), line => line.EndsWith("\trejected", StringComparison.Ordinal));
    }

    private AdminTokenStore CreateStore(
        string? configuredAdminToken = null,
        TokenAuditLog? auditLog = null,
        Action<string, string, bool>? moveFile = null,
        Func<string>? tokenGenerator = null,
        ILogger<AdminTokenStore>? logger = null)
    {
        if (configuredAdminToken is not null)
        {
            _options.CurrentValue = new LabManagerOptions
            {
                AdminToken = configuredAdminToken,
                AgentToken = UpdateAgentConstants.SampleAgentToken,
                AllowDefaultTokens = false,
            };
        }
        return new AdminTokenStore(
            _options,
            _overridePath,
            logger ?? NullLogger<AdminTokenStore>.Instance,
            restrictAcl: false,
            auditLog: auditLog,
            clock: () => _now,
            moveFile: moveFile,
            tokenGenerator: tokenGenerator);
    }

    private AdminTokenRotationService CreateService(
        AdminTokenStore store,
        Func<TimeSpan>? intervalProvider = null) =>
        new(
            store,
            _options,
            NullLogger<AdminTokenRotationService>.Instance,
            intervalProvider ?? (() => TimeSpan.FromMinutes(5)));

    private List<string> AuditLines() => File.Exists(_auditPath)
        ? File.ReadAllLines(_auditPath).Where(line => line.Length > 0).ToList()
        : new List<string>();

    private string ReadPersistedToken()
    {
        var firstLine = File.ReadLines(_overridePath).First();
        using var document = JsonDocument.Parse(firstLine);
        return document.RootElement.GetProperty("AdminToken").GetString() ?? string.Empty;
    }

    private static string Slice(string text, string startMarker, string endMarker)
    {
        var start = text.IndexOf(startMarker, StringComparison.Ordinal);
        var end = text.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, $"구간을 찾지 못했습니다: {startMarker}");
        return text[start..end];
    }

    private static string Read(string relativePath)
    {
        var path = Path.Combine(FindRepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"Missing repository file: {relativePath}");
        return File.ReadAllText(path, System.Text.Encoding.UTF8).Replace("\r\n", "\n");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Repository root not found.");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class MutableOptionsMonitor : IOptionsMonitor<LabManagerOptions>
    {
        public MutableOptionsMonitor(LabManagerOptions value) => CurrentValue = value;

        public LabManagerOptions CurrentValue { get; set; }

        public LabManagerOptions Get(string? name) => CurrentValue;

        public IDisposable OnChange(Action<LabManagerOptions, string?> listener) => new NoopDisposable();
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
