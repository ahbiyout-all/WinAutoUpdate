using System.Globalization;

namespace UpdateAgent.LabManager.Services;

/// <summary>
/// LabManager가 저장/비교/응답에 사용하는 시각을 모두 UTC로 통일한다.
/// SQLite의 TEXT 비교는 오프셋을 무시하므로 로컬 오프셋이 섞이면 만료 시각이 밀린다.
/// </summary>
public static class UtcTimestamps
{
    public static DateTimeOffset Now => DateTimeOffset.UtcNow;

    public static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    public static string FormatNow() => Format(DateTimeOffset.UtcNow);

    public static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
