namespace UpdateAgent.Shared.Comms;

/// <summary>
/// LabManager 체크인 실패 후 다음 시도까지의 지연을 계산하는 순수 정책.
/// OS API에 의존하지 않으므로 서비스/테스트 어디에서나 같은 결과를 낸다.
/// </summary>
public static class CheckInRetryPolicy
{
    /// <summary>지수 증가 상한이자 실패 횟수 상한(2^10).</summary>
    public const int MaxExponent = 10;

    /// <summary>기본 지연의 최소값(초).</summary>
    public const int MinimumBaseDelaySeconds = 1;

    /// <summary>Worker가 적용하는 최대 지연 상한(초).</summary>
    public const int DefaultMaxDelaySeconds = 300;

    /// <summary>
    /// 연속 실패 횟수에 따른 지연(초)을 반환한다.
    /// <c>base * 2^min(failures, MaxExponent)</c>을 <c>maxSeconds</c>로 제한한다.
    /// </summary>
    public static int NextDelaySeconds(int consecutiveFailures, int baseSeconds, int maxSeconds)
    {
        var safeBase = Math.Max(baseSeconds, MinimumBaseDelaySeconds);
        var safeMax = Math.Max(maxSeconds, safeBase);
        var exponent = Math.Clamp(consecutiveFailures, 0, MaxExponent);

        // long으로 계산하므로 int 최댓값 근처의 입력에서도 오버플로가 없다.
        var multiplier = 1L << exponent;
        var delay = (long)safeBase * multiplier;

        return (int)Math.Min(delay, safeMax);
    }
}
