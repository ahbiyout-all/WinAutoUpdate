using System.Text;
using System.Text.RegularExpressions;

namespace UpdateAgent.Tests;

/// <summary>
/// 개선 9(`desktop/tauri` 실험 스캐폴드 정리)의 회귀 테스트.
/// 추적 소스 제거, .gitignore 무시 규칙, 내장 웹뷰 의존성 부재, 릴리스 자산 3개 유지,
/// 문서의 stale 빌드 지시 제거와 비밀값 배제를 저장소 텍스트로 검증한다.
/// </summary>
public sealed class Improvement09TauriCleanupTests
{
    private const string GitIgnore = ".gitignore";
    private const string Workflow = ".github/workflows/release.yml";
    private const string Worklog = "docs/WORKLOG.md";
    private const string CleanupTests = "tests/UpdateAgent.Tests/Improvement09TauriCleanupTests.cs";

    private static readonly string[] ScaffoldPaths =
    {
        "desktop/tauri/.gitignore",
        "desktop/tauri/README.md",
        "desktop/tauri/index.html",
        "desktop/tauri/package-lock.json",
        "desktop/tauri/package.json",
        "desktop/tauri/src-tauri/.gitignore",
        "desktop/tauri/src-tauri/Cargo.lock",
        "desktop/tauri/src-tauri/Cargo.toml",
        "desktop/tauri/src-tauri/build.rs",
        "desktop/tauri/src-tauri/capabilities/default.json",
        "desktop/tauri/src-tauri/icons/icon.ico",
        "desktop/tauri/src-tauri/src/lib.rs",
        "desktop/tauri/src-tauri/src/main.rs",
        "desktop/tauri/src-tauri/tauri.conf.json",
        "desktop/tauri/src/main.js",
        "desktop/tauri/src/styles.css",
        "desktop/tauri/vite.config.js",
    };

