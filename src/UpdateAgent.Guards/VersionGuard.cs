namespace UpdateAgent.Guards;

/// <summary>
/// 릴리스 태그를 버전으로 정규화·비교하는 순수 규칙.
/// 파일/네트워크 접근 없이 문자열만 다루므로 에이전트와 LabManager가 함께 사용할 수 있다.
/// </summary>
public static class VersionGuard
{
    /// <summary>태그 앞의 v/V와 바깥 공백을 제거한다.</summary>
    public static string Normalize(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
        {
            normalized = normalized[1..];
        }
        return normalized.Trim();
    }

    /// <summary>prerelease(-)/빌드 메타데이터(+)를 제외한 숫자 버전을 해석한다.</summary>
    public static bool TryParse(string? value, out Version version)
    {
        var normalized = Normalize(value);
        var metadata = normalized.IndexOfAny(['-', '+']);
        if (metadata >= 0)
        {
            normalized = normalized[..metadata];
        }
        return Version.TryParse(normalized, out version!);
    }

    /// <summary>candidate가 current보다 새 숫자 버전이면 true. 해석할 수 없으면 업데이트가 아니다.</summary>
    public static bool IsNewer(string? candidate, string? current) =>
        TryParse(candidate, out var candidateVersion) &&
        TryParse(current, out var currentVersion) &&
        candidateVersion > currentVersion;
}
