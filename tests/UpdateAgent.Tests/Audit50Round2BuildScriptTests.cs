using System.Text;

namespace UpdateAgent.Tests;

/// <summary>
/// 2차 50회 자아성찰에서 수정한 인스톨러/빌드 스크립트의 정적 회귀 테스트.
/// 토큰 값은 읽지 않고 소스 코드의 안전 장치만 확인한다.
/// </summary>
public sealed class Audit50Round2BuildScriptTests
{
    [Fact]
    public void AgentInstaller_UsesRawBomCheckAndCorrectScQueryQuoting()
    {
        var text = Read("installer/InnoSetup/UpdateAgent.Agent.iss");

        Assert.Contains("LoadStringFromFile(Path, Raw)", text);
        Assert.Contains("Ord(Raw[1]) = $EF", text);
        Assert.Contains("'/c \"\"' + ExpandConstant('{sys}\\sc.exe')", text);
        Assert.Contains("function RemoveAgentConfigBackup", text);
        Assert.Contains("if FileExists(BackupPath) and (not RemoveAgentConfigBackup(BackupPath))", text);
    }

    [Fact]
    public void AgentInstaller_StopsBeforeChangingFirewallWhenTerminationFails()
    {
        var text = Read("installer/InnoSetup/UpdateAgent.Agent.iss");

        var terminate = text.IndexOf(
            "Terminated := TerminateAgentApps;", StringComparison.Ordinal);
        var raise = text.IndexOf(
            "if not Terminated then\n      RaiseException", StringComparison.Ordinal);
        var remove = text.IndexOf("RemoveFirewallRules;", terminate, StringComparison.Ordinal);

        Assert.True(terminate >= 0);
        Assert.True(raise > terminate);
        Assert.True(remove > raise);
    }

    [Fact]
    public void LabManagerInstaller_HonorsSilentTokenFileAndCleansBackups()
    {
        var text = Read("installer/InnoSetup/UpdateAgent.LabManager.iss");

        Assert.Contains("(Trim(CmdParamValue(ExternalTokenSwitch)) <> '')", text);
        Assert.Contains("function RemoveLabManagerConfigBackup", text);
        Assert.Contains("function FailWithLabManagerBackupCleanup", text);
        Assert.Contains("if not RemoveLabManagerConfigBackup(LabManagerConfigBackup)", text);

        var terminate = text.IndexOf(
            "Terminated := TerminateLabManagerApps;", StringComparison.Ordinal);
        var raise = text.IndexOf(
            "if not Terminated then\n      RaiseException", StringComparison.Ordinal);
        var remove = text.IndexOf("RemoveFirewallRules;", terminate, StringComparison.Ordinal);

        Assert.True(terminate >= 0);
        Assert.True(raise > terminate);
        Assert.True(remove > raise);
    }

    [Fact]
    public void BuildInno_ValidatesSecretsBeforeWritingStagingFiles()
    {
        var text = Read("scripts/build-inno.ps1");

        var validate = text.IndexOf(
            "$agentToken = Assert-BundledTokenValue", StringComparison.Ordinal);
        var writeAgent = text.IndexOf(
            "Write-BundledTokenFile -Path (Join-Path $agentStage", StringComparison.Ordinal);
        var writeLab = text.IndexOf(
            "Write-BundledTokenFile -Path (Join-Path $labManagerStage", StringComparison.Ordinal);

        Assert.True(validate >= 0);
        Assert.True(writeAgent > validate);
        Assert.True(writeLab > validate);
        Assert.Contains("Remove-Item -LiteralPath $StagingRoot -Recurse -Force", text);
        Assert.Contains("if (-not $AgentOnly) {", text);
    }

    [Fact]
    public void BuildMsi_RejectsBothPlaceholderFamilies()
    {
        var text = Read("scripts/build-msi.ps1");

        Assert.Contains("change-me-|sample-[^\\r\\n]*-replace-before-pilot", text);
    }

    [Fact]
    public void TokenFileScripts_ReadOnlyFirstLine()
    {
        foreach (var relative in new[]
        {
            "scripts/configure-labmanager.ps1",
            "scripts/configure-agent.ps1",
            "scripts/install-service.ps1",
            "scripts/install-agent-msi.ps1",
        })
        {
            Assert.Contains("-TotalCount 1", Read(relative));
        }
    }

    [Fact]
    public void InstallAgentMsi_ExplicitEmptyTokenFileFailsClosed()
    {
        var text = Read("scripts/install-agent-msi.ps1");

        Assert.Contains("AgentTokenFile was explicitly empty", text);
    }

    [Fact]
    public void UninstallAgentMsi_Requires64BitPowerShell()
    {
        var text = Read("scripts/uninstall-agent-msi.ps1");

        Assert.Contains("function Assert-64BitProcess", text);
        Assert.Contains("Assert-Administrator\nAssert-64BitProcess", text);
    }

    [Fact]
    public void DeployAgents_VerifyHashesInstallWrapper()
    {
        var text = Read("scripts/deploy-agents.ps1");

        Assert.Contains("CmdName         = 'install-agent.cmd'", text);
        Assert.Contains("install wrapper SHA-256 mismatch", text);
    }

    [Fact]
    public void GithubPush_SecretScanContinuesAfterPerFileCap()
    {
        var text = Read("scripts/github-push.ps1");

        Assert.Contains("findings capped at 50 for this file; scanning continued", text);
        Assert.DoesNotContain("... scan stopped after 50 findings.", text);
    }

    [Fact]
    public void ReleaseWorkflow_MarksTruncatedChangeList()
    {
        var text = Read(".github/workflows/release.yml");

        Assert.Contains("표시되지 않은 변경 {0}건은 이 릴리스 노트에서 확인 불가", text);
    }

    private static string Read(string relativePath)
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"Missing repository file: {relativePath}");
        return File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Repository root not found.");
    }
}
