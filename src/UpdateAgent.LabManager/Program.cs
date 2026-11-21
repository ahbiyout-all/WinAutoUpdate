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
if (!startupOptions.AllowDefaultTokens)
{
    var adminToken = startupOptions.AdminToken?.Trim();
    var agentToken = startupOptions.AgentToken?.Trim();
    var invalidTokens = new List<string>();
    if (string.IsNullOrWhiteSpace(adminToken) ||
        string.Equals(adminToken, UpdateAgentConstants.DefaultAdminToken.Trim(), StringComparison.OrdinalIgnoreCase))
    {
        invalidTokens.Add("AdminToken");
    }

    if (string.IsNullOrWhiteSpace(agentToken) ||
        string.Equals(agentToken, UpdateAgentConstants.DefaultAgentToken.Trim(), StringComparison.OrdinalIgnoreCase))
    {
        invalidTokens.Add("AgentToken");
    }

    if (invalidTokens.Count > 0)
    {
        throw new InvalidOperationException(
            $"기본/빈 토큰으로 LabManager를 시작할 수 없습니다: {string.Join(", ", invalidTokens)}. " +
            "scripts\\configure-labmanager.ps1을 실행하거나 appsettings.json의 LabManager 토큰을 변경하세요. " +
            "로컬 개발에서만 LabManager:AllowDefaultTokens=true를 사용하세요.");
    }
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
    return new LabStore(options.ResolveDatabasePath());
});
builder.Services.AddSingleton<WakeOnLanService>();
builder.Services.AddHttpClient("github", client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddSingleton<UpdateCheckService>();
builder.Services.AddSingleton<LiveEventBus>();

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
    serverTime = DateTimeOffset.Now,
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
    LiveEventBus liveEvents,
    CancellationToken cancellationToken) =>
{
    if (!JobStateGuard.AgentIdsMatch(request.Agent.AgentId, request.Telemetry.AgentId))
    {
        await labStore.AddAuditAsync(
            "agent",
            "job-state-rejected",
            $"{request.Agent.MachineName}/{request.Telemetry.AgentId}",
            "에이전트 정보와 텔레메트리의 에이전트 ID 불일치",
            cancellationToken);
        return Results.BadRequest(new { error = "에이전트 ID가 일치하지 않습니다." });
    }

    await labStore.UpsertAgentAsync(request.Agent, request.Telemetry, cancellationToken);

    // 상태 검증과 저장에 사용하는 시각은 서버 수신 시각이다.
    var receivedAt = DateTimeOffset.UtcNow;
    var summary = request.Telemetry.CurrentJob;
    if (summary is not null)
    {
        var previous = await labStore.GetJobAsync(summary.JobId, cancellationToken);
        if (!JobStateGuard.CanAccept(previous?.Summary, summary, request.Agent.AgentId, receivedAt))
        {
            await labStore.AddAuditAsync(
                "agent",
                "job-state-rejected",
                $"{request.Agent.MachineName}/{summary.JobId}",
                $"소유자/종료 상태/시간 순서 검증 실패: {summary.State} ({summary.Phase})",
                cancellationToken);
        }
        else if (request.Telemetry.CurrentJobDetail is { } mismatchedDetail &&
                 !JobStateGuard.DetailSummaryMatches(summary, mismatchedDetail.Summary))
        {
            await labStore.AddAuditAsync(
                "agent",
                "job-state-rejected",
                $"{request.Agent.MachineName}/{summary.JobId}",
                $"작업 상세 요약 불일치: {mismatchedDetail.Summary.State}",
                cancellationToken);
        }
        else
        {
            var validated = JobStateGuard.Normalize(summary, receivedAt);
            if (request.Telemetry.CurrentJobDetail is { } detail)
            {
                // 상세의 요약은 검증된 요약으로 교체해 별도로 위조된 값이 저장되지 않게 한다.
                await labStore.SaveJobAsync(
                    detail with { Summary = validated },
                    request.Agent.MachineName,
                    cancellationToken);
            }
            else
            {
                await labStore.SaveJobSummaryAsync(validated, request.Agent.MachineName, cancellationToken);
            }

            if (validated.RequiresMaestroAttention &&
                await labStore.GetChecklistAsync(summary.JobId, cancellationToken) is null)
            {
                // 학생용 앱에서 시작한 작업은 체크인으로 처음 발견된다.
                // 서비스가 학생 확인을 받아 시작한 작업이므로 보호 해제 확인을 기록해
                // 대시보드에서 재부팅 후 Maestro 단계를 계속 관리할 수 있게 한다.
                await labStore.SaveChecklistAsync(new MaestroChecklist
                {
                    JobId = summary.JobId,
                    ProtectionSuspendedConfirmed = true,
                    ProtectionSuspendedConfirmedAt = DateTimeOffset.Now,
                    ConfirmedBy = "student",
                    RequiresManualConfirmation = true,
                }, cancellationToken);
            }

            if (previous is null || previous.Summary.State != validated.State)
            {
                await labStore.AddAuditAsync(
                    "agent",
                    "job-state",
                    $"{request.Agent.MachineName}/{validated.JobId}",
                    $"{previous?.Summary.State.ToString() ?? "없음"} → {validated.State} ({validated.Phase})",
                    cancellationToken);
            }
        }
    }

    if (request.CommandResults.Count > 0)
    {
        await labStore.CompleteCommandResultsAsync(request.CommandResults, cancellationToken);
        foreach (var result in request.CommandResults)
        {
            await labStore.AddAuditAsync(
                "agent",
                result.Success ? "command-ok" : "command-fail",
                $"{request.Agent.MachineName}/{result.CommandId}",
                result.Message,
                cancellationToken);
        }

        // StartJob 명령이 실패하면 서버 작업이 Pending에 남지 않도록 실패로 정리한다.
        foreach (var result in request.CommandResults.Where(r => !r.Success))
        {
            await FailJobForRejectedCommandAsync(labStore, result, cancellationToken);
        }
    }

    var commands = await labStore.TakePendingCommandsAsync(
        request.Agent.AgentId,
        20,
        cancellationToken);

    var policy = await GetDefaultPolicyAsync(labStore, cancellationToken);

    // 체크인으로 에이전트/작업 상태가 바뀌었음을 모니터에 알린다.
    liveEvents.Signal();

    return Results.Ok(new AgentCheckInResponse
    {
        ServerTime = DateTimeOffset.Now,
        Commands = commands,
        Policy = policy,
        MaestroManualConfirmationRequired = policy.RequireMaestroManualConfirmation,
    });
});

