using System.Windows;
using System.Windows.Media;
using UpdateAgent.LabManager.Desktop.Models;
using UpdateAgent.LabManager.Desktop.Services;
using UpdateAgent.LabManager.Desktop.ViewModels;
using UpdateAgent.Shared.Models;

namespace UpdateAgent.LabManager.Desktop.Views;

public partial class JobDetailDialog : Window
{
    private readonly ApiClient _api;
    private readonly string _jobId;
    private JobDetailResponse? _detail;

    public JobDetailDialog(ApiClient api, string jobId)
    {
        InitializeComponent();
        _api = api;
        _jobId = jobId;
        Loaded += JobDetailDialog_Loaded;
    }

    private async void JobDetailDialog_Loaded(object sender, RoutedEventArgs e) => await RefreshAsync();

    public async Task RefreshAsync()
    {
        try
        {
            _detail = await _api.GetJobDetailAsync(_jobId);
            Apply(_detail);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "작업 상세를 불러올 수 없습니다.",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Apply(JobDetailResponse detail)
    {
        var summary = detail.Summary;
        TitleText.Text = $"{detail.MachineName} · {summary.JobId}";
        StateText.Text = $"{UiFormat.JobStateInfo(summary.State).Label} · {summary.Phase}";
        ProgressText.Text = $"{summary.ProgressPercent}%";
        ItemsText.Text = $"전체 {summary.TotalItems} · 설치 {summary.InstalledItems} · 실패 {summary.FailedItems}";
        RebootText.Text = $"{summary.RebootCount}/{summary.MaxRebootCount}회";
        MessageText.Text = summary.Message;
        StartedText.Text = UiFormat.Time(summary.StartedAt);
        UpdatedText.Text = UiFormat.Time(summary.UpdatedAt);
        ErrorText.Text = summary.LastError ?? "";
        ProgressBar.Value = Math.Clamp(summary.ProgressPercent, 0, 100);

        ItemsList.ItemsSource = (detail.Detail?.Items ?? []).Select(item => new ItemVm
        {
            Kb = string.IsNullOrWhiteSpace(item.KbArticleIds) ? "KB-" : item.KbArticleIds,
            Title = item.Title,
            State = item.State.ToString().ToLowerInvariant(),
            StateBrush = ItemBrush(item.State),
            Error = item.Error ?? "",
            ErrorVisibility = string.IsNullOrWhiteSpace(item.Error) ? Visibility.Collapsed : Visibility.Visible,
        }).ToList();

        ChecksList.ItemsSource = (detail.Detail?.PreflightChecks ?? []).Select(check => new CheckVm
        {
            Name = check.Name,
            Message = check.Message,
            StateBrush = CheckBrush(check.Status),
        }).ToList();

        var checklist = detail.Maestro;
        if (checklist is null)
        {
            MaestroInfoText.Text = "Maestro 확인 정보가 없습니다.";
            ProtectionButton.Visibility = Visibility.Collapsed;
            RestoredButton.Visibility = Visibility.Collapsed;
            BeforeGuide.Visibility = Visibility.Collapsed;
            AfterGuide.Visibility = Visibility.Collapsed;
            MaestroAlert.Visibility = Visibility.Collapsed;
            return;
        }

        var marker = checklist.MarkerStatus switch
        {
            MaestroVerificationStatus.MarkerVerified => "검증됨",
            MaestroVerificationStatus.VerificationFailed => "검증 실패",
            _ => "미검증",
        };

        MaestroInfoText.Text =
            $"보호 해제 확인: {(checklist.ProtectionSuspendedConfirmed ? "완료" : "대기")}\n" +
            $"현재 상태 저장: {(checklist.StateSavedConfirmed ? "완료" : "대기")}\n" +
            $"보호 재활성화: {(checklist.ProtectionRestoredConfirmed ? "완료" : "대기")}\n" +
            $"복원 마커: {marker}" +
            (string.IsNullOrWhiteSpace(checklist.MarkerMessage) ? "" : $"\n{checklist.MarkerMessage}");

        MaestroAlert.Visibility = checklist.MarkerStatus == MaestroVerificationStatus.VerificationFailed
            ? Visibility.Visible
            : Visibility.Collapsed;

        ProtectionButton.Visibility = checklist.ProtectionSuspendedConfirmed
            ? Visibility.Collapsed
            : Visibility.Visible;
        RestoredButton.Visibility = summary.IsTerminal ? Visibility.Visible : Visibility.Collapsed;

        BeforeGuide.Text = detail.BeforeStartMessages.Count > 0
            ? "작업 전 확인\n- " + string.Join("\n- ", detail.BeforeStartMessages)
            : "";
        BeforeGuide.Visibility = detail.BeforeStartMessages.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        AfterGuide.Text = summary.IsTerminal && detail.AfterCompleteMessages.Count > 0
            ? "작업 후 확인\n- " + string.Join("\n- ", detail.AfterCompleteMessages)
            : "";
        AfterGuide.Visibility = string.IsNullOrEmpty(AfterGuide.Text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private static Brush ItemBrush(UpdateItemState state) => state switch
    {
        UpdateItemState.Installed => UiFormat.Green,
        UpdateItemState.Failed => UiFormat.Red,
        UpdateItemState.Installing or UpdateItemState.Downloading or UpdateItemState.Downloaded => UiFormat.Blue,
        _ => UiFormat.Gray,
    };

    private static Brush CheckBrush(PreflightStatus status) => status switch
    {
        PreflightStatus.Pass => UiFormat.Green,
        PreflightStatus.Warn => UiFormat.Amber,
        PreflightStatus.Block => UiFormat.Red,
        _ => UiFormat.Gray,
    };

    private async void Protection_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _api.ConfirmMaestroProtectionSuspendedAsync(_jobId);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "확인 기록에 실패했습니다.", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Restored_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _api.ConfirmMaestroRestoredAsync(_jobId);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "확인 기록에 실패했습니다.", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    public sealed class ItemVm
    {
        public string Kb { get; init; } = "";
        public string Title { get; init; } = "";
        public string State { get; init; } = "";
        public Brush StateBrush { get; init; } = Brushes.Gray;
        public string Error { get; init; } = "";
        public Visibility ErrorVisibility { get; init; }
    }

    public sealed class CheckVm
    {
        public string Name { get; init; } = "";
        public string Message { get; init; } = "";
        public Brush StateBrush { get; init; } = Brushes.Gray;
    }
}
