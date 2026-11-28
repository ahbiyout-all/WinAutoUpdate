using System.Text;
using System.Text.RegularExpressions;

namespace UpdateAgent.Tests;

/// <summary>
/// Improvement 2: static regressions for the optional Authenticode signing flow
/// shared by build-inno.ps1 and build-msi.ps1. No certificate value is read or
/// recorded by these tests.
/// </summary>
public sealed class Improvement02CodeSigningTests
{
    private static readonly string[] SigningFunctions =
    {
        "New-SigningContext",
        "Invoke-StagedAssemblySigning",
        "Invoke-InstallerSigning",
        "Add-Member -NotePropertyName",
        "Write-Host \"  Signed: yes",
        ". (Join-Path $PSScriptRoot \"sign-artifacts.ps1\")",
    };

    [Fact]
    public void InnoSigning_IsOptInAndLoadsTheSharedHelperOnlyWhenRequested()
    {
        var text = Read("scripts/build-inno.ps1");

        Assert.Contains("[switch]$Sign", text);
        Assert.Contains("[string]$CertificateThumbprint", text);
        Assert.Contains("[string]$TimestampUrl", text);
        Assert.Contains("$signingContext = $null", text);
        Assert.Contains("New-SigningContext -Sign -CertificateThumbprint $CertificateThumbprint -TimestampUrl $TimestampUrl", text);
        Assert.Contains("does not require signtool.exe", text);
        AssertAllSigningCallsAreGuarded(text);
    }

    [Fact]
    public void MsiPublicParameters_AreUnchangedAndUnsignedBuildInfoIsPreserved()
    {
        var text = Read("scripts/build-msi.ps1");

        foreach (var parameter in new[]
        {
            "[string]$ProductVersion",
            "[string]$Configuration = \"Release\"",
            "[string]$PublishRoot",
            "[string]$OutputRoot",
            "[string]$StagingRoot",
            "[switch]$KeepStaging",
            "[switch]$SkipValidate",
            "[switch]$Sign",
            "[string]$CertificateThumbprint",
            "[string]$TimestampUrl",
        })
        {
            Assert.Contains(parameter, text);
        }

        Assert.Contains("signed            = [bool]$Sign", text);
        Assert.DoesNotContain("Invoke-SignTool", text);
        Assert.Contains("Invoke-StagedAssemblySigning", text);
        Assert.Contains("Invoke-InstallerSigning", text);
        AssertAllSigningCallsAreGuarded(text);
    }

    [Fact]
    public void Helper_FailsClosedWithoutAValidThumbprintOrSigntool()
    {
        var helper = Read("scripts/sign-artifacts.ps1");

        Assert.Contains("$value = $env:UPDATEAGENT_SIGN_THUMBPRINT", helper);
        Assert.Contains("throw \"-Sign requires -CertificateThumbprint or the UPDATEAGENT_SIGN_THUMBPRINT environment variable.\"", helper);
        Assert.Contains("'^[0-9a-fA-F]{40}$'", helper);
        Assert.Contains("Certificate thumbprint must be a 40-character SHA-1 hex value", helper);
        Assert.Contains("Get-Command -Name 'signtool.exe' -CommandType Application", helper);
        Assert.Contains("throw \"signtool.exe was not found on PATH. Install the Windows SDK or omit -Sign.\"", helper);

        var validation = RequireIndex(helper, "Get-SigningThumbprint -ProvidedThumbprint $CertificateThumbprint");
        var toolLookup = RequireIndex(helper, "Get-Command -Name 'signtool.exe'");
        Assert.True(toolLookup > validation, "The thumbprint must be validated before signtool is resolved.");
    }

    [Fact]
    public void Helper_UnsignedPathNeverRequiresSigningPrerequisites()
    {
        var helper = Read("scripts/sign-artifacts.ps1");
        Assert.Contains("if (-not $Sign) { return $null }", helper);

        foreach (var relativePath in new[] { "scripts/build-inno.ps1", "scripts/build-msi.ps1" })
        {
            var text = Read(relativePath);
            Assert.Contains("$signingContext = $null", text);
            Assert.DoesNotContain("Get-Command -Name 'signtool.exe'", text);
        }

        AssertAllSigningCallsAreGuarded(Read("scripts/build-inno.ps1"));
        AssertAllSigningCallsAreGuarded(Read("scripts/build-msi.ps1"));
        Assert.DoesNotContain("signed            =", Read("scripts/build-inno.ps1"));
    }

    [Fact]
    public void InnoPayload_IsSignedBeforeISCC_SetupsAfterISCC_AndHashesLast()
    {
        var text = Read("scripts/build-inno.ps1");
        var staged = RequireIndex(text, "Invoke-StagedAssemblySigning -Context");
        var isccAgent = RequireIndex(text, "& $iscc \"/DAppVersion");
        var isccLab = text.LastIndexOf("& $iscc \"/DAppVersion", StringComparison.Ordinal);
        var setupSign = RequireIndex(text, "Invoke-InstallerSigning -Context");
        var hash = RequireIndex(text, "Get-FileHash -LiteralPath $path");

        Assert.True(staged < isccAgent && isccAgent < isccLab, "Staged assemblies must be signed before ISCC compiles either installer.");
        Assert.True(isccLab < setupSign && setupSign < hash, "Setup EXEs must be signed after ISCC and before SHA-256 is recorded.");
    }

