using Microsoft.AspNetCore.Http;
using UpdateAgent.Guards;
using UpdateAgent.LabManager.Live;
using UpdateAgent.LabManager.Models;
using UpdateAgent.LabManager.Observability;
using UpdateAgent.LabManager.Storage;
using UpdateAgent.Shared.Models;
using UpdateAgent.Shared.Serialization;

namespace UpdateAgent.LabManager.Services;

/// <summary>작업 시작 요청의 처리 결과. HTTP 상태와 응답 본문을 분리해 테스트한다.</summary>
public sealed record JobStartOutcome(
    int StatusCode,
    StartJobsResponse? Response = null,
    IReadOnlyList<string> Errors = null!);

/// <summary>
/// /api/admin/jobs/start의 본문을 담당한다. 유지보수 창/설정 검증을 작업 생성 전에 수행하고,
/// 배치 예약은 저장소에 남긴 뒤 첫 배치만 즉시 명령화한다.
/// </summary>
public sealed class JobStartService
{
    private readonly LabStore _store;
    private readonly AgentPolicyService _policies;
    private readonly LiveEventBus _liveEvents;
    private readonly JsonLineLogWriter? _observability;

    public JobStartService(
        LabStore store,
        AgentPolicyService policies,
        LiveEventBus liveEvents,
        JsonLineLogWriter? observability = null)
    {
        _store = store;
        _policies = policies;
        _liveEvents = liveEvents;
        _observability = observability;
    }

    /// <summary>실제 요청 경로. 유지보수 창 판정에는 서버 지역 시간을 사용한다.</summary>
    public Task<JobStartOutcome> StartAsync(
        StartJobsRequest request,
        CancellationToken cancellationToken) =>
        StartAsync(request, DateTimeOffset.Now, UtcClock.Now, cancellationToken);

