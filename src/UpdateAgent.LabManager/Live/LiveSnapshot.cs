using UpdateAgent.Guards;
using UpdateAgent.LabManager.Services;
using UpdateAgent.LabManager.Storage;
using UpdateAgent.Shared.Models;

namespace UpdateAgent.LabManager.Live;

/// <summary>실시간 모니터가 표시하는 에이전트 요약.</summary>
public sealed record LiveAgentView(
    string AgentId,
    string MachineName,
    string State,
    string? Phase,
    int ProgressPercent,
    bool Online,
    bool MaestroInstalled,
    bool RebootPending,
    bool HasActiveJob,
    DateTimeOffset LastSeen);

/// <summary>실시간 모니터가 표시하는 작업 요약.</summary>
public sealed record LiveJobView(
    string JobId,
    string AgentId,
    string MachineName,
    string State,
    string Phase,
    int ProgressPercent,
    int CompletedItems,
    int TotalItems,
    int RebootCount,
    int MaxRebootCount,
    bool RequiresMaestroAttention,
    DateTimeOffset UpdatedAt,
    string Message);

/// <summary>모니터 초기 스냅샷 및 변경 시 재전송되는 전체 상태.</summary>
public sealed record LiveSnapshot(
    DateTimeOffset ServerTime,
    int AgentCount,
    int OnlineCount,
    int BusyCount,
    int ActiveJobCount,
    int AttentionCount,
    IReadOnlyList<LiveAgentView> Agents,
    IReadOnlyList<LiveJobView> Jobs);

public static class LiveSnapshotBuilder
{
    private const int MaxJobs = 100;

    public static async Task<LiveSnapshot> BuildAsync(
        LabStore store,
        CancellationToken cancellationToken = default)
    {
        var agents = await store.GetAgentsAsync(cancellationToken).ConfigureAwait(false);
        var jobs = await store.GetJobsAsync(MaxJobs, cancellationToken).ConfigureAwait(false);
        // 상위 100건만 보는 jobViews로 활성 작업 수를 세면 누락된다. DB에서 전체 수를 얻는다.
        var activeJobCount = await store.CountActiveJobsAsync(cancellationToken).ConfigureAwait(false);

        var agentViews = agents
            .Select(agent =>
            {
                var current = agent.Telemetry.CurrentJob;
                return new LiveAgentView(
                    agent.Agent.AgentId,
                    agent.Agent.MachineName,
                    agent.Telemetry.State.ToString(),
                    current?.Phase ?? agent.Telemetry.CurrentPhase,
                    current?.ProgressPercent ?? 0,
                    agent.Online,
                    agent.Agent.MaestroInstalled,
                    agent.Telemetry.RebootPending,
                    current is not null && JobStateRules.IsActive(current.State.ToString()),
                    agent.LastSeen);
            })
            .OrderBy(a => a.MachineName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var jobViews = jobs
            .Select(job => new LiveJobView(
                job.Summary.JobId,
                job.Summary.AgentId,
                job.MachineName,
                job.Summary.State.ToString(),
                job.Summary.Phase,
                job.Summary.ProgressPercent,
                job.Summary.CompletedItems,
                job.Summary.TotalItems,
                job.Summary.RebootCount,
                job.Summary.MaxRebootCount,
                job.Summary.RequiresMaestroAttention &&
                JobStateRules.IsActive(job.Summary.State.ToString()),
                job.Summary.UpdatedAt,
                job.Summary.Message))
            .ToList();

        return new LiveSnapshot(
            UtcTimestamps.Now,
            agentViews.Count,
            agentViews.Count(a => a.Online),
            agentViews.Count(a => JobStateRules.IsBusy(a.State)),
            activeJobCount,
            jobViews.Count(j => j.RequiresMaestroAttention),
            agentViews,
            jobViews);
    }
}
