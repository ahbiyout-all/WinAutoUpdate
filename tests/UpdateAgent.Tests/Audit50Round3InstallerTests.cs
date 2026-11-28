namespace UpdateAgent.Tests;

/// <summary>
/// 3차 50회 자아성찰에서 확인한 인스톨러 정적 회귀 테스트.
/// 실제 토큰 값은 읽지 않고 소스의 안전 장치만 확인한다.
/// </summary>
public sealed class Audit50Round3InstallerTests
{
    [Fact]
    public void AgentInstaller_StopsWhenServiceStatusCannotBeRead()
    {
        var text = Read("installer/InnoSetup/UpdateAgent.Agent.iss");

        Assert.Contains(
            "if not LoadStringsFromFile(StatusFile, Lines) then\n" +
            "    begin\n" +
            "      Result := False;",
            text);
    }

    [Fact]
    public void AgentInstaller_CleansAppsettingsBackupOnFailureAndSuccessPaths()
    {
        var text = Read("installer/InnoSetup/UpdateAgent.Agent.iss");

        var cleanupCalls = Count(text, "FailWithAgentBackupCleanup(");
        // forward + 구현 + 준비 단계 실패 5곳
        Assert.True(cleanupCalls >= 7, $"FailWithAgentBackupCleanup calls: {cleanupCalls}");
        Assert.Contains(
            "if not RemoveAgentConfigBackup(AgentConfigBackup) then\n" +
            "        RaiseException('임시 Agent 설정 백업을 삭제하지 못해 설치를 중단합니다. ' +",
            text);
    }

    [Fact]
    public void AgentInstaller_ExplicitEmptyCommandLineValuesFailClosed()
    {
        var text = Read("installer/InnoSetup/UpdateAgent.Agent.iss");

        Assert.Contains(
            "if CmdParamExists('/LabManagerUrl=') and (Trim(CmdParamValue('/LabManagerUrl=')) = '') then",
            text);
        Assert.Contains(
            "if CmdParamExists('/AgentTokenFile=') and (Trim(CmdParamValue('/AgentTokenFile=')) = '') then",
            text);
    }

    [Fact]
    public void AgentInstaller_CleansTemporaryConfigWithFailClosedHelper()
    {
        var text = Read("installer/InnoSetup/UpdateAgent.Agent.iss");

        var cleanupCalls = Count(text, "RemoveAgentConfigBackup(TempPath)");
        Assert.True(cleanupCalls >= 5, $"TempPath cleanup calls: {cleanupCalls}");
        Assert.Contains("LastAgentBackupCleanupError := '임시 Agent 설정 백업을 삭제하지 못했습니다: '", text);
    }

    [Fact]
    public void LabManagerInstaller_HonorsExternalTokenFileOnFirstInstall()
    {
        var text = Read("installer/InnoSetup/UpdateAgent.LabManager.iss");

        // 첫 설치에서도 외부 토큰 파일을 교체 경로에서 읽는다.
        Assert.Contains("if not ReplaceExistingToken then Exit;", text);
        Assert.Contains("if CmdParamExists(ExternalTokenSwitch) then Exit;", text);
        Assert.Contains(
            "if (not ReplaceExistingToken) or\n" +
            "     (ExternalAdminToken = '') or (ExternalAgentToken = '') then",
            text);
        Assert.DoesNotContain(
            "if FirstInstall or (not ReplaceExistingToken) or",
            text);
    }

    [Fact]
    public void LabManagerInstaller_ExplicitEmptyTokenSwitchFailsClosed()
    {
        var text = Read("installer/InnoSetup/UpdateAgent.LabManager.iss");

        Assert.Contains("function CmdParamExists(const Prefix: String): Boolean;", text);
        Assert.Contains(
            "if CmdParamExists(ExternalTokenSwitch) and\n" +
            "     (Trim(CmdParamValue(ExternalTokenSwitch)) = '') then",
            text);
    }

