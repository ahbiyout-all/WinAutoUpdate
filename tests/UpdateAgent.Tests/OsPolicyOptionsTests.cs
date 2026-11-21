using UpdateAgent.Core.Windows;
using UpdateAgent.Shared.Models;

namespace UpdateAgent.Tests;

public sealed class OsPolicyOptionsTests
{
    [Theory]
    [InlineData("windows-11", "Windows 11 Pro", "Professional", "25H2", 26200, "Client")]
    [InlineData("windows-10", "Windows 10 Pro", "Professional", "22H2", 19045, "Client")]
    [InlineData("ltsc", "Windows 11 IoT Enterprise LTSC", "IoTEnterpriseS", "24H2", 26100, "Client")]
    [InlineData("server", "Windows Server 2019 Standard", "ServerStandard", "1809", 17763, "Server")]
    [InlineData("server-core", "Windows Server 2022 Standard", "ServerStandard", "21H2", 20348, "Server Core")]
    public void OsGroup_ClassifiesWindowsKinds(
        string expectedGroup,
        string productName,
        string editionId,
        string displayVersion,
        int build,
        string installationType)
    {
        var os = WindowsOsInfoProvider.Create(
            productName, editionId, displayVersion, build, installationType: installationType);

        Assert.Equal(expectedGroup, os.GroupId);
        Assert.False(string.IsNullOrWhiteSpace(os.GroupLabel));
    }

    [Fact]
    public void ServerShortName_UsesBuildBasedRelease()
    {
        var os = WindowsOsInfoProvider.Create(
            "Windows Server 2022 Standard", "ServerStandard", "21H2", 20348,
            installationType: "Server");

        Assert.Equal("2022", os.ReleaseLabel);
        Assert.Equal("Windows Server 2022", os.ShortName);
    }

    [Fact]
    public void SanitizeFor_RemovesFeatureUpdatesOnUnsupportedOs()
    {
        var windows10 = WindowsOsInfoProvider.Create("Windows 10 Pro", "Professional", "22H2", 19045);
        var server = WindowsOsInfoProvider.Create(
            "Windows Server 2025 Standard", "ServerStandard", "24H2", 26100,
            installationType: "Server");
        var policy = UpdatePolicy.Default with
        {
            IncludeFeatureUpdates = true,
            ApprovedFeatureRelease = "26H2",
        };

        var windows10Policy = policy.SanitizeFor(windows10);
        var serverPolicy = policy.SanitizeFor(server);

        Assert.False(windows10Policy.IncludeFeatureUpdates);
        Assert.Null(windows10Policy.ApprovedFeatureRelease);
        Assert.False(serverPolicy.IncludeFeatureUpdates);
        Assert.Null(serverPolicy.ApprovedFeatureRelease);
        Assert.Contains("기능 업데이트", policy.SanitizationNotes(windows10).Single());
    }

    [Fact]
    public void SanitizeFor_DisablesOverlayOnServerCore()
    {
        var core = WindowsOsInfoProvider.Create(
            "Windows Server 2025 Standard", "ServerStandard", "24H2", 26100,
            installationType: "Server Core");
        var windows11 = WindowsOsInfoProvider.Create("Windows 11 Pro", "Professional", "25H2", 26200);
        var policy = UpdatePolicy.Default with { OverlayEnabled = true };

        Assert.False(policy.SanitizeFor(core).OverlayEnabled);
        Assert.True(policy.SanitizeFor(windows11).OverlayEnabled);
        Assert.Contains("오버레이", policy.SanitizationNotes(core).Single());
    }

    [Fact]
    public void SanitizeFor_KeepsSupportedOptionsOnWindows11()
    {
        var windows11 = WindowsOsInfoProvider.Create("Windows 11 Pro", "Professional", "25H2", 26200);
        var policy = UpdatePolicy.Default with
        {
            IncludeOptional = true,
            IncludeDefinitionUpdates = true,
            OverlayEnabled = true,
        };

        Assert.Equal(policy, policy.SanitizeFor(windows11));
        Assert.Empty(policy.SanitizationNotes(windows11));
    }

    [Fact]
    public void Presets_ExposeDistinctOptionSets()
    {
        var standard = UpdatePolicyPresets.Get(UpdatePolicyPresets.Standard);
        var securityOnly = UpdatePolicyPresets.Get(UpdatePolicyPresets.SecurityOnly);
        var serverSafe = UpdatePolicyPresets.Get(UpdatePolicyPresets.ServerSafe);
        var full = UpdatePolicyPresets.Get(UpdatePolicyPresets.Full);

        Assert.NotNull(standard);
        Assert.NotNull(securityOnly);
        Assert.NotNull(serverSafe);
        Assert.NotNull(full);
        Assert.True(standard!.IncludeImportant);
        Assert.False(securityOnly!.IncludeImportant);
        Assert.True(serverSafe!.IncludeDefinitionUpdates);
        Assert.True(serverSafe.RebootPolicy == RebootPolicy.Never);
        Assert.False(serverSafe.OverlayEnabled);
        Assert.True(full!.IncludeOptional);
        Assert.True(full.IncludeDrivers);
        Assert.All([standard!, securityOnly!, serverSafe!, full!], p => Assert.False(p.IncludeFeatureUpdates));
    }

    [Fact]
    public void Presets_AreSanitizedForSelectedOs()
    {
        var core = WindowsOsInfoProvider.Create(
            "Windows Server 2025 Standard", "ServerStandard", "24H2", 26100,
            installationType: "Server Core");

        var preset = UpdatePolicyPresets.Get(UpdatePolicyPresets.Full, core);

        Assert.NotNull(preset);
        Assert.False(preset!.OverlayEnabled);
        Assert.False(preset.IncludeFeatureUpdates);
    }
}
