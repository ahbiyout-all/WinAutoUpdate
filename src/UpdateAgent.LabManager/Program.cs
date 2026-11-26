using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Serilog;
using UpdateAgent.LabManager.Auth;
using UpdateAgent.LabManager.Configuration;
using UpdateAgent.LabManager.Live;
using UpdateAgent.LabManager.Models;
using UpdateAgent.LabManager.Services;
using UpdateAgent.LabManager.Storage;
using UpdateAgent.Shared;
using UpdateAgent.Shared.Models;
using UpdateAgent.Shared.Serialization;

// --headless/--no-window는 런처가 서버를 조용히 띄울 때 전달하는 플래그다.
// 서버는 웹 대시보드와 API를 함께 제공하고, 교사용 창은 브라우저 앱 모드로 연다.
var hostArgs = args
    .Where(argument =>
        !argument.StartsWith("--headless", StringComparison.OrdinalIgnoreCase) &&
        !argument.StartsWith("--no-window", StringComparison.OrdinalIgnoreCase))
    .ToArray();

var builder = WebApplication.CreateBuilder(hostArgs);

builder.Host.UseSerilog((context, logger) => logger
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UpdateAgent",
            "LabManager",
            "logs",
            "labmanager-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        shared: true));

builder.Services.Configure<LabManagerOptions>(
    builder.Configuration.GetSection(LabManagerOptions.SectionName));

// 파일럿 배포에서 기본 토큰이 그대로 사용되면 포트 5080에 접근할 수 있는 누구나
// 재부팅/작업 시작 API를 호출할 수 있다. 개발용 예외 옵션이 없으면 시작을 중단한다.
var startupOptions = builder.Configuration
    .GetSection(LabManagerOptions.SectionName)
    .Get<LabManagerOptions>() ?? new LabManagerOptions();
var configuredAdminToken = UpdateAgent.Guards.TokenGuard.Normalize(startupOptions.AdminToken);
var configuredAgentToken = UpdateAgent.Guards.TokenGuard.Normalize(startupOptions.AgentToken);
if (!startupOptions.AllowDefaultTokens)
{
    var invalidTokens = new List<string>();
    // change-me-* 및 sample-*-replace-before-pilot 모두 공개된 값이므로 시작을 막는다.
    if (UpdateAgent.Guards.TokenGuard.IsPlaceholder(configuredAdminToken))
        invalidTokens.Add("AdminToken");

    if (UpdateAgent.Guards.TokenGuard.IsPlaceholder(configuredAgentToken))
        invalidTokens.Add("AgentToken");

    if (invalidTokens.Count > 0)
    {
        throw new InvalidOperationException(
            $"기본/빈 토큰으로 LabManager를 시작할 수 없습니다: {string.Join(", ", invalidTokens)}. " +
            "scripts\\configure-labmanager.ps1을 실행하거나 appsettings.json의 LabManager 토큰을 변경하세요. " +
            "로컬 개발에서만 LabManager:AllowDefaultTokens=true를 사용하세요.");
    }
}

// 관리자와 에이전트가 같은 토큰을 쓰면 학생도 관리자 API를 호출할 수 있다.
// 값을 출력하지 않고, 학생/서버 빌드 간에 의도적으로 같은 에이전트 토큰은 건드리지 않는다.
if (!UpdateAgent.Guards.TokenGuard.AreDistinct(configuredAdminToken, configuredAgentToken))
{
    throw new InvalidOperationException(
        "관리자 토큰과 에이전트 토큰은 서로 달라야 합니다. " +
        "scripts\\configure-labmanager.ps1을 실행하거나 appsettings.json의 LabManager 토큰을 변경하세요.");
}

builder.Services.ConfigureHttpJsonOptions(options =>
{
    var serializer = options.SerializerOptions;
    serializer.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    serializer.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
    serializer.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    serializer.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});

builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<IOptions<LabManagerOptions>>().Value;
    return new LabStore(options.ResolveDatabasePath(), options.OnlineThresholdSeconds);
});
builder.Services.AddSingleton<WakeOnLanService>();
builder.Services.AddHttpClient("github", client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddSingleton<UpdateCheckService>();
builder.Services.AddSingleton<LiveEventBus>();
builder.Services.AddSingleton<AgentPolicyService>();
builder.Services.AddSingleton<CheckinGateRegistry>();

// 대시보드에서 변경한 관리자 토큰은 %LOCALAPPDATA%의 override 파일에 저장되며,
// 미들웨어가 요청마다 최신 값을 읽으므로 재시작 없이 즉시 적용된다.
builder.Services.AddSingleton<AdminTokenStore>();

var app = builder.Build();

app.UseSerilogRequestLogging(options =>
{
    options.MessageTemplate = "{RequestMethod} {RequestPath} → {StatusCode} ({Elapsed:0.0} ms)";
});

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseMiddleware<TokenAuthMiddleware>();

var store = app.Services.GetRequiredService<LabStore>();
await store.InitializeAsync();

app.MapGet("/api/health", () => Results.Ok(new
{
    status = "ok",
    product = UpdateAgentConstants.ProductName,
    version = UpdateAgentConstants.AgentVersion,
    serverTime = DateTimeOffset.UtcNow,
}));

// ---------------- 앱 업데이트 확인 ----------------

app.MapGet("/api/admin/update", async (
    bool? force,
    UpdateCheckService updateCheck,
    CancellationToken cancellationToken) =>
{
    var info = await updateCheck.GetAsync(force == true, cancellationToken);
    return Results.Ok(info);
});

// ---------------- 에이전트 체크인 ----------------

app.MapPost("/api/agents/checkin", async (
    AgentCheckInRequest request,
    LabStore labStore,
    AgentPolicyService agentPolicies,
    LiveEventBus liveEvents,
    CheckinGateRegistry gates,
    CancellationToken cancellationToken) =>
{
    var result = await AgentCheckInHandler.ProcessAsync(
        request, labStore, agentPolicies, liveEvents, gates, cancellationToken);

    // 거부된 명령 결과가 있어도 200 + outcomes로 응답해야 에이전트 큐가 막히지 않는다.
    // 잘못된 에이전트 ID/텔레메트리 불일치/다른 에이전트 결과는 여전히 400이다.
    return result.StatusCode switch
    {
        StatusCodes.Status200OK when result.Response is not null => Results.Ok(result.Response),
        StatusCodes.Status429TooManyRequests => Results.StatusCode(StatusCodes.Status429TooManyRequests),
        _ => Results.BadRequest(new { error = result.Error ?? "체크인 요청이 올바르지 않습니다." }),
    };
});

// ---------------- 대시보드 ----------------

app.MapGet("/api/admin/overview", async (LabStore labStore, CancellationToken cancellationToken) =>
{
    var agents = await labStore.GetAgentsAsync(cancellationToken);
    var active = await labStore.CountActiveJobsAsync(cancellationToken);
    var (completed, failed) = await labStore.CountFinishedJobsAsync(cancellationToken);

    return Results.Ok(new OverviewResponse
    {
        AgentCount = agents.Count,
        OnlineCount = agents.Count(a => a.Online),
        BusyCount = agents.Count(a => UpdateAgent.Guards.JobStateRules.IsBusy(a.Telemetry.State.ToString())),
        ActiveJobs = active,
        CompletedJobs = completed,
        FailedJobs = failed,
    });
});

// ---------------- 실시간 작업 현황 (SSE) ----------------

// EventSource는 사용자 지정 헤더를 보낼 수 없으므로 모니터 페이지에서는
// fetch 스트림으로 X-Admin-Token을 전달한다. 기존 관리자 인증 경로를 그대로 사용한다.
// 스트림은 오래 유지되므로 동시 연결 수를 제한해 메모리/DB 커넥션을 보호한다.
using var liveConnectionGate = new SemaphoreSlim(10, 10);

app.MapGet("/api/admin/live", async (
    HttpContext context,
    LabStore labStore,
    LiveEventBus liveEvents,
    CancellationToken cancellationToken) =>
{
    if (!await liveConnectionGate.WaitAsync(TimeSpan.Zero, cancellationToken))
    {
        return Results.StatusCode(StatusCodes.Status429TooManyRequests);
    }
    try
    {
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";
        context.Response.Headers["X-Accel-Buffering"] = "no";

        await context.Response.WriteAsync(": connected\n\n", cancellationToken);
        await context.Response.Body.FlushAsync(cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            // 신호를 먼저 캡처해야 스냅샷을 만드는 동안 온 변경을 놓치지 않는다.
            var version = liveEvents.Version;
            var snapshot = await LiveSnapshotBuilder.BuildAsync(labStore, cancellationToken);
            await context.Response.WriteAsync(
                $"event: snapshot\ndata: {UpdateAgentJson.Serialize(snapshot)}\n\n",
                cancellationToken);
            await context.Response.Body.FlushAsync(cancellationToken);

            // 변경 시 즉시, 없으면 15초마다 한 번 다시 보낸다(오프라인 전환 반영).
            // 대기 작업 하나를 취소해서 재사용하므로 하트비트마다 대기가 쌓이지 않는다.
            using var waitTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            waitTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                await liveEvents.WaitAsync(version, waitTimeout.Token);
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested) break;
                // 타임아웃 = 하트비트. 아래에서 최신 스냅샷을 다시 보낸다.
            }
        }

        return Results.Empty;
    }
    finally
    {
        liveConnectionGate.Release();
    }
});

