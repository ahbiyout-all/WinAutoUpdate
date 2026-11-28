namespace UpdateAgent.LabManager.Models;

/// <summary>
/// 리포트의 작업 한 건. 값이 없는 필드는 숨기지 않고 "없음"/"확인 불가"로 표시한다.
/// </summary>
public sealed record JobReportEntry(
    string JobId,
    string AgentId,
    string MachineName,
    string State,
    string Phase,
    string Message,
    string StartedAt,
    string UpdatedAt,
    string CorrelationId,
    int TransitionCount,
    string FirstTransitionAt,
    string LastTransitionAt,
    string Timeline);

/// <summary>
/// 기간 내 작업 리포트. 목록이 잘렸으면 truncated/omittedCount로 이를 명시한다.
/// </summary>
public sealed record JobReportResponse(
    DateTimeOffset GeneratedAt,
    DateTimeOffset? From,
    DateTimeOffset? To,
    int Limit,
    int TotalCount,
    int ReturnedCount,
    bool Truncated,
    int OmittedCount,
    IReadOnlyList<JobReportEntry> Jobs);
