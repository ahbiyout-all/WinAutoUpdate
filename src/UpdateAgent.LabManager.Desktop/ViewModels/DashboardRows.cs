using System.ComponentModel;
using System.Globalization;
using System.Windows.Media;
using UpdateAgent.LabManager.Desktop.Models;
using UpdateAgent.Shared.Models;

namespace UpdateAgent.LabManager.Desktop.ViewModels;

/// <summary>웹 대시보드의 문자열/색상 규칙을 WPF 바인딩용으로 옮긴 도우미.</summary>
internal static class UiFormat
{
    internal static readonly Brush Gray = Brush("#6B7280");
    internal static readonly Brush Green = Brush("#10B981");
    internal static readonly Brush Blue = Brush("#3B82F6");
    internal static readonly Brush Amber = Brush("#F59E0B");
    internal static readonly Brush Purple = Brush("#8B5CF6");
    internal static readonly Brush Red = Brush("#EF4444");

    internal static Brush Brush(string hex)
    {
        var color = ColorConverter.ConvertFromString(hex) as Color?;
        return new SolidColorBrush(color ?? Colors.Gray);
    }

    internal static (string Label, Brush Brush) AgentStateInfo(AgentState state) => state switch
    {
        AgentState.Offline => ("오프라인", Gray),
        AgentState.Online => ("온라인", Green),
        AgentState.Idle => ("대기", Green),
        AgentState.Busy => ("작업 중", Blue),
        AgentState.Paused => ("일시중지", Amber),
        AgentState.Rebooting => ("재부팅", Purple),
        AgentState.NeedsAttention => ("확인 필요", Red),
        AgentState.Error => ("오류", Red),
        _ => (state.ToString(), Gray),
    };

    internal static (string Label, Brush Brush) JobStateInfo(UpdateJobState state) => state switch
    {
        UpdateJobState.Pending => ("대기", Gray),
        UpdateJobState.Preflight => ("사전 점검", Blue),
        UpdateJobState.Scanning => ("검색 중", Blue),
        UpdateJobState.Downloading => ("다운로드", Blue),
        UpdateJobState.Installing => ("설치 중", Blue),
        UpdateJobState.PendingReboot => ("재부팅 대기", Amber),
        UpdateJobState.Rebooting => ("재부팅 중", Purple),
        UpdateJobState.Paused => ("일시중지", Amber),
        UpdateJobState.Completed => ("완료", Green),
        UpdateJobState.Failed => ("실패", Red),
        UpdateJobState.Cancelled => ("취소됨", Gray),
        _ => (state.ToString(), Gray),
    };

    internal static string Time(DateTimeOffset value) =>
        value.ToLocalTime().ToString("MM/dd HH:mm:ss", CultureInfo.InvariantCulture);

    internal static string Ago(DateTimeOffset value)
    {
        var seconds = Math.Max(0, (long)(DateTimeOffset.Now - value).TotalSeconds);
        if (seconds < 60) return $"{seconds}초 전";
        if (seconds < 3600) return $"{seconds / 60}분 전";
        if (seconds < 86400) return $"{seconds / 3600}시간 전";
        return $"{seconds / 86400}일 전";
    }

    internal static string Bytes(long? bytes)
    {
        if (bytes is null or <= 0) return "";
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes.Value;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return $"{value.ToString(index == 0 ? "0" : "0.0", CultureInfo.InvariantCulture)} {units[index]}";
    }

    internal static string Date(DateTimeOffset? value) =>
        value?.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
}

/// <summary>학생 PC 목록 행.</summary>
public sealed class AgentRow : INotifyPropertyChanged
{
    private bool _isSelected;

    public AgentRow(AgentRecord record)
    {
        Record = record;
    }

    public AgentRecord Record { get; }

    public string AgentId => Record.Agent.AgentId;
    public string MachineName => Record.Agent.MachineName;

    public string SubText =>
        Record.Agent.IpAddress ??
        (Record.Agent.AgentId.Length > 13 ? Record.Agent.AgentId[..13] : Record.Agent.AgentId);

    public string StateLabel => Record.Online
        ? UiFormat.AgentStateInfo(Record.Telemetry.State).Label
        : "오프라인";

    public Brush StateBrush => Record.Online
        ? UiFormat.AgentStateInfo(Record.Telemetry.State).Brush
        : UiFormat.Gray;

    public string Tags
    {
        get
        {
            var parts = new List<string>();
            if (Record.Telemetry.UserLoggedOn) parts.Add("사용자 로그온");
            if (Record.Telemetry.WuBusy) parts.Add("WU 사용 중");
            return string.Join(" · ", parts);
        }
    }

    public string JobText => Record.Telemetry.CurrentJob is { } job
        ? $"{UiFormat.JobStateInfo(job.State).Label} · {job.Phase}"
        : "-";

    public Brush JobBrush => Record.Telemetry.CurrentJob is { } job
        ? UiFormat.JobStateInfo(job.State).Brush
        : UiFormat.Gray;

    public int JobProgress => Record.Telemetry.CurrentJob?.ProgressPercent ?? 0;

    public string OsText =>
        Record.Agent.Os?.ShortName ?? Record.Agent.Os?.ProductName ?? "알 수 없음";

    public string OsVersion => Record.Agent.Os?.VersionString ?? "";

    public bool MaestroInstalled => Record.Agent.MaestroInstalled;

    public string FreeDiskText => Record.Telemetry.FreeDiskMb > 0
        ? $"{Record.Telemetry.FreeDiskMb / 1024.0:F1} GB"
        : "?";

    public string DeviceText => Record.Agent.IsLaptop ? "노트북" : "데스크톱";

    public bool CanWake => !Record.Online;

    public string SeenText => UiFormat.Ago(Record.LastSeen);

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>업데이트 작업 목록 행.</summary>
public sealed class JobRow
{
    public JobRow(LabJobRecord record)
    {
        Record = record;
    }

    public LabJobRecord Record { get; }

    public JobSummary Summary => Record.Summary;
    public string MachineName =>
        string.IsNullOrWhiteSpace(Record.MachineName) ? Record.Summary.AgentName : Record.MachineName;
    public string JobId => Record.Summary.JobId;
    public string StateLabel => UiFormat.JobStateInfo(Record.Summary.State).Label;
    public Brush StateBrush => UiFormat.JobStateInfo(Record.Summary.State).Brush;
    public string Phase => Record.Summary.Phase;
    public int Progress => Math.Clamp(Record.Summary.ProgressPercent, 0, 100);
    public string ProgressText =>
        $"{Record.Summary.ProgressPercent}% · 설치 {Record.Summary.InstalledItems}/{Record.Summary.TotalItems}";
    public string RebootText => $"{Record.Summary.RebootCount}/{Record.Summary.MaxRebootCount}";
    public string MaestroText => Record.Summary.RequiresMaestroAttention ? "Maestro 확인 필요" : "";
    public string UpdatedText => UiFormat.Time(Record.Summary.UpdatedAt);
    public string UpdatedAgo => UiFormat.Ago(Record.Summary.UpdatedAt);
    public bool IsTerminal => Record.Summary.IsTerminal;
    public bool IsPaused => Record.Summary.State == UpdateJobState.Paused;
}

/// <summary>감사 로그 행.</summary>
public sealed class AuditRow
{
    public AuditRow(AuditEntry entry)
    {
        Entry = entry;
    }

    public AuditEntry Entry { get; }
    public string TimeText => UiFormat.Time(Entry.Timestamp);
    public string Summary => $"{Entry.Action}  {Entry.Target}".Trim();
    public string Detail => Entry.Detail;
}