app.MapGet("/api/admin/agents", async (LabStore labStore, CancellationToken cancellationToken) =>
    Results.Ok(await labStore.GetAgentsAsync(cancellationToken)));

app.MapDelete("/api/admin/agents/{agentId}", async (
    string agentId,
    LabStore labStore,
    CancellationToken cancellationToken) =>
{
    var deleted = await labStore.DeleteAgentAsync(agentId, cancellationToken);
    return deleted ? Results.NoContent() : Results.NotFound(new { error = "등록되지 않은 PC입니다." });
});

// 리터럴 경로는 {agentId} 라우트보다 먼저 등록해야 한다.
app.MapGet("/api/admin/agents/policies", async (
    AgentPolicyService agentPolicies,
    CancellationToken cancellationToken) =>
    Results.Ok(await agentPolicies.GetSummariesAsync(cancellationToken)));

app.MapGet("/api/admin/agents/{agentId}/policy", async (
    string agentId,
    LabStore labStore,
    AgentPolicyService agentPolicies,
    CancellationToken cancellationToken) =>
{
    var agent = await labStore.GetAgentAsync(agentId, cancellationToken);
    if (agent is null) return Results.NotFound(new { error = "등록되지 않은 PC입니다." });

    var effective = await agentPolicies.GetEffectiveAsync(agentId, agent.Agent.Os, cancellationToken);
    return Results.Ok(new AgentPolicyResponse
    {
        AgentId = agentId,
        Policy = effective.Policy,
        HasOverride = effective.HasOverride,
        Source = effective.Source,
        Os = agent.Agent.Os,
    });
});

