using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using UpdateAgent.LabManager.Configuration;
using UpdateAgent.Shared;

namespace UpdateAgent.LabManager.Auth;

/// <summary>에이전트/관리자 토큰을 경로별로 검사한다.</summary>
public sealed class TokenAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IOptionsMonitor<LabManagerOptions> _options;
    private readonly ILogger<TokenAuthMiddleware> _logger;

    public TokenAuthMiddleware(
        RequestDelegate next,
        IOptionsMonitor<LabManagerOptions> options,
        ILogger<TokenAuthMiddleware> logger)
    {
        _next = next;
        _options = options;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;

        if (path.StartsWithSegments("/api/agents"))
        {
            if (!IsAuthorized(context, UpdateAgentConstants.AgentTokenHeader, _options.CurrentValue.AgentToken))
            {
                await RejectAsync(context, "에이전트 토큰이 올바르지 않습니다.");
                return;
            }
        }
        else if (path.StartsWithSegments("/api/admin"))
        {
            if (!IsAuthorized(context, UpdateAgentConstants.AdminTokenHeader, _options.CurrentValue.AdminToken))
            {
                await RejectAsync(context, "관리자 토큰이 올바르지 않습니다.");
                return;
            }
        }

        await _next(context);
    }

    private bool IsAuthorized(HttpContext context, string headerName, string expected)
    {
        if (string.IsNullOrWhiteSpace(expected)) return false;
        if (!context.Request.Headers.TryGetValue(headerName, out var values)) return false;

        var provided = values.ToString();
        if (string.IsNullOrEmpty(provided)) return false;

        return FixedTimeEquals(provided, expected);
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var hashA = SHA256.HashData(Encoding.UTF8.GetBytes(a));
        var hashB = SHA256.HashData(Encoding.UTF8.GetBytes(b));
        return CryptographicOperations.FixedTimeEquals(hashA, hashB);
    }

    private async Task RejectAsync(HttpContext context, string message)
    {
        _logger.LogWarning(
            "인증 실패: {Method} {Path} from {Ip}",
            context.Request.Method,
            context.Request.Path,
            context.Connection.RemoteIpAddress);

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(new { error = message });
    }
}
