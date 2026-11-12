namespace UpdateAgent.Shared;

/// <summary>서비스/세션 헬퍼/대시보드가 공유하는 상수.</summary>
public static class UpdateAgentConstants
{
    public const string ProductName = "UpdateAgent";
    public const string ServiceName = "UpdateAgentService";
    public const string ServiceDisplayName = "UpdateAgent Service";
    public const string AgentVersion = "0.1.0";

    /// <summary>서비스 ↔ SessionHelper 명명된 파이프.</summary>
    public const string OverlayPipeName = "UpdateAgent.Overlay.v1";

    /// <summary>교사 PC LabManager 기본 주소.</summary>
    public const string DefaultLabManagerUrl = "http://localhost:5080";

    /// <summary>에이전트 인증 헤더.</summary>
    public const string AgentTokenHeader = "X-Agent-Token";

    /// <summary>LabManager가 발급하는 기본 개발용 토큰. 파일럿 전 반드시 변경할 것.</summary>
    public const string DefaultAgentToken = "change-me-agent-token";

    /// <summary>관리자(교사) API 토큰.</summary>
    public const string AdminTokenHeader = "X-Admin-Token";

    public const string DefaultAdminToken = "change-me-admin-token";

    /// <summary>%ProgramData%\UpdateAgent</summary>
    public static string DataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), ProductName);

    public static string DatabasePath => Path.Combine(DataDirectory, "agent.db");

    public static string MarkerPath => Path.Combine(DataDirectory, "resume-marker.json");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>복원이 일어나도 살아남는 경로를 지정할 때 사용하는 환경 변수.</summary>
    public const string UnprotectedStatePathVariable = "UPDATEAGENT_STATE_PATH";

    public static string ResolveDataDirectory()
    {
        var overridePath = Environment.GetEnvironmentVariable(UnprotectedStatePathVariable);
        return string.IsNullOrWhiteSpace(overridePath) ? DataDirectory : overridePath;
    }
}
