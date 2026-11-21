using Microsoft.Extensions.Options;
using UpdateAgent.Core.Comms;
using UpdateAgent.Core.Orchestration;
using UpdateAgent.Service.Configuration;
using UpdateAgent.Shared.Models;

namespace UpdateAgent.Service.Overlay;

/// <summary>
/// 작업 진행 상황을 SessionHelper 오버레이로 전달한다.
/// 작업이 끝나면 일정 시간 유지한 뒤 자동으로 숨긴다.
/// </summary>
public sealed class OverlayJobProgressSink : IJobProgressSink, IDisposable
{
    private readonly OverlayPipeServer _pipe;
    private readonly IOptionsMonitor<AgentOptions> _options;
    private readonly ILogger<OverlayJobProgressSink> _logger;
    private readonly object _gate = new();

    private CancellationTokenSource? _hideCts;
    private JobSummary? _lastSummary;
    private string? _suppressedJobId;

    public OverlayJobProgressSink(
        OverlayPipeServer pipe,
        IOptionsMonitor<AgentOptions> options,
        ILogger<OverlayJobProgressSink> logger)
    {
        _pipe = pipe;
        _options = options;
        _logger = logger;
    }

    public async Task OnProgressAsync(JobDetail detail, CancellationToken cancellationToken = default)
    {
        // 명시적으로 숨긴 작업의 잔여 진행률(취소 완료 포함)은 오버레이를 다시 띄우지 않는다.
        // 새 작업이 시작되면 suppression이 해제된다.
        if (_suppressedJobId is not null)
        {
            if (_suppressedJobId == detail.Summary.JobId) return;
            _suppressedJobId = null;
        }

        var options = _options.CurrentValue;
        if (!options.OverlayEnabled ||
            !detail.Policy.OverlayEnabled ||
            detail.Policy.OverlayMode != OverlayMode.FullOverlay)
        {
            return;
        }

        var summary = detail.Summary;
        _lastSummary = summary;

        if (summary.IsTerminal)
        {
            // 완료/실패 직후에는 결과를 보여주고 나서 숨긴다.
            await ShowAsync(OverlayMessage.Update(summary), cancellationToken);
            ScheduleHide(options.OverlayCompletionHoldSeconds, summary);
        }
        else
        {
            CancelHide();
            await ShowAsync(OverlayMessage.Update(summary), cancellationToken);
        }
    }

    /// <summary>교사가 원격으로 입력 잠금을 해제하거나 작업을 중단할 때 사용한다.</summary>
    public async Task HideAsync(string? notice = null, CancellationToken cancellationToken = default)
    {
        _suppressedJobId = _lastSummary?.JobId;
        CancelHide();
        try
        {
            await _pipe.BroadcastAsync(OverlayMessage.Hide(notice), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "오버레이 숨김 전송 실패");
        }
    }

    /// <summary>서비스 시작 시 연결된 헬퍼를 현재 상태로 맞춘다.</summary>
    public async Task ResyncAsync(CancellationToken cancellationToken = default)
    {
        var summary = _lastSummary;
        if (summary is null)
        {
            return;
        }

        if (summary.IsTerminal)
        {
            await ShowAsync(OverlayMessage.Hide(), cancellationToken);
        }
        else
        {
            await ShowAsync(OverlayMessage.Show(summary), cancellationToken);
        }
    }

    private async Task ShowAsync(OverlayMessage message, CancellationToken cancellationToken)
    {
        try
        {
            await _pipe.BroadcastAsync(message, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "오버레이 메시지 전송 실패");
        }
    }

    private void ScheduleHide(int holdSeconds, JobSummary summary)
    {
        CancelHide();
        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            _hideCts = cts;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(3, holdSeconds)), cts.Token);
                await _pipe.BroadcastAsync(OverlayMessage.Hide(), CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                // 새 작업이 시작되면 취소된다.
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "오버레이 자동 숨김 실패 ({JobId})", summary.JobId);
            }
        }, CancellationToken.None);
    }

    private void CancelHide()
    {
        lock (_gate)
        {
            try
            {
                _hideCts?.Cancel();
                _hideCts?.Dispose();
            }
            catch
            {
                // ignore
            }

            _hideCts = null;
        }
    }

    public void Dispose()
    {
        CancelHide();
    }
}
