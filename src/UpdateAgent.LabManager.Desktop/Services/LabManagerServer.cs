using System.Diagnostics;
using System.IO;
using System.Net.Http;
using UpdateAgent.Guards;

namespace UpdateAgent.LabManager.Desktop.Services;

/// <summary>
/// 데스크톱 창이 로컬 LabManager API 서버를 준비한다.
/// 이미 실행 중이면 재사용하고, 로컬 주소인데 서버가 없으면 같은 폴더의
/// UpdateAgent.LabManager.exe를 --headless로 조용히 띄운다.
/// 데스크톱이 띄운 서버는 런처/앱 창이 닫힌 뒤에도 프로세스가 계속 살아 있다.
/// </summary>
public sealed class LabManagerServer
{
    private const string ServerExecutable = "UpdateAgent.LabManager.exe";
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(6);

    private readonly DesktopSettings _settings;
    private Process? _process;

    public LabManagerServer(DesktopSettings settings)
    {
        _settings = settings;
    }

    /// <summary>이 데스크톱 프로세스가 서버를 직접 시작했는지.</summary>
    public bool StartedByDesktop => _process is not null;

    /// <summary>로컬 서버를 시작했지만 곧 종료되었는지(예: 기본 토큰 거부).</summary>
    public bool LocalServerExited { get; private set; }

    /// <summary>
    /// 서버가 준비되었으면 true. 원격 주소인데 연결할 수 없거나 로컬 서버 시작에 실패하면 false.
    /// </summary>
    public async Task<bool> EnsureRunningAsync(CancellationToken cancellationToken = default)
    {
        LocalServerExited = false;

        if (await IsHealthyAsync(cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        if (!_settings.IsLocalServer)
        {
            return false;
        }

        var executable = Path.Combine(AppContext.BaseDirectory, ServerExecutable);
        if (!File.Exists(executable))
        {
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "--headless",
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            _process = Process.Start(startInfo);
            if (_process is null)
            {
                return false;
            }
        }
        catch
        {
            _process = null;
            return false;
        }

        try
        {
            var deadline = UtcClock.Now + StartupTimeout;
            while (UtcClock.Now < deadline)
            {
                await Task.Delay(300, cancellationToken).ConfigureAwait(false);

                if (await IsHealthyAsync(cancellationToken).ConfigureAwait(false))
                {
                    return true;
                }

                if (_process.HasExited)
                {
                    LocalServerExited = true;
                    Stop();
                    return false;
                }
            }

            // 시작에 실패한 프로세스를 남기면 다음 실행에서 포트가 점유된 채로 남는다.
            Stop();
            return false;
        }
        catch (OperationCanceledException)
        {
            Stop();
            throw;
        }
    }

    private async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var client = new HttpClient { Timeout = HealthTimeout };
            var url = BuildHealthUrl();
            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 로컬 주소는 127.0.0.1로 확인한다. 일부 환경에서 localhost의 DNS 해석이 수 초 걸려
    /// 짧은 타임아웃의 헬스체크가 서버가 없다고 오판하는 문제를 방지한다.
    /// 서버는 0.0.0.0 바인딩이므로 루프백 IP로도 동일하게 응답한다.
    /// </summary>
    private Uri BuildHealthUrl()
    {
        // 브라우저 런처와 같은 규칙을 사용해 잘못된 주소로 HTTP 요청을 보내지 않는다.
        var url = UrlGuard.CombineBrowserUrl(_settings.ServerUrl, "api/health");
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("안전하지 않은 LabManager 주소입니다.");
        }
        return uri;
    }

    /// <summary>데스크톱이 시작한 서버만 종료한다.</summary>
    public void Stop()
    {
        var process = _process;
        _process = null;
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
        catch
        {
            // 이미 종료되었거나 권한이 없는 경우 무시한다.
        }
        finally
        {
            process.Dispose();
        }
    }
}
