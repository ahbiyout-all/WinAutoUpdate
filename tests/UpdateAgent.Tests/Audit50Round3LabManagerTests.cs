using Microsoft.Data.Sqlite;
using UpdateAgent.LabManager.Storage;
using UpdateAgent.Shared.Models;

namespace UpdateAgent.Tests;

/// <summary>
/// 3차 50회 자아성찰에서 확인한 LabManager 결함의 회귀 테스트.
/// </summary>
public sealed class Audit50Round3LabManagerTests
{
    [Fact]
    public async Task SaveJobAsync_UpdatesMachineNameAfterRename()
    {
        await WithStoreAsync(async (store, _) =>
        {
            var now = DateTimeOffset.UtcNow;
            var summary = new JobSummary
            {
                JobId = "job-detail-rename",
                AgentId = "pc-1",
                State = UpdateJobState.Installing,
                StartedAt = now,
                UpdatedAt = now,
            };
            var detail = new JobDetail { Summary = summary };

            await store.SaveJobAsync(detail, "OLD-PC");
            await store.SaveJobAsync(
                detail with { Summary = summary with { UpdatedAt = now.AddMinutes(1) } },
                "NEW-PC");

            var stored = await store.GetJobAsync("job-detail-rename");
            Assert.NotNull(stored);
            Assert.Equal("NEW-PC", stored!.MachineName);
        });
    }

    [Fact]
    public async Task CanAcceptNewJobAsync_HonorsPerAgentActiveCap()
    {
        await WithStoreAsync(async (store, _) =>
        {
            var now = DateTimeOffset.UtcNow;
            Assert.True(await store.CanAcceptNewJobAsync("pc-1"));

            for (var i = 0; i < LabStore.MaxActiveJobs; i++)
            {
                await store.SaveJobSummaryAsync(new JobSummary
                {
                    JobId = $"job-{i:D4}",
                    AgentId = "pc-1",
                    State = UpdateJobState.Installing,
                    UpdatedAt = now.AddSeconds(i),
                }, "PC-01");
            }

            // 교사 배정 경로는 대소문자와 무관하게 같은 상한을 사용해야 한다.
            Assert.False(await store.CanAcceptNewJobAsync("PC-1"));

            // 종료된 작업은 활성 수에서 빠지므로 자리가 생기면 다시 배정할 수 있다.
            await store.SaveJobSummaryAsync(new JobSummary
            {
                JobId = "job-0000",
                AgentId = "pc-1",
                State = UpdateJobState.Completed,
                UpdatedAt = now.AddMinutes(1),
            }, "PC-01");
            Assert.True(await store.CanAcceptNewJobAsync("pc-1"));
        });
    }

    [Fact]
    public async Task CanAcceptNewJobAsync_DoesNotCountOtherAgentsJobs()
    {
        await WithStoreAsync(async (store, _) =>
        {
            var now = DateTimeOffset.UtcNow;
            for (var i = 0; i < LabStore.MaxActiveJobs; i++)
            {
                await store.SaveJobSummaryAsync(new JobSummary
                {
                    JobId = $"job-other-{i:D4}",
                    AgentId = "pc-2",
                    State = UpdateJobState.Installing,
                    UpdatedAt = now.AddSeconds(i),
                }, "PC-02");
            }

            Assert.Equal(LabStore.MaxActiveJobs, await store.CountActiveJobsAsync());
            Assert.True(await store.CanAcceptNewJobAsync("pc-1"));
        });
    }

    private static async Task WithStoreAsync(Func<LabStore, string, Task> action)
    {
        var root = Path.Combine(
            Path.GetTempPath(), "UpdateAgentAudit50Round3LabManager", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new LabStore(Path.Combine(root, "lab.db"));
            await store.InitializeAsync();
            await action(store, root);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(root);
        }
    }

    private static void TryDelete(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
