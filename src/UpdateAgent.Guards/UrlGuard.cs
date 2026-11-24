using System.Net;

namespace UpdateAgent.Guards;

/// <summary>
/// 브라우저/릴리스 링크에 적용하는 URL 안전 규칙.
/// 네트워크 요청이나 프로세스 실행 없이 문자열만 검사한다.
/// </summary>
public static class UrlGuard
{
    private static readonly HashSet<string> AllowedGitHubHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.com",
        "www.github.com",
        "api.github.com",
        "objects.githubusercontent.com",
        "githubusercontent.com",
        "raw.githubusercontent.com",
    };

    /// <summary>http(s) 절대 URL인지 검사한다. 사용자 정보 포함 URL은 거부한다.</summary>
    public static bool IsHttpUrl(string? value, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var parsed)) return false;
        if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (string.IsNullOrWhiteSpace(parsed.Host)) return false;
        if (!string.IsNullOrEmpty(parsed.UserInfo)) return false;

        uri = parsed;
        return true;
    }

    /// <summary>외부 링크의 스킴과 (선택적으로) GitHub 호스트를 허용 목록으로 제한한다.</summary>
    public static bool IsAllowedExternalUrl(string? value, bool requireGitHubHost = false)
    {
        if (!IsHttpUrl(value, out var uri) || uri is null) return false;
        return !requireGitHubHost || AllowedGitHubHosts.Contains(uri.Host);
    }

    /// <summary>로컬 서버 주소(localhost 또는 루프백 IP)인지 확인한다.</summary>
    public static bool IsLoopbackHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
    }

    /// <summary>
    /// 앱 모드 브라우저에 넘길 주소 규칙. 로컬 서버는 HTTP를 허용하고 원격 주소는 HTTPS만 허용한다.
    /// 원격 호스트 자체를 제한하지는 않으므로 설정 파일을 쓸 수 있는 사람은 대상을 바꿀 수 있다.
    /// </summary>
    public static bool IsSafeBrowserUrl(string? value)
    {
        if (!IsHttpUrl(value, out var uri) || uri is null) return false;
        return IsLoopbackHost(uri.Host) ||
               string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>안전한 주소를 정규화한다. localhost HTTP는 127.0.0.1로 바꾼다. 실패하면 null.</summary>
    public static string? NormalizeBrowserUrl(string? value)
    {
        if (!IsSafeBrowserUrl(value) || !IsHttpUrl(value, out var uri) || uri is null) return null;

        var normalized = new UriBuilder(uri);
        if (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            normalized.Host = "127.0.0.1";
        }
        return normalized.Uri.AbsoluteUri;
    }

    /// <summary>
    /// 정규화된 서버 주소의 경로 끝에 상대 경로를 붙인다. 쿼리/조각과 포트는 유지한다.
    /// 예: https://host/ua/?tenant=1 + monitor.html → https://host/ua/monitor.html?tenant=1
    /// </summary>
    public static string? CombineBrowserUrl(string? serverUrl, string? relativePath)
    {
        var normalized = NormalizeBrowserUrl(serverUrl);
        if (normalized is null || string.IsNullOrWhiteSpace(relativePath)) return null;
        if (!IsHttpUrl(normalized, out var uri) || uri is null) return null;

        var segment = "/" + relativePath.Trim().TrimStart('/');
        var builder = new UriBuilder(uri)
        {
            Path = uri.AbsolutePath.TrimEnd('/') + segment,
        };
        return builder.Uri.AbsoluteUri;
    }
}
