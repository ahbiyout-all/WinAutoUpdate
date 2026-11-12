namespace UpdateAgent.Shared.Models;

/// <summary>Maestro Network 7 설치/보호 상태 감지 결과. 앱은 Maestro 설정을 변경하지 않는다.</summary>
public sealed record MaestroDetectionResult
{
    public bool Installed { get; init; }
    public string ProductName { get; init; } = "Maestro Network 7";
    public IReadOnlyList<string> Evidence { get; init; } = [];
    public IReadOnlyList<string> Services { get; init; } = [];
    public IReadOnlyList<string> Processes { get; init; } = [];
    public IReadOnlyList<string> InstallPaths { get; init; } = [];

    /// <summary>Maestro의 Windows Update 기능 상태. API가 없으면 Unknown으로 둔다.</summary>
    public MaestroWuFeatureStatus WuFeatureStatus { get; init; } = MaestroWuFeatureStatus.Unknown;

    public DateTimeOffset DetectedAt { get; init; } = DateTimeOffset.Now;

    public static MaestroDetectionResult NotInstalled { get; } = new() { Installed = false };
}

public enum MaestroWuFeatureStatus
{
    Unknown = 0,
    Enabled = 1,
    Disabled = 2,
}

/// <summary>작업 전/후 교사가 수행해야 하는 Maestro 수동 단계.</summary>
public sealed record MaestroChecklist
{
    public required string JobId { get; init; }

    /// <summary>시작 전: 매 부팅 자동 복구 → 수동/해제 전환 확인.</summary>
    public bool ProtectionSuspendedConfirmed { get; init; }

    /// <summary>시작 전 확인 시각/확인자.</summary>
    public DateTimeOffset? ProtectionSuspendedConfirmedAt { get; init; }
    public string? ConfirmedBy { get; init; }

    /// <summary>완료 후: 현재 상태 저장 확인.</summary>
    public bool StateSavedConfirmed { get; init; }

    /// <summary>완료 후: 자동 복구 재활성화 확인.</summary>
    public bool ProtectionRestoredConfirmed { get; init; }
    public DateTimeOffset? ProtectionRestoredConfirmedAt { get; init; }

    /// <summary>재부팅 후 마커 유지 검증 결과.</summary>
    public MaestroVerificationStatus MarkerStatus { get; init; } = MaestroVerificationStatus.Unknown;
    public DateTimeOffset? MarkerVerifiedAt { get; init; }
    public string? MarkerMessage { get; init; }

    /// <summary>교사 확인 없이 작업을 시작할 수 있는지.</summary>
    public bool CanStartJob => ProtectionSuspendedConfirmed || !RequiresManualConfirmation;

    public bool RequiresManualConfirmation { get; init; } = true;

    public IReadOnlyList<string> BeforeStartMessages => BuildBeforeStartMessages();

    public IReadOnlyList<string> AfterCompleteMessages =>
    [
        "Maestro에서 '현재 상태 저장'을 실행해 업데이트된 상태를 새 기준점으로 저장하세요.",
        "그 다음 '자동 복구(매 부팅 복구)'를 다시 활성화하세요.",
        "복구가 다시 켜졌는지 확인한 뒤 대시보드에서 '복원 보호 재설정 완료'를 체크하세요.",
    ];

    private IReadOnlyList<string> BuildBeforeStartMessages()
    {
        var messages = new List<string>
        {
            "Maestro Network 7의 복원 설정을 '매 부팅 자동 복구'에서 '수동 복구' 또는 '해제'로 전환하세요.",
            "전환하지 않으면 재부팅 후 설치된 업데이트가 사라집니다.",
            "Maestro의 Windows Update 기능은 작업 중에 수동으로 실행하지 마세요.",
        };

        if (MarkerStatus == MaestroVerificationStatus.VerificationFailed)
        {
            messages.Add(
                "이전 재부팅 후 마커가 사라졌습니다. 복원이 아직 활성 상태일 가능성이 높습니다. 작업이 중단되었습니다.");
        }

        return messages;
    }
}

/// <summary>복원 검증용 마커. 보호 대상 드라이브에 기록해 재부팅 후 존재 여부를 확인한다.</summary>
public sealed record RestoreMarker
{
    public required string JobId { get; init; }
    public required string AgentId { get; init; }
    public required string Nonce { get; init; }
    public int BootCount { get; init; }
    public string Phase { get; init; } = "";
    public DateTimeOffset WrittenAt { get; init; } = DateTimeOffset.Now;
}

/// <summary>마커 검증 결과.</summary>
public sealed record MarkerVerificationResult
{
    public bool Verified { get; init; }
    public string Message { get; init; } = "";
    public RestoreMarker? Marker { get; init; }
    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.Now;

    public static MarkerVerificationResult Ok(RestoreMarker marker) => new()
    {
        Verified = true,
        Marker = marker,
        Message = "재부팅 후에도 작업 상태가 유지되었습니다.",
    };

    public static MarkerVerificationResult Failed(string message) => new()
    {
        Verified = false,
        Message = message,
    };
}
