using System.Windows;
using UpdateAgent.Agent.Desktop.Services;

namespace UpdateAgent.Agent.Desktop;

public partial class MainWindow : Window
{
    private readonly AgentStatusReader _reader = new();

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

            RecentList.ItemsSource = status.RecentJobs;
            EmptyText.Visibility = status.RecentJobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdatedText.Text = "마지막 새로고침: " + DateTime.Now.ToString("HH:mm:ss");
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => _ = RefreshAsync();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
