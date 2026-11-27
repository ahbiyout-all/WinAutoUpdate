using UpdateAgent.Guards;
using UpdateAgent.LabManager.Live;
using UpdateAgent.LabManager.Storage;
using UpdateAgent.Shared.Models;
using UpdateAgent.Shared.Serialization;

namespace UpdateAgent.LabManager.Services;

/// <summary>체크인 처리 결과. 상태 코드와 응답 본문을 분리해 테스트에서도 확인할 수 있다.</summary>
public sealed record CheckInProcessingResult(
    int StatusCode,
    AgentCheckInResponse? Response = null,
    string? Error = null);

/// <summary>
/// /api/agents/checkin의 본문 처리. 거부된 명령 결과가 있어도 200으로 응답하고
/// 제출된 모든 명령 ID의 수락 여부를 돌려준다. 에이전트는 이를 ack로 사용한다.
/// </summary>
public static class AgentCheckInHandler
{
    private const int MaxCommandsPerCheckIn = 20;
    private const int MaxCommandResultsPerCheckIn = 100;
    private const int MaxRejectedAuditRowsPerCheckIn = 20;
    private const int MaxKnownAgents = 200;

    public static async Task<CheckInProcessingResult> ProcessAsync(
        AgentCheckInRequest request,
        LabStore labStore,
        AgentPolicyService agentPolicies,
        LiveEventBus liveEvents,
        CheckinGateRegistry gates,
        CancellationToken cancellationToken)
    {
        // 공유 에이전트 토큰으로 임의 ID를 무한 생성해 DB/메모리를 오염시키지 않는다.
        // 모든 서버 경로가 같은 정규화 ID를 쓰도록 체크인 진입점에서 한 번만 정규화한다.
        var agentId = AgentIdGuard.Canonical(request.Agent.AgentId);
        request = request with
        {
            Agent = request.Agent with { AgentId = agentId },
            Telemetry = request.Telemetry with { AgentId = agentId },
        };
        if (!TextGuard.IsSafeIdentifier(agentId))
        {
            await labStore.AddAuditAsync(
                "agent", "checkin-rejected", request.Agent.MachineName,
                "잘못된 에이전트 ID 형식", cancellationToken);
            return new CheckInProcessingResult(
                StatusCodes.Status400BadRequest, Error: "에이전트 ID 형식이 올바르지 않습니다.");
        }

        using var lease = await gates.TryAcquireAsync(agentId, cancellationToken);
        if (lease is null)
        {
            return new CheckInProcessingResult(
                StatusCodes.Status429TooManyRequests, Error: "동시 체크인 요청이 많습니다.");
        }

        var receivedAt = UtcTimestamps.Now;
        var summary = request.Telemetry.CurrentJob;
        var commandResults = request.CommandResults ?? [];

        if (!JobStateGuard.AgentIdsMatch(agentId, request.Telemetry.AgentId))
        {
            await labStore.AddAuditAsync(
                "agent", "job-state-rejected",
                $"{request.Agent.MachineName}/{request.Telemetry.AgentId}",
                "에이전트 정보와 텔레메트리의 에이전트 ID 불일치", cancellationToken);
            return new CheckInProcessingResult(
                StatusCodes.Status400BadRequest, Error: "에이전트 ID가 일치하지 않습니다.");
        }

        if (summary is not null && !TextGuard.IsSafeIdentifier(summary.JobId))
        {
            await labStore.AddAuditAsync(
                "agent", "job-state-rejected",
                $"{request.Agent.MachineName}/{summary.JobId}",
                "잘못된 작업 ID 형식", cancellationToken);
            return new CheckInProcessingResult(
                StatusCodes.Status400BadRequest, Error: "작업 ID 형식이 올바르지 않습니다.");
        }

        // 거부/오버사이즈 응답에도 정책을 함께 돌려줘야 학생 PC가 서버 정책을 캐시할 수 있다.
        var effective = await agentPolicies.GetEffectiveAsync(
            agentId, request.Agent.Os, cancellationToken);

        // 거부 응답에도 모든 결과 ID를 ack로 돌려줘야 에이전트 큐가 영구히 막히지 않는다.
        if (commandResults.Count > MaxCommandResultsPerCheckIn)
        {
            await labStore.AddAuditAsync(
                "agent", "command-result-batch-rejected", request.Agent.MachineName,
                $"명령 결과 {commandResults.Count}건이 상한 {MaxCommandResultsPerCheckIn}건을 초과",
                cancellationToken);
            return AcknowledgeAll(
                receivedAt, commandResults, "명령 결과 배치가 너무 큽니다.", effective);
        }

        var foreignResults = commandResults
            .Where(r => !string.Equals(r.AgentId, agentId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (foreignResults.Count > 0)
        {
            await labStore.AddAuditAsync(
                "agent", "command-result-rejected",
                $"{request.Agent.MachineName}/{foreignResults.Count}",
                "다른 에이전트의 명령 결과 제출", cancellationToken);
            return AcknowledgeAll(
                receivedAt, commandResults, "자신의 명령 결과만 제출할 수 있습니다.", effective);
        }

        // 검증을 통과한 체크인만 에이전트 정보를 갱신한다.
        // 유효한 ID를 계속 새로 만들어 DB를 채우는 것도 막는다.
        if (await labStore.GetAgentAsync(agentId, cancellationToken) is null &&
            await labStore.CountAgentsAsync(cancellationToken) >= MaxKnownAgents)
        {
            await labStore.AddAuditAsync(
                "agent", "checkin-rejected", request.Agent.MachineName,
                "등록 가능한 에이전트 수 상한 초과", cancellationToken);
            return new CheckInProcessingResult(
                StatusCodes.Status429TooManyRequests, Error: "등록 가능한 PC 수를 초과했습니다.");
        }

        await labStore.UpsertAgentAsync(request.Agent, request.Telemetry, cancellationToken);

        IReadOnlyList<CommandResultOutcome> outcomes = [];
        if (commandResults.Count > 0)
        {
            var accepted = await labStore.CompleteCommandResultsAsync(
                agentId, commandResults, cancellationToken);
            var resultOutcomes = new List<CommandResultOutcome>(commandResults.Count);
            var rejectedAuditRows = 0;
            var rejectedTotal = 0;

            foreach (var result in commandResults)
            {
                var isAccepted = accepted.Contains(result.CommandId);
                resultOutcomes.Add(new CommandResultOutcome
                {
                    CommandId = result.CommandId,
                    Accepted = isAccepted,
                });

                if (!isAccepted)
                {
                    rejectedTotal++;
                    // 거부 결과가 대량으로 들어와도 감사 테이블을 채우지 않는다.
                    // ID는 위 outcomes에 모두 남기므로 에이전트 ack에는 영향이 없다.
                    if (rejectedAuditRows < MaxRejectedAuditRowsPerCheckIn)
                    {
                        await labStore.AddAuditAsync(
                            "agent",
                            "command-result-rejected",
                            $"{request.Agent.MachineName}/{result.CommandId}",
                            "알 수 없거나 이미 완료/만료된 명령 결과",
                            cancellationToken);
                        rejectedAuditRows++;
                    }
                    continue;
                }

                await labStore.AddAuditAsync(
                    "agent",
                    result.Success ? "command-ok" : "command-fail",
                    $"{request.Agent.MachineName}/{result.CommandId}",
                    TextGuard.Truncate(result.Message, TextGuard.MaxAuditTextLength),
                    cancellationToken);
            }

            if (rejectedTotal > rejectedAuditRows)
            {
                await labStore.AddAuditAsync(
                    "agent",
                    "command-result-audit-truncated",
                    request.Agent.MachineName,
                    $"거부된 명령 결과 {rejectedTotal}건 중 {rejectedAuditRows}건만 감사 로그에 기록",
                    cancellationToken);
            }

            outcomes = resultOutcomes;

            // StartJob 명령이 실패하면 서버 작업이 Pending에 남지 않도록 실패로 정리한다.
            // 텔레메트리보다 먼저 처리해야 학생이 보낸 상충하는 완료 보고가 실패 상태를 덮지 않는다.
            foreach (var result in commandResults.Where(r => !r.Success && accepted.Contains(r.CommandId)))
            {
                await FailJobForRejectedCommandAsync(
                    labStore, agentId, result, receivedAt, cancellationToken);
            }
        }

        if (summary is not null)
        {
            var previous = await labStore.GetJobAsync(summary.JobId, cancellationToken);
            if (!JobStateGuard.CanAccept(previous?.Summary, summary, agentId, receivedAt))
            {
                await labStore.AddAuditAsync(
                    "agent", "job-state-rejected",
                    $"{request.Agent.MachineName}/{summary.JobId}",
                    $"소유자/종료 상태/시간 순서 검증 실패: {summary.State} ({summary.Phase})",
                    cancellationToken);
            }
            else if (request.Telemetry.CurrentJobDetail is { } mismatchedDetail &&
                     !JobStateGuard.DetailSummaryMatches(summary, mismatchedDetail.Summary))
            {
                await labStore.AddAuditAsync(
                    "agent", "job-state-rejected",
                    $"{request.Agent.MachineName}/{summary.JobId}",
                    $"작업 상세 요약 불일치: {mismatchedDetail.Summary.State}",
                    cancellationToken);
            }
            else
            {
                var validated = JobStateGuard.Normalize(summary, receivedAt, previous?.Summary);
                var activeLimitReached = previous is null && !validated.IsTerminal &&
                    await labStore.CountActiveJobsAsync(agentId, cancellationToken) >= LabStore.MaxActiveJobs;

                if (activeLimitReached)
                {
                    // 공유 토큰으로 새 작업 ID를 무한 생성해 SSE 스냅샷을 키우는 것을 막는다.
                    // 저장하지 않았으므로 대시보드에는 해당 작업을 "확인 불가"로 표시한다.
                    await labStore.AddAuditAsync(
                        "agent", "job-active-cap-rejected",
                        $"{request.Agent.MachineName}/{validated.JobId}",
                        $"비종료 작업 {LabStore.MaxActiveJobs}건 상한으로 새 작업은 저장하지 않음(확인 불가)",
                        cancellationToken);
                }
                else
                {
                    if (request.Telemetry.CurrentJobDetail is { } detail)
                    {
                        // 상세의 요약은 검증된 요약으로 교체해 별도로 위조된 값이 저장되지 않게 한다.
                        await labStore.SaveJobAsync(
                            detail with { Summary = validated },
                            request.Agent.MachineName,
                            cancellationToken);
                    }
                    else
                    {
                        await labStore.SaveJobSummaryAsync(validated, request.Agent.MachineName, cancellationToken);
                    }

                    if (validated.RequiresMaestroAttention &&
                        await labStore.GetChecklistAsync(summary.JobId, cancellationToken) is null)
                    {
                        // 학생용 앱에서 시작한 작업은 체크인으로 처음 발견된다.
                        // 서비스가 학생 확인을 받아 시작한 작업이므로 보호 해제 확인을 기록해
                        // 대시보드에서 재부팅 후 Maestro 단계를 계속 관리할 수 있게 한다.
                        await labStore.SaveChecklistAsync(new MaestroChecklist
                        {
                            JobId = summary.JobId,
                            ProtectionSuspendedConfirmed = true,
                            ProtectionSuspendedConfirmedAt = receivedAt,
                            ConfirmedBy = "student",
                            RequiresManualConfirmation = true,
                        }, cancellationToken);
                    }

                    if (previous is null || previous.Summary.State != validated.State)
                    {
                        await labStore.AddAuditAsync(
                            "agent", "job-state",
                            $"{request.Agent.MachineName}/{validated.JobId}",
                            $"{previous?.Summary.State.ToString() ?? "없음"} → {validated.State} ({validated.Phase})",
                            cancellationToken);
                    }
                }
            }
        }

        var commands = await labStore.TakePendingCommandsAsync(
            agentId,
            MaxCommandsPerCheckIn,
            cancellationToken);

        // PC별 저장 정책이 있으면 그것을, 없으면 서버 기본 정책을 사용한다.
        var policy = effective.Policy;

        // 체크인으로 에이전트/작업 상태가 바뀌었음을 모니터에 알린다.
        liveEvents.Signal();

        return new CheckInProcessingResult(
            StatusCodes.Status200OK,
            new AgentCheckInResponse
            {
                ServerTime = receivedAt,
                Commands = commands,
                Policy = policy,
                MaestroManualConfirmationRequired = policy.RequireMaestroManualConfirmation,
                CommandResultOutcomes = outcomes,
            });
    }

    private static CheckInProcessingResult AcknowledgeAll(
        DateTimeOffset serverTime,
        IReadOnlyList<CommandResult> results,
        string notice,
        EffectivePolicy effective) =>
        new(StatusCodes.Status200OK,
            new AgentCheckInResponse
            {
                ServerTime = serverTime,
                CommandResultOutcomes = results
                    .Select(r => new CommandResultOutcome
                    {
                        CommandId = r.CommandId,
                        Accepted = false,
                    })
                    .ToList(),
                Notice = notice,
                Policy = effective.Policy,
                MaestroManualConfirmationRequired = effective.Policy.RequireMaestroManualConfirmation,
            });

    private static async Task FailJobForRejectedCommandAsync(
        LabStore store,
        string reportingAgentId,
        CommandResult result,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var command = await store.GetCommandAsync(result.CommandId, cancellationToken);
        if (command is null || command.Type != CommandType.StartJob) return;
        if (!string.Equals(command.AgentId, reportingAgentId, StringComparison.OrdinalIgnoreCase)) return;

        var payload = UpdateAgentJson.Deserialize<StartJobCommandPayload>(command.PayloadJson);
        if (payload?.JobId is not { Length: > 0 } jobId) return;

        var job = await store.GetJobAsync(jobId, cancellationToken);
        if (job is null || job.Summary.IsTerminal) return;
        if (!string.Equals(job.Summary.AgentId, reportingAgentId, StringComparison.OrdinalIgnoreCase)) return;

        var failed = job.Summary with
        {
            State = UpdateJobState.Failed,
            Phase = "시작 실패",
            Message = TextGuard.Truncate(result.Message, TextGuard.MaxAuditTextLength),
            LastError = TextGuard.Truncate(result.Message, TextGuard.MaxAuditTextLength),
            UpdatedAt = now,
            ReportedAt = now,
            CompletedAt = now,
        };

        await store.SaveJobSummaryAsync(failed, job.MachineName, cancellationToken);
        await store.AddAuditAsync(
            "system",
            "job-failed",
            $"{job.MachineName}/{jobId}",
            $"시작 명령 거부: {TextGuard.Truncate(result.Message, TextGuard.MaxAuditTextLength)}",
            cancellationToken);
    }
}
