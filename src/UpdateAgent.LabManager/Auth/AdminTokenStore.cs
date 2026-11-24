using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Options;
using UpdateAgent.LabManager.Configuration;
using UpdateAgent.Shared;

namespace UpdateAgent.LabManager.Auth;

/// <summary>
/// 대시보드에서 변경한 관리자 토큰을 런타임에 보관한다.
/// Program Files의 appsettings.json은 쓰기 권한이 없을 수 있으므로
/// %LOCALAPPDATA% 아래의 별도 파일에 저장하고, 저장 즉시 미들웨어가 새 토큰을 사용한다.
/// </summary>
public sealed class AdminTokenStore
{
    private const int MinimumTokenLength = 8;

    private readonly IOptionsMonitor<LabManagerOptions>? _options;
    private readonly string _overridePath;
    private readonly ILogger<AdminTokenStore> _logger;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _writeSync = new(1, 1);
    private readonly bool _restrictAcl;
    private string? _overrideToken;
    private DateTime _overrideStampUtc;
    private long _overrideLength;
    private bool _hasOverrideStamp;

    /// <summary>프로덕션 생성자. DI가 사용한다.</summary>
    public AdminTokenStore(
        IOptionsMonitor<LabManagerOptions> options,
        ILogger<AdminTokenStore> logger)
        : this(options, ResolveOverridePath(), logger, restrictAcl: true)
    {
    }

    /// <summary>테스트용 생성자.override 파일 경로를 직접 지정한다.</summary>
    public AdminTokenStore(
        IOptionsMonitor<LabManagerOptions> options,
        string overridePath,
        ILogger<AdminTokenStore> logger,
        bool restrictAcl = false)
    {
        _options = options;
        _overridePath = overridePath;
        _logger = logger;
        _restrictAcl = restrictAcl;
        _overrideToken = LoadOverrideToken();
        CaptureOverrideStamp();
    }

    /// <summary>현재 유효한 관리자 토큰. 런타임 override가 있으면 그것을 우선한다.</summary>
    public string CurrentToken
    {
        get
        {
            RefreshOverrideIfChanged();
            lock (_sync)
            {
                if (!string.IsNullOrWhiteSpace(_overrideToken)) return _overrideToken!;
            }
            return _options?.CurrentValue.AdminToken ?? string.Empty;
        }
    }

    /// <summary>런타임 override가 적용되어 있는지.</summary>
    public bool HasRuntimeOverride
    {
        get
        {
            RefreshOverrideIfChanged();
            lock (_sync) return !string.IsNullOrWhiteSpace(_overrideToken);
        }
    }

    /// <summary>현재 토큰이 기본값/샘플이어서 교체가 필요한지.</summary>
    public bool IsUsingPlaceholder => UpdateAgentConstants.IsPlaceholderToken(CurrentToken);

    private bool AllowDefaultTokens =>
        _options?.CurrentValue.AllowDefaultTokens == true;

