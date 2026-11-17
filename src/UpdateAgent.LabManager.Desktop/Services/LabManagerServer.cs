using System.Diagnostics;
using System.IO;
using System.Net.Http;

namespace UpdateAgent.LabManager.Desktop.Services;

/// <summary>
/// 데스크톱 창이 로컬 LabManager API 서버를 준비한다.
/// 이미 실행 중이면 재사용하고, 로컬 주소인데 서버가 없으면 같은 폴더의
/// UpdateAgent.LabManager.exe를 --headless로 조용히 띄운다.
/// 데스크톱이 띄운 서버는 앱 종료 시 함께 종료한다.
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

    /// <summary>
    /// 서버가 준비되었으면 true. 원격 주소인데 연결할 수 없거나 로컬 서버 시작에 실패하면 false.
    /// </summary>
    public async Task<bool> EnsureRunningAsync(CancellationToken cancellationToken = default)
    {
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

        var deadline = DateTimeOffset.UtcNow + StartupTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(300, cancellationToken).ConfigureAwait(false);

            if (await IsHealthyAsync(cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            if (_process.HasExited)
            {
                return false;
            }
        }

        return false;
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
        var baseUrl = _settings.ServerUrl.TrimEnd('/') + "/";
        var uri = new Uri(baseUrl, UriKind.Absolute);

        if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            var builder = new UriBuilder(uri) { Host = "127.0.0.1" };
            baseUrl = builder.Uri.AbsoluteUri.TrimEnd('/') + "/";
        }

        return new Uri(new Uri(baseUrl, UriKind.Absolute), "api/health");
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
