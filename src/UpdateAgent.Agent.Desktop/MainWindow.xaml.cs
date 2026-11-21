using System.Windows;
using UpdateAgent.Agent.Desktop.Services;

namespace UpdateAgent.Agent.Desktop;

public partial class MainWindow : Window
{
    private readonly AgentStatusReader _reader = new();
    private readonly LocalUpdateClient _updateClient = new();

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RefreshAsync().ConfigureAwait(true);
    }

    private async Task RefreshAsync()
    {
        RefreshButton.IsEnabled = false;
        try
        {
            var status = await _reader.ReadAsync().ConfigureAwait(true);

            ServiceText.Text = status.ServiceStatus;
            MachineText.Text = status.MachineName;
            AgentIdText.Text = status.AgentId;
            UrlText.Text = status.LabManagerUrl;

            var hasError = !string.IsNullOrWhiteSpace(status.Error);
            ErrorText.Visibility = hasError ? Visibility.Visible : Visibility.Collapsed;
            ErrorText.Text = hasError ? "작업 기록을 읽지 못했습니다: " + status.Error : string.Empty;

            if (status.ActiveJob is { } active)
            {
                ActiveCard.Visibility = Visibility.Visible;
                ActiveStateText.Text = active.StateLabel + " · " + active.Phase;
                ActiveMessageText.Text = string.IsNullOrWhiteSpace(active.Message)
                    ? active.CurrentKb ?? string.Empty
                    : active.Message;
                ActiveProgress.Value = active.ProgressPercent;
                ActiveProgressText.Text =
                    $"{active.ProgressPercent}% ({active.CompletedItems}/{active.TotalItems})";
            }
            else
            {
                ActiveCard.Visibility = Visibility.Collapsed;
            }

            StartButton.IsEnabled = status.ActiveJob is null || status.ActiveJob.IsTerminal;

            RecentList.ItemsSource = status.RecentJobs;
            EmptyText.Visibility = status.RecentJobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdatedText.Text = "마지막 새로고침: " + DateTime.Now.ToString("HH:mm:ss");
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private async Task StartUpdateAsync()
    {
        var confirm = MessageBox.Show(
            "업데이트 작업을 시작합니다.\n\n" +
            "• Maestro를 '수동 복구/해제' 모드로 전환했고 확인했습니다.\n" +
            "• 설치 중 입력이 잠기고 PC가 여러 번 재부팅될 수 있습니다.\n\n" +
            "계속하시겠습니까?",
            "수동 업데이트 확인",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (confirm != MessageBoxResult.Yes) return;

        StartButton.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        try
        {
            var response = await _updateClient.StartAsync(maestroConfirmed: true).ConfigureAwait(true);
            if (response.Success)
            {
                MessageBox.Show(
                    $"업데이트 작업을 시작했습니다.\n작업 ID: {response.JobId}\n\n" +
                    "진행 상황은 이 창과 교사 대시보드에서 확인할 수 있습니다.",
                    "업데이트 시작",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(
                    "업데이트 작업을 시작하지 못했습니다.\n\n" + response.Message,
                    "업데이트 시작 실패",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "업데이트 요청 중 오류가 발생했습니다.\n\n" + ex.Message,
                "업데이트 시작 실패",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            StartButton.IsEnabled = true;
            RefreshButton.IsEnabled = true;
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => _ = RefreshAsync();

    private void StartButton_Click(object sender, RoutedEventArgs e) => _ = StartUpdateAsync();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