    /// <summary>새 관리자 토큰을 검증하고 저장한다. 저장 후 즉시 인증에 반영된다.</summary>
    public async Task SetTokenAsync(string newToken, CancellationToken cancellationToken = default)
    {
        var candidate = newToken?.Trim() ?? string.Empty;
        if (candidate.Length < MinimumTokenLength)
            throw new ArgumentException($"관리자 토큰은 {MinimumTokenLength}자 이상이어야 합니다.", nameof(newToken));

        if (!AllowDefaultTokens && UpdateAgentConstants.IsPlaceholderToken(candidate))
            throw new ArgumentException("기본값/샘플 토큰은 관리자 토큰으로 사용할 수 없습니다.", nameof(newToken));

        var directory = Path.GetDirectoryName(_overridePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        // 동시에 두 요청이 같은 임시 파일을 덮어쓰지 않도록 기록 전체를 직렬화한다.
        await _writeSync.WaitAsync(cancellationToken);
        try
        {
            var tempPath = _overridePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var json = JsonSerializer.Serialize(new AdminTokenFile { AdminToken = candidate });
            try
            {
                await File.WriteAllTextAsync(tempPath, json, new System.Text.UTF8Encoding(false), cancellationToken);
                if (_restrictAcl) RestrictAcl(tempPath);
                File.Move(tempPath, _overridePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }

            lock (_sync)
            {
                // 저장 직후 새 값이 즉시 인증에 쓰이도록 메모리와 파일 지문을 함께 갱신한다.
                _overrideToken = candidate;
                CaptureOverrideStamp();
            }
        }
        finally
        {
            _writeSync.Release();
        }
        _logger.LogInformation("관리자 토큰이 대시보드에서 변경되었습니다. (길이: {Length})", candidate.Length);
    }

    /// <summary>토큰 파일을 관리자/SYSTEM만 읽을 수 있도록 제한한다.</summary>
    private void RestrictAcl(string path)
    {
        if (!OperatingSystem.IsWindows()) return;

        // 교사 PC의 대시보드 런처는 관리자 권한이 아니다. UAC 필터 토큰에서는
        // Administrators SID가 deny-only라 서버 재시작 후 파일을 읽을 수 없으므로
        // 현재 사용자 SID에도 명시적으로 권한을 준다.
        var userSid = WindowsIdentity.GetCurrent().User?.Value;
        if (string.IsNullOrWhiteSpace(userSid))
        {
            throw new InvalidOperationException("현재 사용자 SID를 확인할 수 없어 토큰 파일 권한을 제한할 수 없습니다.");
        }

        try
        {
            using var process = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "icacls.exe",
                    Arguments = $"\"{path}\" /inheritance:r /grant:r " +
                        $"\"*S-1-5-18:(F)\" \"*S-1-5-32-544:(F)\" \"*{userSid}:(F)\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });
            if (process is not null && !process.WaitForExit(5000))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("icacls did not finish in time.");
            }
            if (process?.ExitCode != 0)
                throw new InvalidOperationException("icacls returned a non-zero exit code.");
        }
        catch (Exception ex)
        {
            // ACL을 제한하지 못하면 토큰을 남기지 않는다.
            _logger.LogError(ex, "런타임 관리자 토큰 파일 ACL 제한 실패: {Path}", path);
            throw new InvalidOperationException(
                "런타임 관리자 토큰 파일 권한을 제한하지 못해 저장을 중단했습니다.", ex);
        }
    }

    /// <summary>
    /// 외부에서 override 파일을 덮어써도 재시작 없이 반영되도록 파일 지문만 확인한다.
    /// 매 요청마다 파일 전체를 읽지는 않는다.
    /// </summary>
    private void RefreshOverrideIfChanged()
    {
        FileInfo info;
        try
        {
            info = new FileInfo(_overridePath);
            info.Refresh();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "런타임 관리자 토큰 파일 상태를 확인할 수 없습니다: {Path}", _overridePath);
            return;
        }

        lock (_sync)
        {
            if (!info.Exists)
            {
                if (_hasOverrideStamp || _overrideToken is not null)
                {
                    _hasOverrideStamp = false;
                    _overrideToken = null;
                }
                return;
            }

            var stampUtc = info.LastWriteTimeUtc;
            var length = info.Length;
            if (_hasOverrideStamp && _overrideStampUtc == stampUtc && _overrideLength == length) return;

            _overrideStampUtc = stampUtc;
            _overrideLength = length;
            _hasOverrideStamp = true;
            _overrideToken = LoadOverrideToken();
        }
    }

    /// <summary>현재 파일의 수정 시각/크기를 기억한다. 호출 시 _sync 잠금을 보유해야 한다.</summary>
    private void CaptureOverrideStamp()
    {
        try
        {
            var info = new FileInfo(_overridePath);
            info.Refresh();
            if (!info.Exists)
            {
                _hasOverrideStamp = false;
                return;
            }
            _overrideStampUtc = info.LastWriteTimeUtc;
            _overrideLength = info.Length;
            _hasOverrideStamp = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "런타임 관리자 토큰 파일 지문을 기록할 수 없습니다: {Path}", _overridePath);
            _hasOverrideStamp = false;
        }
    }

    private string? LoadOverrideToken()
    {
        if (!File.Exists(_overridePath)) return null;
        try
        {
            var json = File.ReadAllText(_overridePath);
            var file = JsonSerializer.Deserialize<AdminTokenFile>(json);
            var token = file?.AdminToken?.Trim();
            if (string.IsNullOrWhiteSpace(token)) return null;
            if (!AllowDefaultTokens && UpdateAgentConstants.IsPlaceholderToken(token))
            {
                _logger.LogWarning("런타임 관리자 토큰 파일에 기본/샘플 토큰이 있어 무시합니다: {Path}", _overridePath);
                return null;
            }
            return token;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "런타임 관리자 토큰 파일을 읽을 수 없어 무시합니다: {Path}", _overridePath);
            return null;
        }
    }

    private static string ResolveOverridePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UpdateAgent", "LabManager", "runtime-admin-token.json");

    private sealed class AdminTokenFile
    {
        public string AdminToken { get; set; } = string.Empty;
    }
}
