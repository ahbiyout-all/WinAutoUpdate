using UpdateAgent.Shared.Updates;

namespace UpdateAgent.LabManager.Services;

/// <summary>
/// GitHub Releases 최신 버전 확인 결과를 캐시한다.
/// 대시보드가 5초마다 폴링하므로 GitHub API 호출은 일정 시간만 허용한다.
/// </summary>
public sealed class UpdateCheckService
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<UpdateCheckService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private AppUpdateInfo? _cached;

    public UpdateCheckService(
        IHttpClientFactory httpClientFactory,
        ILogger<UpdateCheckService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<AppUpdateInfo> GetAsync(bool force, CancellationToken cancellationToken)
    {
        if (!force && TryGetCached(out var cached))
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!force && TryGetCached(out cached))
            {
                return cached;
            }

            // 대시보드가 링크를 열므로 GitHub 이외의 호스트/스킴은 저장소 상수로 되돌린다.
            var checker = new GitHubReleaseChecker(
                _httpClientFactory.CreateClient("github"), strictUrlValidation: true);
            var result = await checker.CheckAsync(UpdateProduct.LabManager, cancellationToken).ConfigureAwait(false);
            // 실패 결과를 10분간 캐시하면 네트워크 복구 후에도 계속 실패가 표시된다.
            _cached = result.CheckSucceeded ? result : null;

            if (result.CheckSucceeded)
            {
                _logger.LogInformation(
                    "업데이트 확인: 현재 {CurrentVersion}, 최신 {LatestVersion}, 새 버전 {Available}",
                    result.CurrentVersion,
                    result.LatestVersion,
                    result.UpdateAvailable);
            }
            else
            {
                _logger.LogWarning("업데이트 확인 실패: {Error}", result.Error);
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool TryGetCached(out AppUpdateInfo result)
    {
        if (_cached is { CheckSucceeded: true } cached && UtcTimestamps.Now - cached.CheckedAt < CacheDuration)
        {
            result = cached;
            return true;
        }

        result = null!;
        return false;
    }
}
