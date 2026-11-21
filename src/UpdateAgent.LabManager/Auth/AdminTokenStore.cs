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
    private string? _overrideToken;

    /// <summary>프로덕션 생성자. DI가 사용한다.</summary>
    public AdminTokenStore(
        IOptionsMonitor<LabManagerOptions> options,
        ILogger<AdminTokenStore> logger)
        : this(options, ResolveOverridePath(), logger)
    {
    }

    /// <summary>테스트용 생성자.override 파일 경로를 직접 지정한다.</summary>
    public AdminTokenStore(
        IOptionsMonitor<LabManagerOptions> options,
        string overridePath,
        ILogger<AdminTokenStore> logger)
    {
        _options = options;
        _overridePath = overridePath;
        _logger = logger;
        _overrideToken = LoadOverrideToken();
    }

    /// <summary>현재 유효한 관리자 토큰. 런타임 override가 있으면 그것을 우선한다.</summary>
    public string CurrentToken
    {
        get
        {
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

        var tempPath = _overridePath + ".tmp";
        var json = JsonSerializer.Serialize(new AdminTokenFile { AdminToken = candidate });
        await File.WriteAllTextAsync(tempPath, json, new System.Text.UTF8Encoding(false), cancellationToken);
        File.Move(tempPath, _overridePath, overwrite: true);

        lock (_sync) _overrideToken = candidate;
        _logger.LogInformation("관리자 토큰이 대시보드에서 변경되었습니다. (길이: {Length})", candidate.Length);
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
