using System.Text.Json;
using UpdateAgent.Shared.Models;

namespace UpdateAgent.LabManager.Models;

/// <summary>여러 PC에 동시에 업데이트 작업을 배정한다.</summary>
public sealed record StartJobsRequest
{
    public IReadOnlyList<string> AgentIds { get; init; } = [];

    /// <summary>비어 있으면 서버에 저장된 기본 정책을 사용한다.</summary>
    public UpdatePolicy? Policy { get; init; }

    /// <summary>교사가 Maestro 복원 보호 해제를 확인했는지.</summary>
    public bool MaestroConfirmed { get; init; }

    public string ConfirmedBy { get; init; } = "teacher";
    public string? Note { get; init; }
}

public sealed record StartJobsResponse
{
    public IReadOnlyList<string> JobIds { get; init; } = [];
    public IReadOnlyList<string> Errors { get; init; } = [];
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
    public DateTimeOffset ServerTime { get; init; } = DateTimeOffset.Now;
}

public sealed record WakeResponse
{
    public required IReadOnlyList<Services.WakeResult> Results { get; init; }
}
