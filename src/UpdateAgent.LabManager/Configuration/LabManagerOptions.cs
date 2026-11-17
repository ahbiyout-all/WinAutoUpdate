using UpdateAgent.Shared;

namespace UpdateAgent.LabManager.Configuration;

/// <summary>교사 PC LabManager 설정. appsettings.json의 "LabManager" 섹션.</summary>
public sealed class LabManagerOptions
{
    public const string SectionName = "LabManager";

    /// <summary>관리자(교사) API 토큰. 기본값은 개발용이며 파일럿 전 반드시 변경한다.</summary>
    public string AdminToken { get; set; } = UpdateAgentConstants.DefaultAdminToken;

    /// <summary>에이전트 체크인 토큰. 모든 학생 PC에 동일하게 배포한다.</summary>
    public string AgentToken { get; set; } = UpdateAgentConstants.DefaultAgentToken;

    /// <summary>
    /// 개발/자동화 테스트에서만 기본 토큰 사용을 허용한다. 배포에서는 항상 false여야 한다.
    /// </summary>
    public bool AllowDefaultTokens { get; set; }

    /// <summary>SQLite 데이터베이스 경로. 비어 있으면 %LOCALAPPDATA% 아래에 만든다.</summary>
    public string? DatabasePath { get; set; }

    /// <summary>에이전트가 이 시간(초) 안에 체크인하면 온라인으로 표시한다.</summary>
    public int OnlineThresholdSeconds { get; set; } = 90;

    /// <summary>WoL 매직 패킷을 보낼 포트 목록.</summary>
    public int[] WakePorts { get; set; } = [9, 7];

    /// <summary>WoL 패킷 반복 횟수.</summary>
    public int WakeRepeatCount { get; set; } = 3;

    public string ResolveDatabasePath()
    {
        if (!string.IsNullOrWhiteSpace(DatabasePath))
        {
            return Path.GetFullPath(DatabasePath);
        }

        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UpdateAgent",
            "LabManager");
        return Path.Combine(root, "labmanager.db");
    }
}
