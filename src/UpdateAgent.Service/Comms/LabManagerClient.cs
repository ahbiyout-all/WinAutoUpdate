using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using UpdateAgent.Service.Configuration;
using UpdateAgent.Shared;
using UpdateAgent.Shared.Comms;
using UpdateAgent.Shared.Models;
using UpdateAgent.Shared.Serialization;

namespace UpdateAgent.Service.Comms;

/// <summary>교사 PC의 LabManager와 통신하는 HTTP 클라이언트.</summary>
public sealed class LabManagerClient
{
    private static readonly Regex UrlCredentialsPattern = new(
        @"(?<=://)[^/@\s]+:[^/@\s]+(?=@)",
        RegexOptions.CultureInvariant);

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

    /// <summary>연속 체크인 실패 횟수. 성공 시 0으로 초기화되고 최대 10으로 제한된다.</summary>
    public int ConsecutiveFailures { get; private set; }

    /// <summary>가장 최근 체크인 실패 시각. 성공 시 null로 초기화된다.</summary>
    public DateTimeOffset? LastFailureAt { get; private set; }

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
                RecordFailure(
                    $"LabManager 응답 {(int)response.StatusCode} {response.ReasonPhrase}",
                    "체크인 실패");
                return null;
            }

            var result = await response.Content.ReadFromJsonAsync<AgentCheckInResponse>(
                UpdateAgentJson.Options,
                cancellationToken);

            // HTTP 응답을 받은 시점부터는 실패 연쇄가 끊긴다. 응답 본문이 비어 있어도
            // 다음 체크인을 지연할 이유는 없으므로 재시도 상태를 초기화한다.
            var previousFailures = ConsecutiveFailures;
            ConsecutiveFailures = 0;
            LastFailureAt = null;
            LastError = previousFailures > 0
                ? $"오프라인 복구: 이전 체크인 실패 {previousFailures}회"
                : null;

            if (result is not null)
            {
                LastSuccess = DateTimeOffset.Now;
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RecordFailure(ex.Message, "LabManager 체크인 오류");
            return null;
        }
    }

    private void RecordFailure(string message, string logPrefix)
    {
        ConsecutiveFailures = Math.Min(ConsecutiveFailures + 1, CheckInRetryPolicy.MaxExponent);
        LastFailureAt = DateTimeOffset.Now;
        LastError = Redact(message);
        _logger.LogWarning("{LogPrefix}: {Error}", logPrefix, LastError);
    }

    /// <summary>설정된 에이전트 토큰이 오류/로그에 섞이지 않도록 제거한다.</summary>
    private string? Redact(string? message)
    {
        if (string.IsNullOrEmpty(message)) return message;

        var token = _options.CurrentValue.AgentToken;
        var redacted = string.IsNullOrEmpty(token)
            ? message
            : message.Replace(token, "[redacted]", StringComparison.Ordinal);

        return UrlCredentialsPattern.Replace(redacted, "[redacted]");
    }
}