    /// <summary>테스트에서 시각을 고정할 수 있는 오버로드.</summary>
    public async Task<JobStartOutcome> StartAsync(
        StartJobsRequest request,
        DateTimeOffset localNow,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken)
    {
        if (request is null || !request.HasTargets)
            return Reject("대상 PC를 선택하세요.");

        // 상관관계 ID는 토큰이 아니라 임의 식별자다. 형식이 잘못됐거나 토큰/암호처럼
        // 보이는 값은 받지 않고 서버가 새로 발급해 로그·감사·명령 payload가 같은 값을 공유하게 한다.
        var suppliedCorrelation = request.CorrelationId;
        var candidate = TextGuard.IsSafeIdentifier(suppliedCorrelation)
            ? ObservabilityEvent.Sanitize(suppliedCorrelation)
            : null;
        var correlationId = candidate is not null &&
                            string.Equals(candidate, suppliedCorrelation, StringComparison.Ordinal)
            ? candidate
            : Guid.NewGuid().ToString("N");

        // 요청 정책이 없으면 서버 기본 정책에서 롤아웃 설정을 읽는다.
        // PC별 정책의 롤아웃 값은 일괄 시작 범위가 아니므로 사용하지 않는다.
        var rolloutPolicy = request.Policy ?? await _policies.GetDefaultAsync(cancellationToken);
        if (!RolloutConfiguration.TryCreate(rolloutPolicy, out var rollout, out var configErrors))
            return Reject(configErrors);

        if (!rollout.IsWithinWindow(localNow))
        {
            return Reject(
                $"현재 시간({localNow:HH:mm})은 유지보수 시간({rollout.WindowLabel})이 아닙니다. " +
                "유지보수 시간 안에 다시 요청하세요.");
        }

        var targets = request.AgentIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var batchCount = rollout.BatchCount(targets.Count);

        // 오버플로로 일부 작업만 생성되는 것을 막기 위해 모든 예약 시각을 먼저 계산한다.
        DateTimeOffset[] dueTimes;
        try
        {
            dueTimes = targets.Select((_, index) =>
            {
                var batch = rollout.BatchIndexOf(index);
                var seconds = (long)(batch - 1) * rollout.IntervalSeconds;
                return utcNow.Add(TimeSpan.FromSeconds(seconds));
            }).ToArray();
        }
        catch (ArgumentOutOfRangeException)
        {
            return Reject("배치 예약 시간이 범위를 벗어났습니다. 배치 간격을 줄이세요.");
        }

        var jobIds = new List<string>();
        var jobCorrelations = new List<JobCorrelation>();
        var errors = new List<string>();
        var warnings = new List<string>();
        var dispatchedBatches = 0;
        var scheduledBatchIndexes = new HashSet<int>();

        for (var i = 0; i < targets.Count; i++)
        {
            var agentId = targets[i];
            var agent = await _store.GetAgentAsync(agentId, cancellationToken);
            if (agent is null)
            {
                errors.Add($"{agentId}: 등록되지 않은 PC");
                continue;
            }

            // Maestro가 설치된 PC와 감지 자체가 실패한 PC는 모두 보호 해제 확인을 건너뛸 수 없다.
            if (AgentPolicyService.RequiresMaestroConfirmation(agent) && !request.MaestroConfirmed)
            {
                errors.Add($"{agent.Agent.MachineName}: Maestro 보호 해제 확인이 필요합니다.");
                continue;
            }

            // 체크인 경로와 같은 상한을 사용한다. 배치로 나눠도 대기 작업은 전부 활성 작업이다.
            if (!await _store.CanAcceptNewJobAsync(agentId, cancellationToken))
            {
                errors.Add($"{agent.Agent.MachineName}: 활성 작업 {LabStore.MaxActiveJobs}건 상한에 도달했습니다.");
                continue;
            }

            var resolved = await _policies.ResolveForStartAsync(
                agentId,
                agent.Agent.Os,
                request.Policy,
                request.SaveAsAgentPolicy,
                agent.Agent.MachineName,
                cancellationToken);
            warnings.AddRange(resolved.Warnings);

            if (!agent.Online)
            {
                errors.Add($"{agent.Agent.MachineName}: 오프라인 (WoL 후 다시 시도)");
                continue;
            }

            var policy = rollout.ApplyTo(resolved.Policy);
            var batch = rollout.BatchIndexOf(i);
            var jobId = JobIdGuard.Create(utcNow, Guid.NewGuid());

            await _store.SaveJobSummaryAsync(new JobSummary
            {
                JobId = jobId,
                AgentId = agentId,
                AgentName = agent.Agent.MachineName,
                State = UpdateJobState.Pending,
                Phase = "명령 전달 대기",
                Message = "교사가 업데이트 작업을 배정했습니다.",
                StartedAt = utcNow,
                UpdatedAt = utcNow,
                ReportedAt = utcNow,
                MaxRebootCount = policy.MaxRebootCount,
                RequiresMaestroAttention = policy.RequireMaestroManualConfirmation,
            }, agent.Agent.MachineName, cancellationToken, correlationId);

            await _store.AddJobTransitionAsync(new JobTransition
            {
                JobId = jobId,
                Timestamp = utcNow,
                Event = "created",
                FromState = null,
                ToState = UpdateJobState.Pending.ToString(),
                Phase = "명령 전달 대기",
                Message = "교사가 업데이트 작업을 배정했습니다.",
                Actor = "teacher",
                CorrelationId = correlationId,
                DedupeKey = $"created:{jobId}",
            }, cancellationToken);

            await _store.SaveChecklistAsync(new MaestroChecklist
            {
                JobId = jobId,
                ProtectionSuspendedConfirmed = request.MaestroConfirmed,
                ProtectionSuspendedConfirmedAt = request.MaestroConfirmed ? utcNow : null,
                ConfirmedBy = request.MaestroConfirmed ? request.ConfirmedBy : null,
                RequiresManualConfirmation = policy.RequireMaestroManualConfirmation,
            }, cancellationToken);

            var payload = UpdateAgentJson.Serialize(new StartJobCommandPayload
            {
                JobId = jobId,
                Policy = policy,
                MaestroConfirmed = request.MaestroConfirmed,
                ConfirmedBy = request.ConfirmedBy,
                CorrelationId = correlationId,
            });

            // 배치 옵션이 없으면 예약 상태를 만들지 않고 기존과 같이 바로 명령을 발급한다.
            // 배치 옵션이 있으면 첫 배치만 즉시 명령화하고 나머지는 저장된 예약을 읽어 발급한다.
            AgentCommand? firstCommand;
            if (rollout.BatchSize <= 0)
            {
                firstCommand = NewStartCommand(agentId, payload, utcNow);
                await _store.AddCommandAsync(firstCommand, cancellationToken);
            }
            else
            {
                firstCommand = batch == 1
                    ? NewStartCommand(agentId, payload, utcNow)
                    : null;
                await _store.SaveRolloutScheduleAsync(new JobRolloutSchedule
                {
                    JobId = jobId,
                    AgentId = agentId,
                    MachineName = agent.Agent.MachineName,
                    BatchIndex = batch,
                    BatchCount = batchCount,
                    IntervalSeconds = rollout.IntervalSeconds,
                    DueAt = dueTimes[i],
                    PayloadJson = payload,
                }, firstCommand, cancellationToken);
            }

            // 실제로 즉시 발급/예약된 배치만 응답에 반영한다(앞선 배치가 전부 실패할 수 있음).
            if (firstCommand is not null)
            {
                dispatchedBatches = 1;
                await _store.AddJobTransitionAsync(new JobTransition
                {
                    JobId = jobId,
                    Timestamp = firstCommand.CreatedAt,
                    Event = "command-issued",
                    FromState = UpdateJobState.Pending.ToString(),
                    ToState = null,
                    Phase = "명령 전달",
                    Message = "첫 StartJob 명령 발급",
                    Actor = "teacher",
                    CorrelationId = correlationId,
                    DedupeKey = $"command:{firstCommand.CommandId}",
                }, cancellationToken);
            }
            else
            {
                scheduledBatchIndexes.Add(batch);
            }

            await _store.AddAuditAsync(
                "teacher",
                "job-start",
                $"{agent.Agent.MachineName}/{jobId}",
                string.IsNullOrWhiteSpace(request.Note) ? "업데이트 작업 시작" : request.Note,
                cancellationToken,
                correlationId);

            jobIds.Add(jobId);
            jobCorrelations.Add(new JobCorrelation(jobId, correlationId));
            _observability?.Write(new ObservabilityEvent(
                UtcTimestamps.Now, "Information", "job-created", jobId, agentId, correlationId));
        }

        var pendingBatches = scheduledBatchIndexes.Count;
        var createdBatches = dispatchedBatches + pendingBatches;
        if (createdBatches > 1 && jobIds.Count > 0)
        {
            var intervalText = rollout.IntervalSeconds == 0
                ? "바로"
                : $"{rollout.IntervalSeconds}초 간격으로";
            warnings.Add(dispatchedBatches > 0
                ? $"{jobIds.Count}대를 {createdBatches}개 배치로 예약했습니다. " +
                  $"첫 배치는 즉시 시작하고 나머지는 {intervalText} 전송합니다."
                : $"{jobIds.Count}대를 {createdBatches}개 배치로 예약했습니다. " +
                  $"예약 배치를 {intervalText} 전송합니다.");
            warnings.Add($"{pendingBatches}/{createdBatches}개 배치가 서버에 대기 중입니다.");
        }

        if (!string.IsNullOrWhiteSpace(suppliedCorrelation) && suppliedCorrelation != correlationId)
        {
            warnings.Add("상관관계 ID 형식이 올바르지 않아 서버가 새로 발급했습니다.");
        }

        _liveEvents.Signal();
        return new JobStartOutcome(
            StatusCodes.Status200OK,
            new StartJobsResponse
            {
                JobIds = jobIds,
                Errors = errors,
                Warnings = warnings.Distinct(StringComparer.Ordinal).ToList(),
                TotalBatches = createdBatches,
                DispatchedBatches = dispatchedBatches,
                PendingBatches = pendingBatches,
                CorrelationId = correlationId,
                JobCorrelations = jobCorrelations,
            },
            errors);
    }

    private static AgentCommand NewStartCommand(
        string agentId,
        string payloadJson,
        DateTimeOffset issuedAt) => new()
    {
        CommandId = Guid.NewGuid().ToString("N"),
        AgentId = agentId,
        Type = CommandType.StartJob,
        PayloadJson = payloadJson,
        Status = CommandStatus.Pending,
        IssuedBy = "teacher",
        CreatedAt = issuedAt,
        ExpiresAt = issuedAt.AddHours(12),
    };

    private static JobStartOutcome Reject(string error) => Reject([error]);

    private static JobStartOutcome Reject(IEnumerable<string> errors)
    {
        var messages = errors.Distinct(StringComparer.Ordinal).ToList();
        return new JobStartOutcome(
            StatusCodes.Status400BadRequest,
            Errors: messages.Count > 0 ? messages : ["작업 시작 요청이 올바르지 않습니다."]);
    }
}