app.MapPut("/api/admin/agents/{agentId}/policy", async (
    string agentId,
    UpdateAgentPolicyRequest request,
    LabStore labStore,
    AgentPolicyService agentPolicies,
    CancellationToken cancellationToken) =>
{
    var agent = await labStore.GetAgentAsync(agentId, cancellationToken);
    if (agent is null) return Results.NotFound(new { error = "등록되지 않은 PC입니다." });

    var effective = await agentPolicies.SaveAsync(agentId, request.Policy, agent.Agent.Os, cancellationToken);
    await labStore.AddAuditAsync(
        "teacher", "agent-policy", $"{agent.Agent.MachineName}/{agentId}",
        "PC별 업데이트 옵션 저장", cancellationToken);
    return Results.Ok(new AgentPolicyResponse
    {
        AgentId = agentId,
        Policy = effective.Policy,
        HasOverride = true,
        Source = effective.Source,
        Os = agent.Agent.Os,
    });
});

app.MapDelete("/api/admin/agents/{agentId}/policy", async (
    string agentId,
    LabStore labStore,
    AgentPolicyService agentPolicies,
    CancellationToken cancellationToken) =>
{
    var agent = await labStore.GetAgentAsync(agentId, cancellationToken);
    if (agent is null) return Results.NotFound(new { error = "등록되지 않은 PC입니다." });

    await agentPolicies.DeleteAsync(agentId, cancellationToken);
    await labStore.AddAuditAsync(
        "teacher", "agent-policy-reset", $"{agent.Agent.MachineName}/{agentId}",
        "PC별 업데이트 옵션을 기본값으로 재설정", cancellationToken);
    return Results.NoContent();
});

app.MapGet("/api/admin/agents/{agentId}", async (
    string agentId,
    LabStore labStore,
    CancellationToken cancellationToken) =>
{
    var agent = await labStore.GetAgentAsync(agentId, cancellationToken);
    return agent is null ? Results.NotFound() : Results.Ok(agent);
});

