using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using UpdateAgent.Shared.Models;

namespace UpdateAgent.SessionHelper;

/// <summary>오버레이 화면에 표시할 값.</summary>
public sealed class OverlayViewModel : INotifyPropertyChanged
{
    private static readonly Brush RunningAccent = new SolidColorBrush(Color.FromRgb(0x4F, 0x8C, 0xFF));
    private static readonly Brush CompletedAccent = new SolidColorBrush(Color.FromRgb(0x35, 0xD0, 0x7F));
    private static readonly Brush FailedAccent = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B));
    private static readonly Brush PausedAccent = new SolidColorBrush(Color.FromRgb(0xF0, 0xB7, 0x5A));

    private string _phase = "준비 중";
    private string _percentText = "0%";
    private double _progressValue;
    private string _kbLabel = "";
    private string _message = "";
    private string _detailText = "";
    private string _rebootText = "";
    private Visibility _rebootVisibility = Visibility.Collapsed;
    private Brush _accent = RunningAccent;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Phase
    {
        get => _phase;
        private set => Set(ref _phase, value);
    }

    public string PercentText
    {
        get => _percentText;
        private set => Set(ref _percentText, value);
    }

    public double ProgressValue
    {
        get => _progressValue;
        private set => Set(ref _progressValue, value);
    }

    public string KbLabel
    {
        get => _kbLabel;
        private set => Set(ref _kbLabel, value);
    }

    public string Message
    {
        get => _message;
        private set => Set(ref _message, value);
    }

    public string DetailText
    {
        get => _detailText;
        private set => Set(ref _detailText, value);
    }

    public string RebootText
    {
        get => _rebootText;
        private set => Set(ref _rebootText, value);
    }

    public Visibility RebootVisibility
    {
        get => _rebootVisibility;
        private set => Set(ref _rebootVisibility, value);
    }

    public Brush Accent
    {
        get => _accent;
        private set => Set(ref _accent, value);
    }

    public void Apply(JobSummary summary)
    {
        Phase = summary.State switch
        {
            UpdateJobState.Pending => "업데이트 준비 중",
            UpdateJobState.Preflight => "사전 점검 중",
            UpdateJobState.Scanning => "업데이트 검색 중",
            UpdateJobState.Downloading => "업데이트 다운로드 중",
            UpdateJobState.Installing => "업데이트 설치 중",
            UpdateJobState.PendingReboot => "재부팅 준비",
            UpdateJobState.Rebooting => "재부팅 중",
            UpdateJobState.Paused => "잠시 일시중지됨",
            UpdateJobState.Completed => "업데이트 완료",
            UpdateJobState.Failed => "업데이트 실패",
            UpdateJobState.Cancelled => "업데이트 취소됨",
            _ => summary.Phase,
        };

        var progress = Math.Clamp(summary.ProgressPercent, 0, 100);
        PercentText = $"{progress}%";
        ProgressValue = progress;
        KbLabel = string.IsNullOrWhiteSpace(summary.CurrentKb) ? "" : summary.CurrentKb!;
        Message = summary.Message;

        DetailText =
            $"설치 {summary.InstalledItems}/{summary.TotalItems}건 · 실패 {summary.FailedItems}건 · " +
            $"재부팅 {summary.RebootCount}/{summary.MaxRebootCount}회";

        var rebootWanted = summary.State is UpdateJobState.PendingReboot or UpdateJobState.Rebooting;
        RebootText = rebootWanted
            ? "곧 자동으로 재부팅됩니다. 저장하지 않은 작업을 정리하세요."
            : "";
        RebootVisibility = rebootWanted ? Visibility.Visible : Visibility.Collapsed;

        Accent = summary.State switch
        {
            UpdateJobState.Completed => CompletedAccent,
            UpdateJobState.Failed or UpdateJobState.Cancelled => FailedAccent,
            UpdateJobState.Paused or UpdateJobState.PendingReboot => PausedAccent,
            _ => RunningAccent,
        };
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
