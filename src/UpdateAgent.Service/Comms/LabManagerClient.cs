using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using UpdateAgent.Service.Configuration;
using UpdateAgent.Shared;
using UpdateAgent.Shared.Models;
using UpdateAgent.Shared.Serialization;

namespace UpdateAgent.Service.Comms;

/// <summary>교사 PC의 LabManager와 통신하는 HTTP 클라이언트.</summary>
public sealed class LabManagerClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<AgentOptions> _options;
    private readonly ILogger<LabManagerClient> _logger;

    public LabManagerClient(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<AgentOptions> options,
        ILogger<LabManagerClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
    }

    public string? LastError { get; private set; }
    public DateTimeOffset? LastSuccess { get; private set; }

    public async Task<AgentCheckInResponse?> CheckInAsync(
        AgentCheckInRequest request,
        CancellationToken cancellationToken = default)
    {
        var options = _options.CurrentValue;
        if (!options.RemoteEnabled) return null;

        try
        {
            var client = _httpClientFactory.CreateClient("LabManager");
            client.BaseAddress ??= new Uri(options.LabManagerUrl.TrimEnd('/') + "/");
            client.DefaultRequestHeaders.Remove(UpdateAgentConstants.AgentTokenHeader);
            client.DefaultRequestHeaders.Add(UpdateAgentConstants.AgentTokenHeader, options.AgentToken);

            using var response = await client.PostAsJsonAsync(
                "api/agents/checkin",
                request,
                UpdateAgentJson.Options,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                LastError = $"LabManager 응답 {(int)response.StatusCode} {response.ReasonPhrase}";
                _logger.LogWarning("체크인 실패: {Error}", LastError);
                return null;
            }

            var result = await response.Content.ReadFromJsonAsync<AgentCheckInResponse>(
                UpdateAgentJson.Options,
                cancellationToken);

            if (result is not null)
            {
                LastSuccess = DateTimeOffset.Now;
                LastError = null;
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _logger.LogWarning("LabManager 체크인 오류: {Message}", ex.Message);
            return null;
        }
    }
}
