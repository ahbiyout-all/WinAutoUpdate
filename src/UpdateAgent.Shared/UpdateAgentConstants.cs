using System.Reflection;

namespace UpdateAgent.Shared;

/// <summary>서비스/세션 헬퍼/대시보드가 공유하는 상수.</summary>
public static class UpdateAgentConstants
{
    public const string ProductName = "UpdateAgent";
    public const string ServiceName = "UpdateAgentService";
    public const string ServiceDisplayName = "UpdateAgent Service";

    /// <summary>에이전트 버전. Directory.Build.props의 Version에서 생성된다.</summary>
    public static string AgentVersion { get; } = ResolveAgentVersion();

    /// <summary>LabManager도 에이전트와 동일한 단일 버전을 사용한다.</summary>
    public static string LabManagerVersion => AgentVersion;

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

    /// <summary>설치 파일에 기본으로 들어가는 샘플 관리자 토큰. 공개된 예시 값이므로 배포 전 교체해야 한다.</summary>
    public const string SampleAdminToken = "sample-admin-token-replace-before-pilot";

    /// <summary>설치 파일에 기본으로 들어가는 샘플 에이전트 토큰.</summary>
    public const string SampleAgentToken = "sample-agent-token-replace-before-pilot";

    /// <summary>기본값/샘플 토큰인지 판별한다. 실제 배포 토큰으로 교체해야 하는 값이다.</summary>
    public static bool IsPlaceholderToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return true;
        return string.Equals(token.Trim(), DefaultAdminToken, StringComparison.OrdinalIgnoreCase)
            || string.Equals(token.Trim(), DefaultAgentToken, StringComparison.OrdinalIgnoreCase)
            || string.Equals(token.Trim(), SampleAdminToken, StringComparison.OrdinalIgnoreCase)
            || string.Equals(token.Trim(), SampleAgentToken, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>%ProgramData%\UpdateAgent</summary>
    public static string DataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), ProductName);

    public static string DatabasePath => Path.Combine(DataDirectory, "agent.db");

    public static string MarkerPath => Path.Combine(DataDirectory, "resume-marker.json");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>%ProgramData%\UpdateAgent\agent.json (MSI 배포용 머신 설정)</summary>
    public static string ConfigFilePath => Path.Combine(DataDirectory, "agent.json");

    /// <summary>복원이 일어나도 살아남는 경로를 지정할 때 사용하는 환경 변수.</summary>
    public const string UnprotectedStatePathVariable = "UPDATEAGENT_STATE_PATH";

    // ---------------- 제작자 / 공식 채널 ----------------

    /// <summary>개발자 이름.</summary>
    public const string DeveloperName = "AhBiYout";

    /// <summary>GitHub 계정(조직/사용자). 실제 원격과 일치해야 한다.</summary>
    public const string GitHubOwner = "ahbiyout-all";

    /// <summary>GitHub 저장소 이름. 실제 원격과 일치해야 한다.</summary>
    public const string GitHubRepositoryName = "WinAutoUpdate";

    public static string GitHubRepositoryUrl => $"https://github.com/{GitHubOwner}/{GitHubRepositoryName}";

    public static string GitHubReleasesUrl => $"{GitHubRepositoryUrl}/releases";

    /// <summary>자동 업데이트 확인에 사용하는 GitHub Releases 최신 릴리스 API.</summary>
    public static string LatestReleaseApiUrl =>
        $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepositoryName}/releases/latest";

    /// <summary>공식 구글 블로그.</summary>
    public const string BlogUrl = "https://ahbiyoutvibe.blogspot.com/";

    /// <summary>대시보드가 새 릴리스를 다시 확인하는 주기(분).</summary>
    public const int UpdateCheckIntervalMinutes = 30;

    public static string ResolveDataDirectory()
    {
        var overridePath = Environment.GetEnvironmentVariable(UnprotectedStatePathVariable);
        return string.IsNullOrWhiteSpace(overridePath) ? DataDirectory : overridePath;
    }

    private static string ResolveAgentVersion()
    {
        var assembly = typeof(UpdateAgentConstants).Assembly;
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            // SourceLink가 붙이는 +<commit> 접미사를 제거한다.
            var plus = informational.IndexOf('+');
            return plus >= 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
