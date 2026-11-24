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
    private readonly bool _strictUrlValidation;

    /// <summary>기본 호환 생성자. 스킴만 검증한다.</summary>
    public GitHubReleaseChecker(HttpClient httpClient)
        : this(httpClient, strictUrlValidation: false)
    {
    }

    /// <summary>
    /// 대시보드/서비스처럼 링크를 그대로 여는 호출자는 GitHub 호스트까지 허용한다.
    /// </summary>
    public GitHubReleaseChecker(HttpClient httpClient, bool strictUrlValidation)
    {
        _httpClient = httpClient;
        _strictUrlValidation = strictUrlValidation;
    }

    /// <summary>http(s) 스킴과 (선택적으로) GitHub 호스트만 허용한다.</summary>
    public static bool IsAllowedExternalUrl(string? value, bool requireGitHubHost = false) =>
        UpdateAgent.Guards.UrlGuard.IsAllowedExternalUrl(value, requireGitHubHost);

    private string SanitizeUrl(string? value, string fallback)
    {
        if (!IsAllowedExternalUrl(value, requireGitHubHost: _strictUrlValidation)) return fallback;
        // https를 선호한다. http 링크는 공식 저장소 주소로 되돌린다.
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
               string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            ? fallback
            : value!;
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
                ReleaseUrl = SanitizeUrl(release.HtmlUrl, UpdateAgentConstants.GitHubReleasesUrl),
                RepositoryUrl = UpdateAgentConstants.GitHubRepositoryUrl,
                AssetName = asset?.Name,
                AssetUrl = asset is null
                    ? null
                    : SanitizeUrl(asset.BrowserDownloadUrl, UpdateAgentConstants.GitHubReleasesUrl),
                AssetSizeBytes = asset?.Size,
                CheckedAt = UpdateAgent.Guards.UtcClock.Now,
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

    /// <summary>태그/버전 문자열에서 앞의 v와 공백을 제거한다. 규칙은 Guard DLL에 단일 정의되어 있다.</summary>
    public static string NormalizeVersion(string value) =>
        UpdateAgent.Guards.VersionGuard.Normalize(value);

    /// <summary>candidate가 current보다 새 버전인지 판단한다.</summary>
    public static bool IsNewer(string candidate, string current) =>
        UpdateAgent.Guards.VersionGuard.IsNewer(candidate, current);

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
