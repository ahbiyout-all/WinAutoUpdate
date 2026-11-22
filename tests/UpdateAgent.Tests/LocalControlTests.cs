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
    public void LocalControlRequest_Options_RoundTripsThroughSharedJson()
    {
        var json = UpdateAgentJson.Serialize(new LocalControlRequest
        {
            Action = LocalControlActions.StartUpdate,
            MaestroConfirmed = true,
            Options = new ManualUpdateOptions
            {
                PresetId = UpdatePolicyPresets.SecurityOnly,
                IncludeImportant = false,
                IncludeOptional = true,
                PreventAutomaticReboot = true,
                MaxRebootCount = 4,
                OverlayEnabled = false,
            },
        });

        var parsed = UpdateAgentJson.Deserialize<LocalControlRequest>(json);

        Assert.NotNull(parsed);
        Assert.NotNull(parsed!.Options);
        Assert.Equal(UpdatePolicyPresets.SecurityOnly, parsed.Options!.PresetId);
        Assert.True(parsed.Options.IncludeSecurity);
        Assert.True(parsed.Options.IncludeCritical);
        Assert.False(parsed.Options.IncludeImportant);
        Assert.True(parsed.Options.IncludeOptional);
        Assert.False(parsed.Options.IncludeDrivers);
        Assert.False(parsed.Options.IncludeDefinitionUpdates);
        Assert.True(parsed.Options.PreventAutomaticReboot);
        Assert.Equal(4, parsed.Options.MaxRebootCount);
        Assert.False(parsed.Options.OverlayEnabled);
    }

    [Fact]
    public void ManualUpdateOptions_FromPreset_SanitizesForOs()
    {
        var options = ManualUpdateOptions.FromPreset(UpdatePolicyPresets.ServerSafe, ServerCore);

        Assert.True(options.IncludeDefinitionUpdates);
        Assert.True(options.PreventAutomaticReboot);
        Assert.False(options.OverlayEnabled);
    }

    [Fact]
    public void ManualUpdateOptions_HasAnyUpdateCategory_ReflectsSelection()
    {
        Assert.False(new ManualUpdateOptions
        {
            IncludeSecurity = false,
            IncludeCritical = false,
            IncludeImportant = false,
        }.HasAnyUpdateCategory);

        Assert.True(new ManualUpdateOptions
        {
            IncludeSecurity = false,
            IncludeCritical = false,
            IncludeImportant = false,
            IncludeDefinitionUpdates = true,
        }.HasAnyUpdateCategory);
    }

    [Fact]
    public void UpdatePolicy_HasAnyUpdateCategory_IgnoresFeatureUpdates()
    {
        Assert.True(UpdatePolicy.Default.HasAnyUpdateCategory);
        Assert.False(new UpdatePolicy
        {
            IncludeSecurity = false,
            IncludeCritical = false,
            IncludeImportant = false,
            IncludeFeatureUpdates = true,
            ApprovedFeatureRelease = "26H2",
        }.HasAnyUpdateCategory);
    }

    [Fact]
    public void ManualUpdateOptions_ApplyTo_KeepsProtectedTeacherFields()
    {
        var basePolicy = new UpdatePolicy
        {
            RequireMaestroManualConfirmation = false,
            PauseOnUserLogon = true,
            ShutdownAfterComplete = true,
            IncludeOptional = true,
            IncludeDrivers = true,
            IncludeDefinitionUpdates = false,
            RebootPolicy = RebootPolicy.Never,
            OverlayEnabled = false,
            MaxRebootCount = 7,
            IncludeFeatureUpdates = true,
            ApprovedFeatureRelease = "26H2",
        };
        var options = new ManualUpdateOptions
        {
            PresetId = UpdatePolicyPresets.Full,
            IncludeOptional = true,
            IncludeDrivers = true,
            IncludeDefinitionUpdates = true,
            MaxRebootCount = 99,
            OverlayEnabled = true,
        };

        var applied = options.ApplyTo(basePolicy, Windows11);

        Assert.True(applied.IncludeOptional);
        Assert.True(applied.IncludeDrivers);
        Assert.False(applied.IncludeDefinitionUpdates);
        Assert.Equal(7, applied.MaxRebootCount);
        Assert.Equal(RebootPolicy.Never, applied.RebootPolicy);
        Assert.False(applied.OverlayEnabled);
        Assert.False(applied.RequireMaestroManualConfirmation);
        Assert.True(applied.PauseOnUserLogon);
        Assert.True(applied.ShutdownAfterComplete);
        Assert.False(applied.IncludeFeatureUpdates);
        Assert.Null(applied.ApprovedFeatureRelease);
    }

    [Fact]
    public void ManualUpdateOptions_ApplyTo_AllowsStudentToNarrowPolicy()
    {
        var basePolicy = new UpdatePolicy
        {
            IncludeOptional = true,
            IncludeDrivers = true,
            IncludeDefinitionUpdates = true,
            RebootPolicy = RebootPolicy.Auto,
            OverlayEnabled = true,
            MaxRebootCount = 5,
        };
        var options = new ManualUpdateOptions
        {
            IncludeOptional = false,
            IncludeDrivers = false,
            IncludeDefinitionUpdates = false,
            PreventAutomaticReboot = true,
            OverlayEnabled = false,
            MaxRebootCount = 10,
        };

        var applied = options.ApplyTo(basePolicy, Windows11);

        Assert.True(applied.IncludeSecurity);
        Assert.False(applied.IncludeOptional);
        Assert.False(applied.IncludeDrivers);
        Assert.False(applied.IncludeDefinitionUpdates);
        Assert.Equal(RebootPolicy.Never, applied.RebootPolicy);
        Assert.False(applied.OverlayEnabled);
        Assert.Equal(5, applied.MaxRebootCount);
    }

    [Fact]
    public void ManualUpdateOptions_LimitTo_AllowsTeacherZeroRebootCeiling()
    {
        var teacherPolicy = new UpdatePolicy { MaxRebootCount = 0 };
        var options = new ManualUpdateOptions { MaxRebootCount = 10 };

        var applied = options.LimitTo(teacherPolicy, Windows11);

        Assert.Equal(0, applied.MaxRebootCount);
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
    public async Task LocalUpdateTrigger_AppliesStudentOptionsAndDisablesFeatureUpdates()
    {
        UpdatePolicy? usedPolicy = null;
        var cachedPolicy = new UpdatePolicy
        {
            IncludeOptional = true,
            IncludeFeatureUpdates = true,
            ApprovedFeatureRelease = "26H2",
        };
        var trigger = CreateTrigger((p, _, _, _) =>
        {
            usedPolicy = p;
            return Task.FromResult(Started());
        }, cachedPolicy);

        var response = await trigger.HandleAsync(new LocalControlRequest
        {
            Action = LocalControlActions.StartUpdate,
            MaestroConfirmed = true,
            Options = new ManualUpdateOptions
            {
                PresetId = UpdatePolicyPresets.Custom,
                IncludeOptional = true,
                PreventAutomaticReboot = true,
                MaxRebootCount = 99,
                OverlayEnabled = false,
            },
        });

        Assert.True(response.Success);
        Assert.NotNull(usedPolicy);
        Assert.True(usedPolicy!.IncludeOptional);
        Assert.Equal(RebootPolicy.Never, usedPolicy.RebootPolicy);
        Assert.Equal(5, usedPolicy.MaxRebootCount);
        Assert.False(usedPolicy.OverlayEnabled);
        Assert.False(usedPolicy.IncludeFeatureUpdates);
        Assert.Null(usedPolicy.ApprovedFeatureRelease);
    }

    [Fact]
    public async Task LocalUpdateTrigger_RejectsEmptyStudentOptions()
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
            MaestroConfirmed = true,
            Options = new ManualUpdateOptions
            {
                IncludeSecurity = false,
                IncludeCritical = false,
                IncludeImportant = false,
            },
        });

        Assert.False(response.Success);
        Assert.Contains("업데이트 범위", response.Message);
        Assert.False(called);
    }

    [Fact]
    public async Task LocalUpdateTrigger_RejectsOptionsWhenTeacherPolicyLeavesNoCategory()
    {
        var called = false;
        var teacherPolicy = new UpdatePolicy
        {
            IncludeSecurity = false,
            IncludeCritical = false,
            IncludeImportant = false,
            IncludeOptional = false,
            IncludeDrivers = false,
            IncludeDefinitionUpdates = false,
        };
        var trigger = CreateTrigger((_, _, _, _) =>
        {
            called = true;
            return Task.FromResult(Started());
        }, teacherPolicy);

        var response = await trigger.HandleAsync(new LocalControlRequest
        {
            Action = LocalControlActions.StartUpdate,
            MaestroConfirmed = true,
            Options = new ManualUpdateOptions { IncludeOptional = true },
        });

        Assert.False(response.Success);
        Assert.Contains("업데이트 범위", response.Message);
        Assert.False(called);
    }

    [Fact]
    public async Task LocalUpdateTrigger_RejectsNullOptionsWhenTeacherPolicyHasNoCategory()
    {
        var called = false;
        var teacherPolicy = new UpdatePolicy
        {
            IncludeSecurity = false,
            IncludeCritical = false,
            IncludeImportant = false,
        };
        var trigger = CreateTrigger((_, _, _, _) =>
        {
            called = true;
            return Task.FromResult(Started());
        }, teacherPolicy);

        var response = await trigger.HandleAsync(new LocalControlRequest
        {
            Action = LocalControlActions.StartUpdate,
            MaestroConfirmed = true,
        });

        Assert.False(response.Success);
        Assert.Contains("업데이트 범위", response.Message);
        Assert.False(called);
    }

    [Fact]
    public async Task LocalUpdateTrigger_RequiresMaestroConfirmationEvenWithOptions()
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
            Options = ManualUpdateOptions.FromPreset(UpdatePolicyPresets.Standard),
        });

        Assert.False(response.Success);
        Assert.Contains("Maestro", response.Message);
        Assert.False(called);
    }

    [Fact]
    public async Task LocalUpdateTrigger_KeepsTeacherNeverRebootFloor()
    {
        UpdatePolicy? usedPolicy = null;
        var trigger = CreateTrigger((p, _, _, _) =>
        {
            usedPolicy = p;
            return Task.FromResult(Started());
        }, new UpdatePolicy { RebootPolicy = RebootPolicy.Never });

        var response = await trigger.HandleAsync(new LocalControlRequest
        {
            Action = LocalControlActions.StartUpdate,
            MaestroConfirmed = true,
            Options = new ManualUpdateOptions { PreventAutomaticReboot = false },
        });

        Assert.True(response.Success);
        Assert.NotNull(usedPolicy);
        Assert.Equal(RebootPolicy.Never, usedPolicy!.RebootPolicy);
    }

    [Fact]
    public async Task LocalUpdateTrigger_ReportsPolicyResolutionFailure()
    {
        var called = false;
        var trigger = CreateTrigger((_, _, _, _) =>
        {
            called = true;
            return Task.FromResult(Started());
        }, os: () => throw new InvalidOperationException("OS 정보 조회 실패"));

        var response = await trigger.HandleAsync(new LocalControlRequest
        {
            Action = LocalControlActions.StartUpdate,
            MaestroConfirmed = true,
        });

        Assert.False(response.Success);
        Assert.Contains("수동 업데이트 옵션", response.Message);
        Assert.False(called);
    }

    [Fact]
    public async Task LocalUpdateTrigger_SanitizesStudentOptionsForServerCore()
    {
        UpdatePolicy? usedPolicy = null;
        var trigger = CreateTrigger((p, _, _, _) =>
        {
            usedPolicy = p;
            return Task.FromResult(Started());
        }, new UpdatePolicy(), os: () => ServerCore);

        var response = await trigger.HandleAsync(new LocalControlRequest
        {
            Action = LocalControlActions.StartUpdate,
            MaestroConfirmed = true,
            Options = new ManualUpdateOptions { OverlayEnabled = true },
        });

        Assert.True(response.Success);
        Assert.NotNull(usedPolicy);
        Assert.False(usedPolicy!.OverlayEnabled);
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
        string? hiddenJobId = null;
        var trigger = CreateTrigger(
            (_, _, _, _) => Task.FromResult(Started()),
            cancel: (force, _) =>
            {
                usedForce = force;
                return Task.FromResult<JobSummary?>(new JobSummary { JobId = "job-cancel-1" });
            },
            hide: (text, jobId, _) =>
            {
                notice = text;
                hiddenJobId = jobId;
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
        Assert.Equal("job-cancel-1", hiddenJobId);
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
            hide: (_, _, _) =>
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
        Assert.False(hideCalled);
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

        var client = new LocalControlPipeClient(pipeName);
        var response = await SendWithRetryAsync(client, new LocalControlRequest
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

    [Fact]
    public void LocalControlPipe_ClientWaitsLongerThanServerHandler()
    {
        Assert.True(LocalControlPipeServer.HandlerTimeout >= TimeSpan.FromSeconds(60));
        Assert.True(LocalControlPipeServer.ResponseTimeout >=
                    LocalControlPipeServer.HandlerTimeout + TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task LocalUpdateTrigger_RethrowsCancellationForPipeTimeout()
    {
        using var cts = new CancellationTokenSource();
        var trigger = CreateTrigger((_, _, _, token) =>
        {
            cts.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult(Started());
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            trigger.HandleAsync(new LocalControlRequest
            {
                Action = LocalControlActions.StartUpdate,
                MaestroConfirmed = true,
            }, cts.Token));
    }

    [Fact]
    public async Task LocalControlPipe_ReportsTimeoutWhenHandlerExceedsLimit()
    {
        var originalHandler = LocalControlPipeServer.HandlerTimeout;
        var originalResponse = LocalControlPipeServer.ResponseTimeout;
        LocalControlPipeServer.HandlerTimeout = TimeSpan.FromMilliseconds(200);
        LocalControlPipeServer.ResponseTimeout = TimeSpan.FromSeconds(2);
        var pipeName = "UpdateAgent.Test." + Guid.NewGuid().ToString("N");

        try
        {
            await using var server = new LocalControlPipeServer(async (_, token) =>
            {
                await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                return new LocalControlResponse { Success = true };
            }, pipeName);
            server.Start();

            var client = new LocalControlPipeClient(pipeName);
            var response = await SendWithRetryAsync(client, new LocalControlRequest
            {
                Action = LocalControlActions.CancelUpdate,
            });

            Assert.False(response.Success);
            Assert.Equal(LocalControlPipeServer.ResponseTimeoutMessage, response.Message);
        }
        finally
        {
            LocalControlPipeServer.HandlerTimeout = originalHandler;
            LocalControlPipeServer.ResponseTimeout = originalResponse;
        }
    }

    [Fact]
    public async Task LocalControlPipe_NotifiesCheckInOnlyOnSuccess()
    {
        var pipeName = "UpdateAgent.Test." + Guid.NewGuid().ToString("N");
        var notifications = 0;
        await using var server = new LocalControlPipeServer((request, _) =>
            Task.FromResult(new LocalControlResponse
            {
                Success = request.MaestroConfirmed,
                Message = request.MaestroConfirmed ? "ok" : "no",
            }), pipeName);
        server.RequestSucceeded += () => notifications++;
        server.Start();

        var client = new LocalControlPipeClient(pipeName);
        var failed = await SendWithRetryAsync(client, new LocalControlRequest
        {
            Action = LocalControlActions.CancelUpdate,
            MaestroConfirmed = false,
        });
        var succeeded = await SendWithRetryAsync(client, new LocalControlRequest
        {
            Action = LocalControlActions.CancelUpdate,
            MaestroConfirmed = true,
        });

        Assert.False(failed.Success);
        Assert.True(succeeded.Success);
        Assert.Equal(1, notifications);
    }

    /// <summary>서버 리스닝 시작 전 연결 시도를 짧게 재시도한다.</summary>
    private static async Task<LocalControlResponse> SendWithRetryAsync(
        LocalControlPipeClient client,
        LocalControlRequest request)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        do
        {
            var response = await client.SendAsync(request);
            if (!response.Message.StartsWith("서비스에 연결할 수 없습니다", StringComparison.Ordinal) ||
                DateTime.UtcNow >= deadline)
            {
                return response;
            }
            await Task.Delay(50);
        } while (true);
    }

    private static OsInfo Windows11 => new()
    {
        ProductName = "Windows 11 Pro",
        EditionId = "Professional",
        DisplayVersion = "23H2",
        Build = 22631,
    };

    private static OsInfo ServerCore => new()
    {
        ProductName = "Windows Server 2022 Standard",
        EditionId = "ServerStandard",
        Build = 20348,
        IsServer = true,
        IsServerCore = true,
    };

    private static LocalUpdateTrigger CreateTrigger(
        StartJobDelegate start,
        UpdatePolicy? policy = null,
        CancelJobDelegate? cancel = null,
        Func<string, string, CancellationToken, Task>? hide = null,
        Func<OsInfo>? os = null)
    {
        var policies = new ServerPolicyProvider((_, _) => Task.FromResult<string?>(
            policy is null ? null : UpdateAgentJson.Serialize(policy)));
        return new LocalUpdateTrigger(
            start,
            policies,
            NullLogger<LocalUpdateTrigger>.Instance,
            cancel,
            hide,
            os ?? (() => Windows11));
    }

    private static StartJobResult Started(string jobId = "job-test") => new()
    {
        Started = true,
        Preflight = new PreflightResult(),
        Job = new JobSummary { JobId = jobId },
        Message = "started",
    };
}
