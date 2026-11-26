using UpdateAgent.LabManager.Live;

namespace UpdateAgent.Tests;

/// <summary>대시보드 개선(오래됨 판정, 스냅샷 기본값) 회귀 테스트.</summary>
public sealed class DashboardImprovementTests
{
    private static readonly DateTimeOffset BaseTime =
        new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(89, false)]
    [InlineData(90, false)]
    [InlineData(91, true)]
    [InlineData(-5, false)]
    public void IsStale_Uses90SecondThresholdRelativeToNow(int secondsAgo, bool expected)
    {
        var lastSeen = BaseTime.AddSeconds(-secondsAgo);

        Assert.Equal(expected, LiveSnapshot.IsStale(lastSeen, BaseTime));
    }

    [Fact]
    public void StaleCount_IsComputedFromAgentsWithoutDatabase()
    {
        var agents = new[]
        {
            Agent("agent-fresh", BaseTime.AddSeconds(-89)),
            Agent("agent-stale", BaseTime.AddSeconds(-91)),
            Agent("agent-very-old", BaseTime.AddSeconds(-600)),
        };
        var staleCount = agents.Count(agent => LiveSnapshot.IsStale(agent.LastSeen, BaseTime));

        var snapshot = new LiveSnapshot(
            BaseTime,
            agents.Length,
            1,
            0,
            0,
            0,
            agents,
            Array.Empty<LiveJobView>(),
            staleCount,
            BaseTime);

        Assert.Equal(2, snapshot.StaleCount);
        Assert.False(LiveSnapshot.IsStale(agents[0].LastSeen, BaseTime));
        Assert.Equal((DateTimeOffset?)BaseTime, snapshot.SnapshotAt);
        // 기존 목록과 순서가 그대로 유지되어야 한다.
        Assert.Same(agents, snapshot.Agents);
        Assert.Equal("agent-fresh", snapshot.Agents[0].AgentId);
        Assert.Equal(3, snapshot.AgentCount);
        Assert.Equal(1, snapshot.OnlineCount);
    }

    [Fact]
    public void NewSnapshotFields_DefaultSafely()
    {
        var snapshot = new LiveSnapshot(
            BaseTime,
            0,
            0,
            0,
            0,
            0,
            Array.Empty<LiveAgentView>(),
            Array.Empty<LiveJobView>());

        Assert.Equal(0, snapshot.StaleCount);
        Assert.Null(snapshot.SnapshotAt);
        Assert.Empty(snapshot.Agents);
        Assert.Empty(snapshot.Jobs);
        Assert.Equal(90, LiveSnapshot.StaleThresholdSeconds);
    }

    private static LiveAgentView Agent(string agentId, DateTimeOffset lastSeen) =>
        new(agentId, agentId, "Idle", null, 0,
            true, false, false, false, false, lastSeen);
}
