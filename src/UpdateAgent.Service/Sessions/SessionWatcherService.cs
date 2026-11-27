using Microsoft.Extensions.Options;
using UpdateAgent.Core.Sessions;
using UpdateAgent.Core.Windows;
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
    private readonly Dictionary<int, DateTimeOffset> _launched = new();
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
        var options = _options.CurrentValue;
        if (!options.OverlayEnabled || !WindowsOsInfoProvider.GetCurrent().SupportsOverlay)
        {
            return;
        }

        var executable = options.ResolveSessionHelperPath();
        if (!File.Exists(executable))
        {
            // 아직 빌드/배포 전이면 조용히 넘어간다. 오버레이만 비활성화된다.
            _logger.LogDebug("SessionHelper를 찾을 수 없습니다: {Path}", executable);
            return;
        }

        // 이름이 바뀐 헬퍼도 같은 프로세스로 인식해야 중복 실행되지 않는다.
        var processName = Path.GetFileNameWithoutExtension(executable);

        var probe = _probe.Probe();
        if (probe.ProbeFailed)
        {
            // 세션이 없는 것이 아니라 확인하지 못한 것이므로 다음 주기에 다시 시도한다.
            // 여기서 목록을 비우면 실행 중인 헬퍼를 모두 정리해 버린다.
            _logger.LogWarning(
                "사용자 세션을 확인할 수 없어 헬퍼 상태를 유지하고 다시 시도합니다: {Error}",
                probe.Error);
            return;
        }

        var sessions = probe.Sessions;
        foreach (var session in sessions)
        {
            if (IsHelperAlive(session.SessionId, processName))
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
                _launched[session.SessionId] = DateTimeOffset.Now;
                _logger.LogInformation(
                    "SessionHelper 실행: session={SessionId}",
                    session.SessionId);
            }
            else
            {
                _logger.LogWarning(
                    "SessionHelper 실행 실패: session={SessionId}, error={Error}",
                    session.SessionId,
                    error);
            }
        }

        foreach (var sessionId in _launched.Keys.ToList())
        {
            if (!sessions.Any(s => s.SessionId == sessionId))
            {
                _launched.Remove(sessionId);
                _lastAttempt.Remove(sessionId);
            }
        }
    }

    private bool IsHelperAlive(int sessionId, string processName)
    {
        // 서비스가 재시작되면 _launched가 비어 있지만 이전 helper는 여전히 살아 있을 수 있다.
        // 항상 실제 프로세스를 확인해 같은 세션에 오버레이가 두 개 뜨는 것을 막는다.
        try
        {
            var processes = System.Diagnostics.Process.GetProcessesByName(processName);
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
