namespace UpdateAgent.Shared.Models;

/// <summary>대시보드/오버레이가 표시하는 작업 요약.</summary>
public sealed record JobSummary
{
    public required string JobId { get; init; }
    public string AgentId { get; init; } = "";
    public string AgentName { get; init; } = "";
    public UpdateJobState State { get; init; } = UpdateJobState.Pending;

    /// <summary>사용자에게 표시할 현재 단계 (한국어).</summary>
    public string Phase { get; init; } = "대기";

    public int ProgressPercent { get; init; }
    public int TotalItems { get; init; }
    public int CompletedItems { get; init; }
    public int InstalledItems { get; init; }
    public int FailedItems { get; init; }
    public int RebootCount { get; init; }
    public int MaxRebootCount { get; init; } = 5;

    /// <summary>진행 없이 검색을 반복한 횟수. 작업 재시작 후에도 유지한다.</summary>
    public int PassesWithoutProgress { get; init; }

    public string? CurrentKb { get; init; }
    public string Message { get; init; } = "";

    /// <summary>
    /// 실제 설치가 아닌 시뮬레이션(--dry-run) 작업인지. 대시보드가 설치 완료로 오인하지 않게 표시한다.
    /// </summary>
    public bool IsDryRun { get; init; }

    public PauseReason PauseReason { get; init; }
    public DateTimeOffset? PauseUntil { get; init; }
    public DateTimeOffset StartedAt { get; init; } = UpdateAgent.Guards.UtcClock.Now;
    public DateTimeOffset UpdatedAt { get; init; } = UpdateAgent.Guards.UtcClock.Now;

    /// <summary>
    /// 에이전트가 보고한 원본 UpdatedAt. 서버 수신 시각으로 UpdatedAt을 덮어쓴 뒤에도
    /// 시간 순서 검증을 같은 시계로 수행하기 위해 보관한다.
    /// </summary>
    public DateTimeOffset? ReportedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }
    public string? LastError { get; init; }

    /// <summary>재부팅 후 복원 마커 검증을 통과했는지.</summary>
    public bool RestoreVerified { get; init; }

    /// <summary>완료 후 Maestro "현재 상태 저장 → 보호 시작" 확인이 필요한 상태인지.</summary>
    public bool RequiresMaestroAttention { get; init; }

    public bool IsTerminal =>
        UpdateAgent.Guards.JobStateRules.IsTerminal(State.ToString());

    public string StateLabel => State switch
    {
        UpdateJobState.Pending => "대기",
        UpdateJobState.Preflight => "사전 점검",
        UpdateJobState.Scanning => "검색 중",
        UpdateJobState.Downloading => "다운로드 중",
        UpdateJobState.Installing => "설치 중",
        UpdateJobState.PendingReboot => "재부팅 대기",
        UpdateJobState.Rebooting => "재부팅 중",
        UpdateJobState.Paused => "일시중지",
        UpdateJobState.Completed => "완료",
        UpdateJobState.Failed => "실패",
        UpdateJobState.Cancelled => "취소됨",
        _ => State.ToString(),
    };
}

/// <summary>작업 상세 (항목 포함).</summary>
public sealed record JobDetail
{
    public required JobSummary Summary { get; init; }
    public IReadOnlyList<JobItem> Items { get; init; } = [];
    public UpdatePolicy Policy { get; init; } = UpdatePolicy.Default;
    public IReadOnlyList<PreflightCheck> PreflightChecks { get; init; } = [];
    public MarkerVerificationResult? MarkerVerification { get; init; }
}

/// <summary>에이전트가 등록/체크인할 때 보내는 정보.</summary>
public sealed record AgentInfo
{
    public required string AgentId { get; init; }
    public string MachineName { get; init; } = Environment.MachineName;
    public string AgentVersion { get; init; } = UpdateAgentConstants.AgentVersion;
    public string? IpAddress { get; init; }
    public IReadOnlyList<string> MacAddresses { get; init; } = [];
    public OsInfo? Os { get; init; }
    public DateTimeOffset FirstSeen { get; init; } = UpdateAgent.Guards.UtcClock.Now;
    public DateTimeOffset LastSeen { get; init; } = UpdateAgent.Guards.UtcClock.Now;
    public AgentState State { get; init; } = AgentState.Online;
    public bool MaestroInstalled { get; init; }

