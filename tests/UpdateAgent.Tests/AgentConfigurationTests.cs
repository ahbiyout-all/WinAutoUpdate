using UpdateAgent.Service.Configuration;
using UpdateAgent.Shared;

namespace UpdateAgent.Tests;

public sealed class AgentConfigurationTests
{
    [Fact]
    public void AgentVersion_ComesFromAssemblyMetadata()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+", UpdateAgentConstants.AgentVersion);
        Assert.DoesNotContain('+', UpdateAgentConstants.AgentVersion);
    }

    [Fact]
    public void ConfigFilePath_IsAgentJsonUnderDataDirectory()
    {
        Assert.Equal(
            Path.Combine(UpdateAgentConstants.DataDirectory, "agent.json"),
            UpdateAgentConstants.ConfigFilePath);
    }

    [Fact]
    public void ResolveSessionHelperPath_UsesExplicitPath()
    {
        var options = new AgentOptions { SessionHelperPath = @"C:\Tools\UpdateAgent.SessionHelper.exe" };

        Assert.Equal(@"C:\Tools\UpdateAgent.SessionHelper.exe", options.ResolveSessionHelperPath());
    }

    [Fact]
    public void ResolveSessionHelperPath_ReturnsFullyQualifiedFallback()
    {
        var options = new AgentOptions();

        var path = options.ResolveSessionHelperPath();

        Assert.Equal("UpdateAgent.SessionHelper.exe", Path.GetFileName(path));
        Assert.True(Path.IsPathFullyQualified(path));
    }
}
