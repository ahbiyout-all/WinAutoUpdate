using Microsoft.Extensions.Logging.Abstractions;
using UpdateAgent.Core.Comms;
using UpdateAgent.Core.Orchestration;
using UpdateAgent.Service.Comms;
using UpdateAgent.Service.Configuration;
using UpdateAgent.Shared.Models;
using UpdateAgent.Shared.Serialization;

namespace UpdateAgent.Tests;

public sealed class LocalControlTests
{
    [Fact]
    public void LocalControlRequest_RoundTripsThroughSharedJson()
    {
        var json = UpdateAgentJson.Serialize(new LocalControlRequest
        {
            Action = LocalControlActions.StartUpdate,
            MaestroConfirmed = true,
        });

        var parsed = UpdateAgentJson.Deserialize<LocalControlRequest>(json);

        Assert.NotNull(parsed);
        Assert.Equal(LocalControlActions.StartUpdate, parsed!.Action);
        Assert.True(parsed.MaestroConfirmed);
    }

    [Fact]
    public void CancelJobCommandPayload_Force_RoundTripsThroughSharedJson()
    {
        var json = UpdateAgentJson.Serialize(new CancelJobCommandPayload { Force = true });
        var parsed = UpdateAgentJson.Deserialize<CancelJobCommandPayload>(json);

        Assert.NotNull(parsed);
        Assert.True(parsed!.Force);
    }

    [Fact]
    public async Task ServerPolicyProvider_UsesCachedPolicy()
    {
        var json = UpdateAgentJson.Serialize(new UpdatePolicy { IncludeOptional = true, MaxRebootCount = 2 });
        var provider = new ServerPolicyProvider((key, _) =>
            Task.FromResult<string?>(key == ServerPolicyProvider.SettingKey ? json : null));

        var policy = await provider.GetAsync();

        Assert.True(policy.IncludeOptional);
        Assert.Equal(2, policy.MaxRebootCount);
    }

    [Fact]
    public async Task ServerPolicyProvider_FallsBackToDefaultOnInvalidJson()
    {
        var provider = new ServerPolicyProvider((_, _) => Task.FromResult<string?>("not-json"));

        var policy = await provider.GetAsync();

        Assert.Equal(UpdatePolicy.Default.MaxRebootCount, policy.MaxRebootCount);
        Assert.Equal(UpdatePolicy.Default.RequireMaestroManualConfirmation, policy.RequireMaestroManualConfirmation);
    }

    [Fact]
    public async Task LocalUpdateTrigger_RejectsUnknownAction()
    {
        var called = false;
        var trigger = CreateTrigger((_, _, _, _) =>
        {
            called = true;
            return Task.FromResult(Started());
        });

        var response = await trigger.HandleAsync(new LocalControlRequest { Action = "delete-everything" });

        Assert.False(response.Success);
        Assert.False(called);
    }

    [Fact]
    public async Task LocalUpdateTrigger_RequiresMaestroConfirmation()
    {
        var called = false;
        var trigger = CreateTrigger((_, _, _, _) =>
        {
            called = true;
            return Task.FromResult(Started());
        });

        var response = await trigger.HandleAsync(new LocalControlRequest
        {
            Action = LocalControlActions.StartUpdate,
            MaestroConfirmed = false,
        });

        Assert.False(response.Success);
        Assert.Contains("Maestro", response.Message);
        Assert.False(called);
    }

    [Fact]
    public async Task LocalUpdateTrigger_StartsJobWithStudentConfirmation()
    {
        UpdatePolicy? usedPolicy = null;
        var usedConfirmed = false;
        var usedBy = "";
        var policy = new UpdatePolicy { MaxRebootCount = 3 };
        var trigger = CreateTrigger((p, confirmed, by, _) =>
        {
            usedPolicy = p;
            usedConfirmed = confirmed;
            usedBy = by;
            return Task.FromResult(Started("job-local-1"));
        }, policy);

        var response = await trigger.HandleAsync(new LocalControlRequest
        {
            Action = LocalControlActions.StartUpdate,
            MaestroConfirmed = true,
        });

        Assert.True(response.Success);
        Assert.Equal("job-local-1", response.JobId);
        Assert.Equal(policy, usedPolicy);
        Assert.True(usedConfirmed);
        Assert.Equal("student", usedBy);
        Assert.Equal(LocalUpdateTrigger.ConfirmedBy, usedBy);
    }

    [Fact]
    public async Task LocalUpdateTrigger_AllowsUnconfirmedWhenPolicyDoesNotRequireIt()
    {
        bool usedConfirmed = false;
        var trigger = CreateTrigger((_, confirmed, _, _) =>
        {
            usedConfirmed = confirmed;
            return Task.FromResult(Started());
        }, new UpdatePolicy { RequireMaestroManualConfirmation = false });

        var response = await trigger.HandleAsync(new LocalControlRequest
        {
            Action = LocalControlActions.StartUpdate,
            MaestroConfirmed = false,
        });

        Assert.True(response.Success);
        Assert.True(usedConfirmed);
    }

