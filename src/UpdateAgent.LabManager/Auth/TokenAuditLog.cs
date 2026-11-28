using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using UpdateAgent.Guards;

namespace UpdateAgent.LabManager.Auth;

/// <summary>
/// 관리자 토큰의 변경·복구·거부 이벤트를 남기는 감사 로그.
/// UTC 시각·이벤트명·지문만 기록하며 토큰 원문은 메서드 인자로도 받지 않는다.
/// </summary>
public sealed class TokenAuditLog
{
    /// <summary>감사 로그 크기 상한(256 KiB).</summary>
    public const int MaxFileBytes = 256 * 1024;

    public const string Changed = "changed";
    public const string Rotated = "token-rotated";
    public const string RolledBack = "rolled-back";
    public const string Loaded = "loaded";
    public const string Rejected = "rejected";

    private static readonly Encoding LogEncoding = new UTF8Encoding(false);
    private readonly string _path;
    private readonly ILogger _logger;
    private readonly bool _restrictAcl;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _sync = new(1, 1);
    private volatile bool _available;

    /// <summary>프로덕션 감사 로그 경로(%LOCALAPPDATA%)를 사용한다.</summary>
    public TokenAuditLog(ILogger logger)
        : this(ResolvePath(), logger, restrictAcl: true)
    {
    }

    /// <summary>테스트용 생성자. 경로·ACL·시계를 직접 지정한다.</summary>
    public TokenAuditLog(
        string path,
        ILogger logger,
        bool restrictAcl = false,
        Func<DateTimeOffset>? clock = null)
    {
        _path = path;
        _logger = logger;
        _restrictAcl = restrictAcl;
        _clock = clock ?? (() => UtcClock.Now);
        _available = Exists();
    }

    /// <summary>감사 로그를 기록할 수 있는 상태인지.</summary>
    public bool Available => _available;

    /// <summary>감사 로그 파일 경로.</summary>
    public string AuditPath => _path;

    /// <summary>토큰 원문이 아닌 SHA-256 지문 앞 12자리만 반환한다.</summary>
    public static string Fingerprint(string? token)
    {
        if (string.IsNullOrEmpty(token)) return string.Empty;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(hash.AsSpan(0, 6)).ToLowerInvariant();
    }

    /// <summary>이벤트와 이미 계산된 지문만 기록한다. 토큰 원문은 받지 않는다.</summary>
    public void Record(string eventName, string? fingerprint = null)
    {
        var normalizedFingerprint = NormalizeFingerprint(fingerprint);
        var line = UtcClock.Format(_clock()) + "\t" + eventName +
            (normalizedFingerprint is null ? string.Empty : "\t" + normalizedFingerprint) + "\n";

        _sync.Wait();
        try
        {
            try
            {
                var directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                var bytes = LogEncoding.GetBytes(line);
                using (var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(flushToDisk: true);
                }

                // 기존 파일이 느슨한 ACL로 남아 있어도 매 기록마다 다시 제한한다.
                if (_restrictAcl) RestrictAcl(_path);
                TrimIfNeeded();
                _available = true;
            }
            catch (Exception ex)
            {
                _available = false;
                _logger.LogError(ex, "관리자 토큰 감사 로그 기록 실패");
                throw new InvalidOperationException(
                    "관리자 토큰 감사 로그를 기록하지 못해 저장을 중단했습니다.", ex);
            }
        }
        finally
        {
            _sync.Release();
        }
    }

    private static string? NormalizeFingerprint(string? fingerprint)
    {
        if (fingerprint is null) return null;
        if (fingerprint.Length != 12 || fingerprint.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidOperationException("감사 로그 지문 형식이 올바르지 않습니다.");
        return fingerprint.ToLowerInvariant();
    }

    /// <summary>상한을 넘으면 오래된 줄부터 잘라낸다. 최신 줄은 항상 유지한다.</summary>
    private void TrimIfNeeded()
    {
        var info = new FileInfo(_path);
        if (!info.Exists || info.Length <= MaxFileBytes) return;

        var kept = new List<string>();
        long bytes = 0;
        foreach (var line in File.ReadAllLines(_path).Reverse())
        {
            var cost = LogEncoding.GetByteCount(line) + 1;
            if (bytes + cost > MaxFileBytes) break;
            kept.Add(line);
            bytes += cost;
        }
        kept.Reverse();

        // 같은 파일을 덮어써서 기존 ACL을 유지한다.
        File.WriteAllText(_path, string.Concat(kept.Select(line => line + "\n")), LogEncoding);
    }

    /// <summary>감사 로그를 관리자/SYSTEM/현재 사용자만 읽을 수 있도록 제한한다.</summary>
    private void RestrictAcl(string path)
    {
        if (!OperatingSystem.IsWindows()) return;

        // 관리자 권한이 아닌 대시보드 런처에서도 읽을 수 있도록 현재 사용자 SID를 함께 부여한다.
        var userSid = WindowsIdentity.GetCurrent().User?.Value;
        if (string.IsNullOrWhiteSpace(userSid))
            throw new InvalidOperationException("현재 사용자 SID를 확인할 수 없어 감사 로그 권한을 제한할 수 없습니다.");

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
            _logger.LogError(ex, "관리자 토큰 감사 로그 ACL 제한 실패: {Path}", path);
            throw new InvalidOperationException(
                "관리자 토큰 감사 로그 권한을 제한하지 못해 저장을 중단했습니다.", ex);
        }
    }

    private bool Exists()
    {
        try
        {
            return File.Exists(_path);
        }
        catch
        {
            return false;
        }
    }

    private static string ResolvePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UpdateAgent", "LabManager", "runtime-admin-token.audit.log");
}
