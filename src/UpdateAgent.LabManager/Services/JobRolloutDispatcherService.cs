using UpdateAgent.LabManager.Live;
using UpdateAgent.LabManager.Storage;

namespace UpdateAgent.LabManager.Services;

/// <summary>
/// 저장된 배치 예약을 주기적으로 확인해 도래한 StartJob 명령을 발급한다.
/// 중복 틱/재시작은 저장소의 dispatched_at 플래그로 흡수한다.
/// </summary>
public sealed class JobRolloutDispatcherService : BackgroundService
{
    private readonly LabStore _store;
    private readonly LiveEventBus _liveEvents;
    private readonly ILogger<JobRolloutDispatcherService> _logger;

    public JobRolloutDispatcherService(
        LabStore store,
        LiveEventBus liveEvents,
        ILogger<JobRolloutDispatcherService> logger)
    {
        _store = store;
        _liveEvents = liveEvents;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var dispatched = await _store.DispatchDueRolloutJobsAsync(
                    cancellationToken: stoppingToken);
                if (dispatched.Count > 0)
                {
                    _liveEvents.Signal();
                    _logger.LogInformation("예약 배치 {Count}건의 시작 명령을 발급했습니다.", dispatched.Count);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // 한 번의 DB 오류로 서비스가 죽지 않게 하고, 다음 틱에서 다시 시도한다.
                _logger.LogWarning(ex, "예약 배치 전송 실패");
            }

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
