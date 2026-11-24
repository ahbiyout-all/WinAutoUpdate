namespace UpdateAgent.Guards;

/// <summary>
/// 작업/에이전트 상태 문자열 규칙. enum 인스턴스 대신 문자열을 받으므로
/// 공유 모델, 서버 모니터, 개요 API가 같은 정의를 사용할 수 있다.
/// </summary>
public static class JobStateRules
{
    private static readonly HashSet<string> TerminalStates = new(StringComparer.Ordinal)
    {
        "Completed", "Failed", "Cancelled",
    };

    private static readonly HashSet<string> BusyStates = new(StringComparer.Ordinal)
    {
        "Busy", "Rebooting", "Paused",
    };

    /// <summary>완료/실패/취소처럼 더 이상 진행되지 않는 상태인지.</summary>
    public static bool IsTerminal(string? state) =>
        state is not null && TerminalStates.Contains(state);

    /// <summary>진행 중이거나 일시중지되어 사용자의 주의가 필요한 상태인지.</summary>
    public static bool IsBusy(string? state) =>
        state is not null && BusyStates.Contains(state);

    /// <summary>아직 종료되지 않은 상태인지.</summary>
    public static bool IsActive(string? state) => !IsTerminal(state);
}
