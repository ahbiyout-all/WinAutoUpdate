using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using UpdateAgent.Agent.Desktop.Services;
using UpdateAgent.Shared.Models;

namespace UpdateAgent.Agent.Desktop;

public partial class MainWindow : Window
{
    private readonly AgentStatusReader _reader = new();
    private readonly LocalPolicyReader _policyReader = new();
    private readonly LocalUpdateClient _updateClient = new();
    private CancellationTokenSource? _startCts;
    private int _refreshGeneration;
    private bool _startRequestInFlight;
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => _ = RefreshAsync();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel) return;
        // 창이 실제로 닫힐 때만 진행 중인 파이프 요청을 취소하고 결과 창을 생략한다.
        _closing = true;
        _startCts?.Cancel();
    }

    /// <summary>동시에 여러 조회가 돌아도 마지막 요청만 화면에 반영한다.</summary>
    private async Task RefreshAsync()
    {
        var generation = ++_refreshGeneration;
        RefreshButton.IsEnabled = false;
        try
        {
            var status = await _reader.ReadAsync().ConfigureAwait(true);
            if (generation != _refreshGeneration) return;

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

            var canStart = !_startRequestInFlight &&
                           (status.ActiveJob is null || status.ActiveJob.IsTerminal);
            StartButton.IsEnabled = canStart;
            OptionsButton.IsEnabled = canStart;

            RecentList.ItemsSource = status.RecentJobs;
            EmptyText.Visibility = status.RecentJobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdatedText.Text = "마지막 새로고침: " + DateTime.Now.ToString("HH:mm:ss");
        }
        catch (Exception ex)
        {
            if (generation != _refreshGeneration) return;
            ErrorText.Visibility = Visibility.Visible;
            ErrorText.Text = "상태를 새로고침하지 못했습니다: " + ex.Message;
            // 조회가 실패해도 시작 요청이 영구히 막히지 않게 한다. 중복 시작은 서비스가 거부한다.
            if (!_startRequestInFlight && !_closing)
            {
                StartButton.IsEnabled = true;
                OptionsButton.IsEnabled = true;
            }
        }
        finally
        {
            // 시작 요청 중이거나 더 최신 조회가 있으면 버튼 상태를 바꾸지 않는다.
            if (generation == _refreshGeneration && !_startRequestInFlight && !_closing)
                RefreshButton.IsEnabled = true;
        }
    }

    private async Task StartUpdateAsync()
    {
        // 두 버튼에서 같은 흐름을 호출하므로 정책 로딩/대화상자 표시 전에 재진입을 막는다.
        if (_startRequestInFlight) return;

        _startRequestInFlight = true;
        StartButton.IsEnabled = false;
        OptionsButton.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        _startCts = new CancellationTokenSource();
        try
        {
            var teacherPolicy = await _policyReader.ReadAsync().ConfigureAwait(true);
            var optionsWindow = new ManualUpdateOptionsWindow(teacherPolicy) { Owner = this };
            if (optionsWindow.ShowDialog() != true) return;

            var options = optionsWindow.Options;
            var maestroConfirmed = optionsWindow.MaestroConfirmed;

            var response = await _updateClient
                .StartAsync(maestroConfirmed, options, _startCts.Token)
                .ConfigureAwait(true);
            if (_closing) return;

            if (response.Success)
            {
                MessageBox.Show(
                    $"업데이트 작업을 시작했습니다.\n" +
                    $"요청 옵션: {DescribeOptions(options)}\n" +
                    $"작업 ID: {response.JobId}\n\n" +
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
        catch (OperationCanceledException) when (_closing)
        {
            // 창을 닫으면서 취소한 요청이므로 결과 창을 띄우지 않는다.
        }
        catch (Exception ex)
        {
            if (_closing) return;
            MessageBox.Show(
                "수동 업데이트를 시작하지 못했습니다.\n\n" + ex.Message,
                "업데이트 시작 실패",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _startCts?.Dispose();
            _startCts = null;
            _startRequestInFlight = false;
            if (!_closing)
            {
                RefreshButton.IsEnabled = true;
                // 시작 버튼은 최신 작업 상태를 반영하는 새 스냅샷에서 다시 활성화한다.
                await RefreshAsync().ConfigureAwait(true);
            }
        }
    }

    /// <summary>정책이 변경돼 실제 적용값이 달라질 수 있으므로 프리셋 이름 대신 요청한 항목을 표시한다.</summary>
    private static string DescribeOptions(ManualUpdateOptions options)
    {
        var categories = new List<string>();
        if (options.IncludeSecurity) categories.Add("보안");
        if (options.IncludeCritical) categories.Add("중요");
        if (options.IncludeImportant) categories.Add("권장");
        if (options.IncludeOptional) categories.Add("선택");
        if (options.IncludeDrivers) categories.Add("드라이버");
        if (options.IncludeDefinitionUpdates) categories.Add("정의 업데이트");

        var reboot = options.PreventAutomaticReboot
            ? "자동 재부팅 끔"
            : $"자동 재부팅 최대 {options.MaxRebootCount}회";
        var overlay = options.OverlayEnabled ? "오버레이 사용" : "오버레이 사용 안 함";
        return $"범위 {string.Join(", ", categories)} · {reboot} · {overlay}";
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => _ = RefreshAsync();

    private void OptionsButton_Click(object sender, RoutedEventArgs e) => _ = StartUpdateAsync();

    private void StartButton_Click(object sender, RoutedEventArgs e) => _ = StartUpdateAsync();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
