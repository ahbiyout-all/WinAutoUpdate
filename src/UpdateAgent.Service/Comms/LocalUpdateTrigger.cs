using Microsoft.Extensions.Logging;
using UpdateAgent.Core.Orchestration;
using UpdateAgent.Core.Windows;
using UpdateAgent.Service.Configuration;
using UpdateAgent.Shared.Models;

namespace UpdateAgent.Service.Comms;

/// <summary>작업 러너를 시작하는 대리자. 로컬 트리거와 원격 명령이 같은 경로를 사용한다.</summary>
public delegate Task<StartJobResult> StartJobDelegate(
    UpdatePolicy policy,
    bool maestroConfirmed,
    string confirmedBy,
    CancellationToken cancellationToken);

/// <summary>작업 중단 대리자. force=true는 긴급 중단을 뜻한다.</summary>
public delegate Task<JobSummary?> CancelJobDelegate(
    bool force,
    CancellationToken cancellationToken);

/// <summary>
/// 학생용 상태 앱/오버레이의 로컬 시작·중단 요청을 검증한 뒤 서비스의 작업 러너를 실행한다.
/// 확인 주체는 서버에서 "student"로 고정해 교사 확인으로 위조되지 않게 한다.
/// </summary>
public sealed class LocalUpdateTrigger
{
    public const string ConfirmedBy = "student";

    private readonly StartJobDelegate _startJob;
    private readonly CancelJobDelegate? _cancelJob;
    private readonly Func<string, string, CancellationToken, Task>? _hideOverlay;
    private readonly ServerPolicyProvider _policies;
    private readonly Func<OsInfo> _getOs;
    private readonly ILogger<LocalUpdateTrigger> _logger;

    public LocalUpdateTrigger(
        StartJobDelegate startJob,
        ServerPolicyProvider policies,
        ILogger<LocalUpdateTrigger> logger,
        CancelJobDelegate? cancelJob = null,
        Func<string, string, CancellationToken, Task>? hideOverlay = null,
        Func<OsInfo>? osProvider = null)
    {
        _startJob = startJob;
        _policies = policies;
        _logger = logger;
        _cancelJob = cancelJob;
        _hideOverlay = hideOverlay;
        _getOs = osProvider ?? WindowsOsInfoProvider.GetCurrent;
    }

    public async Task<LocalControlResponse> HandleAsync(
        LocalControlRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.Equals(request.Action, LocalControlActions.CancelUpdate, StringComparison.OrdinalIgnoreCase))
        {
            return await HandleCancelAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!string.Equals(request.Action, LocalControlActions.StartUpdate, StringComparison.OrdinalIgnoreCase))
        {
            return Fail("지원하지 않는 로컬 제어 요청입니다.");
        }

        UpdatePolicy policy;
        try
        {
            var cachedPolicy = await _policies.GetAsync(cancellationToken).ConfigureAwait(false);
            var os = _getOs();
            policy = request.Options is null
                ? cachedPolicy.SanitizeFor(os)
                : request.Options.ApplyTo(cachedPolicy, os);
        }
        catch (Exception ex)
        {
            var errorId = Guid.NewGuid().ToString("N")[..8];
            _logger.LogError(ex, "학생 수동 업데이트 옵션 적용 실패: {ErrorId}", errorId);
            return Fail("수동 업데이트 옵션을 적용하지 못했습니다.");
        }

        // UI 검증은 우회될 수 있으므로 서비스에서도 최종 정책 기준으로 빈 범위 요청을 거부한다.
        // 학생이 고른 항목이 모두 제외된 경우와 옵션 없이 요청했지만 교사 정책에 범위가 없는 경우를 모두 막는다.
        if (!policy.HasAnyUpdateCategory)
        {
            return Fail("업데이트 범위를 하나 이상 선택해야 합니다.");
        }

        if (request.Options is not null)
        {
            _logger.LogInformation(
                "학생 수동 옵션 적용: preset={PresetId}, overlay={OverlayEnabled}, reboot={RebootPolicy}",
                request.Options.PresetId ?? UpdatePolicyPresets.Custom,
                policy.OverlayEnabled,
                policy.RebootPolicy);
        }

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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 파이프 핸들러 시간 초과는 응답을 보내지 않고 상위에서 시간 초과 메시지를 전달한다.
            throw;
        }
        catch (Exception ex)
        {
            var errorId = Guid.NewGuid().ToString("N")[..8];
            _logger.LogError(ex, "학생 요청 작업 시작 실패: {ErrorId}", errorId);
            return Fail("작업을 시작하지 못했습니다.");
        }
    }

    /// <summary>오버레이 긴급 중단 단축키 요청을 처리한다. 항상 force 중단이며 확인 주체는 student다.</summary>
    private async Task<LocalControlResponse> HandleCancelAsync(CancellationToken cancellationToken)
    {
        if (_cancelJob is null)
        {
            return Fail("이 에이전트에서는 로컬 중단을 사용할 수 없습니다.");
        }

        _logger.LogInformation("오버레이 긴급 중단 요청 수신: by={ConfirmedBy}", ConfirmedBy);
        try
        {
            var summary = await _cancelJob(true, cancellationToken).ConfigureAwait(false);

            if (summary is not null && _hideOverlay is not null)
            {
                try
                {
                    await _hideOverlay(
                        "긴급 중단 요청으로 업데이트를 중지했습니다.",
                        summary.JobId ?? string.Empty,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "긴급 중단 후 오버레이 숨김 전송 실패");
                }
            }

            return new LocalControlResponse
            {
                Success = summary is not null,
                Message = summary is null
                    ? "중단할 작업이 없습니다."
                    : "업데이트를 긴급 중단했습니다.",
                JobId = summary?.JobId,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var errorId = Guid.NewGuid().ToString("N")[..8];
            _logger.LogError(ex, "오버레이 긴급 중단 처리 실패: {ErrorId}", errorId);
            return Fail("작업을 중단하지 못했습니다.");
        }
    }

    private static LocalControlResponse Fail(string message) =>
        new() { Success = false, Message = message };
}
