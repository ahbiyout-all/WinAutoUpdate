using System.Globalization;

namespace UpdateAgent.Guards;

/// <summary>
/// 저장·비교·응답에 사용하는 시각을 UTC로 통일하는 시계.
/// SQLite TEXT 비교는 오프셋을 무시하므로 로컬 오프셋이 섞이면 만료 시각이 밀린다.
/// </summary>
public static class UtcClock
{
    private static readonly string[] AcceptedFormats =
    [
        "yyyy-MM-ddTHH:mm:ss.fffffffK",
        "yyyy-MM-ddTHH:mm:ss.FFFFFFFK",
        "yyyy-MM-ddTHH:mm:ssK",
        "yyyy-MM-ddTHH:mm:ss",
    ];

    public static DateTimeOffset Now => DateTimeOffset.UtcNow;

    public static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    public static string FormatNow() => Format(Now);

    /// <summary>
    /// 저장된 ISO-8601 문자열만 해석한다. 날짜만 있는 값처럼 모호한 형식은 조용히 보정하지 않고 거부한다.
    /// 오프셋이 없으면 UTC로 간주한다.
    /// </summary>
    public static DateTimeOffset Parse(string value)
    {
        if (DateTimeOffset.TryParseExact(
                value,
                AcceptedFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return parsed;
        }

        throw new FormatException("UTC 시각 형식이 올바르지 않습니다.");
    }
}
