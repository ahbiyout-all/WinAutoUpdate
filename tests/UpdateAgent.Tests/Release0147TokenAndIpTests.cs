using System.Text;

namespace UpdateAgent.Tests;

/// <summary>
/// v0.1.47 regressions: embedded fixed tokens and IP-based deployment with opt-in discovery.
/// No token values are read or recorded by these tests.
/// </summary>
public sealed class Release0147TokenAndIpTests
{
    [Fact]
    public void DeployAgents_DiscoveryIsOptInAndFailClosed()
    {
        var text = Read("scripts/deploy-agents.ps1");

        Assert.Contains("[switch]$DiscoverLabManager", text);
        Assert.Contains("function Get-SubnetScanAddresses", text);
        Assert.Contains("function Test-TcpPortOpen", text);
        Assert.Contains("function Find-LabManagerAddresses", text);
        Assert.Contains("LabManager discovery found no matching host on TCP 5080", text);
        Assert.Contains("LabManager discovery found {0} candidates", text);
        Assert.Contains("Explicit -LabManagerUrl supplied; skipping discovery.", text);
        Assert.Contains("DiscoveryMode       = $discoveryMode", text);
        Assert.DoesNotContain("-DiscoverLabManager", Read("scripts/build-inno.ps1"));
    }

    [Fact]
    public void DeployAgents_DiscoveryValidatesHealthAndWritesIpIni()
    {
        var text = Read("scripts/deploy-agents.ps1");

        Assert.Contains("Test-TcpPortOpen -Addresses $addresses -Port 5080 -TimeoutMs 400", text);
        Assert.Contains("/api/health", text);
        Assert.Contains("[string]$statusProperty.Value -eq 'ok'", text);
        Assert.Contains("[string]$productProperty.Value -eq 'UpdateAgent'", text);
        Assert.Contains("versionProperty", text);
        Assert.Contains("$LabManagerUrl = \"http://$($found[0]):5080\"", text);
        Assert.Contains("LabManagerUrl=__URL__", text);
        Assert.Contains("agent-install.ini uses an IP-based LabManager URL", text);
        Assert.Contains("Hashes and metadata only: no token value is ever written to this report.", text);
    }

    [Fact]
    public void Docs_DescribeIpDeploymentAndAutoDiscovery()
    {
        var deployment = Read("docs/DEPLOYMENT.md");

        Assert.Contains("-DiscoverLabManager", deployment);
        Assert.Contains("http://<교사-PC-IP>:5080", deployment);
        Assert.Contains("0대거나 2대 이상이면 수동 지정", deployment);
        Assert.Contains("명시한 URL은 항상 자동 검색보다 우선합니다.", deployment);
        Assert.Contains("DiscoveryMode", deployment);

        Assert.Contains("v0.1.47 — 지정 토큰 내장 + IP 기반 배포", Read("docs/WORKLOG.md"));
    }

    [Fact]
    public void Version_IsRaisedTo0147Everywhere()
    {
        Assert.Contains("<Version>0.1.47</Version>", Read("Directory.Build.props"));
        foreach (var relative in new[]
        {
            "installer/InnoSetup/UpdateAgent.Agent.iss",
            "installer/InnoSetup/UpdateAgent.LabManager.iss",
        })
        {
            Assert.Contains("#define AppVersion \"0.1.47\"", Read(relative));
        }
        Assert.Contains(
            "<ProductVersion Condition=\"'$(ProductVersion)' == ''\">0.1.47</ProductVersion>",
            Read("installer/UpdateAgent.Agent.Installer/UpdateAgent.Agent.Installer.wixproj"));
    }

    private static string Read(string relativePath)
    {
        var path = Path.Combine(FindRepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"Missing repository file: {relativePath}");
        return File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Repository root not found.");
    }
}
