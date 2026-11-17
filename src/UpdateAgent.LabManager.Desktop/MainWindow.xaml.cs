using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using UpdateAgent.LabManager.Desktop.Services;
using UpdateAgent.LabManager.Desktop.ViewModels;
using UpdateAgent.LabManager.Desktop.Views;
using UpdateAgent.Shared.Updates;

namespace UpdateAgent.LabManager.Desktop;

/// <summary>기존 웹 대시보드와 동일한 기능을 제공하는 네이티브 WPF 메인 창.</summary>
public partial class MainWindow : Window
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromMinutes(30);

    private readonly ObservableCollection<AgentRow> _agents = [];
    private readonly ObservableCollection<JobRow> _jobs = [];
    private readonly ObservableCollection<AuditRow> _audit = [];
    private readonly HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);

    private readonly DispatcherTimer _refreshTimer = new() { Interval = RefreshInterval };
    private readonly DispatcherTimer _updateTimer = new() { Interval = UpdateCheckInterval };
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3.8) };

    private bool _refreshInFlight;
    private bool _updateCheckInFlight;
    private DateTimeOffset _lastUpdateCheck;
    private AppUpdateInfo? _updateInfo;
    private JobDetailDialog? _openJobDetail;

    public MainWindow()
    {
        InitializeComponent();
        AgentsGrid.ItemsSource = _agents;
        JobsGrid.ItemsSource = _jobs;
        AuditList.ItemsSource = _audit;

        _refreshTimer.Tick += (_, _) => _ = RefreshAsync();
        _updateTimer.Tick += (_, _) => _ = CheckUpdatesAsync(force: false);
        _toastTimer.Tick += ToastTimer_Tick;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        RestorePlacement();
        _refreshTimer.Start();
        _updateTimer.Start();
        _ = RefreshAsync();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _refreshTimer.Stop();
        _updateTimer.Stop();
        _toastTimer.Stop();
        SavePlacement();
        App.Settings.Save();
    }

    // ---------------- 데이터 ----------------

    private async Task RefreshAsync()
    {
        if (_refreshInFlight) return;
        _refreshInFlight = true;

        try
        {
            var agentsTask = App.Api.GetAgentsAsync();
            var jobsTask = App.Api.GetJobsAsync(60);
            var overviewTask = App.Api.GetOverviewAsync();
            var auditTask = App.Api.GetAuditAsync(60);
            await Task.WhenAll(agentsTask, jobsTask, overviewTask, auditTask);

            ApplyAgents(await agentsTask);
            ApplyJobs(await jobsTask);
            ApplyOverview(await overviewTask);
            ApplyAudit(await auditTask);
            SetError(null);

            if (_openJobDetail is not null)
            {
                await _openJobDetail.RefreshAsync();
            }

            _ = CheckUpdatesAsync(force: false);
        }
        catch (ApiUnauthorizedException)
        {
            SetError("관리자 토큰이 올바르지 않습니다.");
            ShowTokenDialog(requireToken: false);
        }
        catch (Exception ex)
        {
            SetError($"서버 연결 오류: {ex.Message}");
        }
        finally
        {
            _refreshInFlight = false;
        }
    }

    private void ApplyAgents(IReadOnlyList<Models.AgentRecord> records)
    {
        var ids = new HashSet<string>(records.Select(r => r.Agent.AgentId), StringComparer.OrdinalIgnoreCase);
        _selected.RemoveWhere(id => !ids.Contains(id));

        var rows = records
            .Select(record => new AgentRow(record))
            .ToList();
        foreach (var row in rows)
        {
            row.IsSelected = _selected.Contains(row.AgentId);
        }

        _agents.Clear();
        foreach (var row in rows)
        {
            _agents.Add(row);
        }

        UpdateSelectedCount();
    }

    private void ApplyJobs(IReadOnlyList<Models.LabJobRecord> records)
    {
        _jobs.Clear();
        foreach (var record in records.Take(40))
        {
            _jobs.Add(new JobRow(record));
        }
    }

    private void ApplyOverview(Models.OverviewResponse info)
    {
        AgentCountText.Text = info.AgentCount.ToString();
        OnlineCountText.Text = $"{info.OnlineCount}대 온라인";
        ActiveJobsText.Text = info.ActiveJobs.ToString();
        BusyCountText.Text = $"{info.BusyCount}대 실행 중";
        CompletedJobsText.Text = info.CompletedJobs.ToString();
        FailedJobsText.Text = info.FailedJobs.ToString();
    }

    private void ApplyAudit(IReadOnlyList<Models.AuditEntry> entries)
    {
        _audit.Clear();
        foreach (var entry in entries.Take(25))
        {
            _audit.Add(new AuditRow(entry));
        }
    }

    private void UpdateSelectedCount()
    {
        SelectedCountText.Text = _selected.Count > 0 ? $"{_selected.Count}대 선택됨" : "";
    }

    private void SetError(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            ErrorBar.Visibility = Visibility.Collapsed;
            ErrorText.Text = "";
        }
        else
        {
            ErrorBar.Visibility = Visibility.Visible;
            ErrorText.Text = message;
        }
    }

    // ---------------- 상단 동작 ----------------

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = RefreshAsync();

    private void Token_Click(object sender, RoutedEventArgs e) => ShowTokenDialog(requireToken: false);

    private void ShowTokenDialog(bool requireToken)
    {
        var dialog = new TokenDialog(requireToken) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            App.Api.RefreshConfiguration();
            SetError(null);
            _ = RefreshAsync();
            _ = CheckUpdatesAsync(force: true);
        }
    }

    private void Start_Click(object sender, RoutedEventArgs e) => OpenStartDialog([]);

    private void OpenStartDialog(IReadOnlyList<string> agentIds)
    {
        if (agentIds.Count == 0)
        {
            agentIds = _selected.ToList();
        }

        if (agentIds.Count == 0)
        {
            Toast("먼저 대상 PC를 선택하세요.", isError: true);
            return;
        }

        var anyMaestro = _agents.Any(row =>
            agentIds.Contains(row.AgentId, StringComparer.OrdinalIgnoreCase) && row.MaestroInstalled);

        var dialog = new StartJobDialog(agentIds, anyMaestro) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is null)
        {
            return;
        }

        _ = SubmitStartAsync(dialog.Result);
    }

    private async Task SubmitStartAsync(Models.StartJobsRequest request)
    {
        StartButton.IsEnabled = false;
        try
        {
            var result = await App.Api.StartJobsAsync(request);
            if (result.Errors.Count > 0)
            {
                Toast(string.Join(" / ", result.Errors), isError: true);
            }

            if (result.JobIds.Count > 0)
            {
                Toast($"{result.JobIds.Count}대에 작업을 배정했습니다.", isError: false);
                _selected.Clear();
                UpdateSelectedCount();
                foreach (var row in _agents)
                {
                    row.IsSelected = false;
                }

                await RefreshAsync();
            }
        }
        catch (Exception ex)
        {
            Toast(ex.Message, isError: true);
        }
        finally
        {
            StartButton.IsEnabled = true;
        }
    }

    // ---------------- 학생 PC 동작 ----------------

    private static AgentRow? RowOf(object sender) => (sender as FrameworkElement)?.DataContext as AgentRow;

    private async void Wake_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        try
        {
            var result = await App.Api.WakeAsync(row.AgentId);
            var sent = result.Results.Count(r => r.Sent);
            Toast(sent > 0
                ? $"{row.MachineName}: WoL 패킷 {sent}건 전송"
                : "전송할 MAC 주소가 없거나 실패했습니다.", isError: sent == 0);
        }
        catch (Exception ex) { Toast(ex.Message, isError: true); }
    }

    private void AgentStart_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row) OpenStartDialog([row.AgentId]);
    }

    private async void Unlock_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        try
        {
            await App.Api.UnlockAsync(row.AgentId);
            Toast($"{row.MachineName}: 입력 잠금 해제 명령을 보냈습니다.", isError: false);
        }
        catch (Exception ex) { Toast(ex.Message, isError: true); }
    }

    private async void Reboot_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        var result = MessageBox.Show(this,
            $"{row.MachineName} PC를 60초 후 재부팅할까요?\n저장하지 않은 학생 작업이 사라질 수 있습니다.",
            "재부팅 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        try
        {
            await App.Api.RebootAsync(row.AgentId);
            Toast($"{row.MachineName}: 재부팅 명령을 보냈습니다.", isError: false);
        }
        catch (Exception ex) { Toast(ex.Message, isError: true); }
    }

    private async void Shutdown_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        var result = MessageBox.Show(this,
            $"{row.MachineName} PC를 60초 후 종료할까요?",
            "종료 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        try
        {
            await App.Api.ShutdownAsync(row.AgentId);
            Toast($"{row.MachineName}: 종료 명령을 보냈습니다.", isError: false);
        }
        catch (Exception ex) { Toast(ex.Message, isError: true); }
    }

    // ---------------- 작업 동작 ----------------

    private static JobRow? JobRowOf(object sender) => (sender as FrameworkElement)?.DataContext as JobRow;

    private void JobDetail_Click(object sender, RoutedEventArgs e)
    {
        if (JobRowOf(sender) is not { } row) return;
        var dialog = new JobDetailDialog(App.Api, row.JobId) { Owner = this };
        _openJobDetail = dialog;
        dialog.Closed += (_, _) => _openJobDetail = null;
        dialog.ShowDialog();
    }

    private async void JobAction_Click(object sender, RoutedEventArgs e)
    {
        if (JobRowOf(sender) is not { } row) return;
        await RunJobCommandAsync(row, "pause", "일시중지");
    }

    private async void JobResume_Click(object sender, RoutedEventArgs e)
    {
        if (JobRowOf(sender) is not { } row) return;
        await RunJobCommandAsync(row, "resume", "재개");
    }

    private async void JobCancel_Click(object sender, RoutedEventArgs e)
    {
        if (JobRowOf(sender) is not { } row) return;
        await RunJobCommandAsync(row, "cancel", "취소");
    }

    private async Task RunJobCommandAsync(JobRow row, string action, string label)
    {
        var confirm = MessageBox.Show(this,
            $"{row.MachineName} 작업을 {label}할까요?",
            $"작업 {label}", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            await App.Api.JobActionAsync(row.JobId, action);
            Toast($"{label} 명령을 보냈습니다.", isError: false);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Toast(ex.Message, isError: true);
        }
    }

    // ---------------- 업데이트 알림 ----------------

    private async void Version_Click(object sender, RoutedEventArgs e)
    {
        if (_updateInfo?.UpdateAvailable == true)
        {
            ShowUpdateDialog(_updateInfo);
            return;
        }

        await CheckUpdatesAsync(force: true, fromButton: true);
    }

    private async Task CheckUpdatesAsync(bool force, bool fromButton = false)
    {
        if (!App.Settings.HasAdminToken || _updateCheckInFlight) return;
        if (!force && DateTimeOffset.UtcNow - _lastUpdateCheck < UpdateCheckInterval) return;

        _updateCheckInFlight = true;
        _lastUpdateCheck = DateTimeOffset.UtcNow;
        try
        {
            var info = await App.Api.GetUpdateInfoAsync(force);
            _updateInfo = info;

            VersionText.Text = $"v{info.CurrentVersion}";
            VersionButton.ToolTip = info.CheckSucceeded
                ? $"현재 v{info.CurrentVersion} · 최신 v{info.LatestVersion}"
                : $"현재 v{info.CurrentVersion} · 업데이트 확인 실패";
            UpdateDot.Visibility = info.UpdateAvailable ? Visibility.Visible : Visibility.Collapsed;

            var dismissed = App.Settings.DismissedUpdateVersion == info.LatestVersion;
            if (info.UpdateAvailable && (fromButton || !dismissed))
            {
                ShowUpdateDialog(info);
            }
            else if (fromButton)
            {
                Toast(info.CheckSucceeded
                        ? $"최신 버전을 사용 중입니다 (v{info.CurrentVersion})"
                        : $"업데이트 확인 실패: {info.Error ?? "GitHub에 연결할 수 없습니다."}",
                    isError: !info.CheckSucceeded);
            }
        }
        catch
        {
            // 토큰/네트워크 오류는 새로고침 배너에서 표시한다.
        }
        finally
        {
            _updateCheckInFlight = false;
        }
    }

    private void ShowUpdateDialog(AppUpdateInfo info)
    {
        var dialog = new UpdateDialog(info) { Owner = this };
        if (dialog.ShowDialog() != true && !string.IsNullOrWhiteSpace(info.LatestVersion))
        {
            App.Settings.DismissedUpdateVersion = info.LatestVersion;
            App.Settings.Save();
        }
    }

    // ---------------- 토스트 / 창 위치 ----------------

    private void Toast(string message, bool isError)
    {
        ToastText.Text = message;
        ToastBorder.Background = isError
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(185, 28, 28))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(31, 41, 55));
        ToastBorder.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void ToastTimer_Tick(object? sender, EventArgs e)
    {
        _toastTimer.Stop();
        ToastBorder.Visibility = Visibility.Collapsed;
    }

    private void RestorePlacement()
    {
        var placement = App.Settings.Window;
        if (placement.Width > 0) Width = placement.Width;
        if (placement.Height > 0) Height = placement.Height;
        if (!double.IsNaN(placement.Left)) Left = placement.Left;
        if (!double.IsNaN(placement.Top)) Top = placement.Top;
        WindowState = placement.Maximized ? WindowState.Maximized : WindowState.Normal;
    }

    private void SavePlacement()
    {
        var placement = App.Settings.Window;
        placement.Maximized = WindowState == WindowState.Maximized;
        if (!placement.Maximized)
        {
            placement.Left = Left;
            placement.Top = Top;
            placement.Width = Width;
            placement.Height = Height;
        }
    }
}
