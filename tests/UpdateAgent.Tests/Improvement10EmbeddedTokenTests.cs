using System.Text;
using System.Text.RegularExpressions;

namespace UpdateAgent.Tests;

/// <summary>
/// 개선 10(내장 첫 설치 토큰 경로 재검토) 회귀 테스트.
/// 실제 토큰 값을 읽거나 기록하지 않고 결정 매트릭스, 첫 줄/BOM/플레이스홀더
/// 거부, 인스톨러 간 일치, 스테이징 정리, CI 대칭성과 문서 일치를 검증한다.
/// </summary>
public sealed class Improvement10EmbeddedTokenTests
{
    private const string BuildScript = "scripts/build-inno.ps1";
    private const string Workflow = ".github/workflows/release.yml";
    private const string AgentIss = "installer/InnoSetup/UpdateAgent.Agent.iss";
    private const string LabManagerIss = "installer/InnoSetup/UpdateAgent.LabManager.iss";
    private const string OwnSource = "tests/UpdateAgent.Tests/Improvement10EmbeddedTokenTests.cs";

    [Fact]
    public void DecisionMatrix_IsDocumentedForBothProducts()
    {
        var installer = Read("docs/INSTALLER.md");
        var matrix = Slice(
            installer,
            "| 제품 | 상황 | 외부 토큰 지정 | 결과 |",
            "#### 업데이트 시 기존 토큰 처리 선택");

        Assert.Contains("| Agent | 첫 설치 | 없음 |", matrix);
        Assert.Contains("| Agent | 첫 설치 | 있음(`/AgentTokenFile` 또는 INI) | 외부 토큰 우선 |", matrix);
        Assert.Contains("| Agent | 업데이트, 재사용/무인 | 없음 | 내장 토큰 무시", matrix);
        Assert.Contains("| Agent | 업데이트, 재사용/무인 | `/AgentTokenFile` 또는 INI | 명시한 외부 토큰으로 교체 |", matrix);
        Assert.Contains("| Agent | 업데이트, 교체 선택 | 없음 |", matrix);
        Assert.Contains("| Agent | 업데이트, 교체 선택 | `/AgentTokenFile` 또는 INI |", matrix);
        Assert.Contains("| LabManager | 첫 설치 | 해당 없음 |", matrix);
        Assert.Contains("| LabManager | 업데이트, 재사용/무인 | 없음 | 내장 토큰 무시", matrix);
        Assert.Contains("| LabManager | 업데이트, 재사용/무인 | `/LabManagerTokenFile` |", matrix);
        Assert.Contains("| LabManager | 업데이트, 교체 선택 | 없음 |", matrix);
        Assert.Contains("| LabManager | 업데이트, 교체 선택 | `/LabManagerTokenFile` |", matrix);
    }

    [Fact]
    public void BuildScript_UsesFirstLineOnlyWithBomNormalization()
    {
        var text = Read(BuildScript);
        Assert.Contains("function Get-FirstLine", text);
        Assert.Contains("$Value -split '[\\r\\n]+'", text);
        Assert.Contains("return ([string]$lines[0]).Trim().TrimStart([char]0xFEFF).Trim()", text);

        var validate = Slice(text, "function Assert-BundledTokenValue", "function Write-BundledTokenFile");
        Assert.Contains("$token = Get-FirstLine $Value", validate);
        Assert.Contains("IsNullOrWhiteSpace($token)", validate);
        Assert.Contains("change-me-", validate);
        Assert.Contains("-replace-before-pilot", validate);
    }

    [Fact]
    public void BuildScript_RejectsTokensShorterThanTheServerMinimum()
    {
        var validate = Slice(Read(BuildScript), "function Assert-BundledTokenValue", "function Write-BundledTokenFile");
        Assert.Contains("$minimumTokenLength = 8", validate);
        Assert.Contains("$token.Length -lt $minimumTokenLength", validate);
    }

    [Fact]
    public void BuildScript_ChecksCrossInstallerConsistencyAndRoleSeparation()
    {
        var text = Read(BuildScript);
        Assert.Contains(
            "Set both UPDATEAGENT_LABMANAGER_ADMIN_TOKEN and UPDATEAGENT_LABMANAGER_AGENT_TOKEN, or neither.",
            text);
        Assert.Contains(
            "UPDATEAGENT_AGENT_TOKEN and UPDATEAGENT_LABMANAGER_AGENT_TOKEN must match when both are set.",
            text);
        Assert.Contains("$adminToken -ceq $labAgentToken", text);
        Assert.Contains(
            "UPDATEAGENT_LABMANAGER_ADMIN_TOKEN and UPDATEAGENT_LABMANAGER_AGENT_TOKEN must be different values.",
            text);
    }

