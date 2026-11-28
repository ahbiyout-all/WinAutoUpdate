using System.Text.RegularExpressions;
using UpdateAgent.Guards;
using UpdateAgent.LabManager.Live;
using UpdateAgent.LabManager.Storage;
using UpdateAgent.Shared.Models;
using UpdateAgent.Shared.Serialization;

namespace UpdateAgent.LabManager.Services;

/// <summary>에이전트별로 저장하는 구성 지문 기준선. 지문 원문과 비밀 값은 저장하지 않는다.</summary>
public sealed record AgentIntegrityState(
    string? BaselineFingerprint,
    string? LastFingerprint,
    bool ConfigChanged,
    bool FingerprintUnavailable,
    DateTimeOffset? BaselineAt,
    DateTimeOffset? LastObservedAt);

/// <summary>화면에 그대로 표시하는 무결성/이탈 판정. 값이 없으면 확인 불가로 남는다.</summary>
public sealed record AgentIntegrityView(
    bool IntegrityUnavailable,
    bool ConfigChanged,
    bool VersionMismatch,
    bool VersionUnknown,
    bool Departed,
    string? DepartureReason);

/// <summary>
/// 체크인으로 받은 구성 지문의 기준선을 관리하고, 버전·체크인 공백을 해석한다.
/// 탐지 전용이며 에이전트를 막거나 종료하지 않는다.
/// </summary>
public sealed class AgentIntegrityService
{
    public const string SettingKeyPrefix = "agent-integrity:";
    public const string DepartureReasonText = "체크인 없음";
    private const int MaxFingerprintLength = 256;

    private static readonly Regex SecretLike = new(
        @"(?i)(token|password|passwd|pwd|secret)",
        RegexOptions.CultureInvariant);

    private readonly LabStore _store;
    private readonly string? _expectedAgentVersion;

    public AgentIntegrityService(LabStore store, string? expectedAgentVersion = null)
    {
        _store = store;
        _expectedAgentVersion = expectedAgentVersion;
    }

    /// <summary>
    /// 유효한 체크인에서 기준선을 관찰한다. 처음 지문을 받은 시점의 값이 기준선이며,
    /// 이후 달라지면 플래그가 남는다. 교사가 재설정하기 전에는 사라지지 않는다.
    /// </summary>
    public async Task<AgentIntegrityState> ObserveAsync(
        string agentId,
        AgentInfo agent,
        CancellationToken cancellationToken = default)
    {
        var canonicalId = AgentIdGuard.Canonical(agentId);
        var key = Key(canonicalId);
        var now = UtcTimestamps.Now;
        var existing = Parse(await _store.GetSettingAsync(key, cancellationToken));
        var fingerprint = NormalizeFingerprint(agent.ConfigFingerprint);
        var unavailable = agent.ConfigFingerprintUnavailable || fingerprint is null;

        AgentIntegrityState state;
        var newlyChanged = false;

        if (existing is null)
        {
            var baseline = unavailable ? null : fingerprint;
            state = new AgentIntegrityState(
                BaselineFingerprint: baseline,
                LastFingerprint: fingerprint,
                ConfigChanged: false,
                FingerprintUnavailable: unavailable,
                BaselineAt: baseline is null ? null : now,
                LastObservedAt: now);
        }
        else if (existing.BaselineFingerprint is null && !unavailable)
        {
            // 첫 체크인에 지문이 없었다면 기준선도 없다. 지문을 처음 읽을 수 있게 된
            // 시점이 "첫 유효 체크인"이므로 이 값을 기준선으로 삼는다.
            state = existing with
            {
                BaselineFingerprint = fingerprint,
                LastFingerprint = fingerprint,
                ConfigChanged = false,
                FingerprintUnavailable = false,
                BaselineAt = now,
                LastObservedAt = now,
            };
        }
        else
        {
            // 감사는 플래그가 false에서 true로 바뀐 순간 한 번만 남긴다.
            // 이미 변경 상태인 체크인이 반복되어도 플래그는 유지되고 감사는 중복되지 않는다.
            newlyChanged = !unavailable &&
                           !existing.ConfigChanged &&
                           existing.BaselineFingerprint is not null &&
                           !string.Equals(existing.BaselineFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase);
            state = existing with
            {
                LastFingerprint = fingerprint,
                ConfigChanged = existing.ConfigChanged || newlyChanged,
                FingerprintUnavailable = unavailable,
                LastObservedAt = now,
            };
        }

        await _store.SetSettingAsync(key, UpdateAgentJson.Serialize(state), cancellationToken);

        if (newlyChanged)
        {
            // 지문 값 자체는 감사 로그에 남기지 않는다.
            await _store.AddAuditAsync(
                "agent", "config-changed", agent.MachineName,
                "구성 지문이 기준선과 다릅니다", cancellationToken);
        }

        return state;
    }

