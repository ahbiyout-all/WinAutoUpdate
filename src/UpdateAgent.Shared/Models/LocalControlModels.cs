namespace UpdateAgent.Shared.Models;

/// <summary>학생용 앱이 서비스에 보내는 로컬 제어 요청 종류.</summary>
public static class LocalControlActions
{
    public const string StartUpdate = "start-update";
}

/// <summary>학생용 상태 앱 → 서비스 로컬 제어 요청.</summary>
public sealed record LocalControlRequest
{
    public string Action { get; init; } = "";

    /// <summary>Maestro를 수동 복구 모드로 전환하고 학생이 확인했는지.</summary>
    public bool MaestroConfirmed { get; init; }
}

/// <summary>서비스 → 학생용 상태 앱 로컬 제어 응답.</summary>
public sealed record LocalControlResponse
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public string? JobId { get; init; }
}
