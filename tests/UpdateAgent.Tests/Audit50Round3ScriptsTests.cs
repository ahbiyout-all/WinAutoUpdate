using System.Text;
using System.Text.RegularExpressions;

namespace UpdateAgent.Tests;

/// <summary>
/// Third 50-round audit: static regressions for scripts, release workflow and ignore rules.
/// No secret values are read or recorded by these tests.
/// </summary>
public sealed class Audit50Round3ScriptsTests
{
    [Fact]
    public void TokenFileReaders_UseOnlyTheFirstLine()
    {
        foreach (var relative in new[]
        {
            "scripts/configure-agent.ps1",
            "scripts/configure-labmanager.ps1",
            "scripts/deploy-agents.ps1",
            "scripts/install-agent-msi.ps1",
            "scripts/install-service.ps1",
            "scripts/setup-labmanager-lab.ps1",
        })
        {
            Assert.Contains("-TotalCount 1", Read(relative));
        }

        var msiScript = Read("scripts/install-agent-msi.ps1");
        Assert.DoesNotContain("Get-Content \".\\agent-token.txt\" -Raw", msiScript);
    }

    [Fact]
    public void ExplicitEmptyTokenFiles_FailClosed()
    {
        Assert.Contains("AgentTokenFile was explicitly empty", Read("scripts/install-agent-msi.ps1"));
        Assert.Contains("AgentTokenFile was supplied but is empty", Read("scripts/configure-agent.ps1"));
        Assert.Contains("Agent token is required.", Read("scripts/install-service.ps1"));
        Assert.Contains("Agent token file is empty", Read("scripts/deploy-agents.ps1"));
        Assert.Contains("token file is empty", Read("scripts/setup-labmanager-lab.ps1"));
    }

    [Fact]
    public void TokenValues_AreNotPassedOnInstallOrBuildCommandLines()
    {
        var msi = Read("scripts/install-agent-msi.ps1");
        var msiArgsStart = msi.IndexOf("$msiArguments = @(", StringComparison.Ordinal);
        var msiArgsEnd = msi.IndexOf(
            "$process = Start-Process -FilePath \"msiexec.exe\"", msiArgsStart, StringComparison.Ordinal);
        Assert.True(msiArgsStart >= 0 && msiArgsEnd > msiArgsStart);
        var msiArgs = msi.Substring(msiArgsStart, msiArgsEnd - msiArgsStart);
        Assert.DoesNotContain("$resolvedToken", msiArgs);
        Assert.DoesNotContain("$AgentToken", msiArgs);

        var deploy = Read("scripts/deploy-agents.ps1");
        var setupInvocation = deploy.Split('\n')
            .Single(line => line.Contains("\"%SETUP%\" /VERYSILENT", StringComparison.Ordinal));
        Assert.DoesNotContain("%TOKEN%", setupInvocation);
        Assert.Contains("set \"UPGRADE_FLAG=/AgentTokenFile=\"", deploy);

        var setup = Read("scripts/setup-labmanager-lab.ps1");
        Assert.Contains("-AdminTokenFile ([IO.Path]::GetFullPath($AdminTokenFile))", setup);
        Assert.Contains("-AgentTokenFile ([IO.Path]::GetFullPath($AgentTokenFile))", setup);
        Assert.DoesNotContain("-AdminToken $adminToken", setup);

        var inno = Read("scripts/build-inno.ps1");
        foreach (var line in inno.Split('\n')
                     .Where(line => line.TrimStart().StartsWith("& $iscc", StringComparison.Ordinal)))
        {
            Assert.DoesNotContain("Token", line);
        }
    }

    [Fact]
    public void ReleaseWorkflow_UsesOnlyExpectedGitHubSecrets()
    {
        var workflow = Read(".github/workflows/release.yml");
        var referenced = Regex.Matches(workflow, @"\$\{\{\s*secrets\.([A-Z0-9_]+)\s*\}\}")
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[]
        {
            "GITHUB_TOKEN",
            "UPDATEAGENT_AGENT_TOKEN",
            "UPDATEAGENT_LABMANAGER_ADMIN_TOKEN",
            "UPDATEAGENT_LABMANAGER_AGENT_TOKEN",
        }.OrderBy(name => name, StringComparer.Ordinal), referenced);

