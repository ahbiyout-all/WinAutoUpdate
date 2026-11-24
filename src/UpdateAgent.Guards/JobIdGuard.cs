namespace UpdateAgent.Guards;

/// <summary>에이전트와 LabManager가 같은 형식의 작업 ID를 만들도록 하는 순수 규칙.</summary>
public static class JobIdGuard
{
    private const int MaxLength = 32;

    /// <summary>
    /// UTC 시각과 GUID로 작업 ID를 생성한다. 로컬 오프셋을 쓰지 않고,
    /// 기존 4자리보다 많은 GUID 자릿수를 유지해 같은 초의 배정 충돌 가능성을 낮춘다.
    /// </summary>
    public static string Create(DateTimeOffset now, Guid id)
    {
        var prefix = $"job-{now.ToUniversalTime():yyyyMMdd-HHmmss}-";
        var suffix = id.ToString("N");
        return prefix + suffix[..Math.Min(MaxLength - prefix.Length, suffix.Length)];
    }
}
