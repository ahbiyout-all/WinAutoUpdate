using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using UpdateAgent.Guards;

namespace UpdateAgent.LabManager.Desktop.Services;

/// <summary>
/// 설치된 Edge/Chrome을 찾아 주소창과 툴바가 없는 --app= 단독 창으로 대시보드를 연다.
/// 전용 사용자 프로필을 사용해 일반 브라우저 창/탭과 섞이지 않게 한다.
/// </summary>
internal static class BrowserLauncher
{
    private static readonly string[] BrowserFileNames = ["msedge.exe", "chrome.exe"];

    internal static bool OpenDashboard(string url, double width, double height)
    {
        if (!UrlGuard.IsSafeBrowserUrl(url)) return false;

        var browser = FindBrowser();
        if (browser is null)
        {
            return false;
        }

        var startInfo = BuildStartInfo(browser, url, width, height);

        try
        {
            Process.Start(startInfo);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>실시간 작업 현황 모니터를 별도 앱 모드 창으로 연다. 프로필을 공유하므로 토큰을 다시 입력하지 않는다.</summary>
    internal static bool OpenMonitor(string url, double width, double height) =>
        OpenDashboard(url, width, height);

    internal static string GetProfilePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UpdateAgent",
        "LabManager",
        "browser-profile");

    internal static ProcessStartInfo BuildStartInfo(string browser, string url, double width, double height)
    {
        if (!UrlGuard.IsSafeBrowserUrl(url))
            throw new ArgumentException("안전하지 않은 대시보드 주소입니다.", nameof(url));

        var profile = GetProfilePath();
        Directory.CreateDirectory(profile);

        var startInfo = new ProcessStartInfo(browser)
        {
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add($"--app={url}");
        startInfo.ArgumentList.Add($"--window-size={(int)width},{(int)height}");
        startInfo.ArgumentList.Add($"--user-data-dir={profile}");
        startInfo.ArgumentList.Add("--no-first-run");
        startInfo.ArgumentList.Add("--no-default-browser-check");
        startInfo.ArgumentList.Add("--disable-search-engine-choice-screen");
        return startInfo;
    }

    /// <summary>Edge를 우선하고, 없으면 Chrome을 사용한다.</summary>
    private static string? FindBrowser()
    {
        foreach (var fileName in BrowserFileNames)
        {
            var path = FindFromAppPaths(fileName);
            if (path is not null && File.Exists(path))
            {
                return path;
            }

            foreach (var candidate in GetKnownLocations(fileName))
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static string? FindFromAppPaths(string fileName)
    {
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            using var key = hive.OpenSubKey(
                $@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{fileName}");
            var value = key?.GetValue("") as string;
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim('"');
            }
        }

        return null;
    }

    private static string[] GetKnownLocations(string fileName) => fileName switch
    {
        "msedge.exe" =>
        [
            Expand("%ProgramFiles(x86)%", @"Microsoft\Edge\Application\msedge.exe"),
            Expand("%ProgramFiles%", @"Microsoft\Edge\Application\msedge.exe"),
        ],
        "chrome.exe" =>
        [
            Expand("%ProgramFiles%", @"Google\Chrome\Application\chrome.exe"),
            Expand("%ProgramFiles(x86)%", @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Google\Chrome\Application\chrome.exe"),
        ],
        _ => [],
    };

    private static string Expand(string variable, string relative) =>
        Path.Combine(Environment.ExpandEnvironmentVariables(variable), relative);
}
