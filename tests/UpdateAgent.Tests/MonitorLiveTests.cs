using UpdateAgent.LabManager.Desktop.Services;
using UpdateAgent.LabManager.Live;
using UpdateAgent.LabManager.Storage;
using UpdateAgent.Shared.Models;

namespace UpdateAgent.Tests;

public sealed class MonitorLiveTests
{
    [Fact]
    public async Task LiveEventBus_SignalWakesWaitersAndIncrementsVersion()
    {
        var bus = new LiveEventBus();
        var version = bus.Version;
        var waiter = bus.WaitAsync(version, CancellationToken.None);

        Assert.False(waiter.IsCompleted);

        bus.Signal();
        await waiter.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(version + 1, bus.Version);
        // 신호 이후의 대기는 놓친 변경이 없어도 즉시 반환된다(스냅샷 재전송).
        await bus.WaitAsync(version, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task LiveEventBus_BroadcastsOneSignalToAllWaiters()
    {
        var bus = new LiveEventBus();
        var version = bus.Version;
        var waiters = Enumerable.Range(0, 3)
            .Select(_ => bus.WaitAsync(version, CancellationToken.None))
            .ToArray();

        Assert.All(waiters, waiter => Assert.False(waiter.IsCompleted));

        bus.Signal();
        await Task.WhenAll(waiters).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(version + 1, bus.Version);
    }

    [Fact]
    public void DesktopSettings_MonitorUrl_PointsToMonitorPageAndNormalizesLocalhost()
    {
        var settings = new DesktopSettings { ServerUrl = "http://localhost:5080" };

        Assert.Equal("http://127.0.0.1:5080/", settings.DashboardUrl);
        Assert.Equal("http://127.0.0.1:5080/monitor.html", settings.MonitorUrl);
    }

    [Fact]
    public void DesktopSettings_MonitorUrl_KeepsRemoteServerPath()
    {
        var settings = new DesktopSettings { ServerUrl = "https://lab.example.com:8443/ua/" };

        Assert.Equal("https://lab.example.com:8443/ua/monitor.html", settings.MonitorUrl);
    }

    [Fact]
    public void BrowserLauncher_MonitorUrl_UsesAppModeWithMonitorHtml()
    {
        var monitorUrl = new DesktopSettings { ServerUrl = "http://localhost:5080" }.MonitorUrl;

        var startInfo = BrowserLauncher.BuildStartInfo(
            @"C:\Fake\msedge.exe", monitorUrl, 1280, 800);

        Assert.Equal("--app=http://127.0.0.1:5080/monitor.html", startInfo.ArgumentList.First());
    }

    [Fact]
    public async Task LiveSnapshotBuilder_CountsAgentsJobsAndAttention()
    {
        var root = Path.Combine(Path.GetTempPath(), "UpdateAgentTests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LabStore(Path.Combine(root, "lab.db"));
            await store.InitializeAsync();

            var activeJob = new JobSummary
            {
                JobId = "job-active",
                AgentId = "agent-1",
                AgentName = "PC-01",
                State = UpdateJobState.Installing,
                Phase = "설치 중",
                ProgressPercent = 42,
                CompletedItems = 3,
                TotalItems = 7,
                RequiresMaestroAttention = true,
                UpdatedAt = DateTimeOffset.Now,
            };

            await store.UpsertAgentAsync(
                new AgentInfo
                {
                    AgentId = "agent-1",
                    MachineName = "PC-01",
                    MaestroInstalled = true,
                },
                new AgentTelemetry
                {
                    AgentId = "agent-1",
                    State = AgentState.Busy,
                    CurrentJob = activeJob,
                    CurrentPhase = "설치 중",
                    RebootPending = false,
                });

            await store.SaveJobSummaryAsync(activeJob, "PC-01");
            await store.SaveJobSummaryAsync(
                new JobSummary
                {
                    JobId = "job-done",
                    AgentId = "agent-1",
                    AgentName = "PC-01",
                    State = UpdateJobState.Completed,
                    UpdatedAt = DateTimeOffset.Now,
                },
                "PC-01");

            var snapshot = await LiveSnapshotBuilder.BuildAsync(store);

            Assert.Equal(1, snapshot.AgentCount);
            Assert.Equal(1, snapshot.OnlineCount);
            Assert.Equal(1, snapshot.BusyCount);
            Assert.Equal(1, snapshot.ActiveJobCount);
            Assert.Equal(1, snapshot.AttentionCount);
            Assert.Equal(2, snapshot.Jobs.Count);

            var agent = Assert.Single(snapshot.Agents);
            Assert.Equal("PC-01", agent.MachineName);
            Assert.True(agent.HasActiveJob);
            Assert.True(agent.MaestroInstalled);
            Assert.Equal(42, agent.ProgressPercent);

            var job = Assert.Single(snapshot.Jobs, item => item.JobId == "job-active");
            Assert.Equal("Installing", job.State);
            Assert.Equal("설치 중", job.Phase);
            Assert.True(job.RequiresMaestroAttention);
        }
        finally
        {
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            catch
            {
                // SQLite 연결 풀이 파일을 잡고 있으면 정리를 건너뛴다.
            }
        }
    }
}
