using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using UpdateAgent.Core.Windows;
using UpdateAgent.LabManager.Services;
using UpdateAgent.LabManager.Storage;
using UpdateAgent.Service.Configuration;
using UpdateAgent.Shared;
using UpdateAgent.Shared.Models;

namespace UpdateAgent.Tests;

/// <summary>자아성찰 20회 반복 감사에서 확인한 결함의 회귀 테스트.</summary>
public sealed class Audit20RoundTests
{
    private static readonly OsInfo Windows11 =
        WindowsOsInfoProvider.Create("Windows 11 Pro", "Professional", "25H2", 26200);

    // 1회차: 원격 StartJob 페이로드가 기능 업데이트를 강제로 끄는지.
    [Fact]
    public void ForExecution_StripsFeatureUpdatesEvenOnWindows11()
    {
        var payload = UpdatePolicy.Default with
        {
            IncludeFeatureUpdates = true,
            ApprovedFeatureRelease = "26H2",
        };

        // SanitizeFor만으로는 Windows 11에서 기능 업데이트가 남는다.
        Assert.True(payload.SanitizeFor(Windows11).IncludeFeatureUpdates);

        var executed = payload.ForExecution(Windows11);
        Assert.False(executed.IncludeFeatureUpdates);
        Assert.Null(executed.ApprovedFeatureRelease);
    }

    // 2회차: 감지 실패/unknown OS는 클라이언트 기능을 추정하지 않는다.
    [Fact]
    public void UnknownOs_FailsClosedForFeatureUpdatesAndOverlay()
    {
        var unknown = new OsInfo();

        Assert.Equal("unknown", unknown.GroupId);
        Assert.False(unknown.SupportsFeatureUpdates);
        Assert.False(unknown.SupportsOverlay);

        var policy = UpdatePolicy.Default with
        {
            IncludeFeatureUpdates = true,
            ApprovedFeatureRelease = "26H2",
        };
        var sanitized = policy.ForExecution(unknown);

        Assert.False(sanitized.IncludeFeatureUpdates);
        Assert.Null(sanitized.ApprovedFeatureRelease);
        Assert.False(sanitized.OverlayEnabled);
    }

    // 3회차: 원격 관리가 켜져 있으면 기본/샘플 토큰으로 시작할 수 없다.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("change-me-agent-token")]
    [InlineData("sample-agent-token-replace-before-pilot")]
    public void AgentValidator_RejectsPlaceholderTokenWhenRemoteEnabled(string? token)
    {
        var options = new AgentOptions
        {
            CheckInEnabled = true,
            LabManagerUrl = "http://labmanager:5080",
            AgentToken = token ?? string.Empty,
        };

        var result = new AgentOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("AgentToken", result.FailureMessage);
    }

    [Fact]
    public void AgentValidator_AcceptsRealTokenWhenRemoteEnabled()
    {
        var options = new AgentOptions
        {
            CheckInEnabled = true,
            LabManagerUrl = "http://labmanager:5080",
            AgentToken = "x9K2mP7vT4qR8nW1zB6d",
        };

        Assert.True(new AgentOptionsValidator().Validate(null, options).Succeeded);
    }

    // 로컬 전용 모드에서는 원격 인증이 없으므로 토큰 검사를 하지 않는다.
    [Fact]
    public void AgentValidator_AllowsLocalOnlyModeWithoutToken()
    {
        var options = new AgentOptions
        {
            CheckInEnabled = false,
            AgentToken = UpdateAgentConstants.DefaultAgentToken,
        };

        Assert.True(new AgentOptionsValidator().Validate(null, options).Succeeded);
    }

    // 4회차: 위조/중복 명령 결과는 감사 로그에 성공으로 남지 않는다.
    [Fact]
    public async Task CommandResults_OnlyAcceptedIdsAreReturned()
    {
        await WithStoreAsync(async store =>
        {
            await store.AddCommandAsync(new AgentCommand
            {
                CommandId = "cmd-real",
                AgentId = "pc-1",
                Type = CommandType.Ping,
            });

            var accepted = await store.CompleteCommandResultsAsync("pc-1",
            [
                new CommandResult { CommandId = "cmd-real", AgentId = "pc-1", Success = true },
                new CommandResult { CommandId = "cmd-forged", AgentId = "pc-1", Success = true },
            ]);

            Assert.Single(accepted);
            Assert.Contains("cmd-real", accepted);
            var command = await store.GetCommandAsync("cmd-real");
            Assert.NotNull(command);
            Assert.Equal(CommandStatus.Succeeded, command.Status);
        });
    }

    // 4회차: API가 maestro 확인을 꺼도 서버가 다시 켠다.
    [Fact]
    public async Task AgentPolicy_ForcesMaestroConfirmationOnServer()
    {
        await WithStoreAsync(async store =>
        {
            var policies = new AgentPolicyService(store);
            var requested = UpdatePolicy.Default with
            {
                RequireMaestroManualConfirmation = false,
                IncludeFeatureUpdates = true,
            };

            var (resolved, _) = await policies.ResolveForStartAsync(
                "pc-1", Windows11, requested, saveAsPolicy: false, machineName: "PC-01");

            Assert.True(resolved.RequireMaestroManualConfirmation);
            Assert.False(resolved.IncludeFeatureUpdates);

            await policies.SaveAsync("pc-1", requested, Windows11);
            var effective = await policies.GetEffectiveAsync("pc-1", Windows11);
            Assert.True(effective.Policy.RequireMaestroManualConfirmation);
        });
    }

    private static async Task WithStoreAsync(Func<LabStore, Task> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "UpdateAgentAudit20", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LabStore(Path.Combine(root, "lab.db"));
            await store.InitializeAsync();
            await action(store);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
