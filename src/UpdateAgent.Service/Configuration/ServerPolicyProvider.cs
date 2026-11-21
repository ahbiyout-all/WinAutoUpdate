using UpdateAgent.Core.Storage;
using UpdateAgent.Shared.Models;
using UpdateAgent.Shared.Serialization;

namespace UpdateAgent.Service.Configuration;

/// <summary>LabManager가 캐시해 둔 서버 정책을 읽는다. 없으면 기본 정책을 사용한다.</summary>
public sealed class ServerPolicyProvider
{
    public const string SettingKey = "server-policy";

    private readonly Func<string, CancellationToken, Task<string?>> _readSetting;

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
            return string.IsNullOrWhiteSpace(json)
                ? UpdatePolicy.Default
                : UpdateAgentJson.Deserialize<UpdatePolicy>(json) ?? UpdatePolicy.Default;
        }
        catch
        {
            return UpdatePolicy.Default;
        }
    }
}
