using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UpdateAgent.LabManager.Desktop.Models;
using UpdateAgent.Shared;
using UpdateAgent.Shared.Updates;

namespace UpdateAgent.LabManager.Desktop.Services;

/// <summary>관리자 토큰이 거부되었을 때 발생.</summary>
public sealed class ApiUnauthorizedException : Exception
{
    public ApiUnauthorizedException() : base("관리자 토큰이 올바르지 않습니다.") { }
}

/// <summary>API 요청 실패.</summary>
public sealed class ApiException : Exception
{
    public ApiException(string message) : base(message) { }
}

/// <summary>
/// LabManager HTTP API 클라이언트. 브라우저를 전혀 사용하지 않고 X-Admin-Token 헤더로 통신한다.
/// </summary>
public sealed class ApiClient
{
    private readonly DesktopSettings _settings;
    private readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private HttpClient _http;

    public ApiClient(DesktopSettings settings)
    {
        _settings = settings;
        _http = CreateClient();
    }

    /// <summary>주소/토큰 변경 후 호출한다.</summary>
    public void RefreshConfiguration()
    {
        _http.Dispose();
        _http = CreateClient();
        _settings.Save();
    }

    private HttpClient CreateClient()
    {
        var address = _settings.ServerUrl;
        if (Uri.TryCreate(address, UriKind.Absolute, out var uri) &&
            uri.Scheme == Uri.UriSchemeHttp &&
            uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            // localhost 해석이 느린 환경이 있어 루프백 IP를 직접 사용한다(HTTP 전용).
            address = new UriBuilder(uri) { Host = "127.0.0.1" }.Uri.AbsoluteUri;
        }

        return new HttpClient
        {
            BaseAddress = new Uri(address.TrimEnd('/') + "/", UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    public Task<HealthResponse> GetHealthAsync(CancellationToken cancellationToken = default) =>
        SendAsync<HealthResponse>(HttpMethod.Get, "api/health", cancellationToken: cancellationToken);

    public Task<OverviewResponse> GetOverviewAsync(CancellationToken cancellationToken = default) =>
        SendAsync<OverviewResponse>(HttpMethod.Get, "api/admin/overview", cancellationToken: cancellationToken);

    public Task<List<AgentRecord>> GetAgentsAsync(CancellationToken cancellationToken = default) =>
        SendAsync<List<AgentRecord>>(HttpMethod.Get, "api/admin/agents", cancellationToken: cancellationToken);

    public Task<List<LabJobRecord>> GetJobsAsync(int limit = 60, CancellationToken cancellationToken = default) =>
        SendAsync<List<LabJobRecord>>(
            HttpMethod.Get, $"api/admin/jobs?limit={limit}", cancellationToken: cancellationToken);

    public Task<List<AuditEntry>> GetAuditAsync(int limit = 60, CancellationToken cancellationToken = default) =>
        SendAsync<List<AuditEntry>>(
            HttpMethod.Get, $"api/admin/audit?limit={limit}", cancellationToken: cancellationToken);

    public Task<JobDetailResponse> GetJobDetailAsync(string jobId, CancellationToken cancellationToken = default) =>
        SendAsync<JobDetailResponse>(
            HttpMethod.Get, $"api/admin/jobs/{Uri.EscapeDataString(jobId)}", cancellationToken: cancellationToken);

    public Task<AppUpdateInfo> GetUpdateInfoAsync(bool force = false, CancellationToken cancellationToken = default) =>
        SendAsync<AppUpdateInfo>(
            HttpMethod.Get,
            force ? "api/admin/update?force=true" : "api/admin/update",
            cancellationToken: cancellationToken);

    public Task<WakeResponse> WakeAsync(string agentId, CancellationToken cancellationToken = default) =>
        SendAsync<WakeResponse>(
            HttpMethod.Post, $"api/admin/agents/{Uri.EscapeDataString(agentId)}/wake", cancellationToken: cancellationToken);

    public Task<object?> UnlockAsync(string agentId, CancellationToken cancellationToken = default) =>
        SendAsync<object?>(
            HttpMethod.Post, $"api/admin/agents/{Uri.EscapeDataString(agentId)}/unlock", cancellationToken: cancellationToken);

    public Task<object?> RebootAsync(string agentId, CancellationToken cancellationToken = default) =>
        SendAsync<object?>(
            HttpMethod.Post,
            $"api/admin/agents/{Uri.EscapeDataString(agentId)}/reboot",
            new RebootRequest(),
            cancellationToken);

    public Task<object?> ShutdownAsync(string agentId, CancellationToken cancellationToken = default) =>
        SendAsync<object?>(
            HttpMethod.Post,
            $"api/admin/agents/{Uri.EscapeDataString(agentId)}/shutdown",
            new ShutdownRequest(),
            cancellationToken);

    public Task<object?> JobActionAsync(string jobId, string action, CancellationToken cancellationToken = default) =>
        SendAsync<object?>(
            HttpMethod.Post,
            $"api/admin/jobs/{Uri.EscapeDataString(jobId)}/{action}",
            cancellationToken: cancellationToken);

    public Task<StartJobsResponse> StartJobsAsync(StartJobsRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<StartJobsResponse>(
            HttpMethod.Post, "api/admin/jobs/start", request, cancellationToken);

    public Task<object?> ConfirmMaestroProtectionSuspendedAsync(
        string jobId, CancellationToken cancellationToken = default) =>
        SendAsync<object?>(
            HttpMethod.Post,
            $"api/admin/jobs/{Uri.EscapeDataString(jobId)}/maestro/protection-suspended",
            cancellationToken: cancellationToken);

    public Task<object?> ConfirmMaestroRestoredAsync(
        string jobId, CancellationToken cancellationToken = default) =>
        SendAsync<object?>(
            HttpMethod.Post,
            $"api/admin/jobs/{Uri.EscapeDataString(jobId)}/maestro/restored",
            new MaestroRestoreRequest { StateSaved = true, ProtectionRestored = true },
            cancellationToken);

    private async Task<T> SendAsync<T>(
        HttpMethod method,
        string path,
        object? body = null,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method, path);

        if (!string.IsNullOrEmpty(_settings.AdminToken))
        {
            request.Headers.TryAddWithoutValidation(
                UpdateAgentConstants.AdminTokenHeader, _settings.AdminToken);
        }

        if (body is not null)
        {
            request.Content = new StringContent(
                JsonSerializer.Serialize(body, _json), Encoding.UTF8, "application/json");
        }

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new ApiUnauthorizedException();
        }

        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string? message = null;
            try
            {
                message = JsonSerializer.Deserialize<ErrorResponse>(text, _json)?.Error;
            }
            catch
            {
                // 오류 본문이 JSON이 아닐 수 있다.
            }

            throw new ApiException(message ?? $"요청 실패 ({(int)response.StatusCode})");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return default!;
        }

        return JsonSerializer.Deserialize<T>(text, _json) ?? default!;
    }
}
