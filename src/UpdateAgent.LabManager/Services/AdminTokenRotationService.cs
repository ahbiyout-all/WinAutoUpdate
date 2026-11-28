using Microsoft.Extensions.Options;
using UpdateAgent.LabManager.Auth;
using UpdateAgent.LabManager.Configuration;

namespace UpdateAgent.LabManager.Services;

/// <summary>
/// 관리자 토큰 회전 조건을 주기적으로 확인하는 백그라운드 서비스.
/// 기본 설정에서는 사용하지 않으며, 시작 자체는 토큰을 바꾸지 않는다.
/// </summary>
public sealed class AdminTokenRotationService : BackgroundService
{
    private readonly AdminTokenStore _adminTokenStore;
    private readonly IOptionsMonitor<LabManagerOptions> _options;
    private readonly ILogger<AdminTokenRotationService> _logger;
    private readonly Func<TimeSpan> _intervalProvider;
    private readonly SemaphoreSlim _rotationSync = new(1, 1);

    /// <summary>프로덕션 생성자. DI가 사용한다.</summary>
    public AdminTokenRotationService(
        AdminTokenStore adminTokenStore,
        IOptionsMonitor<LabManagerOptions> options,
        ILogger<AdminTokenRotationService> logger)
        : this(
            adminTokenStore,
            options,
            logger,
            () => TokenRotationOptions.ResolveInterval(
                options.CurrentValue.TokenRotation?.CheckIntervalMinutes
                ?? TokenRotationOptions.DefaultCheckIntervalMinutes))
    {
    }

    /// <summary>테스트용 생성자. 확인 주기를 직접 지정할 수 있다.</summary>
    public AdminTokenRotationService(
        AdminTokenStore adminTokenStore,
        IOptionsMonitor<LabManagerOptions> options,
        ILogger<AdminTokenRotationService> logger,
        Func<TimeSpan> intervalProvider)
    {
        _adminTokenStore = adminTokenStore;
        _options = options;
        _logger = logger;
        _intervalProvider = intervalProvider;
        if (_intervalProvider() <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(intervalProvider), "확인 주기는 0보다 커야 합니다.");
    }

    /// <summary>
    /// 한 번의 확인을 수행한다. 중복 틱·동시 호출은 세마포어로 직렬화하고,
    /// 회전 뒤에는 상태가 갱신되어 다음 확인이 같은 토큰을 다시 바꾸지 않는다.
    /// </summary>
    public async Task<bool> CheckAndRotateAsync(CancellationToken cancellationToken)
    {
        if (_options.CurrentValue.TokenRotation?.Enabled != true) return false;

        await _rotationSync.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = _adminTokenStore.GetStatus();
            if (!status.RotationEnabled ||
                !status.HasRuntimeOverride ||
                status.IsPlaceholder ||
                status.LastChangedAt is null ||
                !status.RotationDue)
            {
                return false;
            }

            try
            {
                await _adminTokenStore.RotateAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 토큰 값은 예외 메시지에도 남기지 않는다. 다음 주기에 다시 시도한다.
                _logger.LogWarning(ex, "관리자 토큰 자동 회전 실패");
                return false;
            }

            _logger.LogInformation("관리자 토큰 자동 회전을 완료했습니다. (토큰 값은 기록하지 않음)");
            return true;
        }
        finally
        {
            _rotationSync.Release();
        }
    }

    /// <summary>첫 확인은 주기 대기 뒤에 한다. 서비스 시작만으로 토큰을 바꾸지 않는다.</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_intervalProvider(), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await CheckAndRotateAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "관리자 토큰 회전 확인 실패");
            }
        }
    }
}