app.MapGet("/api/admin/agents/{agentId}/commands", async (
    string agentId,
    LabStore labStore,
    CancellationToken cancellationToken) =>
    Results.Ok(await labStore.GetCommandsAsync(agentId, 100, cancellationToken)));

app.MapPost("/api/admin/agents/{agentId}/commands", async (
    string agentId,
    CommandRequest request,
    LabStore labStore,
    CancellationToken cancellationToken) =>
{
    var agent = await labStore.GetAgentAsync(agentId, cancellationToken);
    if (agent is null) return Results.NotFound(new { error = "에이전트를 찾을 수 없습니다." });

    var payloadJson = request.Payload is { } payload
        ? payload.GetRawText()
        : "{}";

    var command = NewCommand(agentId, request.Type, payloadJson);
    await labStore.AddCommandAsync(command, cancellationToken);
    await labStore.AddAuditAsync(
        "teacher",
        "command",
        $"{agent.Agent.MachineName}/{agentId}",
        $"{request.Type} 명령 발급",
        cancellationToken);

    return Results.Ok(command);
});

app.MapPost("/api/admin/agents/{agentId}/wake", async (
    string agentId,
    LabStore labStore,
    WakeOnLanService wol,
    CancellationToken cancellationToken) =>
{
    var agent = await labStore.GetAgentAsync(agentId, cancellationToken);
    if (agent is null) return Results.NotFound(new { error = "에이전트를 찾을 수 없습니다." });

    var results = await wol.WakeAsync(
        agent.Agent.MacAddresses,
        agent.Agent.IpAddress,
        cancellationToken);

    await labStore.AddAuditAsync(
        "teacher",
        "wake",
        agent.Agent.MachineName,
        results.Count == 0
            ? "MAC 주소 없음"
            : string.Join(", ", results.Select(r => $"{r.MacAddress}:{(r.Sent ? "전송" : "실패")}")),
        cancellationToken);

    return Results.Ok(new WakeResponse { Results = results });
});

app.MapPost("/api/admin/agents/{agentId}/reboot", async (
    string agentId,
    RebootRequest request,
    LabStore labStore,
    CancellationToken cancellationToken) =>
{
    var agent = await labStore.GetAgentAsync(agentId, cancellationToken);
    if (agent is null) return Results.NotFound(new { error = "에이전트를 찾을 수 없습니다." });

    var payload = UpdateAgentJson.Serialize(new RebootCommandPayload
    {
        DelaySeconds = request.DelaySeconds,
        Reason = request.Reason,
    });

    var command = NewCommand(agentId, CommandType.Reboot, payload);
    await labStore.AddCommandAsync(command, cancellationToken);
    await labStore.AddAuditAsync(
        "teacher", "reboot", agent.Agent.MachineName,
        $"{request.DelaySeconds}초 후 재부팅 명령", cancellationToken);

    return Results.Ok(command);
});

app.MapPost("/api/admin/agents/{agentId}/shutdown", async (
    string agentId,
    ShutdownRequest request,
    LabStore labStore,
    CancellationToken cancellationToken) =>
{
    var agent = await labStore.GetAgentAsync(agentId, cancellationToken);
    if (agent is null) return Results.NotFound(new { error = "에이전트를 찾을 수 없습니다." });

    var payload = UpdateAgentJson.Serialize(new ShutdownCommandPayload
    {
        DelaySeconds = request.DelaySeconds,
        Reason = request.Reason,
    });

    var command = NewCommand(agentId, CommandType.Shutdown, payload);
    await labStore.AddCommandAsync(command, cancellationToken);
    await labStore.AddAuditAsync(
        "teacher", "shutdown", agent.Agent.MachineName,
        $"{request.DelaySeconds}초 후 종료 명령", cancellationToken);

    return Results.Ok(command);
});

app.MapPost("/api/admin/agents/{agentId}/unlock", async (
    string agentId,
    LabStore labStore,
    CancellationToken cancellationToken) =>
{
    var agent = await labStore.GetAgentAsync(agentId, cancellationToken);
    if (agent is null) return Results.NotFound(new { error = "에이전트를 찾을 수 없습니다." });

    var command = NewCommand(agentId, CommandType.UnlockInput, "{}");
    await labStore.AddCommandAsync(command, cancellationToken);
    await labStore.AddAuditAsync(
        "teacher", "unlock", agent.Agent.MachineName, "입력 잠금 해제", cancellationToken);

    return Results.Ok(command);
});

// ---------------- 작업 ----------------

