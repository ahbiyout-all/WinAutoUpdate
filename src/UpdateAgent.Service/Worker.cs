using Microsoft.Extensions.Options;
using UpdateAgent.Core.Comms;
using UpdateAgent.Core.Maestro;
using UpdateAgent.Core.Orchestration;
using UpdateAgent.Core.Reboot;
using UpdateAgent.Core.Sessions;
using UpdateAgent.Core.Storage;
using UpdateAgent.Core.Windows;
using UpdateAgent.Core.Windows.Update;
using UpdateAgent.Service.Commands;
using UpdateAgent.Service.Comms;
using UpdateAgent.Service.Configuration;
using UpdateAgent.Service.Overlay;
using UpdateAgent.Service.Sessions;
using UpdateAgent.Shared;
using UpdateAgent.Shared.Models;

namespace UpdateAgent.Service;

/// <summary>
/// 에이전트 메인 루프.
/// 로컬 업데이트 작업을 복원하고, LabManager에 주기적으로 체크인하며 원격 명령을 실행한다.
/// </summary>
public sealed class Worker : BackgroundService
{
    private readonly UpdateJobRunner _runner;
    private readonly IWuaClient _wua;
    private readonly IJobStore _store;
    private readonly IRestorationProvider _maestro;
    private readonly IUserSessionProbe _sessions;
    private readonly IRebootManager _reboot;
    private readonly OverlayPipeServer _overlayPipe;
    private readonly OverlayJobProgressSink _overlaySink;
    private readonly SessionWatcherService _sessionWatcher;
    private readonly LabManagerClient _labManager;
    private readonly CommandExecutor _commands;
    private readonly IOptionsMonitor<AgentOptions> _options;
    private readonly ILogger<Worker> _logger;

    private DateTimeOffset? _firstSeen;

    public Worker(
        UpdateJobRunner runner,
        IWuaClient wua,
        IJobStore store,
        IRestorationProvider maestro,
        IUserSessionProbe sessions,
        IRebootManager reboot,
        OverlayPipeServer overlayPipe,
        OverlayJobProgressSink overlaySink,
        SessionWatcherService sessionWatcher,
        LabManagerClient labManager,
        CommandExecutor commands,
        IOptionsMonitor<AgentOptions> options,
        ILogger<Worker> logger)
    {
        _runner = runner;
        _wua = wua;
        _store = store;
        _maestro = maestro;
        _sessions = sessions;
        _reboot = reboot;
        _overlayPipe = overlayPipe;
        _overlaySink = overlaySink;
        _sessionWatcher = sessionWatcher;
        _labManager = labManager;
        _commands = commands;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "UpdateAgent Service 시작: agent={AgentId}, machine={Machine}",
            AgentIdentity.GetAgentId(),
            Environment.MachineName);

        await _runner.InitializeAsync(stoppingToken);
        await _commands.InitializeAsync(stoppingToken);
        await LoadFirstSeenAsync(stoppingToken);

        _overlayPipe.Start();
        _ = _sessionWatcher.StartAsync(stoppingToken);