    [Fact]
    public void Helper_SignsOnlyStagedUpdateAgentAssemblies()
    {
        var helper = Read("scripts/sign-artifacts.ps1");
        var function = ExtractFunction(helper, "function Invoke-StagedAssemblySigning");

        Assert.Contains("Get-ChildItem -LiteralPath $StagingRoot -Recurse -File", function);
        Assert.Contains("$_.Name -like 'UpdateAgent.*.exe'", function);
        Assert.Contains("$_.Name -like 'UpdateAgent.*.dll'", function);
        Assert.Contains("Invoke-AuthenticodeSign -Context $Context -FilePath $assembly.FullName", function);
    }

    [Fact]
    public void Verification_IsInvokedForStagedFilesAndFinalInstallers()
    {
        var helper = Read("scripts/sign-artifacts.ps1");

        Assert.Contains("& $Context.SignToolPath verify /pa /all $FilePath", helper);
        Assert.Contains("throw \"signtool verify failed for $FilePath (exit code $LASTEXITCODE).\"", helper);

        var staged = ExtractFunction(helper, "function Invoke-StagedAssemblySigning");
        var installers = ExtractFunction(helper, "function Invoke-InstallerSigning");

        Assert.Contains("Assert-AuthenticodeSignature -Context $Context -FilePath $assembly.FullName", staged);
        Assert.Contains("Assert-AuthenticodeSignature -Context $Context -FilePath $path", installers);
        Assert.Contains("Invoke-AuthenticodeSign -Context $Context -FilePath $path", installers);
        Assert.Contains("signatureVerification", Read("scripts/build-msi.ps1"));
        Assert.Contains("signatureVerification", Read("scripts/build-inno.ps1"));
    }

    [Fact]
    public void MsiFinalSigning_PrecedesHashComputation()
    {
        var text = Read("scripts/build-msi.ps1");
        var sign = RequireIndex(text, "Invoke-InstallerSigning -Context $signingContext -FilePaths @($destinationMsi)");
        var hash = RequireIndex(text, "Get-FileHash -LiteralPath $destinationMsi");
        Assert.True(sign < hash, "The MSI hash must describe the signed bytes.");

        var staged = RequireIndex(text, "Invoke-StagedAssemblySigning -Context");
        var build = RequireIndex(text, "& dotnet build $wixProject");
        Assert.True(staged < build, "Staged assemblies must be signed before WiX builds the MSI.");
    }

    [Fact]
    public void SignToolExitCode_IsClearedBeforeEachNativeInvocation()
    {
        var helper = Read("scripts/sign-artifacts.ps1");
        var sign = ExtractFunction(helper, "function Invoke-AuthenticodeSign");
        var verify = ExtractFunction(helper, "function Assert-AuthenticodeSignature");

        foreach (var function in new[] { sign, verify })
        {
            var reset = RequireIndex(function, "$global:LASTEXITCODE = 0");
            var invocation = function.IndexOf("& $Context.SignToolPath", StringComparison.Ordinal);
            Assert.True(invocation > reset, "A stale exit code must not be mistaken for a signing failure.");
            Assert.Contains("if ($LASTEXITCODE -ne 0)", function);
        }
    }

    [Fact]
    public void TimestampUrl_IsOptionalAndForwardedToSigntool()
    {
        var helper = Read("scripts/sign-artifacts.ps1");
        Assert.Contains("if (-not [string]::IsNullOrWhiteSpace($Context.TimestampUrl))", helper);
        Assert.Contains("'/fd', 'SHA256'", helper);
        Assert.Contains("'/tr', $Context.TimestampUrl, '/td', 'SHA256'", helper);

        foreach (var relativePath in new[] { "scripts/build-inno.ps1", "scripts/build-msi.ps1" })
        {
            Assert.Contains("-TimestampUrl $TimestampUrl", Read(relativePath));
        }
    }

    [Fact]
    public void NoHardCodedThumbprintPasswordTokenOrSecretInScripts()
    {
        foreach (var relativePath in new[] { "scripts/build-inno.ps1", "scripts/build-msi.ps1", "scripts/sign-artifacts.ps1" })
        {
            var text = Read(relativePath);
            Assert.DoesNotMatch(@"\b[0-9a-fA-F]{40}\b", text);
            Assert.DoesNotMatch(@"(?i)(password|passwd|pwd)\s*[:=]\s*['""][^'""]{4,}['""]", text);
            Assert.DoesNotMatch(@"gh[pousr]_[A-Za-z0-9]{20,}", text);
            Assert.DoesNotContain("ABC123", text);
        }
    }