app.MapGet("/api/admin/jobs", async (
    int? limit,
    LabStore labStore,
    CancellationToken cancellationToken) =>
{
    var jobs = await labStore.GetJobsAsync(Math.Clamp(limit ?? 100, 1, 500), cancellationToken);
    return Results.Ok(jobs.Select(j => new
    {
        j.Summary,
        j.MachineName,
    }));
});

app.MapGet("/api/admin/jobs/{jobId}", async (
    string jobId,
    LabStore labStore,
    CancellationToken cancellationToken) =>
{
    var job = await labStore.GetJobAsync(jobId, cancellationToken);
    if (job is null) return Results.NotFound(new { error = "작업을 찾을 수 없습니다." });

    var checklist = await labStore.GetChecklistAsync(jobId, cancellationToken);
    return Results.Ok(new
    {
        job.Summary,
        job.Detail,
        job.MachineName,
        Maestro = checklist,
        BeforeStartMessages = checklist?.BeforeStartMessages ?? [],
        AfterCompleteMessages = checklist?.AfterCompleteMessages ?? [],
    });
});

app.MapPost("/api/admin/jobs/start", async (
    StartJobsRequest request,
    LabStore labStore,
    AgentPolicyService agentPolicies,
    LiveEventBus liveEvents,
    CancellationToken cancellationToken) =>
{
    if (request is null || !request.HasTargets)
    {
        return Results.BadRequest(new { error = "대상 PC를 선택하세요." });
    }

    var jobIds = new List<string>();
    var errors = new List<string>();
    var warnings = new List<string>();
    var now = UpdateAgent.Guards.UtcClock.Now;

    foreach (var agentId in request.AgentIds.Distinct(StringComparer.OrdinalIgnoreCase))
    {
        var agent = await labStore.GetAgentAsync(agentId, cancellationToken);
        if (agent is null)
        {
            errors.Add($"{agentId}: 등록되지 않은 PC");
            continue;
        }

        // Maestro가 설치된 PC와 감지 자체가 실패한 PC는 모두 보호 해제 확인을 건너뛸 수 없다.
        if (AgentPolicyService.RequiresMaestroConfirmation(agent) &&
            !request.MaestroConfirmed)
        {
            errors.Add($"{agent.Agent.MachineName}: Maestro 보호 해제 확인이 필요합니다.");
            continue;
        }

        var os = agent.Agent.Os;
        var (policy, policyWarnings) = await agentPolicies.ResolveForStartAsync(
            agentId, os, request.Policy, request.SaveAsAgentPolicy,
            agent.Agent.MachineName, cancellationToken);
        warnings.AddRange(policyWarnings);

        if (!agent.Online)
        {
            errors.Add($"{agent.Agent.MachineName}: 오프라인 (WoL 후 다시 시도)");
            continue;
        }

        var jobId = UpdateAgent.Guards.JobIdGuard.Create(now, Guid.NewGuid());

        await labStore.SaveJobSummaryAsync(new JobSummary
        {
            JobId = jobId,
            AgentId = agentId,
            AgentName = agent.Agent.MachineName,
            State = UpdateJobState.Pending,
            Phase = "명령 전달 대기",
            Message = "교사가 업데이트 작업을 배정했습니다.",
            StartedAt = now,
            UpdatedAt = now,
            ReportedAt = now,
            MaxRebootCount = policy.MaxRebootCount,
            RequiresMaestroAttention = policy.RequireMaestroManualConfirmation,
        }, agent.Agent.MachineName, cancellationToken);

        await labStore.SaveChecklistAsync(new MaestroChecklist
        {
            JobId = jobId,
            ProtectionSuspendedConfirmed = request.MaestroConfirmed,
            ProtectionSuspendedConfirmedAt = request.MaestroConfirmed ? now : null,
            ConfirmedBy = request.MaestroConfirmed ? request.ConfirmedBy : null,
            RequiresManualConfirmation = policy.RequireMaestroManualConfirmation,
        }, cancellationToken);

        var payload = UpdateAgentJson.Serialize(new StartJobCommandPayload
        {
            JobId = jobId,
            Policy = policy,
            MaestroConfirmed = request.MaestroConfirmed,
            ConfirmedBy = request.ConfirmedBy,
        });

        await labStore.AddCommandAsync(
            NewCommand(agentId, CommandType.StartJob, payload),
            cancellationToken);

        await labStore.AddAuditAsync(
            "teacher",
            "job-start",
            $"{agent.Agent.MachineName}/{jobId}",
            string.IsNullOrWhiteSpace(request.Note) ? "업데이트 작업 시작" : request.Note,
            cancellationToken);

        jobIds.Add(jobId);
    }

    liveEvents.Signal();
    return Results.Ok(new StartJobsResponse
    {
        JobIds = jobIds,
        Errors = errors,
        Warnings = warnings.Distinct(StringComparer.Ordinal).ToList(),
    });
});