        Assert.Contains("UPDATEAGENT_AGENT_TOKEN: ${{ secrets.UPDATEAGENT_AGENT_TOKEN }}", workflow);
        Assert.Contains("UPDATEAGENT_LABMANAGER_ADMIN_TOKEN: ${{ secrets.UPDATEAGENT_LABMANAGER_ADMIN_TOKEN }}", workflow);
        Assert.Contains("UPDATEAGENT_LABMANAGER_AGENT_TOKEN: ${{ secrets.UPDATEAGENT_LABMANAGER_AGENT_TOKEN }}", workflow);
    }

    [Fact]
    public void Placeholders_FailClosedInBuildAndConfigureScripts()
    {
        Assert.Contains(@"change-me-|sample-[^\r\n]*-replace-before-pilot", Read("scripts/build-msi.ps1"));

        var inno = Read("scripts/build-inno.ps1");
        Assert.Contains("$lower.StartsWith('change-me-')", inno);
        Assert.Contains("$lower.StartsWith('sample-')", inno);
        Assert.Contains("$lower.EndsWith('-replace-before-pilot')", inno);
        Assert.Contains("is still a placeholder", inno);

        var install = Read("scripts/install-service.ps1");
        Assert.Contains("$lowerAgentToken.StartsWith('change-me-')", install);
        Assert.Contains("$lowerAgentToken.EndsWith('-replace-before-pilot')", install);

        var setup = Read("scripts/setup-labmanager-lab.ps1");
        Assert.Contains("'change-me-admin-token', 'change-me-agent-token'", setup);
        Assert.Contains("'sample-admin-token-replace-before-pilot', 'sample-agent-token-replace-before-pilot'", setup);
    }

    [Fact]
    public void BuildInno_ValidatesSecretsBeforeWritingTokenFilesAndCleansStaging()
    {
        var text = Read("scripts/build-inno.ps1");
        var validate = text.IndexOf("$agentToken = Assert-BundledTokenValue", StringComparison.Ordinal);
        var writeAgent = text.IndexOf(
            "Write-BundledTokenFile -Path (Join-Path $agentStage", validate, StringComparison.Ordinal);
        var writeLab = text.IndexOf(
            "Write-BundledTokenFile -Path (Join-Path $labManagerStage", validate, StringComparison.Ordinal);

        Assert.True(validate >= 0);
        Assert.True(writeAgent > validate);
        Assert.True(writeLab > validate);
        Assert.Contains("trap {", text);
        Assert.Contains("Remove-Item -LiteralPath $StagingRoot -Recurse -Force", text);
    }

    [Fact]
    public void DeployAgents_VerifyHashesTheInstallWrapper()
    {
        var text = Read("scripts/deploy-agents.ps1");

        Assert.Contains("CmdName         = 'install-agent.cmd'", text);
        Assert.Contains("CmdSha256       = Get-FileSha256", text);
        Assert.Contains("install wrapper SHA-256 mismatch", text);
    }

    [Fact]
    public void GithubPush_SecretScanUsesPerFileCapAndContinuesToLaterFiles()
    {
        var text = ReadOptional("scripts/github-push.ps1");
        if (text is null)
        {
            // This helper is intentionally excluded from CI checkouts.
            Assert.Contains("scripts/github-push.ps1", Read(".gitignore"));
            Assert.Contains("scripts/github-push.bat", Read(".gitignore"));
            return;
        }

        Assert.Contains("$fileFindingCount = 0", text);
        Assert.Contains("$capReported = $false", text);
        Assert.Contains("if ($fileFindingCount -ge 50) { continue }", text);
        Assert.Contains("if ($fileFindingCount -ge 50 -and -not $capReported)", text);
        Assert.DoesNotContain("if ($findings.Count -ge 50)", text);
        Assert.Contains("findings capped at 50 for this file; scanning continued", text);
    }

    [Fact]
    public void ReleaseWorkflow_ChecksVersionTruncationAndInstallerOnlyAssets()
    {
        var text = Read(".github/workflows/release.yml");

        Assert.Contains("if ($env:GITHUB_REF_NAME -cne $tag)", text);
        Assert.Contains("if ($info.productVersion -ne $version)", text);
        Assert.Contains("표시되지 않은 변경 {0}건은 이 릴리스 노트에서 확인 불가", text);

        Assert.Contains("UpdateAgent.Agent-Setup-$version-x64.exe", text);
        Assert.Contains("UpdateAgent.LabManager-Setup-$version-x64.exe", text);
        Assert.Contains("UpdateAgent.Agent-$version-x64.msi", text);
        Assert.Contains("$assets = @($innoAssets + $msiAssets)", text);
        Assert.Contains("if ($extension -notin @('.exe', '.msi'))", text);
        Assert.Contains("$expectedNames = @($expectedInnoNames + $expectedMsiName)", text);
        Assert.Contains("gh release upload $tag @assets --clobber", text);

        var notesStart = text.IndexOf("Prepare release notes", StringComparison.Ordinal);
        var notesEnd = text.IndexOf("Create or update GitHub release", notesStart, StringComparison.Ordinal);
        Assert.True(notesStart >= 0 && notesEnd > notesStart);
        var notesStep = text.Substring(notesStart, notesEnd - notesStart);
        Assert.DoesNotContain("$lines.Add($env:", notesStep);
        Assert.DoesNotContain("$lines.Add(('{0}' -f $env:", notesStep);
    }

    [Fact]
    public void ScopedScripts_AvoidPowerShell7OnlySyntax()
    {
        foreach (var relative in ScriptNames())
        {
            var text = Read(relative);
            Assert.DoesNotContain("??", text);
            Assert.DoesNotContain("-AsHashtable", text);
            Assert.DoesNotContain("ForEach-Object -Parallel", text);
            Assert.DoesNotContain("::new()", text);
        }
    }

    [Fact]
    public void ScriptCommentBasedHelp_IsEnglish()
    {
        var hangul = new Regex(@"[\uAC00-\uD7A3]");
        foreach (var relative in ScriptNames())
        {
            var text = Read(relative);
            var helpEnd = text.IndexOf("#>", StringComparison.Ordinal);
            Assert.True(helpEnd > 0, $"Missing comment-based help: {relative}");
            Assert.False(hangul.IsMatch(text.Substring(0, helpEnd)), $"Non-English help: {relative}");
        }
    }

    [Fact]
    public void FirewallRules_UseNoNetshGroupAndStayTeacherOnly()
    {
        var allScripts = string.Join("\n", ScriptNames().Select(Read));
        Assert.False(Regex.IsMatch(allScripts, @"netsh[^\r\n]*group\s*=", RegexOptions.IgnoreCase));

        var allow = Read("scripts/allow-labmanager.ps1");
        Assert.Contains("on the teacher PC", allow);
        Assert.Contains("[int]$Port = 5080", allow);
        Assert.Contains("-Direction Inbound", allow);
        Assert.Contains("-Action Allow", allow);
        Assert.Contains("-Protocol TCP", allow);
        Assert.Contains("-LocalPort $Port", allow);
        Assert.Contains("-Profile Private, Domain", allow);
        Assert.DoesNotContain("-Profile Public", allow);
    }

    [Fact]
    public void SampleIni_HasNoBomAndKeepsTokensOutOfTheIni()
    {
        var path = Path.Combine(FindRepoRoot(), "scripts", "agent-install.sample.ini");
        var bytes = File.ReadAllBytes(path);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);

        var text = Encoding.UTF8.GetString(bytes);
        Assert.Contains("LabManagerUrl=http://<교사-PC-IP>:5080", text);
        Assert.Contains("-DiscoverLabManager (TCP 5080 health check)", text);
        Assert.Contains("AgentTokenFile=agent-token.txt", text);
        Assert.Contains("Never put the token value here", text);
        Assert.Contains("first line is the shared agent token", text);
    }

    [Fact]
    public void GitIgnore_ExcludesLocalPushHelpersAfterReincludingScripts()
    {
        var text = Read(".gitignore");
        var reincluded = text.IndexOf("!scripts/*.ps1", StringComparison.Ordinal);
        Assert.True(reincluded >= 0);
        Assert.True(text.LastIndexOf("scripts/github-push.ps1", StringComparison.Ordinal) > reincluded);
        Assert.True(text.LastIndexOf("scripts/github-push.bat", StringComparison.Ordinal) > reincluded);
        Assert.Contains("!.github/workflows/*.yml", text);
        Assert.Contains("!scripts/*.sample.ini", text);
    }

    private static IEnumerable<string> ScriptNames() => new[]
    {
        "scripts/allow-labmanager.ps1",
        "scripts/build-inno.ps1",
        "scripts/build-msi.ps1",
        "scripts/configure-agent.ps1",
        "scripts/configure-labmanager.ps1",
        "scripts/deploy-agents.ps1",
        "scripts/install-agent-msi.ps1",
        "scripts/install-service.ps1",
        "scripts/publish.ps1",
        "scripts/setup-labmanager-lab.ps1",
        "scripts/uninstall-agent-msi.ps1",
        "scripts/uninstall-service.ps1",
    };

    private static string Read(string relativePath)
    {
        var path = Path.Combine(FindRepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"Missing repository file: {relativePath}");
        return File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n");
    }

    private static string? ReadOptional(string relativePath)
    {
        var path = Path.Combine(FindRepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n") : null;
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
