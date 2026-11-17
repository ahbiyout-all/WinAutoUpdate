using UpdateAgent.Shared.Models;

namespace UpdateAgent.LabManager.Desktop.Models;

/// <summary>공개 상태 확인 응답.</summary>
public sealed record HealthResponse
{
    public string Status { get; init; } = "";
    public string Product { get; init; } = "";
    public string Version { get; init; } = "";
    public DateTimeOffset ServerTime { get; init; }
}

/// <summary>대시보드 요약 카운트.</summary>
public sealed record OverviewResponse
{
    public int AgentCount { get; init; }
    public int OnlineCount { get; init; }
    public int BusyCount { get; init; }
    public int ActiveJobs { get; init; }
    public int CompletedJobs { get; init; }
    public int FailedJobs { get; init; }
    public DateTimeOffset ServerTime { get; init; }
}

/// <summary>/api/admin/agents 응답 항목.</summary>
public sealed record AgentRecord
{
    public AgentInfo Agent { get; init; } = null!;
    public AgentTelemetry Telemetry { get; init; } = null!;
    public bool Online { get; init; }
    public DateTimeOffset LastSeen { get; init; }
}

/// <summary>/api/admin/jobs一行.</summary>
public sealed record LabJobRecord
{
    public JobSummary Summary { get; init; } = null!;
    public JobDetail? Detail { get; init; }
    public string MachineName { get; init; } = "";
}

/// <summary>감사 로그 항목.</summary>
public sealed record AuditEntry
{
    public long Id { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public string Actor { get; init; } = "";
    public string Action { get; init; } = "";
    public string Target { get; init; } = "";
    public string Detail { get; init; } = "";
}

/// <summary>작업 상세 응답.</summary>
public sealed record JobDetailResponse
{
    public JobSummary Summary { get; init; } = null!;
    public JobDetail? Detail { get; init; }
    public string MachineName { get; init; } = "";
    public MaestroChecklist? Maestro { get; init; }
    public IReadOnlyList<string> BeforeStartMessages { get; init; } = [];
    public IReadOnlyList<string> AfterCompleteMessages { get; init; } = [];
}

/// <summary>작업 시작 요청.</summary>
public sealed record StartJobsRequest
{
    public IReadOnlyList<string> AgentIds { get; init; } = [];
    public UpdatePolicy? Policy { get; init; }
    public bool MaestroConfirmed { get; init; }
    public string ConfirmedBy { get; init; } = "teacher";
    public string? Note { get; init; }
}

/// <summary>작업 시작 결과.</summary>
public sealed record StartJobsResponse
{
    public IReadOnlyList<string> JobIds { get; init; } = [];
    public IReadOnlyList<string> Errors { get; init; } = [];
}

/// <summary>WoL 전송 결과.</summary>
public sealed record WakeResult
{
    public string MacAddress { get; init; } = "";
    public bool Sent { get; init; }
    public string Message { get; init; } = "";
}

public sealed record WakeResponse
{
    public IReadOnlyList<WakeResult> Results { get; init; } = [];
}

public sealed record RebootRequest
{
    public int DelaySeconds { get; init; } = 60;
    public string Reason { get; init; } = "교사 요청에 의한 재부팅";
}

public sealed record ShutdownRequest
{
    public int DelaySeconds { get; init; } = 60;
    public string Reason { get; init; } = "교사 요청에 의한 종료";
}

public sealed record MaestroRestoreRequest
{
    public bool StateSaved { get; init; }
    public bool ProtectionRestored { get; init; }
}

/// <summary>오류 응답(body.error).</summary>
public sealed record ErrorResponse
{
    public string? Error { get; init; }
}