app.MapPost("/api/admin/jobs/{jobId}/pause", async (
    string jobId, LabStore labStore, LiveEventBus liveEvents, CancellationToken cancellationToken) =>
    await SendJobCommandAsync(jobId, CommandType.PauseJob, "{}", "pause", labStore, liveEvents, cancellationToken));

app.MapPost("/api/admin/jobs/{jobId}/resume", async (
    string jobId, LabStore labStore, LiveEventBus liveEvents, CancellationToken cancellationToken) =>
    await SendJobCommandAsync(jobId, CommandType.ResumeJob, "{}", "resume", labStore, liveEvents, cancellationToken));

app.MapPost("/api/admin/jobs/{jobId}/cancel", async (
    string jobId, LabStore labStore, LiveEventBus liveEvents, CancellationToken cancellationToken) =>
    await SendJobCommandAsync(jobId, CommandType.CancelJob, "{}", "cancel", labStore, liveEvents, cancellationToken));

// 개별 수동 업데이트 강제 중단: 예약 재부팅·남은 업데이트·오버레이 잠금을 즉시 해제한다.
app.MapPost("/api/admin/jobs/{jobId}/force-stop", async (
    string jobId, LabStore labStore, LiveEventBus liveEvents, CancellationToken cancellationToken) =>
    await SendJobCommandAsync(
        jobId,
        CommandType.CancelJob,
        UpdateAgentJson.Serialize(new CancelJobCommandPayload { Force = true }),
        "force-stop",
        labStore,
        liveEvents,
        cancellationToken));

// ---------------- Maestro 수동 확인 ----------------

app.MapGet("/api/admin/jobs/{jobId}/maestro", async (
    string jobId, LabStore labStore, CancellationToken cancellationToken) =>
{
    var checklist = await labStore.GetChecklistAsync(jobId, cancellationToken);
    return checklist is null
        ? Results.NotFound(new { error = "Maestro 확인 정보가 없습니다." })
        : Results.Ok(checklist);
});

app.MapPost("/api/admin/jobs/{jobId}/maestro/protection-suspended", async (
    string jobId,
    LabStore labStore,
    LiveEventBus liveEvents,
    CancellationToken cancellationToken) =>
{
    var checklist = await labStore.GetChecklistAsync(jobId, cancellationToken);
    if (checklist is null) return Results.NotFound(new { error = "작업을 찾을 수 없습니다." });

    var updated = checklist with
    {
        ProtectionSuspendedConfirmed = true,
        ProtectionSuspendedConfirmedAt = DateTimeOffset.UtcNow,
        ConfirmedBy = "teacher",
    };
    await labStore.SaveChecklistAsync(updated, cancellationToken);
    await labStore.AddAuditAsync(
        "teacher", "maestro-protection-off", jobId,
        "Maestro 복원 보호 해제 확인", cancellationToken);

    liveEvents.Signal();
    return Results.Ok(updated);
});

app.MapPost("/api/admin/jobs/{jobId}/maestro/restored", async (
    string jobId,
    MaestroRestoreRequest request,
    LabStore labStore,
    LiveEventBus liveEvents,
    CancellationToken cancellationToken) =>
{
    var checklist = await labStore.GetChecklistAsync(jobId, cancellationToken);
    if (checklist is null) return Results.NotFound(new { error = "작업을 찾을 수 없습니다." });

    var updated = checklist with
    {
        StateSavedConfirmed = request.StateSaved,
        ProtectionRestoredConfirmed = request.ProtectionRestored,
        ProtectionRestoredConfirmedAt = DateTimeOffset.UtcNow,
    };
    await labStore.SaveChecklistAsync(updated, cancellationToken);
    await labStore.AddAuditAsync(
        "teacher", "maestro-restored", jobId,
        $"현재 상태 저장={request.StateSaved}, 보호 재활성화={request.ProtectionRestored}",
        cancellationToken);

    liveEvents.Signal();
    return Results.Ok(updated);
});

