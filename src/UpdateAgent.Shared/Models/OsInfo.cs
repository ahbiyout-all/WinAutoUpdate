namespace UpdateAgent.Shared.Models;

/// <summary>대상 PC의 운영체제 정보. 기능 업데이트/오버레이 지원 여부 판단에 사용한다.</summary>
public sealed record OsInfo
{
    public string ProductName { get; init; } = "";
    public string EditionId { get; init; } = "";
    public string DisplayVersion { get; init; } = "";
    public string BuildLab { get; init; } = "";
    public int Build { get; init; }
    public int Revision { get; init; }
    public string Architecture { get; init; } = "";
    public bool IsServer { get; init; }
    public bool IsLtsc { get; init; }
    public bool IsWindows10 { get; init; }

    /// <summary>GUI가 없는 Server Core 여부.</summary>
    public bool IsServerCore { get; init; }

    public string VersionString => $"{DisplayVersion} (build {Build}.{Revision})";

    public string ShortName =>
        IsServer
            ? $"Windows Server {DisplayVersion}"
            : $"{ProductName} {DisplayVersion}";

    /// <summary>기능 업데이트(OS 업그레이드)를 이 앱에서 다룰 수 있는지.</summary>
    public bool SupportsFeatureUpdates => !IsServer && !IsLtsc && !IsWindows10;

    /// <summary>세션 오버레이를 표시할 수 있는지 (Server Core는 헤드리스).</summary>
    public bool SupportsOverlay => !IsServerCore;

    /// <summary>LTSC/IoT LTSC 여부까지 포함한 사람이 읽는 설명.</summary>
    public string SupportSummary
    {
        get
        {
            if (IsServerCore) return "Server Core (헤드리스)";
            if (IsLtsc) return $"{ShortName} (LTSC, 기능 업데이트 제외)";
            if (IsWindows10) return $"{ShortName} (ESU 필요, 기능 업데이트 제외)";
            return ShortName;
        }
    }
}
