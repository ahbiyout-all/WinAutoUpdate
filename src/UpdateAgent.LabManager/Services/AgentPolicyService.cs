using UpdateAgent.Guards;
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

    /// <summary>2차 확인 체크박스는 클라이언트가 끌 수 없다. 서버가 항상 켠 상태로 강제한다.</summary>
    private static UpdatePolicy ForceMaestroGate(UpdatePolicy policy) =>
        policy with { RequireMaestroManualConfirmation = true };

    /// <summary>
    /// 작업 시작 게이트는 저장된 설치 여부뿐 아니라 이번 체크인에서 감지가 실패한 경우도
    /// Maestro가 있을 수 있으므로 보호 해제 확인을 요구한다.
    /// </summary>
    public static bool RequiresMaestroConfirmation(AgentRecord agent) =>
        agent.Agent.MaestroInstalled ||
        agent.Agent.MaestroDetectionFailed ||
        agent.Telemetry.Maestro?.DetectionFailed == true;

    public async Task<UpdatePolicy> GetDefaultAsync(CancellationToken cancellationToken = default) =>
        ForceMaestroGate(
            (TryParse(await _store.GetSettingAsync(DefaultSettingKey, cancellationToken)) ?? UpdatePolicy.Default)
                .WithoutFeatureUpdates());

    public async Task<EffectivePolicy> GetEffectiveAsync(
        string agentId,
        OsInfo? os = null,
        CancellationToken cancellationToken = default)
    {
        // 손상된 JSON은 "저장된 설정"으로 세지 않고 기본 정책으로 되돌린다.
        var overridePolicy = TryParse(await _store.GetSettingAsync(Key(agentId), cancellationToken));
        if (overridePolicy is not null)
        {
            return new EffectivePolicy(
                ForceMaestroGate(overridePolicy.SanitizeFor(os).WithoutFeatureUpdates()), true, "PC별 설정");
        }

        var defaultPolicy = TryParse(await _store.GetSettingAsync(DefaultSettingKey, cancellationToken))
            ?? UpdatePolicy.Default;
        return new EffectivePolicy(
            ForceMaestroGate(defaultPolicy.SanitizeFor(os).WithoutFeatureUpdates()), false, "기본 정책");
    }

    public async Task<EffectivePolicy> SaveAsync(
        string agentId,
        UpdatePolicy policy,
        OsInfo? os = null,
        CancellationToken cancellationToken = default)
    {
        var sanitized = ForceMaestroGate(policy.SanitizeFor(os).WithoutFeatureUpdates());
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
        var policy = ForceMaestroGate((requested ?? fallback.Policy).SanitizeFor(os).WithoutFeatureUpdates());
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
        // 과거 릴리스에서 대소문자만 다른 키가 저장될 수 있다. dictionary 생성자로 한 번에 넣으면
        // 정규화된 키가 중복될 때 예외가 나 대시보드 전체가 멈추므로 TryAdd로 충돌을 흡수한다.
        var overrideLookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in overrides)
            overrideLookup.TryAdd(pair.Key, pair.Value);
        var agents = await _store.GetAgentsAsync(cancellationToken);
        return agents.Select(agent =>
        {
            // GetEffectiveAsync와 동일하게 파싱 가능한 값만 유효한 개별 정책으로 표시한다.
            var hasOverride = overrideLookup.TryGetValue(Key(agent.Agent.AgentId), out var json)
                && TryParse(json) is not null;
            return new AgentPolicySummary
            {
                AgentId = agent.Agent.AgentId,
                HasOverride = hasOverride,
                Source = hasOverride ? "PC별 설정" : "기본 정책",
            };
        }).ToList();
    }

    private static string Key(string agentId) => AgentKeyPrefix + AgentIdGuard.Canonical(agentId);

    /// <summary>파싱할 수 없으면 null을 돌려 기본 정책 폴백과 구분한다.</summary>
    private static UpdatePolicy? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return UpdateAgentJson.Deserialize<UpdatePolicy>(json);
        }
        catch
        {
            return null;
        }
    }
}
