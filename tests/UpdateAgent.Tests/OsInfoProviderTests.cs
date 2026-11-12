using UpdateAgent.Core.Windows;
using UpdateAgent.Shared.Models;

namespace UpdateAgent.Tests;

public sealed class OsInfoProviderTests
{
    [Fact]
    public void RegistryProductName_IsNormalizedToWindows11_OnNewBuilds()
    {
        // Windows 11에서도 ProductName이 "Windows 10 ..."으로 남는 사례를 재현한다.
        var os = WindowsOsInfoProvider.Create(
            "Windows 10 Education", "Education", "25H2", 26200, revision: 1234);

        Assert.Equal("Windows 11 Education", os.ProductName);
        Assert.Equal(26200, os.Build);
        Assert.Equal(1234, os.Revision);
        Assert.False(os.IsWindows10);
        Assert.False(os.IsServer);
        Assert.True(os.SupportsFeatureUpdates);
        Assert.True(os.SupportsOverlay);
    }

    [Fact]
    public void Windows10_22H2_IsDetected_AndFeatureUpdatesAreExcluded()
    {
        var os = WindowsOsInfoProvider.Create(
            "Windows 10 Pro", "Professional", "22H2", 19045);

        Assert.Equal("Windows 10 Pro", os.ProductName);
        Assert.True(os.IsWindows10);
        Assert.False(os.SupportsFeatureUpdates);
        Assert.True(os.SupportsOverlay);
    }

    [Theory]
    [InlineData("EnterpriseS", "Windows 11 Enterprise LTSC")]
    [InlineData("IoTEnterpriseS", "Windows 11 IoT Enterprise LTSC")]
    public void LtscEditions_AreDetected(string editionId, string productName)
    {
        var os = WindowsOsInfoProvider.Create(productName, editionId, "24H2", 26100);

        Assert.True(os.IsLtsc);
        Assert.False(os.SupportsFeatureUpdates);
    }

    [Fact]
    public void ServerEdition_IsDetected()
    {
        var os = WindowsOsInfoProvider.Create(
            "Windows Server 2025 Standard", "ServerStandard", "24H2", 26100,
            installationType: "Server");

        Assert.True(os.IsServer);
        Assert.False(os.IsServerCore);
        Assert.False(os.SupportsFeatureUpdates);
        Assert.True(os.SupportsOverlay);
    }

    [Fact]
    public void ServerCore_IsHeadless()
    {
        var os = WindowsOsInfoProvider.Create(
            "Windows Server 2025 Standard", "ServerStandard", "24H2", 26100,
            installationType: "Server Core");

        Assert.True(os.IsServer);
        Assert.True(os.IsServerCore);
        Assert.False(os.SupportsOverlay);
        Assert.Contains("Server Core", os.SupportSummary);
    }

    [Fact]
    public void DisplayVersion_IsIncludedInReadableSummary()
    {
        var os = WindowsOsInfoProvider.Create(
            "Windows 11 Pro", "Professional", "25H2", 26200, revision: 1);

        Assert.Equal("25H2 (build 26200.1)", os.VersionString);
        Assert.Equal("Windows 11 Pro 25H2", os.ShortName);
    }
}
