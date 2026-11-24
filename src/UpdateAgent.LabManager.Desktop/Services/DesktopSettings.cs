using System.IO;
using System.Text;
using System.Text.Json;
using UpdateAgent.Guards;

namespace UpdateAgent.LabManager.Desktop.Services;

/// <summary>
/// 런처 설정. 관리자 토큰은 브라우저 localStorage에 저장하므로 여기서는 다루지 않는다.
/// </summary>
public sealed class DesktopSettings
{
    private const string DefaultServerUrl = "http://localhost:5080";

    private readonly string _directory;
    private readonly string _settingsPath;

    public DesktopSettings()
    {
        _directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UpdateAgent",
            "LabManager");
        _settingsPath = Path.Combine(_directory, "desktop-settings.json");
        Load();
    }

    /// <summary>LabManager API 주소.</summary>
    public string ServerUrl { get; set; } = DefaultServerUrl;

    /// <summary>브라우저 앱 모드로 열 대시보드 주소. 로컬 주소는 루프백 IP로 정규화한다.</summary>
    public string DashboardUrl => BuildUrl("/");

    /// <summary>브라우저 앱 모드로 열 실시간 작업 현황 모니터 주소.</summary>
    public string MonitorUrl => BuildUrl("/monitor.html");

    private string BuildUrl(string path)
    {
        var address = UrlGuard.NormalizeBrowserUrl(ServerUrl) ?? DefaultServerUrl;
        return address.TrimEnd('/') + path;
    }

    /// <summary>앱 모드 창 크기.</summary>
    public WindowPlacement Window { get; set; } = new();

    public bool IsLocalServer
    {
        get
        {
            if (!UrlGuard.IsHttpUrl(ServerUrl, out var uri) || uri is null) return false;
            return UrlGuard.IsLoopbackHost(uri.Host);
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(_directory);
        var json = JsonSerializer.Serialize(new SettingsDto
        {
            ServerUrl = ServerUrl,
            Window = Window,
        }, JsonOptions);
        File.WriteAllText(_settingsPath, json, new UTF8Encoding(false));
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return;
            }

            var dto = JsonSerializer.Deserialize<SettingsDto>(
                File.ReadAllText(_settingsPath), JsonOptions);
            if (dto is null)
            {
                return;
            }

            // 잘못된 주소는 앱 모드 창에서 임의 사이트를 여는 경로가 되므로 무시한다.
            if (UrlGuard.IsSafeBrowserUrl(dto.ServerUrl))
            {
                ServerUrl = dto.ServerUrl;
            }

            Window = dto.Window ?? new WindowPlacement();
        }
        catch
        {
            // 손상된 설정은 기본값으로 대체한다.
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private sealed class SettingsDto
    {
        public string ServerUrl { get; set; } = DefaultServerUrl;
        public WindowPlacement? Window { get; set; }
    }
}

/// <summary>앱 모드 창 크기.</summary>
public sealed class WindowPlacement
{
    public double Width { get; set; } = 1320;
    public double Height { get; set; } = 820;
}
