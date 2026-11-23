using System.Text.Json;
using System.Text.Json.Serialization;

namespace UpdateAgent.Shared.Updates;

/// <summary>
/// GitHub Releases의 최신 릴리스를 조회하고 현재 버전과 비교한다.
/// 저장소가 비공개이면 인증 없이는 조회할 수 없으므로 실패 결과를 돌려준다.
/// </summary>
public sealed class GitHubReleaseChecker
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private readonly HttpClient _httpClient;

    public GitHubReleaseChecker(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>기본 호출은 에이전트용이다. LabManager는 명시적으로 제품을 지정한다.</summary>
    public Task<AppUpdateInfo> CheckAsync(CancellationToken cancellationToken = default) =>
        CheckAsync(UpdateProduct.Agent, cancellationToken);

    public async Task<AppUpdateInfo> CheckAsync(
        UpdateProduct product,
        CancellationToken cancellationToken = default)
    {
        var current = product == UpdateProduct.LabManager
            ? UpdateAgentConstants.LabManagerVersion
            : UpdateAgentConstants.AgentVersion;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, UpdateAgentConstants.LatestReleaseApiUrl);
            request.Headers.UserAgent.ParseAdd($"{UpdateAgentConstants.ProductName}/{current}");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return AppUpdateInfo.Failed(current, $"GitHub API 응답 {(int)response.StatusCode}");
            }

            await using var stream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);

            var release = await JsonSerializer
                .DeserializeAsync<GitHubRelease>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            if (release is null || string.IsNullOrWhiteSpace(release.TagName))
            {
                return AppUpdateInfo.Failed(current, "릴리스 정보를 해석할 수 없습니다.");
            }

            var latest = NormalizeVersion(release.TagName);

            // 릴리스에는 설치 파일만 첨부한다.
            // 같은 릴리스를 에이전트/LabManager가 공유하므로 제품 접두사로 자산을 먼저 고른다.
            // 다른 제품의 설치 파일을 내려받지 않도록 제품 접두사 자체만 사용한다.
            // 제품 자산이 없으면 다운로드 링크 없이 릴리스 페이지만 안내한다.
            var assets = release.Assets ?? [];
            var prefix = product == UpdateProduct.LabManager
                ? "UpdateAgent.LabManager-"
                : "UpdateAgent.Agent-";
            var candidates = assets
                .Where(a => a.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var asset =
                candidates.FirstOrDefault(a =>
                    a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                    a.Name.Contains("Setup", StringComparison.OrdinalIgnoreCase))
                ?? candidates.FirstOrDefault(a => a.Name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
                ?? candidates.FirstOrDefault();

            return new AppUpdateInfo
            {
                CheckSucceeded = true,
                UpdateAvailable = IsNewer(latest, current),
                CurrentVersion = current,
                LatestVersion = latest,
                ReleaseName = release.Name,
                ReleaseNotes = release.Body,
                PublishedAt = release.PublishedAt,
                ReleaseUrl = string.IsNullOrWhiteSpace(release.HtmlUrl)
                    ? UpdateAgentConstants.GitHubReleasesUrl
                    : release.HtmlUrl,
                RepositoryUrl = UpdateAgentConstants.GitHubRepositoryUrl,
                AssetName = asset?.Name,
                AssetUrl = asset?.BrowserDownloadUrl,
                AssetSizeBytes = asset?.Size,
                CheckedAt = DateTimeOffset.Now,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return AppUpdateInfo.Failed(current, ex.Message);
        }
    }

    /// <summary>태그/버전 문자열에서 앞의 v와 공백을 제거한다.</summary>
    public static string NormalizeVersion(string value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
        {
            normalized = normalized[1..];
        }

        return normalized.Trim();
    }

    /// <summary>candidate가 current보다 새 버전인지 판단한다.</summary>
    public static bool IsNewer(string candidate, string current)
    {
        if (TryParseVersion(candidate, out var candidateVersion) &&
            TryParseVersion(current, out var currentVersion))
        {
            return candidateVersion > currentVersion;
        }

        // 숫자 버전으로 해석할 수 없는 태그는 업데이트로 취급하지 않는다.
        // (예: release-2026, nightly 등이 잘못된 다운로드 팝업을 띄우지 않도록)
        return false;
    }

    private static bool TryParseVersion(string value, out Version version)
    {
        var normalized = NormalizeVersion(value);
        var metadata = normalized.IndexOfAny(['-', '+']);
        if (metadata >= 0)
        {
            normalized = normalized[..metadata];
        }

        return Version.TryParse(normalized, out version!);
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("body")]
        public string? Body { get; init; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; init; }

        [JsonPropertyName("published_at")]
        public DateTimeOffset? PublishedAt { get; init; }

        [JsonPropertyName("assets")]
        public List<GitHubAsset>? Assets { get; init; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; init; }

        [JsonPropertyName("size")]
        public long? Size { get; init; }
    }
}
