using UpdateAgent.Core.Storage;
using UpdateAgent.Shared.Models;

namespace UpdateAgent.Tests;

public sealed class SqliteJobStoreTests : IDisposable
{
    private readonly string _root;
    private readonly string _dbPath;
    private readonly SqliteJobStore _store;

    public SqliteJobStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "UpdateAgentTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(_root, "agent.db");
        _store = new SqliteJobStore(_dbPath);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // SQLite 연결 풀이 파일을 잡고 있으면 정리를 건너뛴다.
        }
    }

    private static JobDetail Job(string jobId, UpdateJobState state, string agentId = "agent-1") => new()
    {
        Summary = new JobSummary
        {
            JobId = jobId,
            AgentId = agentId,
            AgentName = "PC-TEST",
            State = state,
            Phase = state.ToString(),
            UpdatedAt = DateTimeOffset.Now,
        },
        Items =
        [
            new JobItem
            {
                UpdateId = "update-1",
                KbArticleIds = "KB123456",
                Title = "테스트 업데이트",
                Category = UpdateCategory.Security,
            },
        ],
    };

    [Fact]
    public async Task Initialize_IsIdempotent()
    {
        await _store.InitializeAsync();
        await _store.InitializeAsync();

        Assert.Null(await _store.GetActiveJobAsync("agent-1"));
    }

    [Theory]
    [InlineData(UpdateJobState.Pending)]
    [InlineData(UpdateJobState.Preflight)]
    [InlineData(UpdateJobState.Downloading)]
    [InlineData(UpdateJobState.Installing)]
    [InlineData(UpdateJobState.PendingReboot)]
    [InlineData(UpdateJobState.Rebooting)]
    [InlineData(UpdateJobState.Paused)]
    public async Task ActiveStates_AreResumed(UpdateJobState state)
    {
        await _store.InitializeAsync();
        await _store.SaveJobAsync(Job("job-1", state));

        var active = await _store.GetActiveJobAsync("agent-1");

        Assert.NotNull(active);
        Assert.Equal("job-1", active!.Summary.JobId);
    }

    [Theory]
    [InlineData(UpdateJobState.Completed)]
    [InlineData(UpdateJobState.Failed)]
    [InlineData(UpdateJobState.Cancelled)]
    public async Task TerminalStates_AreNotResumed(UpdateJobState state)
    {
        await _store.InitializeAsync();
        await _store.SaveJobAsync(Job("job-1", state));

        Assert.Null(await _store.GetActiveJobAsync("agent-1"));
        Assert.NotNull(await _store.GetJobAsync("job-1"));
    }

    [Fact]
    public async Task SaveJob_UpdatesExistingRow()
    {
        await _store.InitializeAsync();
        await _store.SaveJobAsync(Job("job-1", UpdateJobState.Downloading));

        var downloading = await _store.GetJobAsync("job-1");
        Assert.Equal(UpdateJobState.Downloading, downloading!.Summary.State);

        await _store.SaveJobAsync(Job("job-1", UpdateJobState.Installing));

        var installing = await _store.GetJobAsync("job-1");
        Assert.Equal(UpdateJobState.Installing, installing!.Summary.State);
        Assert.Single(await _store.GetRecentJobsAsync("agent-1"));
    }

    [Fact]
    public async Task SaveJob_PreservesDryRunFlag()
    {
        await _store.InitializeAsync();
        var job = Job("job-dry", UpdateJobState.Completed);
        job = job with { Summary = job.Summary with { IsDryRun = true } };
        await _store.SaveJobAsync(job);

        var stored = await _store.GetJobAsync("job-dry");

        Assert.NotNull(stored);
        Assert.True(stored!.Summary.IsDryRun);
        Assert.False(Job("job-real", UpdateJobState.Completed).Summary.IsDryRun);
    }

    [Fact]
    public async Task GetActiveJob_IsScopedToAgent()
    {
        await _store.InitializeAsync();
        await _store.SaveJobAsync(Job("job-a", UpdateJobState.Installing, "agent-a"));
        await _store.SaveJobAsync(Job("job-b", UpdateJobState.Installing, "agent-b"));

        var active = await _store.GetActiveJobAsync("agent-a");

        Assert.NotNull(active);
        Assert.Equal("job-a", active!.Summary.JobId);
    }

    [Fact]
    public async Task Events_AreReturnedNewestFirst()
    {
        await _store.InitializeAsync();
        await _store.AppendEventAsync("job-1", "Information", "first");
        await _store.AppendEventAsync("job-1", "Warning", "second");

        var events = await _store.GetEventsAsync("job-1");

        Assert.Equal(2, events.Count);
        Assert.Equal("second", events[0].Message);
        Assert.Equal("Warning", events[0].Level);
    }

    [Fact]
    public async Task Settings_RoundTrip()
    {
        await _store.InitializeAsync();
        await _store.SetSettingAsync("key", "value");
        await _store.SetSettingAsync("key", "updated");

        Assert.Equal("updated", await _store.GetSettingAsync("key"));
        Assert.Null(await _store.GetSettingAsync("missing"));
    }

    [Fact]
    public void JobSummary_IsTerminalForFinalStatesOnly()
    {
        static JobSummary Summary(UpdateJobState state) => new()
        {
            JobId = "job",
            State = state,
        };

        Assert.True(Summary(UpdateJobState.Completed).IsTerminal);
        Assert.True(Summary(UpdateJobState.Failed).IsTerminal);
        Assert.True(Summary(UpdateJobState.Cancelled).IsTerminal);
        Assert.False(Summary(UpdateJobState.PendingReboot).IsTerminal);
        Assert.False(Summary(UpdateJobState.Rebooting).IsTerminal);
        Assert.False(Summary(UpdateJobState.Paused).IsTerminal);
    }
}