    public async Task<AgentIntegrityState?> GetAsync(
        string agentId,
        CancellationToken cancellationToken = default) =>
        Parse(await _store.GetSettingAsync(Key(AgentIdGuard.Canonical(agentId)), cancellationToken));

    /// <summary>
    /// 교사가 확인한 뒤 현재 지문을 새 기준선으로 삼는다. 현재 지문이 없으면
    /// 기준선을 지우고 확인 불가 상태로 되돌린다.
    /// </summary>
    public async Task<AgentIntegrityState?> AcknowledgeAsync(
        string agentId,
        CancellationToken cancellationToken = default)
    {
        var canonicalId = AgentIdGuard.Canonical(agentId);
        var key = Key(canonicalId);
        var existing = Parse(await _store.GetSettingAsync(key, cancellationToken));
        if (existing is null) return null;

        var now = UtcTimestamps.Now;
        var baseline = NormalizeFingerprint(existing.LastFingerprint);
        var state = existing with
        {
            BaselineFingerprint = baseline,
            ConfigChanged = false,
            FingerprintUnavailable = baseline is null,
            BaselineAt = baseline is null ? null : now,
            LastObservedAt = now,
        };
        await _store.SetSettingAsync(key, UpdateAgentJson.Serialize(state), cancellationToken);
        await _store.AddAuditAsync(
            "teacher", "config-baseline-reset", canonicalId,
            "구성 지문 기준선 재설정", cancellationToken);
        return state;
    }

    /// <summary>버전 비교. 기대값이나 보고값이 없으면 true/false가 아닌 null(확인 불가)이다.</summary>
    public static bool? CompareVersion(string? expected, string? reported)
    {
        var left = NormalizeVersion(expected);
        var right = NormalizeVersion(reported);
        if (left is null || right is null) return null;
        return !string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    public AgentIntegrityView Evaluate(
        AgentIntegrityState? state,
        AgentInfo agent,
        DateTimeOffset now)
    {
        var fingerprint = NormalizeFingerprint(agent.ConfigFingerprint);
        // 저장된 마지막 관찰값과 화면의 에이전트 값이 다르면 관찰이 완료되지 않은 것이므로
        // 정상으로 단정하지 않는다. 관찰 실패는 확인 불가로 남긴다.
        var observationMismatch = fingerprint is not null &&
                                  state?.LastFingerprint is not null &&
                                  !string.Equals(state.LastFingerprint, fingerprint,
                                      StringComparison.OrdinalIgnoreCase);
        var unavailable = state is null ||
                          state.FingerprintUnavailable ||
                          state.BaselineFingerprint is null ||
                          agent.ConfigFingerprintUnavailable ||
                          fingerprint is null ||
                          observationMismatch;
        // 관찰이 실패해 플래그가 저장되지 않았더라도 기준선과 현재 값이 다르면 변경으로 표시한다.
        var directChange = fingerprint is not null &&
                           state?.BaselineFingerprint is not null &&
                           !string.Equals(state.BaselineFingerprint, fingerprint,
                               StringComparison.OrdinalIgnoreCase);
        var version = CompareVersion(_expectedAgentVersion, agent.AgentVersion);
        var departed = LiveSnapshot.IsStale(agent.LastSeen, now);

        return new AgentIntegrityView(
            IntegrityUnavailable: unavailable,
            ConfigChanged: state?.ConfigChanged == true || directChange,
            VersionMismatch: version == true,
            VersionUnknown: version is null,
            Departed: departed,
            DepartureReason: departed ? DepartureReasonText : null);
    }

    private static string Key(string canonicalAgentId) => SettingKeyPrefix + canonicalAgentId;

    private static AgentIntegrityState? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return UpdateAgentJson.Deserialize<AgentIntegrityState>(json);
        }
        catch
        {
            return null;
        }
    }

    private static string? NormalizeFingerprint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (text.Length > MaxFingerprintLength || SecretLike.IsMatch(text)) return null;
        return text;
    }

    private static string? NormalizeVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];
        return text.Length == 0 ? null : text;
    }
}
