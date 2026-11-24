namespace UpdateAgent.Shared.Updates;

/// <summary>GitHub Releases 기준 앱 업데이트 확인 결과.</summary>
public sealed record AppUpdateInfo
{
    /// <summary>GitHub 조회에 성공했는지. false면 나머지 릴리스 필드는 비어 있다.</summary>
    public bool CheckSucceeded { get; init; }

    /// <summary>현재 실행 중인 버전(Directory.Build.props의 Version).</summary>
    public string CurrentVersion { get; init; } = UpdateAgentConstants.AgentVersion;

    /// <summary>최신 릴리스 버전(태그의 v 접두사 제거).</summary>
    public string LatestVersion { get; init; } = UpdateAgentConstants.AgentVersion;

    /// <summary>현재 버전보다 새 릴리스가 있는지.</summary>
    public bool UpdateAvailable { get; init; }

    public string? ReleaseName { get; init; }
    public string? ReleaseNotes { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }

    /// <summary>GitHub 릴리스 페이지.</summary>
    public string ReleaseUrl { get; init; } = UpdateAgentConstants.GitHubReleasesUrl;

    /// <summary>GitHub 저장소 페이지.</summary>
    public string RepositoryUrl { get; init; } = UpdateAgentConstants.GitHubRepositoryUrl;

    /// <summary>릴리스에 첨부된 설치 파일(MSI) 이름.</summary>
    public string? AssetName { get; init; }

    /// <summary>설치 파일 직접 다운로드 URL.</summary>
    public string? AssetUrl { get; init; }

    public long? AssetSizeBytes { get; init; }

    public DateTimeOffset CheckedAt { get; init; } = UpdateAgent.Guards.UtcClock.Now;

    /// <summary>조회 실패 사유. 성공 시 null.</summary>
    public string? Error { get; init; }

    public static AppUpdateInfo Failed(string currentVersion, string error) => new()
    {
        CheckSucceeded = false,
        UpdateAvailable = false,
        CurrentVersion = currentVersion,
        LatestVersion = currentVersion,
        ReleaseUrl = UpdateAgentConstants.GitHubReleasesUrl,
        RepositoryUrl = UpdateAgentConstants.GitHubRepositoryUrl,
        Error = error,
        CheckedAt = UpdateAgent.Guards.UtcClock.Now,
    };
}