// ---------------- 대시보드 ----------------

app.MapGet("/api/admin/overview", async (LabStore labStore, CancellationToken cancellationToken) =>
{
    var agents = await labStore.GetAgentsAsync(cancellationToken);
    var jobs = await labStore.GetJobsAsync(200, cancellationToken);
    var active = jobs.Count(j => !j.Summary.IsTerminal);

    return Results.Ok(new OverviewResponse
    {
        AgentCount = agents.Count,
        OnlineCount = agents.Count(a => a.Online),
        BusyCount = agents.Count(a => a.Telemetry.State is AgentState.Busy or AgentState.Rebooting),
        ActiveJobs = active,
        CompletedJobs = jobs.Count(j => j.Summary.State == UpdateJobState.Completed),
        FailedJobs = jobs.Count(j => j.Summary.State == UpdateJobState.Failed),
    });
});

// ---------------- 실시간 작업 현황 (SSE) ----------------

// EventSource는 사용자 지정 헤더를 보낼 수 없으므로 모니터 페이지에서는
// fetch 스트림으로 X-Admin-Token을 전달한다. 기존 관리자 인증 경로를 그대로 사용한다.
app.MapGet("/api/admin/live", async (
    HttpContext context,
    LabStore labStore,
    LiveEventBus liveEvents,
    CancellationToken cancellationToken) =>
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
});