// ---------------- 관리자 토큰 ----------------

// 이 엔드포인트들은 /api/admin 아래에 있어 현재 관리자 토큰으로 인증된 사용자만 호출할 수 있다.
app.MapGet("/api/admin/token", (AdminTokenStore adminTokenStore) =>
    Results.Ok(adminTokenStore.GetStatus()));

app.MapPost("/api/admin/token", async (
    UpdateAdminTokenRequest request,
    AdminTokenStore adminTokenStore,
    LabStore labStore,
    CancellationToken cancellationToken) =>
{
    try
    {
        await adminTokenStore.SetTokenAsync(request.NewToken, cancellationToken);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }

    await labStore.AddAuditAsync(
        "teacher", "token-change", "admin", "관리자 토큰 변경", cancellationToken);
    return Results.Ok(adminTokenStore.GetStatus());
});

// ---------------- 정책/감사 ----------------

app.MapGet("/api/admin/policy", async (
    AgentPolicyService agentPolicies,
    CancellationToken cancellationToken) =>
    Results.Ok(await agentPolicies.GetDefaultAsync(cancellationToken)));

app.MapPut("/api/admin/policy", async (
    UpdatePolicy policy,
    LabStore labStore,
    CancellationToken cancellationToken) =>
{
    var stored = policy.WithoutFeatureUpdates();
    await labStore.SetSettingAsync(
        AgentPolicyService.DefaultSettingKey, UpdateAgentJson.Serialize(stored), cancellationToken);
    await labStore.AddAuditAsync(
        "teacher", "policy", "default", "기본 업데이트 정책 변경", cancellationToken);
    return Results.Ok(stored);
});

app.MapGet("/api/admin/audit", async (
    int? limit,
    LabStore labStore,
    CancellationToken cancellationToken) =>
    Results.Ok(await labStore.GetAuditAsync(Math.Clamp(limit ?? 100, 1, 500), cancellationToken)));

// 명령(7일)과 감사 로그(30일)를 주기적으로 정리한다. 활성 작업 이력은 보존한다.
using var retentionCts = CancellationTokenSource.CreateLinkedTokenSource(app.Lifetime.ApplicationStopping);
_ = Task.Run(async () =>
{
    using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
    while (!retentionCts.IsCancellationRequested)
    {
        try
        {
            var removed = await store.CleanupRetentionAsync(cancellationToken: retentionCts.Token);
            if (removed > 0)
            {
                app.Logger.LogInformation("오래된 LabManager 데이터 {Count}건을 정리했습니다.", removed);
            }
        }
        catch (OperationCanceledException) when (retentionCts.IsCancellationRequested)
        {
            break;
        }
        catch (Exception ex)
        {
            app.Logger.LogWarning(ex, "LabManager 데이터 정리 실패");
        }

        try
        {
            await timer.WaitForNextTickAsync(retentionCts.Token);
        }
        catch (OperationCanceledException)
        {
            break;
        }
    }
});

app.Run();
return;

// ---------------- 헬퍼 ----------------

static AgentCommand NewCommand(string agentId, CommandType type, string payloadJson) => new()
{
    CommandId = Guid.NewGuid().ToString("N"),
    AgentId = agentId,
    Type = type,
    PayloadJson = payloadJson,
    Status = CommandStatus.Pending,
    IssuedBy = "teacher",
    CreatedAt = DateTimeOffset.UtcNow,
    ExpiresAt = DateTimeOffset.UtcNow.AddHours(12),
};

static async Task<IResult> SendJobCommandAsync(
    string jobId,
    CommandType type,
    string payloadJson,
    string action,
    LabStore store,
    LiveEventBus liveEvents,
    CancellationToken cancellationToken)
{
    var job = await store.GetJobAsync(jobId, cancellationToken);
    if (job is null) return Results.NotFound(new { error = "작업을 찾을 수 없습니다." });

    if (job.Summary.IsTerminal)
    {
        return Results.BadRequest(new { error = "이미 종료된 작업입니다." });
    }

    await store.AddCommandAsync(
        NewCommand(job.Summary.AgentId, type, payloadJson),
        cancellationToken);

    await store.AddAuditAsync(
        "teacher", action, $"{job.MachineName}/{jobId}", $"{type} 명령 발급", cancellationToken);

    liveEvents.Signal();
    return Results.Ok(new { queued = true, commandType = type.ToString() });
}