    [Fact]
    public void Docs_DescribeTheSharedWorkflowAndSecretHandling()
    {
        var msi = Read("docs/MSI.md");
        Assert.Contains("sign-artifacts.ps1", msi);
        Assert.Contains("UPDATEAGENT_SIGN_THUMBPRINT", msi);
        Assert.Contains("signtool verify /pa /all", msi);
        Assert.Contains("build-inno.ps1", msi);
        Assert.Contains("build-msi.ps1", msi);
        Assert.Contains("컴파일 전", msi);
        Assert.Contains("컴파일 후", msi);
        Assert.Contains("미서명", msi);
        Assert.Contains("SmartScreen", msi);

        var installer = Read("docs/INSTALLER.md");
        Assert.Contains("코드 서명 (선택)", installer);
        Assert.Contains("UPDATEAGENT_SIGN_THUMBPRINT", installer);
        Assert.Contains("ISCC 후에", installer);
        Assert.Contains("SmartScreen", installer);

        var release = Read("docs/RELEASE.md");
        Assert.Contains("signtool verify /pa /all", release);
        Assert.Contains("CI secret 또는 로컬 인증서 저장소", release);
        Assert.Contains("절대 기록하지 않습니다", release);
    }

    [Fact]
    public void Worklog_RecordsTenReflectionRoundsInTheRequiredOrder()
    {
        var worklog = Read("docs/WORKLOG.md");
        var heading = "## 2026-11-27 개선 2 — 코드 서명 도입 (빌드 스크립트 지원)";
        var start = worklog.IndexOf(heading, StringComparison.Ordinal);
        var improvement1 = worklog.IndexOf("## 2026-11-27 개선 1", StringComparison.Ordinal);
        Assert.True(start >= 0 && improvement1 > start, "Improvement 2 must be logged above improvement 1.");

        var section = worklog.Substring(start, improvement1 - start);
        var tableStart = section.IndexOf("| 회차 |", StringComparison.Ordinal);
        var tableEnd = section.IndexOf("\n\n", tableStart, StringComparison.Ordinal);
        Assert.True(tableStart >= 0 && tableEnd > tableStart);
        var table = section.Substring(tableStart, tableEnd - tableStart);

        Assert.Equal(10, Regex.Matches(table, @"^\| \d+ \|", RegexOptions.Multiline).Count);

        var previous = -1;
        foreach (var dimension in new[]
        {
            "요구사항 정합성", "MSI 서명 호환성", "비밀값/지문", "fail-closed 검증",
            "스테이징 순서", "PowerShell 5.1", "검증 증적/로그", "한국어 문서/영어 메시지",
            "회귀 범위", "최종 검증",
        })
        {
            var index = table.IndexOf(dimension, StringComparison.Ordinal);
            Assert.True(index > previous, $"Round dimension out of order: {dimension}");
            previous = index;
        }
    }

    [Fact]
    public void NewHelperIsEnglishAndFileEncodingsAreUnchanged()
    {
        var helper = Read("scripts/sign-artifacts.ps1");
        Assert.DoesNotMatch(@"[\uAC00-\uD7A3]", helper);
        Assert.Contains("Signing and verifying", helper);

        Assert.False(HasBom("scripts/sign-artifacts.ps1"));
        Assert.False(HasBom("scripts/build-inno.ps1"));
        Assert.False(HasBom("scripts/build-msi.ps1"));
        Assert.True(HasBom("installer/InnoSetup/UpdateAgent.Agent.iss"));
        Assert.True(HasBom("installer/InnoSetup/UpdateAgent.LabManager.iss"));
        Assert.False(HasBom("scripts/agent-install.sample.ini"));
    }

    private static void AssertAllSigningCallsAreGuarded(string text)
    {
        var guarded = SignGuardedCode(text);
        foreach (var line in text.Split('\n'))
        {
            if (SigningFunctions.Any(function => line.Contains(function, StringComparison.Ordinal)))
            {
                Assert.Contains(line, guarded);
            }
        }
    }

    private static string SignGuardedCode(string text)
    {
        const string marker = "if ($Sign) {";
        var builder = new StringBuilder();
        var index = 0;
        while ((index = text.IndexOf(marker, index, StringComparison.Ordinal)) >= 0)
        {
            var open = index + marker.Length - 1;
            var depth = 0;
            var end = open;
            for (; end < text.Length; end++)
            {
                if (text[end] == '{') depth++;
                else if (text[end] == '}')
                {
                    depth--;
                    if (depth == 0) break;
                }
            }
            Assert.True(end < text.Length, "Unterminated if ($Sign) block.");
            builder.AppendLine(text.Substring(index, end + 1 - index));
            index = end + 1;
        }
        Assert.True(builder.Length > 0, "No guarded signing block found.");
        return builder.ToString();
    }

    private static string ExtractFunction(string text, string marker)
    {
        var start = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing function: {marker}");
        var end = text.IndexOf("\n}\n", start, StringComparison.Ordinal);
        Assert.True(end > start, $"Unterminated function: {marker}");
        return text.Substring(start, end - start);
    }

    private static int RequireIndex(string text, string marker)
    {
        var index = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Missing marker: {marker}");
        return index;
    }

    private static bool HasBom(string relativePath)
    {
        var path = Path.Combine(FindRepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"Missing repository file: {relativePath}");
        var bytes = File.ReadAllBytes(path);
        return bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
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
