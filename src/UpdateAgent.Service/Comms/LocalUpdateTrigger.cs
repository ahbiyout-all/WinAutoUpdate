using Microsoft.Extensions.Logging;
using UpdateAgent.Core.Orchestration;
using UpdateAgent.Service.Configuration;
using UpdateAgent.Shared.Models;

namespace UpdateAgent.Service.Comms;

/// <summary>작업 러너를 시작하는 대리자. 로컬 트리거와 원격 명령이 같은 경로를 사용한다.</summary>
public delegate Task<StartJobResult> StartJobDelegate(
    UpdatePolicy policy,
    bool maestroConfirmed,
    string confirmedBy,
    CancellationToken cancellationToken);

/// <summary>
/// 학생용 상태 앱의 로컬 시작 요청을 검증한 뒤 서비스의 작업 러너를 실행한다.
/// 확인 주체는 서버에서 "student"로 고정해 교사 확인으로 위조되지 않게 한다.
/// </summary>
public sealed class LocalUpdateTrigger
{
    public const string ConfirmedBy = "student";

    private readonly StartJobDelegate _startJob;
    private readonly ServerPolicyProvider _policies;
    private readonly ILogger<LocalUpdateTrigger> _logger;

    public LocalUpdateTrigger(
        StartJobDelegate startJob,
        ServerPolicyProvider policies,
        ILogger<LocalUpdateTrigger> logger)
    {
        _startJob = startJob;
        _policies = policies;
        _logger = logger;
    }

    public async Task<LocalControlResponse> HandleAsync(
        LocalControlRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(request.Action, LocalControlActions.StartUpdate, StringComparison.OrdinalIgnoreCase))
        {
            return Fail("지원하지 않는 로컬 제어 요청입니다.");
        }

        var policy = await _policies.GetAsync(cancellationToken).ConfigureAwait(false);

        if (policy.RequireMaestroManualConfirmation && !request.MaestroConfirmed)
        {
            return Fail("Maestro를 수동 복구 모드로 전환하고 확인해야 작업을 시작할 수 있습니다.");
        }

        var confirmed = request.MaestroConfirmed || !policy.RequireMaestroManualConfirmation;
        _logger.LogInformation(
            "학생 요청으로 업데이트 작업 시작: confirmed={Confirmed}, by={ConfirmedBy}",
            confirmed,
            ConfirmedBy);

        try
        {
            var result = await _startJob(policy, confirmed, ConfirmedBy, cancellationToken).ConfigureAwait(false);
            return new LocalControlResponse
            {
                Success = result.Started,
                Message = result.Started ? "업데이트 작업을 시작했습니다." : result.Message,
                JobId = result.Job?.JobId,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "학생 요청 작업 시작 실패");
            return Fail("작업을 시작하지 못했습니다: " + ex.Message);
        }
    }

    private static LocalControlResponse Fail(string message) =>
        new() { Success = false, Message = message };
}
