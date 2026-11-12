using UpdateAgent.Core.Windows.Update;
using UpdateAgent.Shared.Models;

namespace UpdateAgent.Core.Policy;

public sealed record PolicyEvaluationResult
{
    public IReadOnlyList<UpdateItemInfo> Selected { get; init; } = [];
    public IReadOnlyList<UpdateItemInfo> Excluded { get; init; } = [];
}

/// <summary>OS/중요도/기능 업데이트 승인 여부에 따라 업데이트를 선택한다.</summary>
public static class UpdatePolicyEvaluator
{
    public static PolicyEvaluationResult Evaluate(
        IEnumerable<UpdateItemInfo> updates,
        UpdatePolicy policy,
        OsInfo os)
    {
        var selected = new List<UpdateItemInfo>();
        var excluded = new List<UpdateItemInfo>();

        foreach (var update in updates)
        {
            var (isSelected, reason) = IsSelected(update, policy, os);
            var item = update with { Selected = isSelected };
            if (isSelected) selected.Add(item);
            else excluded.Add(item with { Description = reason });
        }

        return new PolicyEvaluationResult
        {
            Selected = selected.OrderBy(SelectionOrder).ThenBy(u => u.Title).ToList(),
            Excluded = excluded,
        };
    }

    public static (bool Selected, string Reason) IsSelected(UpdateItemInfo update, UpdatePolicy policy, OsInfo os)
    {
        if (update.IsDriver)
        {
            return policy.IncludeDrivers
                ? (true, "드라이버 포함 정책")
                : (false, "드라이버 업데이트 제외 정책");
        }

        if (update.IsFeatureUpdate || update.Category == UpdateCategory.Upgrade)
        {
            if (!policy.IncludeFeatureUpdates)
                return (false, "기능 업데이트 제외 정책 (2차 승인 대상)");

            if (!os.SupportsFeatureUpdates)
                return (false, $"이 OS는 기능 업데이트를 지원하지 않습니다: {os.SupportSummary}");

            if (!string.IsNullOrWhiteSpace(policy.ApprovedFeatureRelease)
                && !update.Title.Contains(policy.ApprovedFeatureRelease, StringComparison.OrdinalIgnoreCase))
            {
                return (false, $"승인된 릴리스({policy.ApprovedFeatureRelease})가 아닙니다");
            }

            return (true, "기능 업데이트 승인됨");
        }

        if (update.Category == UpdateCategory.Definition)
        {
            return policy.IncludeDefinitionUpdates
                ? (true, "정의 업데이트 포함 정책")
                : (false, "정의 업데이트 제외 정책");
        }

        var selected = update.Category switch
        {
            UpdateCategory.Security => policy.IncludeSecurity,
            UpdateCategory.Critical => policy.IncludeCritical,
            UpdateCategory.Important => policy.IncludeImportant,
            UpdateCategory.Moderate => policy.IncludeOptional,
            UpdateCategory.Low => policy.IncludeOptional,
            UpdateCategory.Unspecified => policy.IncludeOptional,
            UpdateCategory.FeaturePack => policy.IncludeOptional,
            UpdateCategory.ServicePack => policy.IncludeImportant,
            _ => policy.IncludeOptional,
        };

        if (!selected && update.IsMandatory) selected = true;

        return selected
            ? (true, "정책에 포함")
            : (false, $"{update.Category} 등급 제외 정책");
    }

    private static int SelectionOrder(UpdateItemInfo update) => update.Category switch
    {
        UpdateCategory.Security => 0,
        UpdateCategory.Critical => 1,
        UpdateCategory.Important => 2,
        UpdateCategory.ServicePack => 3,
        UpdateCategory.FeaturePack => 4,
        UpdateCategory.Moderate => 5,
        UpdateCategory.Low => 6,
        UpdateCategory.Unspecified => 7,
        UpdateCategory.Other => 8,
        UpdateCategory.Upgrade => 9,
        UpdateCategory.Definition => 10,
        UpdateCategory.Driver => 11,
        _ => 12,
    };

    /// <summary>WUA 검색 결과를 정책에 맞게 필터링한다.</summary>
    public static (IReadOnlyList<WuaUpdate> Selected, IReadOnlyList<UpdateItemInfo> Excluded) Filter(
        IReadOnlyList<WuaUpdate> updates,
        UpdatePolicy policy,
        OsInfo os)
    {
        var selected = new List<WuaUpdate>();
        var excluded = new List<UpdateItemInfo>();

        foreach (var update in updates)
        {
            var (isSelected, reason) = IsSelected(update.Info, policy, os);
            if (isSelected)
            {
                selected.Add(new WuaUpdate
                {
                    Info = update.Info with { Selected = true },
                    Native = update.Native,
                });
            }
            else
            {
                excluded.Add(update.Info with { Selected = false, Description = reason });
            }
        }

        return (selected, excluded);
    }
}
