using System.Text.Json;
using UpdateAgent.LabManager.Services;
using UpdateAgent.Shared.Models;

namespace UpdateAgent.LabManager.Models;

/// <summary>여러 PC에 동시에 업데이트 작업을 배정한다.</summary>
public sealed record StartJobsRequest
{
    public IReadOnlyList<string> AgentIds { get; init; } = [];

    /// <summary>JSON 본문이 null이어도 500 대신 명확한 400으로 거부할 수 있게 한다.</summary>
    public bool HasTargets => AgentIds is { Count: > 0 };

    /// <summary>비어 있으면 서버에 저장된 기본 정책을 사용한다.</summary>
    public UpdatePolicy? Policy { get; init; }

    /// <summary>교사가 Maestro 복원 보호 해제를 확인했는지.</summary>
    public bool MaestroConfirmed { get; init; }

    public string ConfirmedBy { get; init; } = "teacher";
    public string? Note { get; init; }

    /// <summary>전달한 정책을 선택한 PC의 기본값으로 저장할지 여부.</summary>
    public bool SaveAsAgentPolicy { get; init; }

    /// <summary>
    /// 작업 추적용 상관관계 ID. 비어 있거나 형식이 잘못되면 서버가 발급한다.
    /// 토큰이 아니라 임의의 식별자이므로 명령 payload와 감사 로그에만 사용한다.
    /// </summary>
    public string? CorrelationId { get; init; }
}

public sealed record StartJobsResponse
{
    public IReadOnlyList<string> JobIds { get; init; } = [];
    public IReadOnlyList<string> Errors { get; init; } = [];

    /// <summary>OS에 맞지 않아 자동으로 조정된 옵션 설명.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>전체 배치 수. 배치 옵션을 쓰지 않으면 1이다.</summary>
    public int TotalBatches { get; init; } = 1;

    /// <summary>요청 즉시 명령이 발급된 배치 수.</summary>
    public int DispatchedBatches { get; init; } = 1;

    /// <summary>서버가 이후에 전송할 예약 배치 수.</summary>
    public int PendingBatches { get; init; }

    /// <summary>요청 전체에 부여한 상관관계 ID. 실패 응답에는 비어 있다.</summary>
    public string CorrelationId { get; init; } = "";

    /// <summary>생성된 작업별 상관관계 ID. 작업 ID와 1:1이다.</summary>
    public IReadOnlyList<JobCorrelation> JobCorrelations { get; init; } = [];
}

/// <summary>작업 ID와 상관관계 ID의 짝.</summary>
public sealed record JobCorrelation(string JobId, string CorrelationId);

/// <summary>PC별로 저장된 업데이트 옵션의 현재 상태.</summary>
public sealed record AgentPolicyResponse
{
    public required string AgentId { get; init; }
    public required UpdatePolicy Policy { get; init; }
    public bool HasOverride { get; init; }
    public string Source { get; init; } = "기본 정책";
    public OsInfo? Os { get; init; }
}

public sealed record AgentPolicySummary
{
    public required string AgentId { get; init; }
    public bool HasOverride { get; init; }
    public string Source { get; init; } = "기본 정책";
}

public sealed record UpdateAgentPolicyRequest
{
    public required UpdatePolicy Policy { get; init; }
}

public sealed record CommandRequest
{
    public CommandType Type { get; init; }
    public JsonElement? Payload { get; init; }
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

/// <summary>대시보드에서 서버의 관리자 토큰을 변경할 때 사용한다.</summary>
public sealed record UpdateAdminTokenRequest
{
    public string NewToken { get; init; } = string.Empty;
}

public sealed record OverviewResponse
{
    public int AgentCount { get; init; }
    public int OnlineCount { get; init; }
    public int BusyCount { get; init; }
    public int ActiveJobs { get; init; }
    public int CompletedJobs { get; init; }
    public int FailedJobs { get; init; }
    public DateTimeOffset ServerTime { get; init; } = UtcTimestamps.Now;
}

public sealed record WakeResponse
{
    public required IReadOnlyList<Services.WakeResult> Results { get; init; }
}
