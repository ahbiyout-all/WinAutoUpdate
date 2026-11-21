using Microsoft.Data.Sqlite;
using UpdateAgent.Core.Windows;
using UpdateAgent.LabManager.Services;
using UpdateAgent.LabManager.Storage;
using UpdateAgent.Shared.Models;
using UpdateAgent.Shared.Serialization;

namespace UpdateAgent.Tests;

public sealed class AgentPolicyServiceTests
{
    [Fact]
    public async Task EffectivePolicy_FallsBackToDefault_WhenNoOverrideExists()
    {
        await WithStore(async (store, service) =>
        {
            var effective = await service.GetEffectiveAsync("pc-1");

            Assert.Equal(UpdatePolicy.Default, effective.Policy);
            Assert.False(effective.HasOverride);
            Assert.Equal("기본 정책", effective.Source);
        });
    }

    [Fact]
    public async Task SaveOverride_SanitizesForAgentOs_AndPersists()
    {
        var core = WindowsOsInfoProvider.Create(
            "Windows Server 2025 Standard", "ServerStandard", "24H2", 26100,
            installationType: "Server Core");

        await WithStore(async (store, service) =>
        {
            var requested = UpdatePolicy.Default with
            {
                IncludeFeatureUpdates = true,
                ApprovedFeatureRelease = "26H2",
                OverlayEnabled = true,
            };

            var saved = await service.SaveAsync("pc-1", requested, core);
            var reloaded = await service.GetEffectiveAsync("pc-1", core);

            Assert.True(saved.HasOverride);
            Assert.False(saved.Policy.IncludeFeatureUpdates);
            Assert.False(saved.Policy.OverlayEnabled);
            Assert.Equal(saved.Policy, reloaded.Policy);
            Assert.True(reloaded.HasOverride);
            Assert.Equal("PC별 설정", reloaded.Source);
        });
    }

    [Fact]
    public async Task DeleteOverride_RestoresServerDefault()
    {
        var windows11 = WindowsOsInfoProvider.Create("Windows 11 Pro", "Professional", "25H2", 26200);

        await WithStore(async (store, service) =>
        {
            await store.SetSettingAsync(
                AgentPolicyService.DefaultSettingKey,
                UpdateAgentJson.Serialize(UpdatePolicy.Default with { IncludeOptional = true }));
            await service.SaveAsync("pc-1", UpdatePolicy.Default with { IncludeDrivers = true }, windows11);

            await service.DeleteAsync("pc-1");
            var effective = await service.GetEffectiveAsync("pc-1", windows11);

            Assert.False(effective.HasOverride);
            Assert.True(effective.Policy.IncludeOptional);
            Assert.False(effective.Policy.IncludeDrivers);
        });
    }

    [Fact]
    public async Task GetSummaries_MarksOnlyAgentsWithOverrides()
    {
        var windows11 = WindowsOsInfoProvider.Create("Windows 11 Pro", "Professional", "25H2", 26200);

        await WithStore(async (store, service) =>
        {
            foreach (var id in new[] { "pc-1", "pc-2" })
            {
                await store.UpsertAgentAsync(
                    new AgentInfo { AgentId = id, MachineName = id },
                    new AgentTelemetry { AgentId = id });
            }

            await service.SaveAsync("pc-2", UpdatePolicy.Default, windows11);
            var summaries = await service.GetSummariesAsync();

            Assert.Equal(2, summaries.Count);
            Assert.False(summaries.Single(s => s.AgentId == "pc-1").HasOverride);
            Assert.True(summaries.Single(s => s.AgentId == "pc-2").HasOverride);
        });
    }

    [Fact]
    public async Task ResolveForStart_UsesSavedOverride_WhenTeacherSendsNoPolicy()
    {
        var windows11 = WindowsOsInfoProvider.Create("Windows 11 Pro", "Professional", "25H2", 26200);

        await WithStore(async (store, service) =>
        {
            var saved = UpdatePolicy.Default with { IncludeDefinitionUpdates = true };
            await service.SaveAsync("pc-1", saved, windows11);

            var (policy, warnings) = await service.ResolveForStartAsync(
                "pc-1", windows11, requested: null, saveAsPolicy: false, machineName: "PC-01");

            Assert.Equal(saved, policy);
            Assert.Empty(warnings);
        });
    }

    [Fact]
    public async Task ResolveForStart_SanitizesAndOptionallySavesRequestedPolicy()
    {
        var core = WindowsOsInfoProvider.Create(
            "Windows Server 2025 Standard", "ServerStandard", "24H2", 26100,
            installationType: "Server Core");
        var requested = UpdatePolicy.Default with
        {
            IncludeFeatureUpdates = true,
            OverlayEnabled = true,
        };

        await WithStore(async (store, service) =>
        {
            var (policy, warnings) = await service.ResolveForStartAsync(
                "pc-1", core, requested, saveAsPolicy: true, machineName: "PC-01");

            Assert.False(policy.IncludeFeatureUpdates);
            Assert.False(policy.OverlayEnabled);
            Assert.Equal(2, warnings.Count);
            Assert.All(warnings, w => Assert.StartsWith("PC-01:", w));

            var saved = await service.GetEffectiveAsync("pc-1", core);
            Assert.True(saved.HasOverride);
            Assert.Equal(policy, saved.Policy);
        });
    }

    private static async Task WithStore(Func<LabStore, AgentPolicyService, Task> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "UpdateAgentTests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LabStore(Path.Combine(root, "lab.db"));
            await store.InitializeAsync();
            await action(store, new AgentPolicyService(store));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
