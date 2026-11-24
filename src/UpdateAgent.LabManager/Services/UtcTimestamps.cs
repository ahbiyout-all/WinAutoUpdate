namespace UpdateAgent.LabManager.Services;

/// <summary>
/// LabManager가 저장/비교/응답에 사용하는 시각을 모두 UTC로 통일한다.
/// 실제 규칙은 순수 창작 Guard DLL에 있으며 이 클래스는 기존 호출부용 얇은 어댑터다.
/// </summary>
public static class UtcTimestamps
{
    public static DateTimeOffset Now => UpdateAgent.Guards.UtcClock.Now;

    public static string Format(DateTimeOffset value) => UpdateAgent.Guards.UtcClock.Format(value);

    public static string FormatNow() => UpdateAgent.Guards.UtcClock.FormatNow();

    public static DateTimeOffset Parse(string value) => UpdateAgent.Guards.UtcClock.Parse(value);
}