        if (_options.CurrentValue.ResumeOnStartup)
        {
            try
            {
                var resumed = await _runner.ResumeIfNeededAsync(stoppingToken);
                if (resumed is not null)
                {
                    _logger.LogInformation("남아 있는 작업을 재개했습니다: {JobId} ({State})", resumed.JobId, resumed.State);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "작업 재개 실패");
            }
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await CheckInAsync(stoppingToken);

                try
                {
                    await Task.Delay(
                        TimeSpan.FromSeconds(_options.CurrentValue.EffectivePollSeconds),
                        stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            try
            {
                await _sessionWatcher.StopAsync(CancellationToken.None);
            }
            catch
            {
                // ignore
            }
        }
    }

    private async Task CheckInAsync(CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        if (!options.RemoteEnabled) return;

        try
        {
            var request = await BuildCheckInAsync(cancellationToken);
            var response = await _labManager.CheckInAsync(request, cancellationToken);
            if (response is null) return;

            // 서버가 받은 결과는 큐에서 제거한다.
            if (request.CommandResults.Count > 0)
            {
                _commands.AcknowledgeResults(request.CommandResults.Select(r => r.CommandId));
            }

            if (response.Commands.Count > 0)
            {
                await _commands.ExecuteAsync(
                    response.Commands.Take(options.MaxCommandsPerCheckIn),
                    cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "체크인 처리 중 오류");
        }
    }

    private async Task<AgentCheckInRequest> BuildCheckInAsync(CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        var os = _runner.Os ?? WindowsOsInfoProvider.GetCurrent();
        var hardware = HardwareInfo.GetSnapshot();
        var sessions = _sessions.GetInteractiveSessions();

        MaestroDetectionResult maestro;
        try
        {
            maestro = await _maestro.DetectAsync(cancellationToken);
        }
        catch
        {
            maestro = MaestroDetectionResult.NotInstalled;
        }

        var wuBusy = false;
        try
        {
            wuBusy = await _wua.IsBusyAsync(cancellationToken);
        }
        catch
        {
            // WUA 확인 실패는 "사용 중 아님"으로 보고한다.
        }

        var current = _runner.Current;
        var state = DetermineState(current);

        var agent = new AgentInfo
        {
            AgentId = AgentIdentity.GetAgentId(),
            MachineName = AgentIdentity.GetMachineName(),
            AgentVersion = UpdateAgentConstants.AgentVersion,
            IpAddress = AgentIdentity.GetIpAddress(),
            MacAddresses = AgentIdentity.GetMacAddresses(),
            Os = os,
            FirstSeen = _firstSeen ?? DateTimeOffset.Now,
            LastSeen = DateTimeOffset.Now,
            State = state,
            MaestroInstalled = maestro.Installed,
            IsLaptop = hardware.IsLaptop,
            HasBattery = hardware.HasBattery,
            FreeDiskMb = hardware.FreeDiskMb,
        };

        var telemetry = new AgentTelemetry
        {
            AgentId = agent.AgentId,
            Timestamp = DateTimeOffset.Now,
            State = state,
            CurrentJob = current?.Summary,
            CurrentJobDetail = current,
            CurrentPhase = current?.Summary.Phase,
            UserLoggedOn = sessions.Count > 0,
            LoggedOnUsers = sessions.Select(s => s.DisplayName).ToList(),
            WuBusy = wuBusy,
            RebootPending = _reboot.IsRebootPending(),
            Maestro = maestro,
            FreeDiskMb = hardware.FreeDiskMb,
            OnAcPower = hardware.OnAcPower,
            LastError = current?.Summary.LastError ?? _labManager.LastError,
        };

        return new AgentCheckInRequest
        {
            Agent = agent,
            Telemetry = telemetry,
            CommandResults = _commands.SnapshotResults(),
            SentAt = DateTimeOffset.Now,
        };

        AgentState DetermineState(JobDetail? job)
        {
            if (job is null) return AgentState.Idle;
            return job.Summary.State switch
            {
                UpdateJobState.Paused => AgentState.Paused,
                UpdateJobState.Rebooting => AgentState.Rebooting,
                UpdateJobState.Failed => AgentState.NeedsAttention,
                UpdateJobState.Pending or UpdateJobState.Preflight or UpdateJobState.Scanning
                    or UpdateJobState.Downloading or UpdateJobState.Installing
                    or UpdateJobState.PendingReboot => AgentState.Busy,
                _ => AgentState.Idle,
            };
        }
    }

    private async Task LoadFirstSeenAsync(CancellationToken cancellationToken)
    {
        try
        {
            var text = await _store.GetSettingAsync("first-seen", cancellationToken);
            if (DateTimeOffset.TryParse(text, out var parsed))
            {
                _firstSeen = parsed;
                return;
            }

            _firstSeen = DateTimeOffset.Now;
            await _store.SetSettingAsync("first-seen", _firstSeen.Value.ToString("O"), cancellationToken);
        }
        catch
        {
            _firstSeen = DateTimeOffset.Now;
        }
    }
}
