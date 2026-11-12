namespace UpdateAgent.Shared.Models;

/// <summary>Windows Update 분류.</summary>
public enum UpdateCategory
{
    Other = 0,
    Security = 1,
    Critical = 2,
    Important = 3,
    Moderate = 4,
    Low = 5,
    Unspecified = 6,
    Definition = 7,
    Driver = 8,
    FeaturePack = 9,
    ServicePack = 10,
    Upgrade = 11,
}

/// <summary>업데이트 작업의 상태. LabManager가 원본이며 에이전트 로컬 저장소는 캐시다.</summary>
public enum UpdateJobState
{
    Pending = 0,
    Preflight = 1,
    Scanning = 2,
    Downloading = 3,
    Installing = 4,
    PendingReboot = 5,
    Rebooting = 6,
    Paused = 7,
    Completed = 8,
    Failed = 9,
    Cancelled = 10,
}

/// <summary>에이전트가 대시보드에 보고하는 현재 상태.</summary>
public enum AgentState
{
    Offline = 0,
    Online = 1,
    Idle = 2,
    Busy = 3,
    Paused = 4,
    Rebooting = 5,
    NeedsAttention = 6,
    Error = 7,
}

public enum CommandType
{
    Ping = 0,
    StartJob = 1,
    PauseJob = 2,
    ResumeJob = 3,
    CancelJob = 4,
    ScanNow = 5,
    Reboot = 6,
    UnlockInput = 7,
    Shutdown = 8,
    Wake = 9,
    ApplyPolicy = 10,
    RefreshInventory = 11,
}

public enum CommandStatus
{
    Pending = 0,
    Delivered = 1,
    Succeeded = 2,
    Failed = 3,
    Expired = 4,
    Rejected = 5,
}

/// <summary>일시중지 이유. 사용자가 로그온했거나 교사가 수동으로 멈춘 경우를 구분한다.</summary>
public enum PauseReason
{
    None = 0,
    UserLoggedOn = 1,
    Manual = 2,
    WuBusy = 3,
    RestoreRisk = 4,
    PreflightBlocked = 5,
    WaitingForNetwork = 6,
}

public enum PreflightStatus
{
    Pass = 0,
    Warn = 1,
    Block = 2,
}

public enum MaestroVerificationStatus
{
    Unknown = 0,
    NotInstalled = 1,
    ManualConfirmationRequired = 2,
    Confirmed = 3,
    MarkerVerified = 4,
    VerificationFailed = 5,
}

public enum UpdateItemState
{
    Pending = 0,
    Downloading = 1,
    Downloaded = 2,
    Installing = 3,
    Installed = 4,
    Failed = 5,
    Skipped = 6,
    Cancelled = 7,
}

public enum RebootPolicy
{
    Auto = 0,
    Never = 1,
}

public enum OverlayMode
{
    /// <summary>전체화면 검은 오버레이 + 중앙 진행바 (기본).</summary>
    FullOverlay = 0,

    /// <summary>오버레이 없이 트레이/원격으로만 진행 표시.</summary>
    Hidden = 1,
}
