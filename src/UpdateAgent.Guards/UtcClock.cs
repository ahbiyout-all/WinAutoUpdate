using System.Globalization;

namespace UpdateAgent.Guards;

/// <summary>
/// 저장·비교·응답에 사용하는 시각을 UTC로 통일하는 시계.
/// SQLite TEXT 비교는 오프셋을 무시하므로 로컬 오프셋이 섞이면 만료 시각이 밀린다.
/// </summary>
public static class UtcClock
{
    public static DateTimeOffset Now => DateTimeOffset.UtcNow;

    public static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    public static string FormatNow() => Format(Now);

    public static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
