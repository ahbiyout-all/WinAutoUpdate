using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Options;
using UpdateAgent.Guards;
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
    /// <summary>토큰 회전이 필요한 기준 기간.</summary>
    public const int RotationAfterDays = 90;

    /// <summary>회전용 토큰에 사용하는 난수 바이트 수(256비트).</summary>
    public const int GeneratedTokenBytes = 32;

    /// <summary>생성값이 현재/에이전트 토큰과 같을 때 재시도하는 횟수.</summary>
    public const int MaxTokenGenerationAttempts = 8;

    private readonly IOptionsMonitor<LabManagerOptions>? _options;
    private readonly string _overridePath;
    private readonly ILogger<AdminTokenStore> _logger;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _writeSync = new(1, 1);
    private readonly bool _restrictAcl;
    private readonly TokenAuditLog _audit;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action<string, string, bool>? _moveFile;
    private readonly Func<string>? _tokenGenerator;
    private string? _overrideToken;
    private string? _overrideFingerprint;
    private bool _hasOverrideFingerprint;
    private DateTimeOffset? _lastChangedAt;
    private volatile bool _failClosed;

    /// <summary>프로덕션 생성자. DI가 사용한다.</summary>
    public AdminTokenStore(
        IOptionsMonitor<LabManagerOptions> options,
        ILogger<AdminTokenStore> logger)
        : this(options, ResolveOverridePath(), logger, restrictAcl: true)
    {
    }

    /// <summary>테스트용 생성자. override 파일 경로·감사 로그·시계를 직접 지정한다.</summary>
    public AdminTokenStore(
        IOptionsMonitor<LabManagerOptions> options,
        string overridePath,
        ILogger<AdminTokenStore> logger,
        bool restrictAcl = false,
        TokenAuditLog? auditLog = null,
        Func<DateTimeOffset>? clock = null,
        Action<string, string, bool>? moveFile = null,
        Func<string>? tokenGenerator = null)
    {
        _options = options;
        _overridePath = overridePath;
        _logger = logger;
        _restrictAcl = restrictAcl;
        _clock = clock ?? (() => UtcClock.Now);
        _moveFile = moveFile;
        _tokenGenerator = tokenGenerator;
        _audit = auditLog ?? new TokenAuditLog(ResolveAuditPath(overridePath), logger, restrictAcl, clock);
        _overrideToken = LoadOverrideToken();
        CaptureFingerprint();
        if (_overrideToken is not null)
        {
            _lastChangedAt = ReadOverrideLastWriteTimeUtc();
            _audit.Record(TokenAuditLog.Loaded, TokenAuditLog.Fingerprint(_overrideToken));
        }
    }

    /// <summary>대시보드에 표시하는 현재 토큰 상태.</summary>
    public sealed record AdminTokenStatus(
        bool IsPlaceholder,
        bool HasRuntimeOverride,
        DateTimeOffset? LastChangedAt,
        bool RotationDue,
        bool AuditAvailable,
        bool RotationEnabled = false);

    /// <summary>현재 유효한 관리자 토큰. 런타임 override가 있으면 그것을 우선한다.</summary>
    public string CurrentToken
    {
        get
        {
            RefreshOverrideIfChanged();
            if (_failClosed) return string.Empty;

            string token;
            lock (_sync)
            {
                token = !string.IsNullOrWhiteSpace(_overrideToken)
                    ? _overrideToken!
                    : _options?.CurrentValue.AdminToken ?? string.Empty;
            }

            // 설정 파일이 다시 로드되어 에이전트 토큰과 같아지면 학생도 관리자 API를 호출할 수 있다.
            // 시작 시 검사만으로 부족하므로 요청마다 사용할 토큰을 다시 확인한다.
            return CollidesWithAgentToken(token) ? string.Empty : token;
        }
    }

    /// <summary>토큰 상태를 한 번의 조회로 일관되게 반환한다.</summary>
    public AdminTokenStatus GetStatus()
    {
        RefreshOverrideIfChanged();
        lock (_sync)
        {
            var token = _failClosed
                ? string.Empty
                : !string.IsNullOrWhiteSpace(_overrideToken)
                    ? _overrideToken!
                    : _options?.CurrentValue.AdminToken ?? string.Empty;
            var effective = CollidesWithAgentToken(token) ? string.Empty : token;
            var lastChangedAt = _lastChangedAt;
            var rotationDue = lastChangedAt is { } changed &&
                _clock() - changed > TimeSpan.FromDays(RotationAfterDays);
            return new AdminTokenStatus(
                TokenGuard.IsPlaceholder(effective),
                !_failClosed && !string.IsNullOrWhiteSpace(_overrideToken),
                lastChangedAt,
                rotationDue,
                _audit.Available,
                _options?.CurrentValue.TokenRotation?.Enabled == true);
        }
    }

    /// <summary>런타임 override가 적용되어 있는지.</summary>
    public bool HasRuntimeOverride
    {
        get
        {
            RefreshOverrideIfChanged();
            lock (_sync) return !_failClosed && !string.IsNullOrWhiteSpace(_overrideToken);
        }
    }

    /// <summary>현재 토큰이 기본값/샘플이어서 교체가 필요한지.</summary>
    public bool IsUsingPlaceholder => TokenGuard.IsPlaceholder(CurrentToken);

    private bool AllowDefaultTokens =>
        _options?.CurrentValue.AllowDefaultTokens == true;

    /// <summary>
    /// 새 관리자 토큰을 검증하고 저장한다. 저장 후 즉시 인증에 반영된다.
    /// auditEvent는 회전처럼 별도 이벤트를 남겨야 할 때만 지정한다.
    /// </summary>
    public async Task SetTokenAsync(
        string newToken,
        CancellationToken cancellationToken = default,
        string? auditEvent = null)
    {
        var candidate = TokenGuard.Normalize(newToken);
        if (candidate.Length < TokenGuard.MinimumLength)
        {
            Reject();
            throw new ArgumentException($"관리자 토큰은 {TokenGuard.MinimumLength}자 이상이어야 합니다.", nameof(newToken));
        }

        if (!AllowDefaultTokens && TokenGuard.IsPlaceholder(candidate))
        {
            Reject();
            throw new ArgumentException("기본값/샘플 토큰은 관리자 토큰으로 사용할 수 없습니다.", nameof(newToken));
        }

        // 관리자 토큰이 에이전트 토큰과 같아지면 학생 토큰으로 관리자 API를 호출할 수 있다.
        if (CollidesWithAgentToken(candidate))
        {
            Reject();
            throw new ArgumentException("관리자 토큰은 에이전트 토큰과 달라야 합니다.", nameof(newToken));
        }

        var directory = Path.GetDirectoryName(_overridePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        // 동시에 두 요청이 같은 임시 파일을 덮어쓰지 않도록 기록 전체를 직렬화한다.
        await _writeSync.WaitAsync(cancellationToken);
        try
        {
            string? previousToken;
            DateTimeOffset? previousChangedAt;
            lock (_sync)
            {
                previousToken = _failClosed ? null : _overrideToken;
                previousChangedAt = _lastChangedAt;
            }

            try
            {
                await WriteAtomicallyAsync(candidate, cancellationToken);
                lock (_sync)
                {
                    if (_failClosed)
                        throw new InvalidOperationException("감사 로그 복구 실패 후에는 토큰을 변경할 수 없습니다.");

                    // 저장 직후 새 값이 즉시 인증에 쓰이도록 메모리와 파일 지문을 함께 갱신한다.
                    _overrideToken = candidate;
                    _lastChangedAt = _clock();
                    CaptureFingerprintLocked();
                }
                _audit.Record(
                    auditEvent ?? TokenAuditLog.Changed,
                    TokenAuditLog.Fingerprint(candidate));
            }
            catch (Exception ex)
            {
                if (previousToken is not null)
                {
                    try
                    {
                        // 이전 토큰은 디스크 백업 없이 메모리에서만 유지한 뒤 같은 원자적 경로로 되돌린다.
                        await WriteAtomicallyAsync(previousToken, cancellationToken);
                        _audit.Record(TokenAuditLog.RolledBack, TokenAuditLog.Fingerprint(previousToken));
                        lock (_sync)
                        {
                            _overrideToken = previousToken;
                            _lastChangedAt = previousChangedAt;
                            CaptureFingerprintLocked();
                        }
                    }
                    catch (Exception rollbackEx)
                    {
                        EnterFailClosed(rollbackEx);
                        throw new InvalidOperationException(
                            "관리자 토큰 저장 실패 후 이전 토큰 복구에도 실패해 인증을 중단했습니다.", rollbackEx);
                    }
                }
                else
                {
                    EnterFailClosed(ex);
                }
                throw new InvalidOperationException("관리자 토큰을 저장하지 못했습니다.", ex);
            }

            if (auditEvent == TokenAuditLog.Rotated)
                _logger.LogInformation("관리자 토큰이 회전되었습니다. (길이: {Length})", candidate.Length);
            else
                _logger.LogInformation("관리자 토큰이 대시보드에서 변경되었습니다. (길이: {Length})", candidate.Length);
        }
        finally
        {
            _writeSync.Release();
        }
    }

    /// <summary>
    /// 강한 새 토큰을 만들어 기존 SetTokenAsync 검증·원자적 기록·롤백 경로로 저장한다.
    /// 토큰 원문은 로그·감사·반환값에 포함하지 않는다.
    /// </summary>
    public async Task RotateAsync(CancellationToken cancellationToken = default)
    {
        var current = CurrentToken;
        if (string.IsNullOrEmpty(current) || TokenGuard.IsPlaceholder(current) ||
            !TokenGuard.IsAcceptable(current, allowPlaceholder: false))
        {
            Reject();
            throw new InvalidOperationException("회전할 수 있는 유효한 관리자 토큰이 없습니다.");
        }

        for (var attempt = 0; attempt < MaxTokenGenerationAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = TokenGuard.Normalize(_tokenGenerator?.Invoke() ?? GenerateStrongToken());
            if (candidate.Length < TokenGuard.MinimumLength ||
                TokenGuard.IsPlaceholder(candidate) ||
                TokenGuard.FixedTimeEquals(candidate, current) ||
                CollidesWithAgentToken(candidate))
            {
                continue;
            }

            await SetTokenAsync(candidate, cancellationToken, TokenAuditLog.Rotated);
            return;
        }

        Reject();
        throw new InvalidOperationException("안전한 새 관리자 토큰을 생성하지 못해 회전하지 않았습니다.");
    }

    /// <summary>BCL 난수로 URL 안전한 256비트 토큰을 만든다.</summary>
    private static string GenerateStrongToken()
    {
        var bytes = new byte[GeneratedTokenBytes];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    /// <summary>거부된 요청은 지문 없이 감사 로그에 남긴다.</summary>
    private void Reject() => _audit.Record(TokenAuditLog.Rejected);

    /// <summary>복구까지 실패하면 토큰을 비워 조용히 이전 값으로 돌아가지 않게 한다.</summary>
    private void EnterFailClosed(Exception reason)
    {
        lock (_sync)
        {
            _failClosed = true;
            _overrideToken = null;
            _lastChangedAt = null;
            _overrideFingerprint = null;
            _hasOverrideFingerprint = false;
        }
        _logger.LogError(reason, "관리자 토큰 저장·복구 실패로 인증 토큰을 비웠습니다.");
    }

    /// <summary>임시 파일 + 원자적 이동으로 토큰 파일을 교체한다.</summary>
    private async Task WriteAtomicallyAsync(string token, CancellationToken cancellationToken)
    {
        var tempPath = _overridePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var json = JsonSerializer.Serialize(new AdminTokenFile { AdminToken = token });
        try
        {
            await File.WriteAllTextAsync(tempPath, json, new System.Text.UTF8Encoding(false), cancellationToken);
            if (_restrictAcl) RestrictAcl(tempPath);
            MoveFile(tempPath, _overridePath);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    private void MoveFile(string tempPath, string destination)
    {
        if (_moveFile is not null) _moveFile(tempPath, destination, true);
        else File.Move(tempPath, destination, overwrite: true);
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
    /// 외부에서 override 파일을 덮어써도 재시작 없이 반영되도록 내용 해시를 확인한다.
    /// 수정 시각/크기만 보면 같은 크기·같은 시각으로 덮어쓴 토큰을 놓칠 수 있다.
    /// </summary>
    private void RefreshOverrideIfChanged()
    {
        string? fingerprint;
        DateTimeOffset? fileChangedAt = null;
        try
        {
            if (File.Exists(_overridePath))
            {
                fingerprint = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(_overridePath)));
                fileChangedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(_overridePath), TimeSpan.Zero);
            }
            else
            {
                fingerprint = null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "런타임 관리자 토큰 파일 상태를 확인할 수 없습니다: {Path}", _overridePath);
            return;
        }

        lock (_sync)
        {
            if (_failClosed) return;

            if (fingerprint is null)
            {
                if (_hasOverrideFingerprint || _overrideToken is not null)
                {
                    _hasOverrideFingerprint = false;
                    _overrideFingerprint = null;
                    _overrideToken = null;
                    _lastChangedAt = null;
                }
                return;
            }

            if (_hasOverrideFingerprint && string.Equals(_overrideFingerprint, fingerprint, StringComparison.Ordinal))
                return;

            _overrideFingerprint = fingerprint;
            _hasOverrideFingerprint = true;
            _overrideToken = LoadOverrideToken();
            _lastChangedAt = _overrideToken is null ? null : fileChangedAt;
        }
    }

    private void CaptureFingerprint()
    {
        var fingerprint = ComputeFingerprint(out var exists);
        lock (_sync)
        {
            _overrideFingerprint = fingerprint;
            _hasOverrideFingerprint = exists;
        }
    }

    /// <summary>호출 시 _sync 잠금을 보유해야 한다.</summary>
    private void CaptureFingerprintLocked()
    {
        var fingerprint = ComputeFingerprint(out var exists);
        _overrideFingerprint = fingerprint;
        _hasOverrideFingerprint = exists;
    }

    private string? ComputeFingerprint(out bool exists)
    {
        exists = false;
        try
        {
            if (!File.Exists(_overridePath)) return null;
            var fingerprint = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(_overridePath)));
            exists = true;
            return fingerprint;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "런타임 관리자 토큰 파일 지문을 기록할 수 없습니다: {Path}", _overridePath);
            return null;
        }
    }

    private string? LoadOverrideToken()
    {
        if (!File.Exists(_overridePath)) return null;
        try
        {
            // 토큰 파일은 첫 줄에만 값을 둔다. 뒤에 덧붙은 줄은 토큰으로 해석하지 않는다.
            var json = File.ReadLines(_overridePath).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(json))
            {
                _logger.LogWarning("런타임 관리자 토큰 파일이 비어 있어 무시합니다: {Path}", _overridePath);
                return null;
            }
            var file = JsonSerializer.Deserialize<AdminTokenFile>(json);
            var token = TokenGuard.Normalize(file?.AdminToken);
            if (!TokenGuard.IsAcceptable(token, AllowDefaultTokens))
            {
                _logger.LogWarning("런타임 관리자 토큰 파일의 값이 사용할 수 없어 무시합니다: {Path}", _overridePath);
                return null;
            }
            if (CollidesWithAgentToken(token))
            {
                _logger.LogWarning("런타임 관리자 토큰이 에이전트 토큰과 같아 무시합니다: {Path}", _overridePath);
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

    private bool CollidesWithAgentToken(string token) =>
        TokenGuard.FixedTimeEquals(token, _options?.CurrentValue.AgentToken);

    private DateTimeOffset? ReadOverrideLastWriteTimeUtc()
    {
        try
        {
            return File.Exists(_overridePath)
                ? new DateTimeOffset(File.GetLastWriteTimeUtc(_overridePath), TimeSpan.Zero)
                : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "런타임 관리자 토큰 변경 시각을 확인할 수 없습니다: {Path}", _overridePath);
            return null;
        }
    }

    private static string ResolveAuditPath(string overridePath) => Path.Combine(
        Path.GetDirectoryName(overridePath) ?? string.Empty,
        "runtime-admin-token.audit.log");

    private static string ResolveOverridePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UpdateAgent", "LabManager", "runtime-admin-token.json");

    private sealed class AdminTokenFile
    {
        public string AdminToken { get; set; } = string.Empty;
    }
}
