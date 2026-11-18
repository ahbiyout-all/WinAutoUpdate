using UpdateAgent.LabManager.Services;
using UpdateAgent.Shared.Models;

namespace UpdateAgent.Tests;

public sealed class JobStateGuardTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 11, 18, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("terminal-to-nonterminal", false)]
    [InlineData("older-timestamp", false)]
    [InlineData("agent-id-mismatch", false)]
    [InlineData("new-terminal-job", false)]
    [InlineData("detail-summary-job-mismatch", false)]
    [InlineData("detail-summary-state-mismatch", false)]
    [InlineData("normal-transition", true)]
    public void CanAccept_ValidatesTheWholeJobStateUpdate(string scenario, bool expected)
    {
        var stored = new JobSummary
        {
            JobId = "job-1",
            AgentId = "agent-1",
            State = UpdateJobState.Installing,
            UpdatedAt = BaseTime,
        };
        var incoming = new JobSummary
        {
            JobId = "job-1",
            AgentId = "agent-1",
            State = UpdateJobState.Completed,
            UpdatedAt = BaseTime.AddSeconds(10),
        };
        var detailSummary = incoming;
        var receivedAt = BaseTime.AddSeconds(11);

        switch (scenario)
        {
            case "terminal-to-nonterminal":
                stored = stored with { State = UpdateJobState.Completed };
                incoming = incoming with { State = UpdateJobState.Installing };
                detailSummary = incoming;
                break;
            case "older-timestamp":
                incoming = incoming with
                {
                    State = UpdateJobState.Installing,
                    UpdatedAt = BaseTime.AddSeconds(-1),
                };
                detailSummary = incoming;
                break;
            case "agent-id-mismatch":
                incoming = incoming with { AgentId = "agent-2" };
                detailSummary = incoming;
                break;
            case "new-terminal-job":
                stored = null;
                break;
            case "detail-summary-job-mismatch":
                detailSummary = incoming with { JobId = "job-2" };
                break;
            case "detail-summary-state-mismatch":
                detailSummary = incoming with { State = UpdateJobState.Failed };
                break;
        }

        var accepted = JobStateGuard.CanAccept(stored, incoming, "agent-1", receivedAt);
        var detailMatches = JobStateGuard.DetailSummaryMatches(incoming, detailSummary);

        Assert.Equal(expected, accepted && detailMatches);
    }

    [Theory]
    [InlineData("agent-1", "agent-1", true)]
    [InlineData("agent-1", "AGENT-1", true)]
    [InlineData("agent-1", "agent-2", false)]
    public void AgentIdsMatch_IgnoresCaseButNotIdentity(string agentId, string telemetryAgentId, bool expected)
    {
        Assert.Equal(expected, JobStateGuard.AgentIdsMatch(agentId, telemetryAgentId));
    }

    [Fact]
    public void Normalize_UsesTheServerReceiptTime()
    {
        var incoming = new JobSummary
        {
            JobId = "job-1",
            AgentId = "agent-1",
            UpdatedAt = BaseTime.AddDays(30),
        };
        var receivedAt = BaseTime.AddSeconds(11);

        var normalized = JobStateGuard.Normalize(incoming, receivedAt);

        Assert.Equal(receivedAt, normalized.UpdatedAt);
        Assert.Equal(incoming.JobId, normalized.JobId);
    }

    [Fact]
    public void CanAccept_UsesReportedClientClockWhenAgentClockIsSkewed()
    {
        var stored = new JobSummary
        {
            JobId = "job-1",
            AgentId = "agent-1",
            State = UpdateJobState.Installing,
            ReportedAt = BaseTime,
            UpdatedAt = BaseTime.AddMinutes(5),
        };
        var incoming = new JobSummary
        {
            JobId = "job-1",
            AgentId = "agent-1",
            State = UpdateJobState.Completed,
            UpdatedAt = BaseTime.AddMinutes(5).AddSeconds(10),
        };
        var receivedAt = BaseTime.AddMinutes(5).AddSeconds(11);

        Assert.True(JobStateGuard.CanAccept(stored, incoming, "agent-1", receivedAt));

        var normalized = JobStateGuard.Normalize(incoming, receivedAt);
        Assert.Equal(incoming.UpdatedAt, normalized.ReportedAt);
        Assert.Equal(receivedAt, normalized.UpdatedAt);
    }
}
