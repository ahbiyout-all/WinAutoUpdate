using UpdateAgent.Core.Storage;
using UpdateAgent.Shared.Models;
using UpdateAgent.Shared.Serialization;

namespace UpdateAgent.Agent.Desktop.Services;

/// <summary>
/// 서비스 로컬 DB에 캐시된 교사 정책을 읽는다. 학생용 앱은 토큰 없이 로컬 설정만 읽는다.
/// </summary>
public sealed class LocalPolicyReader
{
    public async Task<UpdatePolicy> ReadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var store = new SqliteJobStore();
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
            var json = await store
                .GetSettingAsync(UpdateAgent.Shared.UpdateAgentConstants.ServerPolicySettingKey, cancellationToken)
                .ConfigureAwait(false);

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
