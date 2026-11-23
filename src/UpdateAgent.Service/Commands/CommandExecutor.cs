using Microsoft.Extensions.Logging.Abstractions;
using UpdateAgent.Core.Orchestration;
using UpdateAgent.Core.Reboot;
using UpdateAgent.Core.Storage;
using UpdateAgent.Core.Windows;
using UpdateAgent.Service.Configuration;
using UpdateAgent.Service.Overlay;
using UpdateAgent.Shared.Models;
using UpdateAgent.Shared.Serialization;

namespace UpdateAgent.Service.Commands;

/// <summary>LabManager 원격 명령을 실행하고 결과를 모아 둔다.</summary>
public sealed class CommandExecutor
{
    private const string ProcessedSettingKey = "processed-command-ids";
    private const int MaxRememberedCommands = 200;

    private readonly UpdateJobRunner _runner;
    private readonly IRebootManager _reboot;
    private readonly OverlayJobProgressSink _overlay;
    private readonly IJobStore _store;
    private readonly ServerPolicyProvider _policies;
    private readonly ILogger<CommandExecutor> _logger;
    private readonly List<CommandResult> _pendingResults = new();
    private readonly LinkedList<string> _processed = new();
    private readonly HashSet<string> _processedSet = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CommandExecutor(
        UpdateJobRunner runner,
        IRebootManager reboot,
        OverlayJobProgressSink overlay,
        IJobStore store,
        ServerPolicyProvider policies,
        ILogger<CommandExecutor> logger)
    {
        _runner = runner;
        _reboot = reboot;
        _overlay = overlay;
        _store = store;
        _policies = policies;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var json = await _store.GetSettingAsync(ProcessedSettingKey, cancellationToken);
            if (string.IsNullOrWhiteSpace(json)) return;

            var ids = UpdateAgentJson.Deserialize<List<string>>(json) ?? [];
            foreach (var id in ids.Where(i => !string.IsNullOrWhiteSpace(i)))
            {
                if (_processedSet.Add(id)) _processed.AddLast(id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "처리된 명령 목록을 읽지 못했습니다.");
        }
    }

    public IReadOnlyList<CommandResult> SnapshotResults()
    {
        lock (_pendingResults)
        {
            return _pendingResults.ToList();
        }
    }

    public void AcknowledgeResults(IEnumerable<string> commandIds)
    {
        var set = commandIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (_pendingResults)
        {
            _pendingResults.RemoveAll(r => set.Contains(r.CommandId));
        }
    }

    private void RememberCommand(string commandId)
    {
        if (!_processedSet.Add(commandId)) return;
        _processed.AddLast(commandId);
        while (_processed.Count > MaxRememberedCommands)
        {
            var oldest = _processed.First!.Value;
            _processed.RemoveFirst();
            _processedSet.Remove(oldest);
        }
    }

    public async Task ExecuteAsync(
        IEnumerable<AgentCommand> commands,
        CancellationToken cancellationToken = default)
    {
        foreach (var command in commands)
        {
            if (command.ExpiresAt < DateTimeOffset.Now)
            {
                AddResult(new CommandResult
                {
                    CommandId = command.CommandId,
                    AgentId = command.AgentId,
                    Success = false,
                    Message = "명령 유효 시간이 지났습니다.",
                });
                RememberCommand(command.CommandId);
                continue;
            }

            if (_processedSet.Contains(command.CommandId))
            {
                continue;
            }

            CommandResult result;
            try
            {
                result = await ExecuteOneAsync(command, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "명령 실행 실패: {Type} {CommandId}", command.Type, command.CommandId);
                result = Fail(command, $"명령 실행 중 오류: {ex.Message}");
            }

            AddResult(result);
            RememberCommand(command.CommandId);
        }

        await PersistProcessedAsync(cancellationToken);
    }

