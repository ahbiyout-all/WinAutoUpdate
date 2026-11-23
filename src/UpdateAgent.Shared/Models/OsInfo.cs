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

    /// <summary>대시보드 필터와 프리셋이 공유하는 OS 그룹 ID.</summary>
    public string GroupId =>
        IsServerCore ? "server-core"
        : IsServer ? "server"
        : IsLtsc ? "ltsc"
        : IsWindows10 ? "windows-10"
        : Build >= 22000 || ProductName.Contains("Windows 11", StringComparison.OrdinalIgnoreCase)
            ? "windows-11"
            : "unknown";

    public string GroupLabel => GroupId switch
    {
        "server-core" => "Server Core",
        "server" => "Windows Server",
        "ltsc" => "LTSC / IoT LTSC",
        "windows-10" => "Windows 10 (ESU)",
        "windows-11" => "Windows 11",
        _ => "미확인 OS",
    };

    /// <summary>서버는 빌드 번호로 릴리스를 표시한다(DisplayVersion이 21H2처럼 남는 경우가 많음).</summary>
    public string ReleaseLabel => !IsServer
        ? DisplayVersion
        : Build switch
        {
            >= 26000 => "2025",
            >= 20300 => "2022",
            >= 17700 => "2019",
            >= 14300 => "2016",
            _ => DisplayVersion,
        };

    public string ShortName =>
        IsServer
            ? $"Windows Server {ReleaseLabel}"
            : $"{ProductName} {DisplayVersion}";

    /// <summary>
    /// 기능 업데이트(OS 업그레이드)를 이 앱에서 다룰 수 있는지.
    /// 감지 실패/unknown은 클라이언트 Windows로 추정하지 않고 fail closed한다.
    /// </summary>
    public bool SupportsFeatureUpdates => GroupId == "windows-11";

    /// <summary>
    /// 세션 오버레이를 표시할 수 있는지. Server Core(헤드리스)와 미확인 OS는 제외한다.
    /// </summary>
    public bool SupportsOverlay => GroupId is "windows-11" or "windows-10" or "ltsc" or "server";

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
