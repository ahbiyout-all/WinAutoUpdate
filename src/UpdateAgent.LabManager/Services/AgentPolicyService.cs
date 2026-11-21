using UpdateAgent.LabManager.Models;
using UpdateAgent.LabManager.Storage;
using UpdateAgent.Shared.Models;
using UpdateAgent.Shared.Serialization;

namespace UpdateAgent.LabManager.Services;

public sealed record EffectivePolicy(UpdatePolicy Policy, bool HasOverride, string Source);

/// <summary>
/// 서버 기본 정책과 PC별 저장 정책을 해석한다. 모든 경로에서 OS 안전 규칙을 적용한다.
/// </summary>
public sealed class AgentPolicyService
{
    public const string DefaultSettingKey = "default-policy";
    private const string AgentKeyPrefix = "agent-policy:";

    private readonly LabStore _store;

    public AgentPolicyService(LabStore store) => _store = store;

    public async Task<UpdatePolicy> GetDefaultAsync(CancellationToken cancellationToken = default) =>
        Parse(await _store.GetSettingAsync(DefaultSettingKey, cancellationToken));

    public async Task<EffectivePolicy> GetEffectiveAsync(
        string agentId,
        OsInfo? os = null,
        CancellationToken cancellationToken = default)
    {
        var json = await _store.GetSettingAsync(Key(agentId), cancellationToken);
        if (json is not null)
        {
            return new EffectivePolicy(Parse(json).SanitizeFor(os), true, "PC별 설정");
        }

        var defaultJson = await _store.GetSettingAsync(DefaultSettingKey, cancellationToken);
        return new EffectivePolicy(Parse(defaultJson).SanitizeFor(os), false, "기본 정책");
    }

    public async Task<EffectivePolicy> SaveAsync(
        string agentId,
        UpdatePolicy policy,
        OsInfo? os = null,
        CancellationToken cancellationToken = default)
    {
        var sanitized = policy.SanitizeFor(os);
        await _store.SetSettingAsync(Key(agentId), UpdateAgentJson.Serialize(sanitized), cancellationToken);
        return new EffectivePolicy(sanitized, true, "PC별 설정");
    }

    public async Task DeleteAsync(string agentId, CancellationToken cancellationToken = default) =>
        await _store.DeleteSettingAsync(Key(agentId), cancellationToken);

    /// <summary>
    /// 작업 배정 시 사용할 정책을 해석한다. 교사가 지정한 정책이 있으면 OS 안전 규칙만 적용하고,
    /// 없으면 PC별 저장 정책(없으면 기본 정책)을 사용한다.
    /// </summary>
    public async Task<(UpdatePolicy Policy, IReadOnlyList<string> Warnings)> ResolveForStartAsync(
        string agentId,
        OsInfo? os,
        UpdatePolicy? requested,
        bool saveAsPolicy,
        string machineName,
        CancellationToken cancellationToken = default)
    {
        var fallback = await GetEffectiveAsync(agentId, os, cancellationToken);
        var policy = (requested ?? fallback.Policy).SanitizeFor(os);
        var warnings = new List<string>();

        if (requested is not null)
        {
            warnings.AddRange(requested.SanitizationNotes(os).Select(note => $"{machineName}: {note}"));
            if (saveAsPolicy)
            {
                await SaveAsync(agentId, requested, os, cancellationToken);
            }
        }

        return (policy, warnings);
    }

    public async Task<IReadOnlyList<AgentPolicySummary>> GetSummariesAsync(
        CancellationToken cancellationToken = default)
    {
        var overrides = await _store.GetSettingsByPrefixAsync(AgentKeyPrefix, cancellationToken);
        var agents = await _store.GetAgentsAsync(cancellationToken);
        return agents.Select(agent =>
        {
            var hasOverride = overrides.ContainsKey(Key(agent.Agent.AgentId));
            return new AgentPolicySummary
            {
                AgentId = agent.Agent.AgentId,
                HasOverride = hasOverride,
                Source = hasOverride ? "PC별 설정" : "기본 정책",
            };
        }).ToList();
    }

    private static string Key(string agentId) => AgentKeyPrefix + agentId;

    private static UpdatePolicy Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return UpdatePolicy.Default;
        try
        {
            return UpdateAgentJson.Deserialize<UpdatePolicy>(json) ?? UpdatePolicy.Default;
        }
        catch
        {
            return UpdatePolicy.Default;
        }
    }
}