    [Fact]
    public void BuildScript_WritesConstantFileSetAndReportsContainsSecrets()
    {
        var text = Read(BuildScript);
        Assert.Contains("$agentTokenLines = @()", text);
        Assert.Contains("$labManagerTokenLines = @()", text);
        Assert.Contains("Write-BundledTokenFile -Path", text);
        Assert.Contains("$containsSecrets = ($agentTokenLines.Count -gt 0)", text);
        Assert.Contains("containsSecrets     = [bool]$containsSecrets", text);
    }

    [Fact]
    public void BuildScript_RemovesStagingOnFailureAndSuccess()
    {
        var text = Read(BuildScript);
        Assert.Contains("trap {", text);
        Assert.Contains("if (-not $KeepStaging -and (Test-Path -LiteralPath $StagingRoot))", text);
        Assert.Contains(
            "Remove-Item -LiteralPath $StagingRoot -Recurse -Force -ErrorAction SilentlyContinue",
            text);
        Assert.Contains(
            "if (-not $KeepStaging) {\n    Remove-Item -LiteralPath $StagingRoot -Recurse -Force\n}",
            text);
    }

    [Fact]
    public void AgentInstaller_UsesEmbeddedTokenOnlyOnFirstInstallOrExplicitReplacement()
    {
        var text = Read(AgentIss);
        Assert.Contains("if not HasExternalToken then", text);
        Assert.Contains("TokenFile := BundledTokenPath;", text);
        Assert.Contains("ExtractTemporaryFile(BundledTokenFileName);", text);
        Assert.Contains("if (not FirstInstall) and (not (ReplaceExistingToken and CanKeepExisting)) then", text);
        Assert.Contains("AgentConfigToken := ReadExistingAgentToken(ExistingConfigPath);", text);
        Assert.Contains("Upgrade: backed up the existing token in memory", text);
        Assert.Contains("LoadStringsFromFile(TokenFile, TokenLines)", text);
        Assert.Contains("Token := Trim(TokenLines[0]);", text);
        Assert.Contains("(Token[1] = #65279)", text);
        Assert.Contains("if IsPlaceholderToken(Token) then", text);
        Assert.Contains("if not IsTokenLongEnough(Token) then", text);
    }

    [Fact]
    public void LabManagerInstaller_UsesTwoLineEmbeddedTokenOnlyOnFirstInstallOrReplacement()
    {
        var text = Read(LabManagerIss);
        Assert.Contains("function IsTokenLongEnough", text);
        Assert.Contains("AdminToken := NormalizeTokenLine(Lines[0]);", text);
        Assert.Contains("AgentToken := NormalizeTokenLine(Lines[1]);", text);
        Assert.Contains("(S[1] = #65279)", text);
        Assert.Contains("if not FirstInstall then Exit;", text);
        Assert.Contains("if CmdParamExists(ExternalTokenSwitch) then Exit;", text);
        Assert.Contains("if not ReplaceExistingToken then Exit;", text);
        Assert.Contains("if AdminToken = AgentToken then", text);
        Assert.Contains("if (not IsTokenLongEnough(AdminToken)) or (not IsTokenLongEnough(AgentToken)) then", text);
        // 정의 1회 + 내장/외부 검증 각 2회.
        Assert.True(
            Regex.Matches(text, "IsTokenLongEnough\\(").Count >= 5,
            "LabManager installer must check both token lengths on both paths.");
    }

    [Fact]
    public void ReleaseWorkflow_ValidatesTheSameFirstLineAsTheBuild()
    {
        var text = Read(Workflow);
        Assert.Contains("function Get-SecretFirstLine", text);
        Assert.Contains("$lines = @($Value -split '[\\r\\n]+')", text);
        Assert.Contains("return ([string]$lines[0]).Trim().TrimStart([char]0xFEFF).Trim()", text);
        Assert.Contains("if ($agentToken -cne $labAgentToken)", text);
        Assert.Contains("if ($hasAdminToken -and ($adminToken -ceq $labAgentToken))", text);
        Assert.Contains("LabManager admin and agent tokens must be different values", text);
        Assert.Contains("$expectedSecrets = $hasAgentToken -or $hasAdminToken", text);
        Assert.Contains("[bool]$innoInfo.containsSecrets -ne [bool]$expectedSecrets", text);
        Assert.Contains("내장 토큰은 설치 파일에서 추출할 수 있으므로", text);
    }

