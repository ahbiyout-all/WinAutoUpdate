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

    /// <summary>
    /// 선택적 기대 에이전트 버전. 비어 있으면 버전 일치를 가정하지 않고 화면에 확인 불가로 표시한다.
    /// </summary>
    public string? ExpectedAgentVersion { get; set; }

    /// <summary>WoL 매직 패킷을 보낼 포트 목록.</summary>
    public int[] WakePorts { get; set; } = [9, 7];

    /// <summary>WoL 패킷 반복 횟수.</summary>
    public int WakeRepeatCount { get; set; } = 3;

    /// <summary>
    /// 관리자 토큰 자동 회전 설정. 기본값은 사용 안 함이며 명시적으로 켜야 동작한다.
    /// </summary>
    public TokenRotationOptions TokenRotation { get; set; } = new();

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

/// <summary>
/// 교사 PC의 선택적 토큰 회전 정책. 기본 설치에서는 어떤 토큰도 자동으로 바꾸지 않는다.
/// </summary>
public sealed class TokenRotationOptions
{
    /// <summary>자동 회전 기본값. 기존 설치가 업그레이드만으로 토큰을 바꾸지 않도록 꺼둔다.</summary>
    public const bool DefaultEnabled = false;

    /// <summary>기본 확인 주기(분).</summary>
    public const int DefaultCheckIntervalMinutes = 60;

    /// <summary>잘못된 설정으로 과도하게 자주 확인하지 않도록 하는 최소 주기(분).</summary>
    public const int MinimumCheckIntervalMinutes = 5;

    /// <summary>최대 확인 주기(분).</summary>
    public const int MaximumCheckIntervalMinutes = 24 * 60;

    /// <summary>자동 회전 사용 여부. false이면 스케줄러가 아무 것도 하지 않는다.</summary>
    public bool Enabled { get; set; } = DefaultEnabled;

    /// <summary>회전 조건을 확인하는 주기(분). 범위를 벗어나면 안전한 값으로 제한한다.</summary>
    public int CheckIntervalMinutes { get; set; } = DefaultCheckIntervalMinutes;

    /// <summary>설정값을 안전한 범위의 TimeSpan으로 변환한다.</summary>
    public static TimeSpan ResolveInterval(int minutes) => TimeSpan.FromMinutes(
        Math.Clamp(minutes, MinimumCheckIntervalMinutes, MaximumCheckIntervalMinutes));
}
