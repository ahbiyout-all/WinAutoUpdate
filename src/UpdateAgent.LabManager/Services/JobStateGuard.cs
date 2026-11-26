using UpdateAgent.Shared.Models;

namespace UpdateAgent.LabManager.Services;

/// <summary>
/// 작업 상태의 원본은 LabManager다. 에이전트가 보낸 상태의 소유자/종료 상태/시간 순서를
/// 검증하고, 서버가 받은 시각으로 UpdatedAt을 정규화한다.
/// </summary>
internal static class JobStateGuard
{
    public static bool AgentIdsMatch(string agentId, string telemetryAgentId) =>
        string.Equals(agentId, telemetryAgentId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 학생 PC가 미래 시각을 보고해도 이후 모든 보고가 영구히 거부되지 않도록
    /// 서버 수신 시각을 상한으로 사용한다.
    /// </summary>
    public static DateTimeOffset ReportedAtOf(JobSummary incoming, DateTimeOffset receivedAt)
    {
        var reported = incoming.ReportedAt ?? incoming.UpdatedAt;
        return reported > receivedAt ? receivedAt : reported;
    }

    public static bool CanAccept(
        JobSummary? stored,
        JobSummary incoming,
        string agentId,
        DateTimeOffset receivedAt)
    {
        if (!AgentIdsMatch(agentId, incoming.AgentId)) return false;

        if (stored is null)
        {
            // 새 작업이 완료/실패/취소 상태로 들어오는 것은 허용하지 않는다.
            return !incoming.IsTerminal;
        }

        if (!AgentIdsMatch(stored.AgentId, agentId)) return false;
        if (stored.IsTerminal) return false;

        // 진행 중 상태가 대기 상태로 되돌아가는 것도 거부한다.
        if (stored.State != UpdateJobState.Pending &&
            incoming.State == UpdateJobState.Pending)
        {
            return false;
        }

        // 다른 시계끼리 비교하지 않는다. 보고 시각은 보고 시끼리, 수신 시각은 수신 시끼리 비교한다.
        var incomingReportedAt = ReportedAtOf(incoming, receivedAt);
        var storedReportedAt = stored.ReportedAt ?? stored.UpdatedAt;
        if (incomingReportedAt < storedReportedAt) return false;
        if (receivedAt < stored.UpdatedAt) return false;

        return true;
    }

    /// <summary>상세에 포함된 요약이 검증 대상 요약과 같은 작업/상태인지 확인한다.</summary>
    public static bool DetailSummaryMatches(JobSummary summary, JobSummary detailSummary) =>
        AgentIdsMatch(summary.JobId, detailSummary.JobId) &&
        summary.State == detailSummary.State;

    /// <summary>
    /// 서버 수신 시각을 기록하고 에이전트 보고 시각은 미래값을 잘라 ReportedAt에 보관한다.
    /// Dry-run 여부/Maestro 주의 플래그/최대 재부팅 횟수는 서버가 배정한 값이므로
    /// 에이전트가 이후 체크인에서 임의로 되돌리지 못하게 이전 저장값을 유지한다.
    /// </summary>
    public static JobSummary Normalize(
        JobSummary incoming,
        DateTimeOffset receivedAt,
        JobSummary? stored = null)
    {
        var merged = stored is null
            ? incoming
            : incoming with
            {
                IsDryRun = stored.IsDryRun,
                RequiresMaestroAttention = stored.RequiresMaestroAttention,
                MaxRebootCount = stored.MaxRebootCount,
            };

        return merged with
        {
            ReportedAt = ReportedAtOf(incoming, receivedAt),
            UpdatedAt = receivedAt,
        };
    }
}