    private async Task<CommandResult> ExecuteOneAsync(
        AgentCommand command,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("원격 명령 수신: {Type} ({CommandId})", command.Type, command.CommandId);

        switch (command.Type)
        {
            case CommandType.Ping:
                return Ok(command, "pong");

            case CommandType.StartJob:
            {
                var payload = Deserialize<StartJobCommandPayload>(command.PayloadJson)
                              ?? new StartJobCommandPayload();
                var result = await _runner.StartAsync(
                    payload.Policy,
                    payload.MaestroConfirmed || !payload.Policy.RequireMaestroManualConfirmation,
                    payload.ConfirmedBy,
                    payload.JobId,
                    cancellationToken);

                return new CommandResult
                {
                    CommandId = command.CommandId,
                    AgentId = command.AgentId,
                    Success = result.Started,
                    Message = result.Started
                        ? $"작업을 시작했습니다: {result.Job?.JobId}"
                        : result.Message,
                };
            }

            case CommandType.PauseJob:
            {
                var summary = await _runner.PauseAsync(PauseReason.Manual, cancellationToken);
                return Ok(command, summary is null ? "일시중지할 작업이 없습니다." : "작업을 일시중지했습니다.");
            }

            case CommandType.ResumeJob:
            {
                var summary = await _runner.ResumeFromPauseAsync(cancellationToken);
                return Ok(command, summary is null ? "재개할 작업이 없습니다." : "작업을 재개했습니다.");
            }

            case CommandType.CancelJob:
            {
                var payload = Deserialize<CancelJobCommandPayload>(command.PayloadJson)
                              ?? new CancelJobCommandPayload();
                var summary = await _runner.CancelAsync(payload.Force, cancellationToken);
                var notice = payload.Force
                    ? "긴급 중단으로 업데이트가 중지되었습니다."
                    : "작업이 취소되었습니다.";
                await _overlay.HideAsync(notice, summary?.JobId, cancellationToken);
                return Ok(command,
                    summary is null
                        ? "중단할 작업이 없습니다."
                        : payload.Force ? "작업을 강제 중단했습니다." : "작업을 취소했습니다.");
            }

            case CommandType.ScanNow:
            {
                var policy = await _policies.GetAsync(cancellationToken);
                var report = await _runner.ScanAsync(policy, cancellationToken);
                return Ok(command,
                    $"검색 완료: 대상 {report.Selected.Count}건, 제외 {report.Excluded.Count}건");
            }

            case CommandType.Reboot:
            {
                var payload = Deserialize<RebootCommandPayload>(command.PayloadJson)
                              ?? new RebootCommandPayload();
                await _reboot.ScheduleRebootAsync(payload.DelaySeconds, payload.Reason, cancellationToken);
                return Ok(command, $"{payload.DelaySeconds}초 후 재부팅합니다.");
            }

            case CommandType.Shutdown:
            {
                var payload = Deserialize<ShutdownCommandPayload>(command.PayloadJson)
                              ?? new ShutdownCommandPayload();
                await _reboot.ScheduleShutdownAsync(payload.DelaySeconds, payload.Reason, cancellationToken);
                return Ok(command, $"{payload.DelaySeconds}초 후 종료합니다.");
            }

            case CommandType.UnlockInput:
            {
                await _overlay.HideAsync("교사가 입력 잠금을 해제했습니다.", null, cancellationToken);
                return Ok(command, "오버레이를 숨겼습니다.");
            }

            case CommandType.ApplyPolicy:
            {
                var policy = Deserialize<UpdatePolicy>(command.PayloadJson) ?? UpdatePolicy.Default;
                // 캐시에도 기능 업데이트 승인 값을 남기지 않는다(대시보드가 원본 JSON을 읽는다).
                var sanitized = policy.ForExecution(WindowsOsInfoProvider.GetCurrent());
                await _store.SetSettingAsync(
                    ServerPolicyProvider.SettingKey, UpdateAgentJson.Serialize(sanitized), cancellationToken);
                return Ok(command, "정책을 적용했습니다.");
            }

            case CommandType.RefreshInventory:
                return Ok(command, "다음 체크인에서 최신 정보를 보고합니다.");

            case CommandType.Wake:
                return new CommandResult
                {
                    CommandId = command.CommandId,
                    AgentId = command.AgentId,
                    Success = false,
                    Message = "Wake 명령은 LabManager에서 직접 전송합니다.",
                };

            default:
                return Fail(command, $"지원하지 않는 명령입니다: {command.Type}");
        }
    }

    private static T? Deserialize<T>(string json) where T : class
    {
        try
        {
            return UpdateAgentJson.Deserialize<T>(json);
        }
        catch
        {
            return null;
        }
    }

    private static CommandResult Ok(AgentCommand command, string message) => new()
    {
        CommandId = command.CommandId,
        AgentId = command.AgentId,
        Success = true,
        Message = message,
    };

    private static CommandResult Fail(AgentCommand command, string message) => new()
    {
        CommandId = command.CommandId,
        AgentId = command.AgentId,
        Success = false,
        Message = message,
    };

    private void AddResult(CommandResult result)
    {
        lock (_pendingResults)
        {
            _pendingResults.Add(result);
            if (_pendingResults.Count > 100)
            {
                _pendingResults.RemoveAt(0);
            }
        }
    }

    private async Task PersistProcessedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _store.SetSettingAsync(
                ProcessedSettingKey,
                UpdateAgentJson.Serialize(_processed.ToList()),
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "처리된 명령 목록 저장 실패");
        }
    }
}
