using System.Text;
using System.Text.RegularExpressions;

namespace UpdateAgent.Tests;

/// <summary>
/// 개선 8(설치 패키지 경량화/Framework-dependent 옵션)의 회귀 테스트.
/// 기본 self-contained 동작, 옵트인 스위치 배선, 레이아웃 판별 임계값,
/// fail-closed 안내, 비밀값 배제, BOM/문서 정합성을 소스 텍스트로 검증한다.
/// </summary>
public sealed class Improvement08PackageSlimmingTests
{
    private const string Publish = "scripts/publish.ps1";
    private const string BuildInno = "scripts/build-inno.ps1";
    private const string BuildMsi = "scripts/build-msi.ps1";
    private const string Deploy = "scripts/deploy-agents.ps1";
    private const string LayoutHelper = "scripts/package-layout.ps1";

    [Fact]
    public void DefaultBuildRemainsSelfContained()
    {
        var publish = Read(Publish);
        Assert.Contains("[switch]$FrameworkDependent", publish, StringComparison.Ordinal);
        Assert.Contains(
            "$selfContained = if ($FrameworkDependent) { \"false\" } else { \"true\" }",
            publish, StringComparison.Ordinal);
        Assert.DoesNotContain("[switch]$FrameworkDependent = $true", publish, StringComparison.Ordinal);

        foreach (var relative in new[] { BuildInno, BuildMsi })
        {
            var text = Read(relative);
            Assert.Contains("$selfContained = -not $FrameworkDependent", text, StringComparison.Ordinal);
            Assert.Contains("selfContained       = $selfContained", text, StringComparison.Ordinal);
            Assert.DoesNotContain("selfContained   = $true", text, StringComparison.Ordinal);
            Assert.DoesNotContain("selfContained     = $true", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void FrameworkDependentSwitch_IsWiredThroughAllScripts()
    {
        foreach (var relative in new[] { Publish, BuildInno, BuildMsi, Deploy })
        {
            Assert.Contains("[switch]$FrameworkDependent", Read(relative), StringComparison.Ordinal);
        }

        foreach (var relative in new[] { Publish, BuildInno, BuildMsi })
        {
            Assert.Contains(
                ". (Join-Path $PSScriptRoot \"package-layout.ps1\")",
                Read(relative), StringComparison.Ordinal);
        }

        var inno = Read(BuildInno);
        Assert.Contains("$publishArgs += '-FrameworkDependent'", inno, StringComparison.Ordinal);
        Assert.Contains("Assert-PackageLayout -Path $source", inno, StringComparison.Ordinal);
        Assert.Contains("Assert-FrameworkDependentRuntimeFiles -Root $agentStage", inno, StringComparison.Ordinal);

        var msi = Read(BuildMsi);
        Assert.Contains("Assert-PackageLayout -Path $source", msi, StringComparison.Ordinal);
        Assert.Contains("Assert-FrameworkDependentRuntimeFiles -Root $StagingRoot", msi, StringComparison.Ordinal);
        // The metadata alignment must not break the existing signing assertions.
        Assert.Contains("signed            = [bool]$Sign", msi, StringComparison.Ordinal);

        // Comment-based help stays English even though runtime output is Korean.
        var hangul = new Regex(@"[\uAC00-\uD7A3]");
        foreach (var relative in new[] { Publish, BuildInno, BuildMsi, Deploy })
        {
            var text = Read(relative);
            var helpEnd = text.IndexOf("#>", StringComparison.Ordinal);
            Assert.True(helpEnd > 0, $"Missing comment-based help: {relative}");
            Assert.False(hangul.IsMatch(text[..helpEnd]), $"Non-English help: {relative}");
        }

        var deploy = Read(Deploy);
        Assert.Contains("Get-InstallerRuntimeMode -InstallerPath $installer.FullName", deploy, StringComparison.Ordinal);
        Assert.Contains("RuntimeMode         = $installerRuntimeMode", deploy, StringComparison.Ordinal);
        Assert.Contains("pass -FrameworkDependent to scripts\\deploy-agents.ps1", Read(Publish), StringComparison.Ordinal);
    }

    [Fact]
    public void LayoutDetection_UsesRuntimeConfigMarkersAndThresholds()
    {
        var helper = Read(LayoutHelper);
        foreach (var marker in new[]
                 {
                     "hostfxr.dll", "hostpolicy.dll", "coreclr.dll", "clrjit.dll",
                     "System.Private.CoreLib.dll",
                 })
        {
            Assert.Contains(marker, helper, StringComparison.Ordinal);
        }

        Assert.Contains("includedFrameworks", helper, StringComparison.Ordinal);
        Assert.Contains("$hasIncluded -eq $hasFramework", helper, StringComparison.Ordinal);
        Assert.Contains("$markerCount -eq 0", helper, StringComparison.Ordinal);
        Assert.Contains("-ge $script:SelfContainedMinAssemblies", helper, StringComparison.Ordinal);
        Assert.Contains("-lt $script:FrameworkDependentMaxAssemblies", helper, StringComparison.Ordinal);

        var frameworkDependentMax = ReadThreshold(helper, "$script:FrameworkDependentMaxAssemblies");
        var selfContainedMin = ReadThreshold(helper, "$script:SelfContainedMinAssemblies");
        Assert.Equal(80, frameworkDependentMax);
        Assert.Equal(80, selfContainedMin);

        // The script uses the same exclusive/inclusive boundary for the two modes.
        Assert.Equal("framework-dependent", Classify(frameworkDependentMax - 1, 0, true));
        Assert.Equal("unknown", Classify(frameworkDependentMax, 0, true));
        Assert.Equal("self-contained", Classify(selfContainedMin, 5, false));
        Assert.Equal("unknown", Classify(selfContainedMin - 1, 5, false));
        Assert.Equal("unknown", Classify(10, 1, true));
    }

    [Fact]
    public void MismatchAndUnknown_FailClosedWithRuntimeGuidance()
    {
        var helper = Read(LayoutHelper);
        foreach (var message in new[]
                 {
                     "Runtime mode mismatch", "expected framework-dependent", "expected self-contained",
                     "Microsoft .NET Desktop Runtime (win-x64)", "Re-run scripts\\publish.ps1 -FrameworkDependent",
                     "Re-run scripts\\publish.ps1 without -FrameworkDependent",
                     "unknown (runtime config state=",
                 })
        {
            Assert.Contains(message, helper, StringComparison.Ordinal);
        }

        // Both errors must fill placeholders before throwing; a bare throw of a
        // concatenated string leaves '{0}' behind in Windows PowerShell 5.1.
        Assert.Equal(3, Regex.Matches(helper, @"throw \$message").Count);
        Assert.DoesNotContain("throw (\"[{0}]", helper, StringComparison.Ordinal);

        var deploy = Read(Deploy);
        Assert.Contains("Cannot verify a framework-dependent installer", deploy, StringComparison.Ordinal);
        Assert.Contains("A framework-dependent installer was selected", deploy, StringComparison.Ordinal);
        Assert.Contains("if ($runtimeModeText -eq 'unknown') { $runtimeModeText = '확인 불가' }", deploy, StringComparison.Ordinal);
        Assert.Contains("default { '확인 불가' }", deploy, StringComparison.Ordinal);
    }

    [Fact]
    public void NoTokensOrSecrets_InNewHelpStagingAndReports()
    {
        foreach (var relative in new[] { BuildInno, BuildMsi, Deploy })
        {
            var help = Slice(Read(relative), ".PARAMETER FrameworkDependent", ".EXAMPLE");
            foreach (var secretMarker in new[] { "change-me-", "sample-", "-AdminToken ", "UPDATEAGENT_" })
            {
                Assert.DoesNotContain(secretMarker, help, StringComparison.Ordinal);
            }
        }

        var deploy = Read(Deploy);
        var report = Slice(deploy, "$deploymentReport = [ordered]@{", "$reportPath = ");
        Assert.Contains("InstallerSha256", report, StringComparison.Ordinal);
        Assert.Contains("RuntimeMode", report, StringComparison.Ordinal);
        Assert.Contains("RuntimeRequirement", report, StringComparison.Ordinal);
        Assert.DoesNotContain("$AgentTokenFile", report, StringComparison.Ordinal);
        Assert.DoesNotContain("$tokenPath", report, StringComparison.Ordinal);
        Assert.Contains("Get-SafeLabManagerUrl -Url $LabManagerUrl", deploy, StringComparison.Ordinal);
        Assert.DoesNotContain("LabManagerUrl       = $LabManagerUrl", deploy, StringComparison.Ordinal);
        // First-line-only token rules and hash-only reports remain intact.
        Assert.Contains("-TotalCount 1", deploy, StringComparison.Ordinal);
        Assert.Contains("$tokenName = 'agent-token.txt'", deploy, StringComparison.Ordinal);

        foreach (var relative in new[]
                 {
                     "docs/INSTALLER.md", "docs/MSI.md", "docs/PILOT.md", "docs/DEPLOYMENT.md",
                 })
        {
            var section = Improvement08Section(Read(relative));
            Assert.DoesNotContain("change-me-", section, StringComparison.Ordinal);
            Assert.DoesNotContain("sample-", section, StringComparison.Ordinal);
            Assert.DoesNotContain("--admin-token", section, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void IssBomAndSampleIniNoBom_RulesArePreserved()
    {
        Assert.True(HasBom("installer/InnoSetup/UpdateAgent.Agent.iss"));
        Assert.True(HasBom("installer/InnoSetup/UpdateAgent.LabManager.iss"));
        Assert.False(HasBom("scripts/agent-install.sample.ini"));

        var inno = Read(BuildInno);
        Assert.Contains("Assert-InnoScriptBom", inno, StringComparison.Ordinal);
        Assert.Contains("$bytes[0] -ne 0xEF", inno, StringComparison.Ordinal);
        Assert.Contains("Write-BundledTokenFile", inno, StringComparison.Ordinal);

        // The deployment script now contains non-ASCII user-facing strings, so
        // it must carry a BOM for Windows PowerShell 5.1 string decoding.
        Assert.True(HasBom(Deploy));
    }

    [Fact]
    public void Docs_StatePrerequisiteOptInFlowAndLimits()
    {
        foreach (var relative in new[] { "docs/INSTALLER.md", "docs/MSI.md", "docs/PILOT.md" })
        {
            var section = Improvement08Section(Read(relative));
            Assert.True(section.Length > 0, $"개선 8 문서 구간이 없습니다: {relative}");
            foreach (var keyword in new[]
                     {
                         "-FrameworkDependent", "Desktop Runtime", "win-x64", "build-info.json",
                         "토큰", "방화벽", "확인 불가",
                     })
            {
                Assert.Contains(keyword, section, StringComparison.Ordinal);
            }
        }

        var deployment = Read("docs/DEPLOYMENT.md");
        Assert.Contains("-FrameworkDependent", deployment, StringComparison.Ordinal);
        Assert.Contains("확인 불가", deployment, StringComparison.Ordinal);
        Assert.Contains("INSTALLER.md", deployment, StringComparison.Ordinal);
    }

    [Fact]
    public void SizeReporting_UsesMeasuredValues_NotFabricatedNumbers()
    {
        Assert.Contains("(Get-Item -LiteralPath $path).Length", Read(BuildInno), StringComparison.Ordinal);
        Assert.Contains("(Get-Item -LiteralPath $destinationMsi).Length", Read(BuildMsi), StringComparison.Ordinal);
        Assert.Contains("Measure-Object -Property Length -Sum", Read(Publish), StringComparison.Ordinal);

        foreach (var relative in new[] { "docs/INSTALLER.md", "docs/MSI.md", "docs/PILOT.md" })
        {
            var section = Improvement08Section(Read(relative));
            Assert.False(
                Regex.IsMatch(section, @"\d+(?:\.\d+)?\s*MB"),
                $"추정 용량 숫자는 문서에 넣지 않습니다: {relative}");
        }
    }

    [Fact]
    public void Worklog_DocumentsImprovement08WithTenOrderedRounds()
    {
        var worklog = Read("docs/WORKLOG.md");
        var heading = "## 2026-11-28 개선 8 — 설치 패키지 경량화 (Framework-dependent 옵션)";
        var start = worklog.IndexOf(heading, StringComparison.Ordinal);
        var next = worklog.IndexOf("## 2026-11-28 개선 7", StringComparison.Ordinal);
        Assert.True(start >= 0 && next > start, "개선 8은 개선 7 위에 있어야 합니다.");
        var section = worklog[start..next];
        var tableStart = section.IndexOf("| 회차 |", StringComparison.Ordinal);
        var tableEnd = section.IndexOf("\n\n", tableStart, StringComparison.Ordinal);
        Assert.True(tableStart >= 0 && tableEnd > tableStart);
        var table = section[tableStart..tableEnd];
        Assert.Equal(10, Regex.Matches(table, @"^\| \d+ \|", RegexOptions.Multiline).Count);

        var previous = -1;
        foreach (var dimension in new[]
                 {
                     "요구사항 정합성", "하위 호환성", "프레임워크 종속 레이아웃 감지 정확성",
                     "fail-closed 불일치/런타임 전제 정직성", "크기 보고의 실측성",
                     "패키지/로그의 토큰·설정 배제", "BOM/인코딩/문서 정합성",
                     "한국어 문서/영문 도움말 정합성", "회귀 범위", "최종 검증",
                 })
        {
            var position = table.IndexOf(dimension, StringComparison.Ordinal);
            Assert.True(position > previous, $"회차 순서가 올바르지 않습니다: {dimension}");
            previous = position;
        }
    }

    private static string Classify(int assemblies, int markers, bool frameworkReferences)
    {
        // Mirrors Get-PackageLayoutInfo in scripts/package-layout.ps1.
        if (frameworkReferences && markers == 0 && assemblies < 80) return "framework-dependent";
        if (!frameworkReferences && markers == 5 && assemblies >= 80) return "self-contained";
        return "unknown";
    }

    private static int ReadThreshold(string helper, string name)
    {
        var match = Regex.Match(helper, Regex.Escape(name) + @"\s*=\s*(\d+)");
        Assert.True(match.Success, $"임계값을 찾지 못했습니다: {name}");
        return int.Parse(match.Groups[1].Value);
    }

    private static string Improvement08Section(string text)
    {
        var headings = new[]
        {
            "### 프레임워크 종속(경량) 설치 파일 — 선택",
            "### 8-7. 프레임워크 종속(경량) MSI — 선택",
            "### 2-A. 경량(프레임워크 종속) 패키지 — 선택",
        };
        var start = -1;
        foreach (var heading in headings)
        {
            var found = text.IndexOf(heading, StringComparison.Ordinal);
            if (found >= 0) { start = found; break; }
        }
        if (start < 0) return string.Empty;
        var nextHeading = text.IndexOf("\n### ", start + 1, StringComparison.Ordinal);
        if (nextHeading < 0) nextHeading = text.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
        if (nextHeading < 0) nextHeading = text.IndexOf("\n> **주의:**", start + 1, StringComparison.Ordinal);
        if (nextHeading < 0) nextHeading = text.Length;
        return text[start..nextHeading];
    }

    private static bool HasBom(string relativePath)
    {
        var bytes = File.ReadAllBytes(Path.Combine(FindRepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));
        return bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
    }

    private static string Slice(string text, string startMarker, string endMarker)
    {
        var start = text.IndexOf(startMarker, StringComparison.Ordinal);
        var end = text.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, $"구간을 찾지 못했습니다: {startMarker}");
        return text[start..end];
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
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Repository root not found.");
    }
}