    /// <summary>감지 오류로 설치 여부를 확인하지 못했는지. false를 "미설치"로 단정하지 않는다.</summary>
    public bool MaestroDetectionFailed { get; init; }

    public bool IsLaptop { get; init; }

    /// <summary>노트북 여부 조회 실패 여부.</summary>
    public bool LaptopDetectionFailed { get; init; }

    public bool HasBattery { get; init; }
    public int FreeDiskMb { get; init; }

    /// <summary>
    /// 허용 목록에 있는 비밀성이 없는 설정만으로 계산한 구성 지문.
    /// 설정을 읽거나 파싱하지 못하면 null이며 서버는 이를 확인 불가로 취급한다.
    /// </summary>
    public string? ConfigFingerprint { get; init; }

    /// <summary>
    /// 구성 지문을 신뢰할 수 없는 경우. 이 필드를 보내지 않는 이전 에이전트도
    /// 확인 불가로 취급되도록 기본값은 true이다.
    /// </summary>
    public bool ConfigFingerprintUnavailable { get; init; } = true;

    /// <summary>
    /// 체크인을 직접 보낼 수 있는 실행 중 서비스의 자체 표시. 체크인이 없으면
    /// 서버가 오래됨(이탈)으로 판단하며, 이 필드로 정지 신호를 위조하지 않는다.
    /// </summary>
    public bool? ServiceAlive { get; init; }
}

/// <summary>에이전트의 주기적 상태 보고.</summary>
public sealed record AgentTelemetry
{
    public required string AgentId { get; init; }
    public DateTimeOffset Timestamp { get; init; } = UpdateAgent.Guards.UtcClock.Now;
    public AgentState State { get; init; } = AgentState.Online;
    public JobSummary? CurrentJob { get; init; }
    public JobDetail? CurrentJobDetail { get; init; }
    public string? CurrentPhase { get; init; }
    public bool UserLoggedOn { get; init; }

    /// <summary>WTS 세션 조회 실패 여부. false를 "사용자 없음"으로 단정하지 않는다.</summary>
    public bool UserSessionProbeFailed { get; init; }

    public IReadOnlyList<string> LoggedOnUsers { get; init; } = [];
    public bool WuBusy { get; init; }

    /// <summary>WUA 사용 중 여부를 확인하지 못했는지. false를 "유휴"로 단정하지 않는다.</summary>
    public bool WuBusyProbeFailed { get; init; }

    public bool RebootPending { get; init; }

    /// <summary>재부팅 대기 여부를 확인하지 못했는지. false를 "대기 아님"으로 단정하지 않는다.</summary>
    public bool RebootPendingProbeFailed { get; init; }

    public MaestroDetectionResult? Maestro { get; init; }
    public int FreeDiskMb { get; init; }
    public bool OnAcPower { get; init; } = true;
    public string? LastError { get; init; }
}

