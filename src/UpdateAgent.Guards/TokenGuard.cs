using System.Security.Cryptography;
using System.Text;

namespace UpdateAgent.Guards;

/// <summary>
/// 토큰의 정규화·기본값 판별·고정 시간 비교 규칙.
/// 파일/프로세스/환경에 접근하지 않는 순수 함수만 제공한다.
/// </summary>
public static class TokenGuard
{
    /// <summary>대시보드에서 설정할 수 있는 관리자 토큰의 최소 길이.</summary>
    public const int MinimumLength = 8;

    private const string ChangeMePrefix = "change-me-";
    private const string SamplePrefix = "sample-";
    private const string SampleSuffix = "-replace-before-pilot";

    /// <summary>
    /// 바깥 공백과 앞의 BOM만 제거한다. 토큰 내부의 U+FEFF는 의미 있는 문자로 유지해
    /// 서로 다른 토큰이 같게 취급되지 않게 한다.
    /// </summary>
    public static string Normalize(string? token)
    {
        var value = (token ?? string.Empty).Trim();
        return value.TrimStart('\uFEFF').Trim();
    }

    /// <summary>공개된 기본값/샘플 토큰인지 판별한다. 파일럿 전에 반드시 교체해야 한다.</summary>
    public static bool IsPlaceholder(string? token)
    {
        var value = Normalize(token);
        if (value.Length == 0) return true;
        if (value.StartsWith(ChangeMePrefix, StringComparison.OrdinalIgnoreCase)) return true;
        return value.StartsWith(SamplePrefix, StringComparison.OrdinalIgnoreCase) &&
               value.EndsWith(SampleSuffix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>길이와 기본값 규칙을 함께 검사한다. 기본값 허용은 로컬 개발/테스트용이다.</summary>
    public static bool IsAcceptable(string? token, bool allowPlaceholder = false)
    {
        var value = Normalize(token);
        return value.Length >= MinimumLength && (allowPlaceholder || !IsPlaceholder(value));
    }

    /// <summary>
    /// 해시를 고정 시간으로 비교한다. 빈 토큰은 항상 거부해 빈 값끼리 일치하는 사고를 막는다.
    /// 해시 계산 자체는 입력 길이에 비례하지만, 최종 비교는 항상 32바이트를 사용한다.
    /// </summary>
    public static bool FixedTimeEquals(string? left, string? right)
    {
        var first = Normalize(left);
        var second = Normalize(right);
        if (first.Length == 0 || second.Length == 0) return false;

        var hashFirst = SHA256.HashData(Encoding.UTF8.GetBytes(first));
        var hashSecond = SHA256.HashData(Encoding.UTF8.GetBytes(second));
        return CryptographicOperations.FixedTimeEquals(hashFirst, hashSecond);
    }

    /// <summary>
    /// 관리자 토큰과 에이전트 토큰이 서로 다른지 확인한다.
    /// 어느 쪽이든 비어 있으면 서로 다르더라도 유효한 설정이 아니므로 false를 반환한다.
    /// </summary>
    public static bool AreDistinct(string? adminToken, string? agentToken)
    {
        var admin = Normalize(adminToken);
        var agent = Normalize(agentToken);
        if (admin.Length == 0 || agent.Length == 0) return false;
        return !FixedTimeEquals(admin, agent);
    }
}
