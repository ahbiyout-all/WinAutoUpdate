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
    public DateTimeOffset ScannedAt { get; init; } = DateTimeOffset.Now;
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
        var result = await _wua.ScanAsync(policy.IncludeDrivers, cancellationToken);
        var evaluation = UpdatePolicyEvaluator.Evaluate(
            result.Updates.Select(u => u.Info),
            policy,
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