    [Fact]
    public void ReleaseWorkflow_StillPublishesExactlyThreeAssets()
    {
        var text = Read(Workflow);
        Assert.Contains("$expectedInnoNames = @(", text);
        Assert.Contains("\"UpdateAgent.Agent-Setup-$version-x64.exe\",", text);
        Assert.Contains("\"UpdateAgent.LabManager-Setup-$version-x64.exe\"", text);
        Assert.Contains("$expectedMsiName = \"UpdateAgent.Agent-$version-x64.msi\"", text);
        Assert.Contains("if ($innoAssets.Count -ne 2)", text);
        Assert.Contains("if ($msiAssets.Count -ne 1)", text);
    }

    [Fact]
    public void TokenValuesAreNeverLoggedWrittenToReportsOrStoredInTheRepo()
    {
        var build = Read(BuildScript);
        Assert.DoesNotContain("Write-Host $agentToken", build);
        Assert.DoesNotContain("Write-Host $adminToken", build);
        Assert.DoesNotContain("Write-Host $labAgentToken", build);
        Assert.Contains("Embedded first-install agent token file (value hidden).", build);
        Assert.Contains("Embedded first-install LabManager token file (values hidden).", build);

        var agent = Read(AgentIss);
        Assert.Contains("Using embedded first-install agent token (value hidden).", agent);
        Assert.Contains("Log('Agent token file: ' + TokenFile);", agent);
        var labManager = Read(LabManagerIss);
        Assert.Contains("Using embedded first-install LabManager tokens (values hidden).", labManager);

        var deploy = Read("scripts/deploy-agents.ps1");
        Assert.Contains("Get-FileHash -LiteralPath $Path -Algorithm SHA256", deploy);
        Assert.Contains("Hashes and metadata only: no token value is ever written to this report.", deploy);
    }

    [Fact]
    public void Docs_DescribeEmbeddedTokenFailClosedRules()
    {
        var installer = Read("docs/INSTALLER.md");
        Assert.Contains("첫 줄만", installer);
        Assert.Contains("8자 미만이면 중단합니다", installer);

        var release = Read("docs/RELEASE.md");
        Assert.Contains("첫 줄이 같은 값", release);
        Assert.Contains("빌드·워크플로·서버가 이를 검증합니다", release);

        var pilot = Read("docs/PILOT.md");
        Assert.Contains("파일의 첫 줄에만 두고", pilot);

        var instructions = Read("docs/AI-WORK-INSTRUCTIONS.md");
        Assert.Contains("첫 설치의 내장 토큰이 비었거나", instructions);

        var nextStage = Read("docs/NEXT-STAGE-FEATURES.md");
        Assert.Contains("(인스톨러와 동일한 규칙)", nextStage);
    }

    [Fact]
    public void Worklog_ContainsTenRoundReviewForImprovement10()
    {
        var worklog = Read("docs/WORKLOG.md");
        var heading = "## 2026-11-28 개선 10 — 내장 첫 설치 토큰 경로 재검토";
        var start = worklog.IndexOf(heading, StringComparison.Ordinal);
        var next = worklog.IndexOf("## 2026-11-28 개선 9", StringComparison.Ordinal);
        Assert.True(start >= 0 && next > start, "개선 10은 개선 9 위에 있어야 합니다.");

        var section = worklog[start..next];
        Assert.Contains("| 회차 | 발견 | 조치 | 검증 명령/결과 |", section);
        for (var round = 1; round <= 10; round++)
        {
            Assert.Contains($"| {round} |", section);
        }
        Assert.Contains("본 개선 단계에서는 커밋·태그·푸시·Release·게시와 버전 상승을 하지 않았고,", section);
    }

    [Fact]
    public void TestSource_DoesNotContainPlaintextTokenValues()
    {
        var source = Read(OwnSource);
        Assert.DoesNotMatch(
            "(?i)\\b(token|secret)\\b\\s*[:=]\\s*\"[A-Za-z0-9_-]{16,}\"",
            source);
        Assert.DoesNotMatch("\\$env:UPDATEAGENT_[A-Z_]+\\s*=\\s*\"", source);
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
