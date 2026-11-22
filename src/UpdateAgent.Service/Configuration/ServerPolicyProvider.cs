using UpdateAgent.Core.Storage;
using UpdateAgent.Shared.Models;
using UpdateAgent.Shared.Serialization;

namespace UpdateAgent.Service.Configuration;

/// <summary>LabManager가 캐시해 둔 서버 정책을 읽는다. 없으면 기본 정책을 사용한다.</summary>
public sealed class ServerPolicyProvider
{
    public const string SettingKey = UpdateAgent.Shared.UpdateAgentConstants.ServerPolicySettingKey;

    private readonly Func<string, CancellationToken, Task<string?>> _readSetting;
    private UpdatePolicy? _lastKnownGood;

    public ServerPolicyProvider(IJobStore store)
        : this(store.GetSettingAsync)
    {
    }

    public ServerPolicyProvider(Func<string, CancellationToken, Task<string?>> readSetting)
    {
        _readSetting = readSetting;
    }

    public async Task<UpdatePolicy> GetAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var json = await _readSetting(SettingKey, cancellationToken).ConfigureAwait(false);
            var policy = string.IsNullOrWhiteSpace(json)
                ? null
                : UpdateAgentJson.Deserialize<UpdatePolicy>(json);
            if (policy is not null)
            {
                policy = policy.SanitizeFor(null).WithoutFeatureUpdates();
                _lastKnownGood = policy;
                return policy;
            }
        }
        catch
        {
            // 일시적인 DB 오류에서 정책을 관대한 기본값으로 되돌리면 교사 제한이 풀린다.
        }

        return _lastKnownGood ?? UpdatePolicy.Default;
    }
}
