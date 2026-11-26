using UpdateAgent.Core.Policy;
using UpdateAgent.Core.Windows;
using UpdateAgent.Core.Windows.Update;
using UpdateAgent.Shared.Models;

namespace UpdateAgent.Core.Orchestration;

public sealed record ScanReport
{
    public required OsInfo Os { get; init; }
    public IReadOnlyList<UpdateItemInfo> Selected { get; init; } = [];
    public IReadOnlyList<UpdateItemInfo> Excluded { get; init; } = [];
    public WuaSystemStatus SystemStatus { get; init; } = new();
    public DateTimeOffset ScannedAt { get; init; } = UpdateAgent.Guards.UtcClock.Now;
}

/// <summary>정책을 적용한 WUA 검색.</summary>
public sealed class UpdateScanner
{
    private readonly IWuaClient _wua;
    private readonly OsInfo _os;

    public UpdateScanner(IWuaClient wua, OsInfo? os = null)
    {
        _wua = wua;
        _os = os ?? WindowsOsInfoProvider.GetCurrent();
    }

    public OsInfo Os => _os;

    public async Task<ScanReport> ScanAsync(UpdatePolicy policy, CancellationToken cancellationToken = default)
    {
        // CLI/직접 호출에서도 실행 경로와 같은 규칙을 적용한다. 승인되지 않은 기능 업데이트가
        // 검색 결과에 나타나지 않도록 러너(UpdateJobRunner.ScanAsync)와 동일하게 정규화한다.
        var effectivePolicy = policy.ForExecution(_os);
        var result = await _wua.ScanAsync(effectivePolicy.IncludeDrivers, cancellationToken);
        var evaluation = UpdatePolicyEvaluator.Evaluate(
            result.Updates.Select(u => u.Info),
            effectivePolicy,
            _os);

        return new ScanReport
        {
            Os = _os,
            Selected = evaluation.Selected,
            Excluded = evaluation.Excluded,
            SystemStatus = result.Status,
            ScannedAt = result.ScannedAt,
        };
    }
}
