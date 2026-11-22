using Microsoft.Data.Sqlite;
using UpdateAgent.Core.Policy;
using UpdateAgent.Core.Windows;
using UpdateAgent.LabManager.Storage;
using UpdateAgent.Service.Configuration;
using UpdateAgent.Shared;
using UpdateAgent.Shared.Models;
using UpdateAgent.Shared.Serialization;

namespace UpdateAgent.Tests;

/// <summary>자아성찰 6~10회에서 확인한 결함의 회귀 테스트.</summary>
public sealed class AuditFixTests
{
    private static readonly OsInfo Windows11Pro =
        WindowsOsInfoProvider.Create("Windows 11 Pro", "Professional", "25H2", 26200);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("change-me-agent-token")]
    [InlineData("CHANGE-ME-ADMIN-TOKEN")]
    [InlineData("sample-agent-token-replace-before-pilot")]
    [InlineData("sample-admin-token-replace-before-pilot")]
    public void PlaceholderTokens_AreRejected(string? token)
    {
        Assert.True(UpdateAgentConstants.IsPlaceholderToken(token));
    }

    [Theory]
    [InlineData("x9K2mP7vT4qR8nW1zB6d")]
    [InlineData("aB3dE7gJ9kL2mN5pQ8sT1vX4z")]
    public void RealTokens_AreAccepted(string token)
    {
        Assert.False(UpdateAgentConstants.IsPlaceholderToken(token));
    }

    [Fact]
    public void WithoutFeatureUpdates_ClearsBothFeatureFields()
    {
        var policy = UpdatePolicy.Default with
        {
            IncludeFeatureUpdates = true,
            ApprovedFeatureRelease = "26H2",
        };

        var sanitized = policy.WithoutFeatureUpdates();

        Assert.False(sanitized.IncludeFeatureUpdates);
        Assert.Null(sanitized.ApprovedFeatureRelease);
    }

    [Fact]
    public void FeatureUpdate_WithoutSecondApproval_IsExcluded()
    {
        var update = new UpdateItemInfo
        {
            UpdateId = "feature",
            Title = "Windows 11 26H2 feature update",
            Category = UpdateCategory.Upgrade,
            IsFeatureUpdate = true,
        };
        var policy = UpdatePolicy.Default with { IncludeFeatureUpdates = true };

        var (selected, reason) = UpdatePolicyEvaluator.IsSelected(update, policy, Windows11Pro);

        Assert.False(selected);
        Assert.Contains("2차 승인", reason);
    }

    [Fact]
    public void AllCategoriesDisabled_MandatoryUpdateIsStillExcluded()
    {
        var update = new UpdateItemInfo
        {
            UpdateId = "mandatory",
            Title = "mandatory",
            Category = UpdateCategory.Moderate,
            IsMandatory = true,
        };
        var policy = new UpdatePolicy
        {
            IncludeSecurity = false,
            IncludeCritical = false,
            IncludeImportant = false,
        };

        var (selected, reason) = UpdatePolicyEvaluator.IsSelected(update, policy, Windows11Pro);

        Assert.False(selected);
        Assert.Contains("비활성화", reason);
    }

    [Fact]
    public async Task ServerPolicyProvider_KeepsLastKnownGood_WhenReadFails()
    {
        string? json = UpdateAgentJson.Serialize(
            UpdatePolicy.Default with { IncludeSecurity = false, IncludeOptional = true });
        var provider = new ServerPolicyProvider((_, _) =>
            json is null
                ? throw new InvalidOperationException("temporary db failure")
                : Task.FromResult<string?>(json));

        var first = await provider.GetAsync();
        json = null;
        var second = await provider.GetAsync();

        Assert.Equal(first, second);
        Assert.False(second.IncludeSecurity);
        Assert.True(second.IncludeOptional);
    }

    [Fact]
    public async Task CompleteCommandResults_DoesNotUpdateAnotherAgentsCommand()
    {
        await WithStore(async store =>
        {
            await store.AddCommandAsync(new AgentCommand
            {
                CommandId = "cmd-1",
                AgentId = "pc-1",
                Type = CommandType.Ping,
            });

            var forged = new CommandResult
            {
                CommandId = "cmd-1",
                AgentId = "pc-2",
                Success = true,
                Message = "forged",
            };
            await store.CompleteCommandResultsAsync("pc-2", [forged]);

            var rejected = await store.GetCommandAsync("cmd-1");
            Assert.NotNull(rejected);
            Assert.Equal(CommandStatus.Pending, rejected.Status);

            await store.CompleteCommandResultsAsync("pc-1", [forged with { AgentId = "pc-1" }]);
            var completed = await store.GetCommandAsync("cmd-1");
            Assert.NotNull(completed);
            Assert.Equal(CommandStatus.Succeeded, completed.Status);
        });
    }

    [Fact]
    public async Task DeleteAgent_RemovesAgentAndItsPolicy()
    {
        await WithStore(async store =>
        {
            await store.UpsertAgentAsync(
                new AgentInfo { AgentId = "pc-1", MachineName = "PC-01" },
                new AgentTelemetry { AgentId = "pc-1" });
            await store.SetSettingAsync("agent-policy:pc-1", "{}");

            Assert.True(await store.DeleteAgentAsync("pc-1"));
            Assert.Empty(await store.GetAgentsAsync());
            Assert.Null(await store.GetSettingAsync("agent-policy:pc-1"));
            Assert.False(await store.DeleteAgentAsync("pc-1"));
        });
    }

    [Fact]
    public async Task UpsertAgent_StripsLoggedOnUserNames()
    {
        await WithStore(async store =>
        {
            await store.UpsertAgentAsync(
                new AgentInfo { AgentId = "pc-1", MachineName = "PC-01" },
                new AgentTelemetry
                {
                    AgentId = "pc-1",
                    UserLoggedOn = true,
                    LoggedOnUsers = [@"LAB\student-01"],
                });

            var agents = await store.GetAgentsAsync();
            var agent = Assert.Single(agents);
            Assert.True(agent.Telemetry.UserLoggedOn);
            Assert.Empty(agent.Telemetry.LoggedOnUsers);
        });
    }

    [Fact]
    public async Task OnlineThreshold_IsConfigurable()
    {
        var root = Path.Combine(Path.GetTempPath(), "UpdateAgentTests", Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(root, "lab.db");
        try
        {
            var strict = new LabStore(databasePath, onlineThresholdSeconds: 60);
            await strict.InitializeAsync();
            await strict.UpsertAgentAsync(
                new AgentInfo { AgentId = "pc-1", MachineName = "PC-01" },
                new AgentTelemetry { AgentId = "pc-1" });

            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
            }.ToString();
            await using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var update = connection.CreateCommand();
                update.CommandText = "UPDATE agents SET last_seen = $ts WHERE agent_id = $id";
                update.Parameters.AddWithValue("$ts", DateTimeOffset.Now.AddMinutes(-2).ToString("O"));
                update.Parameters.AddWithValue("$id", "pc-1");
                await update.ExecuteNonQueryAsync();
            }

            Assert.False((await strict.GetAgentsAsync()).Single().Online);

            var lenient = new LabStore(databasePath, onlineThresholdSeconds: 300);
            Assert.True((await lenient.GetAgentsAsync()).Single().Online);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void LabStore_RejectsNonPositiveOnlineThreshold()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LabStore(Path.Combine(Path.GetTempPath(), "unused-lab.db"), onlineThresholdSeconds: 0));
    }

    private static async Task WithStore(Func<LabStore, Task> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "UpdateAgentTests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LabStore(Path.Combine(root, "lab.db"));
            await store.InitializeAsync();
            await action(store);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