    [Fact]
    public async Task LocalUpdateTrigger_PropagatesRunnerFailure()
    {
        var trigger = CreateTrigger((_, _, _, _) =>
            Task.FromResult(new StartJobResult
            {
                Started = false,
                Preflight = new PreflightResult(),
                Message = "이미 업데이트 작업이 진행 중입니다.",
            }));

        var response = await trigger.HandleAsync(new LocalControlRequest
        {
            Action = LocalControlActions.StartUpdate,
            MaestroConfirmed = true,
        });

        Assert.False(response.Success);
        Assert.Equal("이미 업데이트 작업이 진행 중입니다.", response.Message);
        Assert.Null(response.JobId);
    }

    [Fact]
    public async Task LocalUpdateTrigger_CancelUpdate_ForcesStopAndHidesOverlay()
    {
        bool? usedForce = null;
        string? notice = null;
        var trigger = CreateTrigger(
            (_, _, _, _) => Task.FromResult(Started()),
            cancel: (force, _) =>
            {
                usedForce = force;
                return Task.FromResult<JobSummary?>(new JobSummary { JobId = "job-cancel-1" });
            },
            hide: (text, _) =>
            {
                notice = text;
                return Task.CompletedTask;
            });

        var response = await trigger.HandleAsync(new LocalControlRequest
        {
            Action = LocalControlActions.CancelUpdate,
        });

        Assert.True(response.Success);
        Assert.Equal("job-cancel-1", response.JobId);
        Assert.True(usedForce);
        Assert.False(string.IsNullOrWhiteSpace(notice));
    }

    [Fact]
    public async Task LocalUpdateTrigger_CancelUpdate_WithoutDelegate_Fails()
    {
        var trigger = CreateTrigger((_, _, _, _) => Task.FromResult(Started()));

        var response = await trigger.HandleAsync(new LocalControlRequest
        {
            Action = LocalControlActions.CancelUpdate,
        });

        Assert.False(response.Success);
        Assert.Contains("로컬 중단", response.Message);
    }

    [Fact]
    public async Task LocalUpdateTrigger_CancelUpdate_NoActiveJob_ReportsNoJob()
    {
        var hideCalled = false;
        var trigger = CreateTrigger(
            (_, _, _, _) => Task.FromResult(Started()),
            cancel: (_, _) => Task.FromResult<JobSummary?>(null),
            hide: (_, _) =>
            {
                hideCalled = true;
                return Task.CompletedTask;
            });

        var response = await trigger.HandleAsync(new LocalControlRequest
        {
            Action = LocalControlActions.CancelUpdate,
        });

        Assert.False(response.Success);
        Assert.Contains("중단할 작업이 없습니다", response.Message);
        Assert.True(hideCalled);
    }

    [Fact]
    public async Task LocalControlPipe_RoundTripsRequestAndResponse()
    {
        var pipeName = "UpdateAgent.Test." + Guid.NewGuid().ToString("N");
        LocalControlRequest? received = null;
        await using var server = new LocalControlPipeServer((request, _) =>
        {
            received = request;
            return Task.FromResult(new LocalControlResponse
            {
                Success = true,
                Message = "ok",
                JobId = "job-pipe-1",
            });
        }, pipeName);

        server.Start();
        await Task.Delay(200);

        var client = new LocalControlPipeClient(pipeName);
        var response = await client.SendAsync(new LocalControlRequest
        {
            Action = LocalControlActions.StartUpdate,
            MaestroConfirmed = true,
        });

        Assert.True(response.Success);
        Assert.Equal("job-pipe-1", response.JobId);
        Assert.NotNull(received);
        Assert.Equal(LocalControlActions.StartUpdate, received!.Action);
        Assert.True(received.MaestroConfirmed);
    }

    private static LocalUpdateTrigger CreateTrigger(
        StartJobDelegate start,
        UpdatePolicy? policy = null,
        CancelJobDelegate? cancel = null,
        Func<string, CancellationToken, Task>? hide = null)
    {
        var policies = new ServerPolicyProvider((_, _) => Task.FromResult<string?>(
            policy is null ? null : UpdateAgentJson.Serialize(policy)));
        return new LocalUpdateTrigger(
            start,
            policies,
            NullLogger<LocalUpdateTrigger>.Instance,
            cancel,
            hide);
    }

    private static StartJobResult Started(string jobId = "job-test") => new()
    {
        Started = true,
        Preflight = new PreflightResult(),
        Job = new JobSummary { JobId = jobId },
        Message = "started",
    };
}
