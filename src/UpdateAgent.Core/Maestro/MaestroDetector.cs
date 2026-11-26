using System.Diagnostics;
using Microsoft.Win32;
using UpdateAgent.Shared.Models;

namespace UpdateAgent.Core.Maestro;

/// <summary>
/// Maestro Network 7 설치 여부를 흔적 기반으로 감지한다.
/// 이 앱은 Maestro의 설정/서비스/스케줄을 절대 변경하지 않는다.
/// </summary>
public sealed class MaestroDetector
{
    private static readonly string[] NameHints = ["maestro", "마에스트로", "qotech", "solutem"];

    public MaestroDetectionResult Detect()
    {
        var evidence = new List<string>();
        var probeFailed = false;
        var services = FindServices(evidence, ref probeFailed);
        var processes = FindProcesses(evidence);
        var paths = FindInstallPaths(evidence);
        var uninstall = FindUninstallEntries(evidence, ref probeFailed);

        var installed = HasInstallationEvidence(services.Count, processes.Count, paths.Count, uninstall);

        return new MaestroDetectionResult
        {
            Installed = installed,
            // 흔적이 없더라도 주요 조회가 실패했다면 "미설치"가 아니라 "확인 불가"로 표시한다.
            DetectionFailed = !installed && probeFailed,
            Evidence = evidence,
            Services = services,
            Processes = processes,
            InstallPaths = paths,
            WuFeatureStatus = MaestroWuFeatureStatus.Unknown,
        };
    }

    /// <summary>수집한 흔적이 설치를 입증하는지. 프로세스 흔적도 설치 증거로 취급한다.</summary>
    public static bool HasInstallationEvidence(
        int services,
        int processes,
        int installPaths,
        int uninstallEntries) =>
        services > 0 || processes > 0 || installPaths > 0 || uninstallEntries > 0;

    private static List<string> FindServices(List<string> evidence, ref bool probeFailed)
    {
        var found = new List<string>();
        try
        {
            using var servicesKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            if (servicesKey is null) return found;

            foreach (var name in servicesKey.GetSubKeyNames())
            {
                try
                {
                    using var key = servicesKey.OpenSubKey(name);
                    if (key is null) continue;

                    var displayName = key.GetValue("DisplayName")?.ToString() ?? name;
                    var imagePath = key.GetValue("ImagePath")?.ToString() ?? "";
                    var text = $"{name} {displayName} {imagePath}";
                    if (!Matches(text)) continue;

                    found.Add(displayName);
                    evidence.Add($"서비스: {displayName} ({name})");
                }
                catch
                {
                    // 개별 서비스 접근 실패는 무시한다.
                }
            }
        }
        catch
        {
            probeFailed = true;
            // 레지스트리 접근 실패는 감지 실패로 처리한다.
        }

        return found;
    }

    private static List<string> FindProcesses(List<string> evidence)
    {
        var found = new List<string>();
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        if (!Matches(process.ProcessName)) continue;
                        found.Add(process.ProcessName);
                        evidence.Add($"프로세스: {process.ProcessName} (PID {process.Id})");
                    }
                    catch
                    {
                        // 종료된 프로세스는 무시한다.
                    }
                }
            }
        }
        catch
        {
            // 프로세스 열람 실패는 무시한다.
        }

        return found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> FindInstallPaths(List<string> evidence)
    {
        var found = new List<string>();
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        };

        foreach (var root in roots.Where(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r)))
        {
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(root))
                {
                    var name = Path.GetFileName(dir);
                    if (!Matches(name)) continue;
                    found.Add(dir);
                    evidence.Add($"설치 경로: {dir}");
                }
            }
            catch
            {
                // 접근 불가 디렉터리는 무시한다.
            }
        }

        return found;
    }

    private static int FindUninstallEntries(List<string> evidence, ref bool probeFailed)
    {
        var count = 0;
        var keys = new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        };

        foreach (var path in keys)
        {
            try
            {
                using var root = Registry.LocalMachine.OpenSubKey(path);
                if (root is null) continue;

                foreach (var subName in root.GetSubKeyNames())
                {
                    try
                    {
                        using var sub = root.OpenSubKey(subName);
                        var displayName = sub?.GetValue("DisplayName")?.ToString() ?? "";
                        if (!Matches(displayName)) continue;
                        count++;
                        evidence.Add($"설치 정보: {displayName}");
                    }
                    catch
                    {
                        // ignore
                    }
                }
            }
            catch
            {
                probeFailed = true;
                // Uninstall 레지스트리 접근 실패도 감지 실패로 기록한다.
            }
        }

        return count;
    }

    private static bool Matches(string text) =>
        NameHints.Any(hint => text.Contains(hint, StringComparison.OrdinalIgnoreCase));
}
