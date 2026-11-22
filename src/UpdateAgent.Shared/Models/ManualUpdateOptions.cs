namespace UpdateAgent.Shared.Models;

/// <summary>
/// 학생용 상태 앱에서 수동 업데이트를 시작하기 전 선택하는 옵션.
/// 서비스는 이 값 중 허용된 필드만 캐시된 정책에 덮어쓰며, 기능 업데이트·Maestro 확인 요구·
/// 로그온 일시중지 등 교사/2차 승인이 필요한 항목은 학생 요청으로 바꿀 수 없다.
/// </summary>
public sealed record ManualUpdateOptions
{
    /// <summary>화면에서 선택한 프리셋 ID. 기록용이며 정책 결정에는 사용하지 않는다.</summary>
    public string? PresetId { get; init; }

    public bool IncludeSecurity { get; init; } = true;
    public bool IncludeCritical { get; init; } = true;
    public bool IncludeImportant { get; init; } = true;
    public bool IncludeOptional { get; init; }
    public bool IncludeDrivers { get; init; }
    public bool IncludeDefinitionUpdates { get; init; }

    /// <summary>전체화면 진행 오버레이 사용 여부. Server Core에서는 강제로 꺼진다.</summary>
    public bool OverlayEnabled { get; init; } = true;

    /// <summary>true면 자동 재부팅을 하지 않는다.</summary>
    public bool PreventAutomaticReboot { get; init; }

    /// <summary>최대 자동 재부팅 횟수. 0~10으로 제한한다.</summary>
    public int MaxRebootCount { get; init; } = 5;

    /// <summary>프리셋 ID에 해당하는 옵션을 반환한다. 알 수 없는 ID는 표준값을 사용한다.</summary>
    public static ManualUpdateOptions FromPreset(string? presetId, OsInfo? os = null)
    {
        var preset = UpdatePolicyPresets.Get(presetId, os);
        return FromPolicy(
            preset ?? UpdatePolicy.Default,
            preset is null ? UpdatePolicyPresets.Custom : presetId,
            os);
    }

    /// <summary>정책에서 학생이 조정할 수 있는 필드만 옵션으로 만든다.</summary>
    public static ManualUpdateOptions FromPolicy(UpdatePolicy policy, string? presetId = null, OsInfo? os = null)
    {
        var sanitized = policy.SanitizeFor(os);
        return new ManualUpdateOptions
        {
            PresetId = presetId ?? UpdatePolicyPresets.Custom,
            IncludeSecurity = sanitized.IncludeSecurity,
            IncludeCritical = sanitized.IncludeCritical,
            IncludeImportant = sanitized.IncludeImportant,
            IncludeOptional = sanitized.IncludeOptional,
            IncludeDrivers = sanitized.IncludeDrivers,
            IncludeDefinitionUpdates = sanitized.IncludeDefinitionUpdates,
            OverlayEnabled = sanitized.OverlayEnabled,
            PreventAutomaticReboot = sanitized.RebootPolicy == RebootPolicy.Never,
            MaxRebootCount = Clamp(sanitized.MaxRebootCount),
        };
    }

    /// <summary>
    /// 캐시된 교사 정책에 학생 선택 옵션을 반영한다. 학생이 바꿀 수 없는 필드는 원래 정책을 유지하고
    /// 기능 업데이트는 2차 승인 전까지 항상 꺼 둔다.
    /// </summary>
    public UpdatePolicy ApplyTo(UpdatePolicy basePolicy, OsInfo? os = null)
    {
        var merged = basePolicy with
        {
            IncludeSecurity = IncludeSecurity,
            IncludeCritical = IncludeCritical,
            IncludeImportant = IncludeImportant,
            IncludeOptional = IncludeOptional,
            IncludeDrivers = IncludeDrivers,
            IncludeDefinitionUpdates = IncludeDefinitionUpdates,
            RebootPolicy = PreventAutomaticReboot ? RebootPolicy.Never : RebootPolicy.Auto,
            MaxRebootCount = Clamp(MaxRebootCount),
            OverlayEnabled = OverlayEnabled,
            IncludeFeatureUpdates = false,
            ApprovedFeatureRelease = null,
        };

        return merged.SanitizeFor(os);
    }

    internal static int Clamp(int value) => Math.Clamp(value, 0, 10);
}