/// <summary>LabManager가 발급하는 원격 명령.</summary>
public sealed record AgentCommand
{
    public required string CommandId { get; init; }
    public required string AgentId { get; init; }
    public CommandType Type { get; init; }
    public string PayloadJson { get; init; } = "{}";
    public CommandStatus Status { get; init; } = CommandStatus.Pending;
    public string IssuedBy { get; init; } = "system";
    public DateTimeOffset CreatedAt { get; init; } = UpdateAgent.Guards.UtcClock.Now;
    public DateTimeOffset ExpiresAt { get; init; } = UpdateAgent.Guards.UtcClock.Now.AddHours(24);
    public string? ResultMessage { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
}

/// <summary>명령 실행 결과.</summary>
public sealed record CommandResult
{
    public required string CommandId { get; init; }
    public required string AgentId { get; init; }
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public DateTimeOffset CompletedAt { get; init; } = UpdateAgent.Guards.UtcClock.Now;
}

/// <summary>에이전트 → LabManager 체크인 요청.</summary>
public sealed record AgentCheckInRequest
{
    public required AgentInfo Agent { get; init; }
    public required AgentTelemetry Telemetry { get; init; }
    public IReadOnlyList<CommandResult> CommandResults { get; init; } = [];
    public DateTimeOffset SentAt { get; init; } = UpdateAgent.Guards.UtcClock.Now;
}

/// <summary>
/// 서버가 명령 결과를 각각 수락했는지 알려주는 응답 항목.
/// 거부된 결과도 에이전트 큐에서 제거해야 중복 재전송으로 명령 대기열이 막히지 않는다.
/// </summary>
public sealed record CommandResultOutcome
{
    public required string CommandId { get; init; }
    public bool Accepted { get; init; }
}

/// <summary>LabManager → 에이전트 체크인 응답.</summary>
public sealed record AgentCheckInResponse
{
    public DateTimeOffset ServerTime { get; init; } = UpdateAgent.Guards.UtcClock.Now;
    public IReadOnlyList<AgentCommand> Commands { get; init; } = [];
    public JobAssignment? Assignment { get; init; }
    public UpdatePolicy? Policy { get; init; }
    public bool MaestroManualConfirmationRequired { get; init; } = true;
    public string? Notice { get; init; }

    /// <summary>
    /// 결과 ID별 서버 처리 여부. 이전 서버는 보내지 않으므로 기본값은 빈 목록이다.
    /// 비어 있으면 제출한 결과 전체를 ack 처리한다.
    /// </summary>
    public IReadOnlyList<CommandResultOutcome> CommandResultOutcomes { get; init; } = [];
}

public enum JobAssignmentAction
{
    Start = 0,
    Resume = 1,
    Cancel = 2,
}

/// <summary>LabManager가 내리는 작업 배정. 에이전트는 이 정보를 캐시한다.</summary>
public sealed record JobAssignment
{
    public required string JobId { get; init; }
    public JobAssignmentAction Action { get; init; } = JobAssignmentAction.Start;
    public UpdatePolicy Policy { get; init; } = UpdatePolicy.Default;
    public DateTimeOffset AssignedAt { get; init; } = UpdateAgent.Guards.UtcClock.Now;
    public string? Note { get; init; }
    public bool MaestroConfirmed { get; init; }
    public string ConfirmedBy { get; init; } = "teacher";
}

/// <summary>StartJob 명령의 payload.</summary>
public sealed record StartJobCommandPayload
{
    /// <summary>LabManager가 미리 발급한 작업 ID. 비어 있으면 에이전트가 생성한다.</summary>
    public string? JobId { get; init; }

    public UpdatePolicy Policy { get; init; } = UpdatePolicy.Default;
    public bool MaestroConfirmed { get; init; }
    public string ConfirmedBy { get; init; } = "teacher";

    /// <summary>작업 추적용 상관관계 ID. 에이전트는 무시해도 된다.</summary>
    public string? CorrelationId { get; init; }
}

/// <summary>작업 중단 명령 본문. Force=true면 예약 재부팅·남은 업데이트·오버레이 잠금을 즉시 해제한다.</summary>
public sealed record CancelJobCommandPayload
{
    public bool Force { get; init; }

    /// <summary>작업 추적용 상관관계 ID. 에이전트는 무시해도 된다.</summary>
    public string? CorrelationId { get; init; }
}

public sealed record RebootCommandPayload
{
    public int DelaySeconds { get; init; } = 60;
    public string Reason { get; init; } = "교사 요청에 의한 재부팅";
}

public sealed record ShutdownCommandPayload
{
    public int DelaySeconds { get; init; } = 60;
    public string Reason { get; init; } = "교사 요청에 의한 종료";
}
