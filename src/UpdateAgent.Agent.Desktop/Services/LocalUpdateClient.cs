using UpdateAgent.Core.Comms;
using UpdateAgent.Shared.Models;

namespace UpdateAgent.Agent.Desktop.Services;

/// <summary>학생용 상태 앱에서 실행 중인 서비스에 작업 시작을 요청한다.</summary>
public sealed class LocalUpdateClient
{
    private readonly LocalControlPipeClient _pipe = new();

    public Task<LocalControlResponse> StartAsync(
        bool maestroConfirmed,
        CancellationToken cancellationToken = default) =>
        _pipe.SendAsync(new LocalControlRequest
        {
            Action = LocalControlActions.StartUpdate,
            MaestroConfirmed = maestroConfirmed,
        }, cancellationToken);
}