app.MapGet("/api/admin/agents", async (LabStore labStore, CancellationToken cancellationToken) =>
    Results.Ok(await labStore.GetAgentsAsync(cancellationToken)));

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
    LiveEventBus liveEvents,
    CancellationToken cancellationToken) =>
{
    if (request.AgentIds.Count == 0)
    {
        return Results.BadRequest(new { error = "대상 PC를 선택하세요." });
    }

    var policy = request.Policy ?? await GetDefaultPolicyAsync(labStore, cancellationToken);
    var jobIds = new List<string>();
    var errors = new List<string>();
    var now = DateTimeOffset.Now;

    foreach (var agentId in request.AgentIds.Distinct(StringComparer.OrdinalIgnoreCase))
    {
        var agent = await labStore.GetAgentAsync(agentId, cancellationToken);
        if (agent is null)
        {
            errors.Add($"{agentId}: 등록되지 않은 PC");
            continue;
        }

        if (!agent.Online)
        {
            errors.Add($"{agent.Agent.MachineName}: 오프라인 (WoL 후 다시 시도)");
            continue;
        }

        var jobId = $"job-{now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..24];

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
    return Results.Ok(new StartJobsResponse { JobIds = jobIds, Errors = errors });
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
        ProtectionSuspendedConfirmedAt = DateTimeOffset.Now,
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
        ProtectionRestoredConfirmedAt = DateTimeOffset.Now,
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
    Results.Ok(new
    {
        isPlaceholder = adminTokenStore.IsUsingPlaceholder,
        hasRuntimeOverride = adminTokenStore.HasRuntimeOverride,
        sampleAdminToken = UpdateAgentConstants.SampleAdminToken,
    }));

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
    return Results.Ok(new
    {
        isPlaceholder = false,
        hasRuntimeOverride = true,
    });
});

// ---------------- 정책/감사 ----------------

app.MapGet("/api/admin/policy", async (LabStore labStore, CancellationToken cancellationToken) =>
    Results.Ok(await GetDefaultPolicyAsync(labStore, cancellationToken)));

app.MapPut("/api/admin/policy", async (
    UpdatePolicy policy,
    LabStore labStore,
    CancellationToken cancellationToken) =>
{
    await labStore.SetSettingAsync("default-policy", UpdateAgentJson.Serialize(policy), cancellationToken);
    await labStore.AddAuditAsync(
        "teacher", "policy", "default", "기본 업데이트 정책 변경", cancellationToken);
    return Results.Ok(policy);
});

app.MapGet("/api/admin/audit", async (
    int? limit,
    LabStore labStore,
    CancellationToken cancellationToken) =>
    Results.Ok(await labStore.GetAuditAsync(Math.Clamp(limit ?? 100, 1, 500), cancellationToken)));

app.Run();
return;

// ---------------- 헬퍼 ----------------

static async Task FailJobForRejectedCommandAsync(
    LabStore store,
    CommandResult result,
    CancellationToken cancellationToken)
{
    var command = await store.GetCommandAsync(result.CommandId, cancellationToken);
    if (command is null || command.Type != CommandType.StartJob) return;

    var payload = UpdateAgentJson.Deserialize<StartJobCommandPayload>(command.PayloadJson);
    if (payload?.JobId is not { Length: > 0 } jobId) return;

    var job = await store.GetJobAsync(jobId, cancellationToken);
    if (job is null || job.Summary.IsTerminal) return;

    var now = DateTimeOffset.Now;
    var failed = job.Summary with
    {
        State = UpdateJobState.Failed,
        Phase = "시작 실패",
        Message = result.Message,
        LastError = result.Message,
        UpdatedAt = now,
        ReportedAt = now,
        CompletedAt = now,
    };

    await store.SaveJobSummaryAsync(failed, job.MachineName, cancellationToken);
    await store.AddAuditAsync(
        "system",
        "job-failed",
        $"{job.MachineName}/{jobId}",
        $"시작 명령 거부: {result.Message}",
        cancellationToken);
}

static AgentCommand NewCommand(string agentId, CommandType type, string payloadJson) => new()
{
    CommandId = Guid.NewGuid().ToString("N"),
    AgentId = agentId,
    Type = type,
    PayloadJson = payloadJson,
    Status = CommandStatus.Pending,
    IssuedBy = "teacher",
    CreatedAt = DateTimeOffset.Now,
    ExpiresAt = DateTimeOffset.Now.AddHours(12),
};

static async Task<UpdatePolicy> GetDefaultPolicyAsync(LabStore store, CancellationToken cancellationToken)
{
    var json = await store.GetSettingAsync("default-policy", cancellationToken);
    if (string.IsNullOrWhiteSpace(json)) return UpdatePolicy.Default;

    try
    {
        return UpdateAgentJson.Deserialize<UpdatePolicy>(json) ?? UpdatePolicy.Default;
    }
    catch
    {
        return UpdatePolicy.Default;
    }
}

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
