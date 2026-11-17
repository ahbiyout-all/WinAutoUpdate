using System.Diagnostics;

namespace UpdateAgent.LabManager.Desktop.Views;

/// <summary>외부 GitHub 링크는 OS 기본 브라우저로 연다. 앱 안에는 브라우저/WebView를 쓰지 않는다.</summary>
internal static class ExternalLinks
{
    internal static void Open(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url)
            {
                UseShellExecute = true,
            });
        }
        catch
        {
            // 기본 브라우저가 없으면 조용히 무시한다.
        }
    }
}
