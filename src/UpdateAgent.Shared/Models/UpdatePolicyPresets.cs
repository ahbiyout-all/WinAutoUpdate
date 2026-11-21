namespace UpdateAgent.Shared.Models;

/// <summary>교사 대시보드가 제공하는 업데이트 옵션 프리셋.</summary>
public sealed record UpdatePolicyPresetInfo(string Id, string Label, string Description);

public static class UpdatePolicyPresets
{
    public const string Custom = "custom";
    public const string Standard = "standard";
    public const string SecurityOnly = "security-only";
    public const string ServerSafe = "server-safe";
    public const string Full = "full";

    public static IReadOnlyList<UpdatePolicyPresetInfo> All { get; } =
    [
        new(Standard, "표준", "보안 · 중요 · 권장 업데이트"),
        new(SecurityOnly, "보안 중심", "보안 · 중요 업데이트만 설치"),
        new(ServerSafe, "서버 안전", "정의 업데이트 포함, 자동 재부팅/오버레이 없음"),
        new(Full, "전체", "선택 · 드라이버 · 정의 업데이트까지 포함"),
    ];

    /// <summary>프리셋 ID에 해당하는 정책을 반환한다. 알 수 없는 ID는 null.</summary>
    public static UpdatePolicy? Get(string? presetId, OsInfo? os = null)
    {
        var policy = presetId switch
        {
            Standard => new UpdatePolicy(),
            SecurityOnly => new UpdatePolicy
            {
                IncludeSecurity = true,
                IncludeCritical = true,
                IncludeImportant = false,
                MaxRebootCount = 3,
            },
            ServerSafe => new UpdatePolicy
            {
                IncludeSecurity = true,
                IncludeCritical = true,
                IncludeImportant = true,
                IncludeDefinitionUpdates = true,
                OverlayEnabled = false,
                RebootPolicy = RebootPolicy.Never,
            },
            Full => new UpdatePolicy
            {
                IncludeSecurity = true,
                IncludeCritical = true,
                IncludeImportant = true,
                IncludeOptional = true,
                IncludeDrivers = true,
                IncludeDefinitionUpdates = true,
            },
            _ => null,
        };

        // 기능 업데이트는 승인 워크플로가 별도이므로 어떤 프리셋에서도 켜지 않는다.
        return policy?.SanitizeFor(os);
    }

    public static string Label(string? presetId) =>
        All.FirstOrDefault(p => p.Id == presetId)?.Label ?? "사용자 설정";
}