    [Fact]
    public void InnoInstallers_RetainUtf8BomAndKoreanText()
    {
        foreach (var relative in new[]
        {
            "installer/InnoSetup/UpdateAgent.Agent.iss",
            "installer/InnoSetup/UpdateAgent.LabManager.iss",
        })
        {
            var path = Path.Combine(FindRepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
            var bytes = File.ReadAllBytes(path);
            Assert.True(bytes.Length >= 3);
            Assert.Equal(0xEF, bytes[0]);
            Assert.Equal(0xBB, bytes[1]);
            Assert.Equal(0xBF, bytes[2]);
        }

        Assert.Contains("기존 토큰 처리", Read("installer/InnoSetup/UpdateAgent.Agent.iss"));
        Assert.Contains("기존 토큰 처리", Read("installer/InnoSetup/UpdateAgent.LabManager.iss"));
    }

    [Fact]
    public void InstallerFirewallRulesAreTeacherOnlyFixedPortDomainPrivate()
    {
        var agent = Read("installer/InnoSetup/UpdateAgent.Agent.iss");
        var lab = Read("installer/InnoSetup/UpdateAgent.LabManager.iss");

        Assert.Contains("FirewallGroup = 'UpdateAgent';", agent);
        Assert.DoesNotContain("New-NetFirewallRule", agent);
        Assert.Contains("FirewallGroup = 'UpdateAgent LabManager';", lab);
        Assert.Contains("Result := '5080';", lab);
        Assert.Contains("' -Action Allow -Protocol TCP -LocalPort ' + FirewallPort", lab);
        Assert.Contains("' -Enabled True -Profile Domain,Private -Group ' + PsLiteral(FirewallGroup)", lab);

        foreach (var text in new[] { agent, lab })
        {
            // 주석 설명은 허용하고, 실행 가능한 netsh/group= 사용만 금지한다.
            Assert.DoesNotMatch(@"(?im)^\s*(?!;|[{]).*netsh\s+advfirewall", text);
            Assert.DoesNotMatch(@"(?im)^\s*(?!;|[{]).*netsh.*group\s*=", text);
        }
    }

    [Fact]
    public void UninstallTerminationFailurePrecedesFirewallAndRecoveryChanges()
    {
        var agent = Read("installer/InnoSetup/UpdateAgent.Agent.iss");
        var lab = Read("installer/InnoSetup/UpdateAgent.LabManager.iss");

        foreach (var text in new[] { agent, lab })
        {
            var uninstall = text.Substring(text.IndexOf("procedure CurUninstallStepChanged", StringComparison.Ordinal));
            var raise = uninstall.IndexOf("if not Terminated then", StringComparison.Ordinal);
            var firewall = uninstall.IndexOf("RemoveFirewallRules;", StringComparison.Ordinal);
            Assert.True(raise >= 0, "Missing termination guard in uninstall.");
            Assert.True(firewall > raise, "Firewall cleanup must not run after failed termination.");
        }

        var agentUninstall = agent.Substring(agent.IndexOf("procedure CurUninstallStepChanged", StringComparison.Ordinal));
        var recovery = agentUninstall.IndexOf("if not RestoreServiceRecovery then", StringComparison.Ordinal);
        Assert.True(recovery > agentUninstall.IndexOf("if not Terminated then", StringComparison.Ordinal),
            "Recovery changes must not run after failed termination.");
    }

    [Fact]
    public void InstallerVersionFallbacksMatchPublishedTag()
    {
        Assert.Contains("#define AppVersion \"0.1.47\"", Read("installer/InnoSetup/UpdateAgent.Agent.iss"));
        Assert.Contains("#define AppVersion \"0.1.47\"", Read("installer/InnoSetup/UpdateAgent.LabManager.iss"));
        Assert.Contains(
            "<ProductVersion Condition=\"'$(ProductVersion)' == ''\">0.1.47</ProductVersion>",
            Read("installer/UpdateAgent.Agent.Installer/UpdateAgent.Agent.Installer.wixproj"));
    }

    private static int Count(string text, string pattern)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }

    private static string Read(string relativePath)
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"Missing repository file: {relativePath}");
        return File.ReadAllText(path).Replace("\r\n", "\n");
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