    [Fact]
    public void TrackedScaffoldSourceFiles_AreAbsent()
    {
        var root = FindRepoRoot();
        foreach (var relative in ScaffoldPaths)
        {
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.False(File.Exists(path), $"제거되지 않은 스캐폴드 소스: {relative}");
        }

        var readme = Read("README.md");
        Assert.DoesNotContain("| `desktop/tauri`", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("독립 실험용 Tauri 2 스캐폴드. LabManager", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void GitIgnore_IgnoresWholeExperiment_WithoutReinclusion()
    {
        var ignore = Read(GitIgnore);
        Assert.DoesNotContain("!desktop/", ignore, StringComparison.Ordinal);
        Assert.DoesNotContain("!desktop/tauri", ignore, StringComparison.Ordinal);

        var rules = ignore.Split('\n').Select(line => line.Trim()).ToArray();
        Assert.Contains("desktop/tauri/", rules);
        Assert.DoesNotContain("!desktop/tauri/", rules);
        Assert.True(
            ignore.IndexOf("desktop/tauri/", StringComparison.Ordinal) >
            ignore.IndexOf("!*/", StringComparison.Ordinal),
            "무시 규칙은 디렉터리 순회 재포함(!*/) 뒤에 있어야 합니다.");

        // Other allowlist rules and the final safety net stay intact.
        foreach (var rule in new[]
                 {
                     "!.gitignore", "!README.md", "!.github/workflows/*.yml", "!docs/*.md",
                     "!scripts/*.ps1", "!src/**/*.cs", "!tests/**/*.cs", "!installer/**/*.iss",
                     "scripts/github-push.ps1", "scripts/github-push.bat",
                 })
        {
            Assert.Contains(rule, rules);
        }

        var tail = rules.Where(line => line.Length > 0).TakeLast(4).ToArray();
        Assert.Equal(new[] { "[Bb]in/", "[Oo]bj/", "publish/", "artifacts/" }, tail);
    }

    [Fact]
    public void LocalExperimentArtifacts_HaveNoSourceAllowlist()
    {
        var ignore = Read(GitIgnore);
        foreach (var artifact in new[]
                 {
                     "!desktop/tauri/node_modules", "!desktop/tauri/dist",
                     "!desktop/tauri/src-tauri/target", "!desktop/tauri/src-tauri/gen",
                     "!desktop/tauri/src-tauri/Cargo.lock",
                 })
        {
            Assert.DoesNotContain(artifact, ignore, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ShippedSourceAndWorkflow_HaveNoEmbeddedWebViewDependency()
    {
        var root = FindRepoRoot();
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".csproj", ".props", ".targets", ".json", ".js", ".html", ".css",
            ".ps1", ".yml", ".yaml", ".iss", ".wxs", ".wxl", ".wixproj",
        };
        var markers = new[]
        {
            "@tauri-apps", "tauri::", "Microsoft.Web.WebView2", "CoreWebView2",
            "desktop/tauri", "src-tauri", "tauri.conf.json", "vite.config.js", "Cargo.toml",
        };

        foreach (var directory in new[] { "src", "installer", "scripts", ".github/workflows" })
        {
            var files = Directory.EnumerateFiles(
                    Path.Combine(root, directory.Replace('/', Path.DirectorySeparatorChar)),
                    "*",
                    SearchOption.AllDirectories)
                .Where(file => extensions.Contains(Path.GetExtension(file)))
                .ToArray();
            Assert.NotEmpty(files);

            foreach (var file in files)
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (relative.StartsWith("desktop/", StringComparison.Ordinal) ||
                    relative.Contains("/bin/", StringComparison.Ordinal) ||
                    relative.Contains("/obj/", StringComparison.Ordinal) ||
                    relative.Contains("/node_modules/", StringComparison.Ordinal))
                {
                    continue;
                }

                var text = File.ReadAllText(file, Encoding.UTF8);
                foreach (var marker in markers)
                {
                    Assert.False(
                        text.Contains(marker, StringComparison.OrdinalIgnoreCase),
                        $"내장 웹뷰/스캐폴드 흔적이 남아 있습니다: {relative} → {marker}");
                }
            }
        }
    }

    [Fact]
    public void ReleaseWorkflow_StillUploadsOnlyTwoExesAndOneMsi()
    {
        var workflow = Read(Workflow);
        foreach (var asset in new[]
                 {
                     "\"UpdateAgent.Agent-Setup-$version-x64.exe\"",
                     "\"UpdateAgent.LabManager-Setup-$version-x64.exe\"",
                     "\"UpdateAgent.Agent-$version-x64.msi\"",
                     "$innoAssets.Count -ne 2", "$msiAssets.Count -ne 1",
                     "gh release upload $tag @assets --clobber",
                 })
        {
            Assert.Contains(asset, workflow, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("tauri", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".msix", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".appx", workflow, StringComparison.OrdinalIgnoreCase);

        var release = Read("docs/RELEASE.md");
        Assert.Contains("UpdateAgent.Agent-Setup-<버전>-x64.exe", release, StringComparison.Ordinal);
        Assert.Contains("UpdateAgent.LabManager-Setup-<버전>-x64.exe", release, StringComparison.Ordinal);
        Assert.Contains("UpdateAgent.Agent-<버전>-x64.msi", release, StringComparison.Ordinal);
        Assert.DoesNotContain("Tauri", release, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Docs_RemoveBuildInstructionsAndStateTheRemoval()
    {
        foreach (var relative in new[]
                 {
                     "README.md", "docs/DEPLOYMENT.md", "docs/RELEASE.md",
                     "docs/INSTALLER.md", "docs/NEXT-STAGE-FEATURES.md", Worklog,
                 })
        {
            var text = Read(relative);
            Assert.DoesNotContain("npm run tauri", text, StringComparison.Ordinal);
            Assert.DoesNotContain("cd desktop\\tauri", text, StringComparison.Ordinal);
            Assert.DoesNotContain("tauri build", text, StringComparison.OrdinalIgnoreCase);
        }

        var readme = Read("README.md");
        Assert.Contains("Tauri 2 스캐폴드(`desktop/tauri`)는 제거되었습니다", readme, StringComparison.Ordinal);
        Assert.Contains("관련 릴리스 자산: 없음", readme, StringComparison.Ordinal);
        Assert.Contains("Edge/Chrome의 `--app=` 단독 창으로 계속 실행됩니다", readme, StringComparison.Ordinal);

        var deployment = Read("docs/DEPLOYMENT.md");
        Assert.Contains("Tauri 스캐폴드는 제거되었고", deployment, StringComparison.Ordinal);
        Assert.Contains("추가 릴리스 자산: 없음", deployment, StringComparison.Ordinal);

        var history = HistoricalTauriSection(Read(Worklog));
        Assert.Contains("개선 9", history, StringComparison.Ordinal);
        Assert.Contains("현재 빌드·실행 절차가 아닙니다", history, StringComparison.Ordinal);
    }

    [Fact]
    public void KoreanDocs_RemainConsistentAfterCleanup()
    {
        var deployment = Read("docs/DEPLOYMENT.md");
        Assert.Contains("학생 → 학생 통신 없음.", deployment, StringComparison.Ordinal);
        Assert.DoesNotContain("students 통신", deployment, StringComparison.Ordinal);

        var worklog = Read(Worklog);
        var history = HistoricalTauriSection(worklog);
        Assert.Contains("실제 절차는", history, StringComparison.Ordinal);
        Assert.DoesNotContain("실절차", history, StringComparison.Ordinal);

        var cleanup = Improvement09Section(worklog);
        Assert.Contains("제거/실행 경로 안내", cleanup, StringComparison.Ordinal);
        Assert.DoesNotContain("运行", cleanup, StringComparison.Ordinal);
    }

    [Fact]
    public void Worklog_DocumentsImprovement09WithTenOrderedRounds()
    {
        var section = Improvement09Section(Read(Worklog));
        Assert.True(section.Length > 0, "개선 9 작업 로그 구간이 없습니다.");

        var tableStart = section.IndexOf("| 회차 |", StringComparison.Ordinal);
        var tableEnd = section.IndexOf("\n\n", tableStart, StringComparison.Ordinal);
        Assert.True(tableStart >= 0 && tableEnd > tableStart, "개선 9 자아성찰 표가 없습니다.");
        var table = section[tableStart..tableEnd];
        Assert.Equal(10, Regex.Matches(table, @"^\| \d+ \|", RegexOptions.Multiline).Count);

        var previous = -1;
        foreach (var dimension in new[]
                 {
                     "요구사항 정합성", "추적 파일 목록·제거 완전성", ".gitignore 허용 목록 안전성",
                     "출시 소스·빌드·릴리스 워크플로 비변경", "내장 웹뷰 의존성·API 부재",
                     "문서 정확성·stale 지시 제거", "로컬 무시 산출물 보존",
                     "한국어 문서 일관성", "회귀 범위", "최종 검증",
                 })
        {
            var position = table.IndexOf(dimension, StringComparison.Ordinal);
            Assert.True(position > previous, $"회차 순서가 올바르지 않습니다: {dimension}");
            previous = position;
        }
    }

    [Fact]
    public void NoTokenOrSecretValues_InCleanupText()
    {
        var markers = new[]
        {
            "change-" + "me-", "sam" + "ple-",
            "UPDATEAGENT_" + "AGENT_TOKEN", "UPDATEAGENT_" + "ADMIN_TOKEN",
            "X-Admin-" + "Token",
        };
        var sections = new[]
        {
            Slice(Read("README.md"), "> 독립 실험용 Tauri 2 스캐폴드", "\nMSI는"),
            Slice(Read("docs/DEPLOYMENT.md"), "> 교사용 콘솔은 시스템 Edge/Chrome의", "\n| 항목 |"),
            Improvement09Section(Read(Worklog)),
            Read(GitIgnore),
            Read(CleanupTests),
        };

        foreach (var section in sections)
        {
            foreach (var marker in markers)
            {
                Assert.DoesNotContain(marker, section, StringComparison.Ordinal);
            }
        }

        Assert.False(HasBom(CleanupTests), "신규 테스트는 BOM 없이 저장해야 합니다.");
    }

    private static string HistoricalTauriSection(string worklog)
    {
        var start = worklog.IndexOf("## 2026-11-17 — 독립 Tauri 데스크톱 앱 스캐폴드 추가", StringComparison.Ordinal);
        Assert.True(start >= 0, "Tauri 스캐폴드 과거 이력이 없습니다.");
        var end = worklog.IndexOf("\n---", start, StringComparison.Ordinal);
        Assert.True(end > start, "과거 이력 구간 끝이 없습니다.");
        return worklog[start..end];
    }

    private static string Improvement09Section(string worklog)
    {
        var heading = "## 2026-11-28 개선 9 — `desktop/tauri` 실험 스캐폴드 정리";
        var start = worklog.IndexOf(heading, StringComparison.Ordinal);
        var next = worklog.IndexOf("## 2026-11-28 개선 8", StringComparison.Ordinal);
        Assert.True(start >= 0 && next > start, "개선 9는 개선 8 위에 있어야 합니다.");
        return worklog[start..next];
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
