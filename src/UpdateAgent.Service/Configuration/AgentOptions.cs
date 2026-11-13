using UpdateAgent.Shared;

namespace UpdateAgent.Service.Configuration;

/// <summary>appsettings.json의 "Agent" 섹션.</summary>
public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    /// <summary>교사 PC의 LabManager 주소. 비어 있으면 원격 관리를 사용하지 않는다.</summary>
    public string LabManagerUrl { get; set; } = UpdateAgentConstants.DefaultLabManagerUrl;

    /// <summary>LabManager 에이전트 API 토큰. 파일럿 전 반드시 변경할 것.</summary>
    public string AgentToken { get; set; } = UpdateAgentConstants.DefaultAgentToken;

    /// <summary>체크인 주기(초). 15초 미만은 허용하지 않는다.</summary>
    public int PollIntervalSeconds { get; set; } = 30;

    /// <summary>원격 체크인 사용 여부. false면 로컬 전용 모드.</summary>
    public bool CheckInEnabled { get; set; } = true;

    /// <summary>전체화면 오버레이 사용 여부 (정책에서 다시 켜고 끌 수 있다).</summary>
    public bool OverlayEnabled { get; set; } = true;

    /// <summary>완료/실패 후 오버레이를 유지하는 시간(초).</summary>
    public int OverlayCompletionHoldSeconds { get; set; } = 20;

    /// <summary>SessionHelper 실행 파일 경로. 비어 있으면 서비스 폴더에서 찾는다.</summary>
    public string? SessionHelperPath { get; set; }

    /// <summary>서비스 시작 시 남아 있는 작업을 자동으로 재개할지 여부.</summary>
    public bool ResumeOnStartup { get; set; } = true;

    /// <summary>명령 폴링에서 허용하는 최대 명령 수.</summary>
    public int MaxCommandsPerCheckIn { get; set; } = 20;

    /// <summary>테스트용: 실제 다운로드/설치/재부팅 없이 흐름만 검증한다.</summary>
    public bool DryRun { get; set; }

    public int EffectivePollSeconds => Math.Max(15, PollIntervalSeconds);

    public bool RemoteEnabled => CheckInEnabled && !string.IsNullOrWhiteSpace(LabManagerUrl);

    public string ResolveSessionHelperPath()
    {
        if (!string.IsNullOrWhiteSpace(SessionHelperPath))
        {
            return SessionHelperPath;
        }

        // 기존 파일럿 배치: 서비스 폴더에 SessionHelper가 함께 있다.
        var local = Path.Combine(AppContext.BaseDirectory, "UpdateAgent.SessionHelper.exe");
        if (File.Exists(local))
        {
            return local;
        }

        // MSI 배치: Program Files\UpdateAgent\SessionHelper
        return Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "SessionHelper", "UpdateAgent.SessionHelper.exe"));
    }
}
