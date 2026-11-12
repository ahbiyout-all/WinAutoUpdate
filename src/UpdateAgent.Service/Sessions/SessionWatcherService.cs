using Microsoft.Extensions.Options;
using UpdateAgent.Core.Sessions;
using UpdateAgent.Service.Configuration;

namespace UpdateAgent.Service.Sessions;

/// <summary>
/// 로그온한 사용자 세션에 SessionHelper(WPF 오버레이)를 실행한다.
/// 세션이 끝나면 프로세스도 함께 종료된다.
/// </summary>
public sealed class SessionWatcherService : BackgroundService
{
    private readonly IUserSessionProbe _probe;
    private readonly SessionLauncher _launcher;
    private readonly IOptionsMonitor<AgentOptions> _options;
    private readonly ILogger<SessionWatcherService> _logger;
    private readonly Dictionary<int, (int ProcessId, DateTimeOffset StartedAt)> _launched = new();
    private readonly Dictionary<int, DateTimeOffset> _lastAttempt = new();

    public SessionWatcherService(
        IUserSessionProbe probe,
        SessionLauncher launcher,
        IOptionsMonitor<AgentOptions> options,
        ILogger<SessionWatcherService> logger)
    {
        _probe = probe;
        _launcher = launcher;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                EnsureHelpers();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "세션 헬퍼 확인 중 오류");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void EnsureHelpers()
    {
        var executable = _options.CurrentValue.ResolveSessionHelperPath();
        if (!File.Exists(executable))
        {
            // 아직 빌드/배포 전이면 조용히 넘어간다. 오버레이만 비활성화된다.
            _logger.LogDebug("SessionHelper를 찾을 수 없습니다: {Path}", executable);
            return;
        }

        foreach (var session in _probe.GetInteractiveSessions())
        {
            if (IsHelperAlive(session.SessionId))
            {
                continue;
            }

            if (_lastAttempt.TryGetValue(session.SessionId, out var last)
                && DateTimeOffset.Now - last < TimeSpan.FromSeconds(60))
            {
                continue;
            }

            _lastAttempt[session.SessionId] = DateTimeOffset.Now;

            var ok = _launcher.TryLaunch(
                session.SessionId,
                executable,
                $"--session {session.SessionId}",
                out var error);

            if (ok)
            {
                _launched[session.SessionId] = (0, DateTimeOffset.Now);
                _logger.LogInformation(
                    "SessionHelper 실행: session={SessionId}, user={User}",
                    session.SessionId,
                    session.DisplayName);
            }
            else
            {
                _logger.LogWarning(
                    "SessionHelper 실행 실패: session={SessionId}, user={User}, error={Error}",
                    session.SessionId,
                    session.DisplayName,
                    error);
            }
        }

        foreach (var sessionId in _launched.Keys.ToList())
        {
            if (!_probe.GetInteractiveSessions().Any(s => s.SessionId == sessionId))
            {
                _launched.Remove(sessionId);
                _lastAttempt.Remove(sessionId);
            }
        }
    }

    private bool IsHelperAlive(int sessionId)
    {
        if (!_launched.TryGetValue(sessionId, out var info))
        {
            return false;
        }

        try
        {
            var processes = System.Diagnostics.Process.GetProcessesByName("UpdateAgent.SessionHelper");
            var alive = processes.Any(p =>
            {
                try
                {
                    return p.SessionId == sessionId;
                }
                catch
                {
                    return false;
                }
                finally
                {
                    p.Dispose();
                }
            });

            if (!alive) _launched.Remove(sessionId);
            return alive;
        }
        catch
        {
            // 확인 실패 시 중복 실행을 막기 위해 살아 있다고 간주한다.
            return true;
        }
    }
}
