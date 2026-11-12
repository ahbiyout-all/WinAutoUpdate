namespace UpdateAgent.Shared.Models;

/// <summary>
/// 업데이트 선택 정책. LabManager가 배포하고 에이전트가 캐시한다.
/// 기본값은 "보안/중요/선택 업데이트, 드라이버 제외"이다.
/// </summary>
public sealed record UpdatePolicy
{
    public bool IncludeSecurity { get; init; } = true;
    public bool IncludeCritical { get; init; } = true;
    public bool IncludeImportant { get; init; } = true;

    /// <summary>선택적(권장) 업데이트 포함 여부. 기본 제외.</summary>
    public bool IncludeOptional { get; init; }

    /// <summary>드라이버 업데이트 포함 여부. 기본 제외.</summary>
    public bool IncludeDrivers { get; init; }

    /// <summary>정의 업데이트(Defender 등) 포함 여부. 기본 제외.</summary>
    public bool IncludeDefinitionUpdates { get; init; }

    /// <summary>기능 업데이트 포함 여부. MVP 기본 제외, 2차 승인 워크플로에서 사용.</summary>
    public bool IncludeFeatureUpdates { get; init; }

    /// <summary>승인된 기능 업데이트 대상 버전 (예: "26H2"). 비어 있으면 미승인.</summary>
    public string? ApprovedFeatureRelease { get; init; }

    /// <summary>최대 자동 재부팅 횟수.</summary>
    public int MaxRebootCount { get; init; } = 5;

    /// <summary>작업 전체 제한 시간(시간). 0이면 제한 없음.</summary>
    public int MaxRuntimeHours { get; init; } = 12;

    /// <summary>사용자가 로그온하면 일시중지할지 여부.</summary>
    public bool PauseOnUserLogon { get; init; } = true;

    /// <summary>일시중지가 유지되는 최대 시간(시간). 초과 시 재개.</summary>
    public int MaxPauseHours { get; init; } = 12;

    /// <summary>재부팅 방식. MVP는 Auto(자동) 또는 Never(수동)만 지원.</summary>
    public RebootPolicy RebootPolicy { get; init; } = RebootPolicy.Auto;

    /// <summary>재부팅 전 대기 시간(초).</summary>
    public int RebootDelaySeconds { get; init; } = 60;

    /// <summary>완료 후 PC 자동 종료 여부 (WoL 환경에서 유용).</summary>
    public bool ShutdownAfterComplete { get; init; }

    /// <summary>전체화면 오버레이 사용 여부.</summary>
    public bool OverlayEnabled { get; init; } = true;

    public OverlayMode OverlayMode { get; init; } = OverlayMode.FullOverlay;

    /// <summary>작업 시작 전 Maestro 수동 확인이 필요한지. MVP는 항상 true.</summary>
    public bool RequireMaestroManualConfirmation { get; init; } = true;

    /// <summary>첫 재부팅 후 복원 마커 검증을 수행할지 여부.</summary>
    public bool VerifyRestoreMarkerAfterReboot { get; init; } = true;

    /// <summary>디스크 여유 공간 최소값(MB).</summary>
    public int MinimumFreeDiskMb { get; init; } = 20 * 1024;

    /// <summary>WU 사용 중일 때 대기할 최대 시간(분). 초과 시 일시중지.</summary>
    public int WuBusyWaitMinutes { get; init; } = 30;

    /// <summary>동일 KB가 이 횟수만큼 실패하면 격리한다.</summary>
    public int MaxItemFailureCount { get; init; } = 2;

    public static UpdatePolicy Default { get; } = new();
}
