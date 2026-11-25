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
using UpdateAgent.Shared.Serialization;

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
    private readonly LocalControlPipeServer _localControlPipe;
    private readonly OverlayJobProgressSink _overlaySink;
    private readonly SessionWatcherService _sessionWatcher;
    private readonly LabManagerClient _labManager;
    private readonly CommandExecutor _commands;
    private readonly IOptionsMonitor<AgentOptions> _options;
    private readonly ILogger<Worker> _logger;

    private DateTimeOffset? _firstSeen;
    private int _checkInRequested;
    private volatile CancellationTokenSource? _pollDelayCts;

    public Worker(
        UpdateJobRunner runner,
        IWuaClient wua,
        IJobStore store,
        IRestorationProvider maestro,
        IUserSessionProbe sessions,
        IRebootManager reboot,
        OverlayPipeServer overlayPipe,
        LocalControlPipeServer localControlPipe,
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
        _localControlPipe = localControlPipe;
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

        void OnOverlayClientConnected()
        {
            // 헬퍼는 재연결 후 오버레이를 숨긴 상태이므로 현재 작업 상태로 다시 그린다.
            _ = Task.Run(async () =>
            {
                try
                {
                    await _overlaySink.ResyncAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "오버레이 재연결 동기화 실패");
                }
            }, CancellationToken.None);
        }

        void OnLocalControlRequestSucceeded()
        {
            // 학생용 앱의 로컬 제어가 성공하면 대시보드 반영을 위해 체크인을 앞당긴다.
            Interlocked.Exchange(ref _checkInRequested, 1);
            _pollDelayCts?.Cancel();
        }

        _overlayPipe.ClientConnected += OnOverlayClientConnected;
        _overlayPipe.Start();
        _localControlPipe.RequestSucceeded += OnLocalControlRequestSucceeded;
        _localControlPipe.Start();

        try
        {
            await _overlaySink.ResyncAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "오버레이 초기 동기화 실패");
        }

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
                Interlocked.Exchange(ref _checkInRequested, 0);
                await CheckInAsync(stoppingToken);

                if (Volatile.Read(ref _checkInRequested) == 1) continue;

                using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                _pollDelayCts = delayCts;
                if (Volatile.Read(ref _checkInRequested) == 1) delayCts.Cancel();

                try
                {
                    await Task.Delay(
                        TimeSpan.FromSeconds(_options.CurrentValue.EffectivePollSeconds),
                        delayCts.Token);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // 학생용 앱의 로컬 작업 시작 → 즉시 체크인
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                finally
                {
                    if (ReferenceEquals(_pollDelayCts, delayCts)) _pollDelayCts = null;
                }
            }
        }
        finally
        {
            _overlayPipe.ClientConnected -= OnOverlayClientConnected;
            _localControlPipe.RequestSucceeded -= OnLocalControlRequestSucceeded;

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

            // 교사가 저장한 정책을 로컬 캐시에 반영한다. 그렇지 않으면 학생용 앱과
            // ScanNow가 서버 제한을 모른 채 기본 정책을 사용하게 된다.
            if (response.Policy is { } serverPolicy)
            {
                try
                {
                    var os = _runner.Os ?? WindowsOsInfoProvider.GetCurrent();
                    var effective = serverPolicy.SanitizeFor(os).WithoutFeatureUpdates();
                    await _store.SetSettingAsync(
                        ServerPolicyProvider.SettingKey,
                        UpdateAgentJson.Serialize(effective),
                        cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "서버 정책 캐시 저장 실패");
                }
            }

            // 서버가 받은 결과는 큐에서 제거한다. 거부/중복/만료 결과도 서버가 이미
            // 처리했으므로 함께 제거해야 이후 명령과 정책이 영구히 막히지 않는다.
            var acknowledgedIds = ResolveAcknowledgedCommandIds(request.CommandResults, response);
            if (acknowledgedIds.Count > 0)
            {
                await _commands.AcknowledgeResultsAsync(acknowledgedIds, cancellationToken);
            }

            if (response.CommandResultOutcomes.Count > 0)
            {
                foreach (var rejected in response.CommandResultOutcomes.Where(o => !o.Accepted))
                {
                    // 결과 본문/토큰은 남기지 않고 ID만 기록한다.
                    _logger.LogDebug("서버가 명령 결과를 거부했습니다: {CommandId}", rejected.CommandId);
                }
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

    /// <summary>
    /// 체크인 응답에서 큐에서 제거할 결과 ID를 고른다.
    /// 서버가 결과별 판정을 보내면 수락/거부 여부와 무관하게 그 ID들을 모두 제거한다.
    /// 이전 서버는 해당 필드가 없으므로 제출한 전체 ID를 제거한다(하위 호환).
    /// </summary>
    public static IReadOnlyList<string> ResolveAcknowledgedCommandIds(
        IReadOnlyList<CommandResult> submittedResults,
        AgentCheckInResponse response)
    {
        var source = response.CommandResultOutcomes.Count > 0
            ? response.CommandResultOutcomes.Select(o => o.CommandId)
            : submittedResults.Select(r => r.CommandId);

        return source
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
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
            // 감지 자체가 실패하면 "미설치"가 아니라 "확인 불가"로 보고한다.
            maestro = MaestroDetectionResult.Failed;
        }

        var wuBusy = false;
        var wuBusyProbeFailed = false;
        try
        {
            wuBusy = await _wua.IsBusyAsync(cancellationToken);
        }
        catch
        {
            // 확인에 실패했으므로 false를 "WU 유휴"로 해석하지 않는다.
            wuBusyProbeFailed = true;
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
            MaestroDetectionFailed = maestro.DetectionFailed,
            IsLaptop = hardware.IsLaptop,
            LaptopDetectionFailed = hardware.LaptopDetectionFailed,
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
            WuBusy = wuBusy,
            WuBusyProbeFailed = wuBusyProbeFailed,
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
