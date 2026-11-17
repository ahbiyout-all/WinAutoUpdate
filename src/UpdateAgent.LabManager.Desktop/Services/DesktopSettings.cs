using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UpdateAgent.LabManager.Desktop.Services;

/// <summary>
/// 데스크톱 클라이언트 설정. 관리자 토큰은 DPAPI로 암호화해 사용자별 프로필에 저장한다.
/// 브라우저 localStorage를 사용하지 않는다.
/// </summary>
public sealed class DesktopSettings
{
    private const string DefaultServerUrl = "http://localhost:5080";

    private readonly string _directory;
    private readonly string _settingsPath;
    private readonly string _tokenPath;

    public DesktopSettings()
    {
        _directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UpdateAgent",
            "LabManager");
        _settingsPath = Path.Combine(_directory, "desktop-settings.json");
        _tokenPath = Path.Combine(_directory, "admin-token.bin");

        Load();
    }

    /// <summary>LabManager API 기본 주소.</summary>
    public string ServerUrl { get; set; } = DefaultServerUrl;

    /// <summary>사용자가 "나중에"를 누른 업데이트 버전.</summary>
    public string? DismissedUpdateVersion { get; set; }

    /// <summary>메인 창 위치/크기.</summary>
    public WindowPlacement Window { get; set; } = new();

    /// <summary>관리자 토큰(메모리에만 평문으로 보관).</summary>
    public string AdminToken { get; set; } = "";

    public bool HasAdminToken => !string.IsNullOrWhiteSpace(AdminToken);

    public bool IsLocalServer
    {
        get
        {
            if (!Uri.TryCreate(ServerUrl, UriKind.Absolute, out var uri))
            {
                return false;
            }

            if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return System.Net.IPAddress.TryParse(uri.Host, out var address) &&
                   System.Net.IPAddress.IsLoopback(address);
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(_directory);

        var json = JsonSerializer.Serialize(new SettingsDto
        {
            ServerUrl = ServerUrl,
            DismissedUpdateVersion = DismissedUpdateVersion,
            Window = Window,
        }, JsonOptions);
        File.WriteAllText(_settingsPath, json, new UTF8Encoding(false));

        if (!string.IsNullOrEmpty(AdminToken))
        {
            var protectedBytes = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(AdminToken), null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(_tokenPath, protectedBytes);
        }
        else if (File.Exists(_tokenPath))
        {
            File.Delete(_tokenPath);
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                var dto = JsonSerializer.Deserialize<SettingsDto>(json, JsonOptions);
                if (dto is not null)
                {
                    if (!string.IsNullOrWhiteSpace(dto.ServerUrl))
                    {
                        ServerUrl = dto.ServerUrl;
                    }

                    DismissedUpdateVersion = dto.DismissedUpdateVersion;
                    Window = dto.Window ?? new WindowPlacement();
                }
            }
        }
        catch
        {
            // 손상된 설정은 기본값으로 대체한다.
        }

        try
        {
            if (File.Exists(_tokenPath))
            {
                var bytes = File.ReadAllBytes(_tokenPath);
                var plain = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
                AdminToken = Encoding.UTF8.GetString(plain);
            }
        }
        catch
        {
            // DPAPI 복호화 실패(다른 사용자/기기)는 토큰이 없는 것으로 처리한다.
            AdminToken = "";
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
        public string? DismissedUpdateVersion { get; set; }
        public WindowPlacement? Window { get; set; }
    }
}

/// <summary>창 위치/크기 및 최대화 상태.</summary>
public sealed class WindowPlacement
{
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public double Width { get; set; } = 1280;
    public double Height { get; set; } = 800;
    public bool Maximized { get; set; }
}
