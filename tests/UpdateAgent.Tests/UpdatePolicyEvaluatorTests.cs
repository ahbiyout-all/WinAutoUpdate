using UpdateAgent.Core.Policy;
using UpdateAgent.Core.Windows;
using UpdateAgent.Shared.Models;

namespace UpdateAgent.Tests;

public sealed class UpdatePolicyEvaluatorTests
{
    private static readonly OsInfo Windows11Pro =
        WindowsOsInfoProvider.Create("Windows 11 Pro", "Professional", "25H2", 26200);

    private static UpdateItemInfo Item(
        string id,
        UpdateCategory category,
        bool isDriver = false,
        bool isFeatureUpdate = false,
        bool isMandatory = false,
        string? title = null) => new()
        {
            UpdateId = id,
            Title = title ?? id,
            Category = category,
            CategoryName = category.ToString(),
            IsSecurity = category == UpdateCategory.Security,
            IsDriver = isDriver,
            IsFeatureUpdate = isFeatureUpdate,
            IsMandatory = isMandatory,
        };

    [Fact]
    public void DefaultPolicy_SelectsSecurityCriticalImportant_AndExcludesRest()
    {
        var updates = new[]
        {
            Item("security", UpdateCategory.Security),
            Item("critical", UpdateCategory.Critical),
            Item("important", UpdateCategory.Important),
            Item("optional", UpdateCategory.Moderate),
            Item("definition", UpdateCategory.Definition),
            Item("driver", UpdateCategory.Driver, isDriver: true),
            Item("feature", UpdateCategory.Upgrade, isFeatureUpdate: true),
        };

        var result = UpdatePolicyEvaluator.Evaluate(updates, UpdatePolicy.Default, Windows11Pro);

        Assert.Equal(3, result.Selected.Count);
        Assert.Equal(["security", "critical", "important"], result.Selected.Select(u => u.UpdateId));
        Assert.Equal(4, result.Excluded.Count);
        Assert.All(result.Selected, u => Assert.True(u.Selected));
        Assert.All(result.Excluded, u => Assert.False(u.Selected));
    }

    [Fact]
    public void Evaluate_OrdersSelectedItemsByCategory()
    {
        var updates = new[]
        {
            Item("important", UpdateCategory.Important),
            Item("security", UpdateCategory.Security),
            Item("critical", UpdateCategory.Critical),
        };

        var result = UpdatePolicyEvaluator.Evaluate(updates, UpdatePolicy.Default, Windows11Pro);

        Assert.Equal(["security", "critical", "important"], result.Selected.Select(u => u.UpdateId));
    }

    [Fact]
    public void MandatoryUpdate_IsSelectedEvenWhenCategoryExcluded()
    {
        var updates = new[] { Item("mandatory", UpdateCategory.Moderate, isMandatory: true) };

        var result = UpdatePolicyEvaluator.Evaluate(updates, UpdatePolicy.Default, Windows11Pro);

        Assert.Single(result.Selected);
        Assert.Empty(result.Excluded);
    }

    [Fact]
    public void OptionalPolicy_IncludesModerateAndLow()
    {
        var policy = UpdatePolicy.Default with { IncludeOptional = true };
        var updates = new[]
        {
            Item("moderate", UpdateCategory.Moderate),
            Item("low", UpdateCategory.Low),
            Item("unspecified", UpdateCategory.Unspecified),
        };

        var result = UpdatePolicyEvaluator.Evaluate(updates, policy, Windows11Pro);

        Assert.Equal(3, result.Selected.Count);
    }

    [Fact]
    public void Drivers_AreIncludedOnlyWhenPolicyAllows()
    {
        var driver = Item("driver", UpdateCategory.Driver, isDriver: true);

        Assert.False(UpdatePolicyEvaluator.IsSelected(driver, UpdatePolicy.Default, Windows11Pro).Selected);

        var allowed = UpdatePolicy.Default with { IncludeDrivers = true };
        Assert.True(UpdatePolicyEvaluator.IsSelected(driver, allowed, Windows11Pro).Selected);
    }

    [Fact]
    public void DefinitionUpdates_AreIncludedOnlyWhenPolicyAllows()
    {
        var definition = Item("definition", UpdateCategory.Definition);

        Assert.False(UpdatePolicyEvaluator.IsSelected(definition, UpdatePolicy.Default, Windows11Pro).Selected);

        var allowed = UpdatePolicy.Default with { IncludeDefinitionUpdates = true };
        Assert.True(UpdatePolicyEvaluator.IsSelected(definition, allowed, Windows11Pro).Selected);
    }

    [Fact]
    public void FeatureUpdate_RequiresOptIn()
    {
        var feature = Item("feature", UpdateCategory.Upgrade, isFeatureUpdate: true);

        var result = UpdatePolicyEvaluator.IsSelected(feature, UpdatePolicy.Default, Windows11Pro);

        Assert.False(result.Selected);
        Assert.Contains("기능 업데이트", result.Reason);
    }

    [Fact]
    public void FeatureUpdate_IsExcludedOnUnsupportedOs()
    {
        var ltsc = WindowsOsInfoProvider.Create(
            "Windows 11 Enterprise LTSC", "EnterpriseS", "24H2", 26100);
        var feature = Item("feature", UpdateCategory.Upgrade, isFeatureUpdate: true);
        var policy = UpdatePolicy.Default with { IncludeFeatureUpdates = true };

        var result = UpdatePolicyEvaluator.IsSelected(feature, policy, ltsc);

        Assert.False(result.Selected);
        Assert.Contains("지원하지 않습니다", result.Reason);
    }

    [Fact]
    public void FeatureUpdate_RequiresApprovedReleaseMatch()
    {
        var feature = Item("feature", UpdateCategory.Upgrade, isFeatureUpdate: true, title: "Windows 11, version 26H2");
        var policy = UpdatePolicy.Default with
        {
            IncludeFeatureUpdates = true,
            ApprovedFeatureRelease = "26H2",
        };

        Assert.True(UpdatePolicyEvaluator.IsSelected(feature, policy, Windows11Pro).Selected);

        var wrongRelease = UpdatePolicy.Default with
        {
            IncludeFeatureUpdates = true,
            ApprovedFeatureRelease = "27H2",
        };
        Assert.False(UpdatePolicyEvaluator.IsSelected(feature, wrongRelease, Windows11Pro).Selected);
    }

    [Fact]
    public void ServicePack_FollowsImportantPolicy()
    {
        var servicePack = Item("sp", UpdateCategory.ServicePack);

        Assert.True(UpdatePolicyEvaluator.IsSelected(servicePack, UpdatePolicy.Default, Windows11Pro).Selected);

        var noImportant = UpdatePolicy.Default with { IncludeImportant = false };
        Assert.False(UpdatePolicyEvaluator.IsSelected(servicePack, noImportant, Windows11Pro).Selected);
    }
}
