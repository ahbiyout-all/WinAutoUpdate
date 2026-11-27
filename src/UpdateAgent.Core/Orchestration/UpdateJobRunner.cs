using Microsoft.Extensions.Logging;
using UpdateAgent.Guards;
using UpdateAgent.Core.Maestro;
using UpdateAgent.Core.Policy;
using UpdateAgent.Core.Preflight;
using UpdateAgent.Core.Reboot;
using UpdateAgent.Core.Sessions;
using UpdateAgent.Core.Storage;
using UpdateAgent.Core.Windows;
using UpdateAgent.Core.Windows.Update;
using UpdateAgent.Shared.Models;

namespace UpdateAgent.Core.Orchestration;

public sealed class UpdateJobRunnerOptions
{
    public string AgentId { get; init; } = AgentIdentity.GetAgentId();
    public string AgentName { get; init; } = Environment.MachineName;
    public bool DryRun { get; init; }
    public OsInfo? OsOverride { get; init; }
}

public sealed record StartJobResult
{
    public bool Started { get; init; }
    public required PreflightResult Preflight { get; init; }
    public JobSummary? Job { get; init; }
    public string Message { get; init; } = "";
}

/// <summary>
/// 업데이트 작업 상태 머신.
/// 검색 → 다운로드 → 설치 → 재부팅 → 재개를 반복하며, 최대 재부팅 횟수와 실패 격리를 적용한다.
/// </summary>
public sealed class UpdateJobRunner : IAsyncDisposable
{
    private readonly IWuaClient _wua;
    private readonly IJobStore _store;
    private readonly IPreflightChecker _preflight;
    private readonly IRestorationProvider _maestro;
    private readonly IRebootManager _reboot;
    private readonly IUserSessionProbe _sessions;
    private readonly IJobProgressSink _sink;
    private readonly UpdateJobRunnerOptions _options;
    private readonly ILogger<UpdateJobRunner> _logger;
    private readonly SemaphoreSlim _control = new(1, 1);
    private readonly SemaphoreSlim _persistLock = new(1, 1);

    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private JobContext? _activeContext;
    private JobDetail? _current;
    private OsInfo? _os;
    private volatile bool _isRunning;
    private volatile bool _manualPause;
    private volatile bool _cancelRequested;
    private volatile bool _forceCancelled;
    private volatile bool _shutdownRequested;
    private volatile PauseReason _pauseReason;
    private DateTimeOffset? _pausedSince;

    public UpdateJobRunner(
        IWuaClient wua,
        IJobStore store,
        IPreflightChecker preflight,
        IRestorationProvider maestro,
        IRebootManager reboot,
        IUserSessionProbe sessions,
        IJobProgressSink sink,
        ILogger<UpdateJobRunner> logger,
        UpdateJobRunnerOptions? options = null)
    {
        _wua = wua;
        _store = store;
        _preflight = preflight;
        _maestro = maestro;
        _reboot = reboot;
        _sessions = sessions;
        _sink = sink;
        _logger = logger;
        _options = options ?? new UpdateJobRunnerOptions();
    }

    public bool IsRunning => _isRunning;
    public JobDetail? Current => _current;
    public OsInfo? Os => _os;

    public event EventHandler<JobDetail>? ProgressChanged;
    public event EventHandler<JobSummary>? RebootScheduled;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _store.InitializeAsync(cancellationToken);
        _os = _options.OsOverride ?? WindowsOsInfoProvider.GetCurrent();
        _logger.LogInformation(
            "UpdateAgent 초기화: {Os}, agent={AgentId}, dryRun={DryRun}",
            _os.SupportSummary,
            _options.AgentId,
            _options.DryRun);
    }

    public async Task<PreflightResult> RunPreflightAsync(
        UpdatePolicy policy,
        bool maestroConfirmed,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        var maestro = await _maestro.DetectAsync(cancellationToken);
        return await _preflight.CheckAsync(policy, _os!, maestro, maestroConfirmed, cancellationToken);
    }

    public async Task<ScanReport> ScanAsync(UpdatePolicy policy, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        var scanner = new UpdateScanner(_wua, _os);
        // 검색 결과와 실제 실행이 같은 정책 규칙을 사용해야 한다.
        // 기능 업데이트 2차 승인 전에는 검색 단계에서도 선택되지 않도록 ForExecution을 적용한다.
        return await scanner.ScanAsync(policy.ForExecution(_os), cancellationToken);
    }

    public async Task<StartJobResult> StartAsync(
        UpdatePolicy policy,
        bool maestroConfirmed,
        string confirmedBy = "teacher",
        string? jobIdOverride = null,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        // 서버가 정책을 잘못 전달하거나 구버전 에이전트 캐시가 남아 있어도
        // 실행 직전에 OS 안전 규칙과 기능 업데이트 2차 승인 규칙을 한 번 더 강제한다.
        // 원격 StartJob 페이로드는 이 지점을 반드시 통과한다.
        policy = policy.ForExecution(_os);
        await _control.WaitAsync(cancellationToken);
        try
        {
            if (_isRunning && _current is { Summary.IsTerminal: false })
            {
                return new StartJobResult
                {
                    Started = false,
                    Preflight = new PreflightResult(),
                    Job = _current.Summary,
                    Message = "이미 업데이트 작업이 진행 중입니다.",
                };
            }

            var detection = await _maestro.DetectAsync(cancellationToken);

            if (policy.RequireMaestroManualConfirmation && !maestroConfirmed)
            {
                return new StartJobResult
                {
                    Started = false,
                    Preflight = new PreflightResult
                    {
                        Checks =
                        [
                            PreflightCheck.Block(
                                "Maestro 복원 확인",
                                "Maestro 복원을 '매 부팅 자동 복구'에서 '수동 복구/해제'로 전환한 뒤 확인해야 합니다."),
                        ],
                    },
                    Message = "Maestro 수동 확인이 필요합니다.",
                };
            }

            var preflight = await _preflight.CheckAsync(
                policy, _os!, detection, maestroConfirmed, cancellationToken);

            if (!preflight.CanStart)
            {
                var reason = string.Join(" / ", preflight.BlockingChecks.Select(c => c.Message));
                _logger.LogWarning("사전 점검 차단: {Reason}", reason);
                return new StartJobResult
                {
                    Started = false,
                    Preflight = preflight,
                    Message = $"사전 점검 실패: {reason}",
                };
            }

            var jobId = string.IsNullOrWhiteSpace(jobIdOverride)
                ? JobIdGuard.Create(UtcClock.Now, Guid.NewGuid())
                : jobIdOverride;
            if (policy.RequireMaestroManualConfirmation)
            {
                await _maestro.ConfirmProtectionSuspendedAsync(jobId, confirmedBy, cancellationToken);
            }

            var context = new JobContext
            {
                JobId = jobId,
                AgentId = _options.AgentId,
                AgentName = _options.AgentName,
                Policy = policy,
                IsDryRun = _options.DryRun,
                PreflightChecks = preflight.Checks.ToList(),
                MaxRebootCount = policy.MaxRebootCount,
                State = UpdateJobState.Pending,
                Phase = "작업 준비",
                Message = "업데이트 작업을 시작합니다.",
                RequiresMaestroAttention = detection.Installed && policy.RequireMaestroManualConfirmation,
            };

            await PersistAsync(context, cancellationToken);
            StartRunLoop(context);
            _logger.LogInformation("작업 시작: {JobId}", jobId);

            return new StartJobResult
            {
                Started = true,
                Preflight = preflight,
                Job = context.ToSummary(),
                Message = "업데이트 작업을 시작했습니다.",
            };
        }
        finally
        {
            _control.Release();
        }
    }

    /// <summary>서비스 시작 시 호출. 재부팅/크래시 후 남은 작업을 재개한다.</summary>
    public async Task<JobSummary?> ResumeIfNeededAsync(CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await _control.WaitAsync(cancellationToken);
        try
        {
            if (_isRunning) return _current?.Summary;

            var detail = await _store.GetActiveJobAsync(_options.AgentId, cancellationToken);
            if (detail is null) return null;

            if (detail.Summary.IsTerminal) return detail.Summary;

            var context = JobContext.FromDetail(detail);
            // 캐시된 정책이 오래된/OS 안전 규칙을 어기는 상태로 남아 있어도 재개 시 다시 정규화한다.
            // 기능 업데이트 2차 승인이 없는 정책이 재부팅 후 되살아나지 않도록 StartAsync와 같은 규칙을 적용한다.
            context.Policy = context.Policy.ForExecution(_os);
            context.MaxRebootCount = context.Policy.MaxRebootCount;
            _current = detail;

            if (context.RebootCount > 0
                && !context.RestoreVerified
                && context.Policy.VerifyRestoreMarkerAfterReboot
                && context.State is UpdateJobState.Rebooting
                    or UpdateJobState.PendingReboot
                    or UpdateJobState.Downloading
                    or UpdateJobState.Installing
                    or UpdateJobState.Scanning
                    or UpdateJobState.Paused)
            {
                var verification = await _maestro.VerifyAfterRebootAsync(context.JobId, cancellationToken);
                context.MarkerVerification = verification;

                if (!verification.Verified)
                {
                    _logger.LogError("복원 마커 검증 실패: {Message}", verification.Message);
                    context.State = UpdateJobState.Failed;
                    context.Phase = "복원 위험";
                    context.PauseReason = PauseReason.RestoreRisk;
                    context.LastError = verification.Message;
                    context.Message = verification.Message;
                    context.RequiresMaestroAttention = true;
                    context.CompletedAt = UtcClock.Now;
                    await PersistAsync(context, CancellationToken.None);
                    _current = context.ToDetail();
                    return context.ToSummary();
                }

                context.RestoreVerified = true;
                context.Message = verification.Message;
                _logger.LogInformation("복원 마커 검증 통과: {Message}", verification.Message);
            }

            // 크래시/재부팅 직후 Downloading/Installing으로 남은 항목은 완료되었는지 알 수 없다.
            // 건너뛰면 작업이 100% 완료로 보고되므로 재시도 큐로 되돌린 뒤 저장한다.
            var requeued = 0;
            for (var i = 0; i < context.Items.Count; i++)
            {
                var item = context.Items[i];
                if (item.State is not (UpdateItemState.Downloading or UpdateItemState.Installing)) continue;

                context.Items[i] = item with
                {
                    State = UpdateItemState.Pending,
                    CompletedAt = null,
                };
                requeued++;
            }

            if (requeued > 0)
            {
                _logger.LogWarning("중단된 작업 항목 {Count}건을 재시도 큐로 되돌립니다.", requeued);
            }

            context.State = context.State == UpdateJobState.Paused ? UpdateJobState.Paused : UpdateJobState.Pending;
            context.Phase = context.State == UpdateJobState.Paused ? "일시중지" : "재개 준비";
            context.Message = "중단된 업데이트 작업을 재개합니다.";
            await PersistAsync(context, cancellationToken);

            StartRunLoop(context);
            return context.ToSummary();
        }
        finally
        {
            _control.Release();
        }
    }

    public async Task<JobSummary?> PauseAsync(
        PauseReason reason = PauseReason.Manual,
        CancellationToken cancellationToken = default)
    {
        await _control.WaitAsync(cancellationToken);
        try
        {
            _pauseReason = reason;
            _manualPause = reason == PauseReason.Manual;
            _pausedSince ??= UtcClock.Now;

            if (_activeContext is not null && !_activeContext.IsTerminal)
            {
                _activeContext.PauseReason = reason;
                _activeContext.Message = reason == PauseReason.Manual
                    ? "교사가 작업을 일시중지했습니다."
                    : "일시중지 중입니다.";
                // 실행 중이면 현재 항목이 끝난 뒤 러너가 상태를 갱신한다.
                if (!_isRunning)
                {
                    _activeContext.State = UpdateJobState.Paused;
                    _activeContext.Phase = "일시중지";
                    await PersistAsync(_activeContext, cancellationToken);
                }
            }
            else
            {
                var detail = await _store.GetActiveJobAsync(_options.AgentId, cancellationToken);
                if (detail is not null)
                {
                    var context = JobContext.FromDetail(detail);
                    context.State = UpdateJobState.Paused;
                    context.Phase = "일시중지";
                    context.PauseReason = reason;
                    context.Message = "교사가 작업을 일시중지했습니다.";
                    await PersistAsync(context, cancellationToken);
                    _current = context.ToDetail();
                }
            }

            return _current?.Summary;
        }
        finally
        {
            _control.Release();
        }
    }

    public async Task<JobSummary?> ResumeFromPauseAsync(CancellationToken cancellationToken = default)
    {
        await _control.WaitAsync(cancellationToken);
        try
        {
            _manualPause = false;
            _pauseReason = PauseReason.None;
            _pausedSince = null;

            if (_isRunning)
            {
                if (_activeContext is not null && _activeContext.State == UpdateJobState.Paused)
                {
                    _activeContext.State = UpdateJobState.Pending;
                    _activeContext.Phase = "재개 준비";
                    _activeContext.PauseReason = PauseReason.None;
                    _activeContext.Message = "작업을 재개합니다.";
                    await PersistAsync(_activeContext, cancellationToken);
                }

                return _current?.Summary;
            }
        }
        finally
        {
            _control.Release();
        }

        return await ResumeIfNeededAsync(cancellationToken);
    }

    /// <summary>기존 협력적 취소. 일반 "취소" 명령은 이 경로를 그대로 사용한다.</summary>
    public Task<JobSummary?> CancelAsync(CancellationToken cancellationToken = default) =>
        CancelAsync(false, cancellationToken);

    /// <summary>
    /// 작업 중단. force=true면 예약 재부팅·남은 업데이트를 취소하고 오버레이 잠금도 즉시 해제 대상으로 표시한다.
    /// 진행 중인 Windows Update 설치 자체는 안전한 체크포인트까지 기다린 뒤 멈춘다(설치 도중 강제 종료 불가).
    /// </summary>
    public async Task<JobSummary?> CancelAsync(bool force, CancellationToken cancellationToken = default)
    {
        // 시작 요청이 사전 점검/저장을 위해 _control을 쥐고 있는 동안 취소 플래그를 먼저 세팅하면
        // StartRunLoop가 플래그를 초기화해 중단 요청이 사라진다. 플래그 설정과 런루프 취소를
        // 시작과 같은 임계구역에서 수행해 이 경쟁을 막는다.
        await _control.WaitAsync(cancellationToken);
        try
        {
            if (force) _forceCancelled = true;
            _cancelRequested = true;
            _manualPause = false;
            _pauseReason = PauseReason.None;

            try
            {
                await _reboot.CancelRebootAsync(CancellationToken.None);
            }
            catch
            {
                // 예약된 재부팅이 없으면 실패한다. 무시한다.
            }

            try
            {
                _runCts?.Cancel();
            }
            catch
            {
                // ignore
            }

            if (_activeContext is not null && !_activeContext.IsTerminal)
            {
                if (!_isRunning)
                {
                    MarkCancelled(_activeContext);
                    await PersistAsync(_activeContext, cancellationToken);
                }
                // 실행 중이면 런루프가 다음 체크포인트에서 MarkCancelled + 저장을 수행한다.
            }
            else
            {
                var detail = await _store.GetActiveJobAsync(_options.AgentId, cancellationToken);
                if (detail is not null)
                {
                    var context = JobContext.FromDetail(detail);
                    MarkCancelled(context);
                    await PersistAsync(context, cancellationToken);
                    _current = context.ToDetail();
                }
            }

            return _current?.Summary;
        }
        finally
        {
            _control.Release();
        }
    }

    private void MarkCancelled(JobContext context)
    {
        context.State = UpdateJobState.Cancelled;
        context.Phase = _forceCancelled ? "강제 중단됨" : "취소됨";
        context.Message = _forceCancelled
            ? "강제 중단 요청으로 업데이트를 중지했습니다. 설치 중이던 항목은 완료될 수 있습니다."
            : "교사가 작업을 취소했습니다.";
        context.CompletedAt = UtcClock.Now;
    }

    /// <summary>
    /// 서비스 종료로 인한 취소는 사용자 취소가 아니다. 완료 처리하지 않고 재개 가능한 상태로 남긴다.
    /// </summary>
    private void LeaveResumableOnShutdown(JobContext context)
    {
        context.CompletedAt = null;
        context.LastError = null;

        if (context.State == UpdateJobState.Paused || _pauseReason != PauseReason.None)
        {
            context.State = UpdateJobState.Paused;
            context.Phase = "일시중지";
            context.PauseReason = _pauseReason == PauseReason.None ? PauseReason.Manual : _pauseReason;
            context.Message = "서비스 종료로 중단되었습니다. 다시 시작하면 이어서 진행합니다.";
            return;
        }

        context.State = UpdateJobState.Pending;
        context.Phase = "재개 대기";
        context.PauseReason = PauseReason.None;
        context.PauseUntil = null;
        context.Message = "서비스 종료로 중단되었습니다. 다시 시작하면 이어서 진행합니다.";
    }

    private void StartRunLoop(JobContext context)
    {
        _activeContext = context;
        _current = context.ToDetail();
        _isRunning = true;
        _cancelRequested = false;
        _forceCancelled = false;
        // 서비스 재시작 후 복원된 수동 일시정지는 유지한다. 자동 재개 대상이 아니므로
        // 교사가 명시적으로 ResumeJob를 보내기 전까지 대기한다.
        var resumePaused = context.State == UpdateJobState.Paused;
        _pauseReason = resumePaused ? context.PauseReason : PauseReason.None;
        // PauseUntil은 마감 시각이지 일시중지 시작 시각이 아니다. 저장된 마감을 그대로
        // _pausedSince로 쓰면 재시작할 때마다 최대 일시중지 시간이 다시 더해진다.
        _pausedSince = resumePaused
            ? context.PauseUntil.HasValue && context.Policy.MaxPauseHours > 0
                ? SubtractHoursSafely(context.PauseUntil.Value, context.Policy.MaxPauseHours)
                : UtcClock.Now
            : null;
        _manualPause = resumePaused && context.PauseReason == PauseReason.Manual;
        _runCts = new CancellationTokenSource();
        var token = _runCts.Token;
        _runTask = Task.Run(() => RunJobAsync(context, token), CancellationToken.None);
    }

    private async Task RunJobAsync(JobContext context, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_forceCancelled) throw new OperationCanceledException(cancellationToken);
                await WaitForResumeAsync(context, cancellationToken);
                CheckRuntimeLimit(context);

                if (context.RebootCount > context.MaxRebootCount)
                {
                    throw new JobFailureException(
                        $"최대 재부팅 횟수({context.MaxRebootCount}회)를 초과했습니다. 남은 업데이트를 수동으로 확인하세요.");
                }

                context.State = UpdateJobState.Scanning;
                context.Phase = "업데이트 검색 중";
                context.CurrentKb = null;
                context.Message = "Windows Update에서 설치 가능한 업데이트를 검색합니다.";
                await PersistAsync(context, cancellationToken);

                WuaScanResult scan;
                try
                {
                    scan = await _wua.ScanAsync(context.Policy.IncludeDrivers, cancellationToken);
                }
                catch (WuaException ex)
                {
                    throw new JobFailureException($"업데이트 검색 실패: {ex.Message}");
                }

                var evaluation = UpdatePolicyEvaluator.Filter(scan.Updates, context.Policy, _os!);
                var selected = evaluation.Selected;
                MergeItems(context, selected.Select(u => u.Info).ToList());
                await PersistAsync(context, cancellationToken);

                var pending = context.Items
                    .Where(i => i.State is UpdateItemState.Pending or UpdateItemState.Downloaded)
                    .ToList();

                if (pending.Count == 0)
                {
                    // 검색 중 들어온 일시중지 요청이 완료/재부팅을 가로지르지 않도록 확인한다.
                    await WaitForResumeAsync(context, cancellationToken);

                    // 시뮬레이션은 실제 설치가 없으므로 실제 WU 재부팅 요구를 근거로
                    // shutdown.exe를 예약하지 않는다. 검색 결과의 상태도 같은 이유로 무시한다.
                    var rebootRequiredAfterScan = false;
                    if (!_options.DryRun)
                    {
                        var status = await _wua.GetSystemStatusAsync(cancellationToken);
                        rebootRequiredAfterScan = status.RebootRequired || scan.Status.RebootRequired;
                    }

                    if (rebootRequiredAfterScan && context.RebootCount < context.MaxRebootCount)
                    {
                        await RebootAndStopAsync(context, "업데이트 적용을 위해 재부팅합니다.", cancellationToken);
                        return;
                    }

                    await CompleteAsync(context, cancellationToken);
                    return;
                }

                var nativeById = selected.ToDictionary(u => u.Info.UpdateId, u => u, StringComparer.OrdinalIgnoreCase);

                // 다운로드
                context.State = UpdateJobState.Downloading;
                foreach (var item in pending.ToList())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await WaitForResumeAsync(context, cancellationToken);

                    if (!nativeById.TryGetValue(item.UpdateId, out var native))
                    {
                        MarkFailure(context, item, "검색 결과에서 업데이트를 찾을 수 없습니다.");
                        await PersistAsync(context, cancellationToken);
                        continue;
                    }

                    context.CurrentKb = item.KbArticleIds;
                    context.Phase = _options.DryRun ? "업데이트 다운로드 시뮬레이션" : "업데이트 다운로드 중";
                    context.Message = _options.DryRun
                        ? $"{item.KbArticleIds} 다운로드 시뮬레이션(실제 다운로드 없음)"
                        : $"{item.KbArticleIds} 다운로드 중";
                    SetItemState(context, item.UpdateId, UpdateItemState.Downloading);
                    await PersistAsync(context, cancellationToken);

                    WuaOperationResult result;
                    if (_options.DryRun)
                    {
                        result = WuaOperationResult.Ok("DRY RUN");
                    }
                    else
                    {
                        result = await _wua.DownloadAsync(native, cancellationToken);
                    }

                    if (result.Success)
                    {
                        SetItemState(context, item.UpdateId, UpdateItemState.Downloaded);
                    }
                    else
                    {
                        MarkFailure(context, FindItem(context, item.UpdateId), result.Message);
                    }

                    await PersistAsync(context, cancellationToken);
                }

                // 설치
                context.State = UpdateJobState.Installing;
                var rebootRequired = false;
                var installedThisPass = 0;
                var itemsToInstall = context.Items
                    .Where(i => i.State == UpdateItemState.Downloaded)
                    .ToList();

                foreach (var item in itemsToInstall)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await WaitForResumeAsync(context, cancellationToken);

                    if (!nativeById.TryGetValue(item.UpdateId, out var native))
                    {
                        continue;
                    }

                    context.CurrentKb = item.KbArticleIds;
                    context.Phase = _options.DryRun ? "업데이트 설치 시뮬레이션" : "업데이트 설치 중";
                    context.Message = _options.DryRun
                        ? $"{item.KbArticleIds} 설치 시뮬레이션(실제 설치 없음)"
                        : $"{item.KbArticleIds} 설치 중";
                    SetItemState(context, item.UpdateId, UpdateItemState.Installing);
                    await PersistAsync(context, cancellationToken);

                    WuaInstallResult result;
                    if (_options.DryRun)
                    {
                        result = WuaInstallResult.Ok(false, "DRY RUN");
                    }
                    else
                    {
                        result = await InstallWithBusyRetryAsync(
                            native, context.Policy, cancellationToken);
                    }

                    if (result.Success)
                    {
                        installedThisPass++;
                        rebootRequired |= result.RebootRequired;
                        SetItemState(context, item.UpdateId, UpdateItemState.Installed);
                    }
                    else
                    {
                        MarkFailure(context, FindItem(context, item.UpdateId), result.Message);
                    }

                    context.CurrentKb = null;
                    await PersistAsync(context, cancellationToken);
                }

                // DRY RUN의 설치 결과는 시뮬레이션이므로 실제 시스템 상태로 재부팅을 결정하지 않는다.
                if (!_options.DryRun)
                {
                    var systemStatus = await _wua.GetSystemStatusAsync(cancellationToken);
                    rebootRequired |= systemStatus.RebootRequired;
                }

                // 설치 직후의 일시중지 요청도 재부팅 예약 전에 반영한다.
                await WaitForResumeAsync(context, cancellationToken);

                if (rebootRequired)
                {
                    if (context.Policy.RebootPolicy != RebootPolicy.Auto)
                    {
                        context.State = UpdateJobState.Paused;
                        context.Phase = "재부팅 필요";
                        context.PauseReason = PauseReason.Manual;
                        context.Message = "업데이트 적용을 위해 재부팅이 필요합니다. 교사가 재부팅한 뒤 재개하세요.";
                        await PersistAsync(context, cancellationToken);
                        return;
                    }

                    if (context.RebootCount >= context.MaxRebootCount)
                    {
                        throw new JobFailureException(
                            $"최대 재부팅 횟수({context.MaxRebootCount}회)에 도달했습니다. 일부 업데이트가 남아 있을 수 있습니다.");
                    }

                    await RebootAndStopAsync(context, "Windows Update 적용을 위해 자동 재부팅합니다.", cancellationToken);
                    return;
                }

                if (installedThisPass == 0)
                {
                    context.PassesWithoutProgress++;
                    if (context.PassesWithoutProgress >= 2)
                    {
                        throw new JobFailureException(
                            "업데이트 설치가 진행되지 않았습니다. 실패한 업데이트 로그를 확인하세요.");
                    }
                }
                else
                {
                    context.PassesWithoutProgress = 0;
                }

                // 재부팅 없이 추가 업데이트가 있을 수 있으므로 다시 검색한다.
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _forceCancelled)
        {
            if (_shutdownRequested && !_forceCancelled && !_cancelRequested)
            {
                LeaveResumableOnShutdown(context);
            }
            else
            {
                MarkCancelled(context);
            }

            await PersistSafeAsync(context);
        }
        catch (JobFailureException ex)
        {
            context.State = UpdateJobState.Failed;
            context.Phase = "실패";
            context.LastError = ex.Message;
            context.Message = ex.Message;
            context.CompletedAt = UtcClock.Now;
            context.RequiresMaestroAttention = context.RequiresMaestroAttention || context.RebootCount > 0;
            await PersistSafeAsync(context);
            _logger.LogError(ex, "작업 실패: {JobId}", context.JobId);
        }
        catch (Exception ex)
        {
            context.State = UpdateJobState.Failed;
            context.Phase = "실패";
            context.LastError = ex.Message;
            context.Message = $"예기치 않은 오류: {ex.Message}";
            context.CompletedAt = UtcClock.Now;
            await PersistSafeAsync(context);
            _logger.LogError(ex, "작업 중 예외: {JobId}", context.JobId);
        }
        finally
        {
            _isRunning = false;
            _activeContext = null;
        }
    }

    private async Task<WuaInstallResult> InstallWithBusyRetryAsync(
        WuaUpdate update,
        UpdatePolicy policy,
        CancellationToken cancellationToken)
    {
        // 정책의 WuBusyWaitMinutes가 실제 대기 시간을 결정한다. 무한 대기하지 않도록
        // 0/음수는 1분으로 보정하고, 마지막 재시도는 남은 시간만 기다린다.
        var retryDelay = TimeSpan.FromSeconds(30);
        var maxWait = TimeSpan.FromMinutes(Math.Max(1, policy.WuBusyWaitMinutes));
        var deadline = UtcClock.Now + maxWait;
        var attempt = 0;

        while (true)
        {
            var result = await _wua.InstallAsync(update, cancellationToken);
            if (result.Success) return result;

            var busy = result.Message.Contains("진행 중", StringComparison.Ordinal)
                       || result.ResultCode == 5
                       || await _wua.IsBusyAsync(cancellationToken);

            if (!busy) return result;

            var remaining = deadline - UtcClock.Now;
            if (remaining <= TimeSpan.Zero) break;

            var delay = remaining < retryDelay ? remaining : retryDelay;
            attempt++;
            _logger.LogWarning(
                "Windows Update가 사용 중입니다. {Delay}초 후 재시도 ({Attempt}): {Message}",
                (int)delay.TotalSeconds,
                attempt,
                result.Message);
            await Task.Delay(delay, cancellationToken);
        }

        return WuaInstallResult.Failed(
            5,
            0,
            $"Windows Update가 {policy.WuBusyWaitMinutes}분 동안 사용 중입니다. " +
            "Maestro/Windows Update 작업이 끝난 뒤 재개하세요.");
    }

    private async Task WaitForResumeAsync(JobContext context, CancellationToken cancellationToken)
    {
        var policy = context.Policy;

        if (!_manualPause && _pauseReason == PauseReason.None && policy.PauseOnUserLogon)
        {
            var probe = _sessions.Probe();
            if (probe.ProbeFailed || probe.Sessions.Count > 0)
            {
                _pauseReason = PauseReason.UserLoggedOn;
                _pausedSince ??= UtcClock.Now;
                if (probe.ProbeFailed)
                {
                    _logger.LogWarning(
                        "사용자 세션을 확인할 수 없어 일시중지합니다: {Error}", probe.Error);
                }
            }
        }

        if (!_manualPause && _pauseReason == PauseReason.None)
        {
            if (context.State == UpdateJobState.Paused)
            {
                context.State = UpdateJobState.Pending;
                context.PauseReason = PauseReason.None;
                context.PauseUntil = null;
                await PersistAsync(context, cancellationToken);
            }

            return;
        }

        context.State = UpdateJobState.Paused;
        context.Phase = _pauseReason == PauseReason.UserLoggedOn
            ? "사용자 로그온으로 일시중지"
            : "교사 일시중지";
        context.PauseReason = _pauseReason;
        context.PauseUntil = policy.MaxPauseHours > 0 && _pausedSince.HasValue
            ? AddHoursSafely(_pausedSince.Value, policy.MaxPauseHours)
            : null;
        context.Message = _pauseReason switch
        {
            PauseReason.UserLoggedOn =>
                "사용자가 로그온하여 업데이트를 일시중지했습니다. 로그오프하면 자동 재개됩니다.",
            PauseReason.WuBusy =>
                "Windows Update가 사용 중이어서 일시중지했습니다. 교사가 재개할 때까지 대기합니다.",
            PauseReason.RestoreRisk =>
                "복원 위험이 감지되어 일시중지했습니다. 교사가 확인한 뒤 재개하세요.",
            PauseReason.PreflightBlocked =>
                "사전 점검에서 차단되어 일시중지했습니다. 교사가 확인한 뒤 재개하세요.",
            PauseReason.WaitingForNetwork =>
                "네트워크를 확인할 수 없어 일시중지했습니다. 교사가 확인한 뒤 재개하세요.",
            _ => "교사가 작업을 일시중지했습니다.",
        };
        await PersistAsync(context, cancellationToken);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_cancelRequested) throw new OperationCanceledException(cancellationToken);

            if (_manualPause)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
                continue;
            }

            // ResumeFromPauseAsync가 이유를 지울 때까지 여기서 대기한다.
            if (_pauseReason == PauseReason.None) break;

            if (_pauseReason == PauseReason.UserLoggedOn)
            {
                var probe = _sessions.Probe();
                // 조회 실패는 "사용자 없음"이 아니므로 계속 대기한다(fail closed).
                if (!probe.ProbeFailed && probe.Sessions.Count == 0) break;

                // TimeSpan.FromHours(int.MaxValue)는 오버플로한다. double 비교로
                // 매우 큰 값도 안전하게 "아직 만료되지 않음"으로 처리한다.
                if (policy.MaxPauseHours > 0
                    && _pausedSince.HasValue
                    && (UtcClock.Now - _pausedSince.Value).TotalHours > policy.MaxPauseHours)
                {
                    _logger.LogWarning(
                        "사용자 로그온 일시중지가 최대 시간({Hours}시간)을 초과하여 작업을 재개합니다.",
                        policy.MaxPauseHours);
                    break;
                }

                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                continue;
            }

            // WuBusy/RestoreRisk/PreflightBlocked/WaitingForNetwork 등은 자동 재개하지 않는다.
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        }

        _pauseReason = PauseReason.None;
        _pausedSince = null;
        context.State = UpdateJobState.Pending;
        context.Phase = "재개 준비";
        context.PauseReason = PauseReason.None;
        context.PauseUntil = null;
        context.Message = "업데이트 작업을 재개합니다.";
        await PersistAsync(context, cancellationToken);
    }

    private async Task RebootAndStopAsync(JobContext context, string reason, CancellationToken cancellationToken)
    {
        context.RebootCount++;
        context.RestoreVerified = false;
        context.State = UpdateJobState.PendingReboot;
        context.Phase = "재부팅 준비";
        context.Message = $"{context.Policy.RebootDelaySeconds}초 후 자동으로 재부팅합니다. ({context.RebootCount}/{context.MaxRebootCount})";
        context.CurrentKb = null;

        await _maestro.WriteResumeMarkerAsync(
            context.JobId,
            context.AgentId,
            context.RebootCount,
            "before-reboot",
            cancellationToken);

        await PersistAsync(context, cancellationToken);

        context.State = UpdateJobState.Rebooting;
        context.Phase = "재부팅 중";
        await PersistAsync(context, cancellationToken);
        RebootScheduled?.Invoke(this, context.ToSummary());

        _logger.LogInformation(
            "재부팅 예약: job={JobId}, count={Count}, delay={Delay}s",
            context.JobId,
            context.RebootCount,
            context.Policy.RebootDelaySeconds);

        await _reboot.ScheduleRebootAsync(context.Policy.RebootDelaySeconds, reason, cancellationToken);

        // shutdown.exe가 예약되면 서비스는 곧 종료된다. 상태는 이미 저장되어 있다.
    }

    private async Task CompleteAsync(JobContext context, CancellationToken cancellationToken)
    {
        context.State = UpdateJobState.Completed;
        context.Phase = "완료";
        context.Message = BuildCompletionMessage(context);
        context.CompletedAt = UtcClock.Now;
        context.CurrentKb = null;
        context.PauseReason = PauseReason.None;
        context.RequiresMaestroAttention =
            context.RequiresMaestroAttention && context.Policy.RequireMaestroManualConfirmation;
        await PersistAsync(context, cancellationToken);

        _logger.LogInformation(
            "작업 완료: {JobId}, installed={Installed}, failed={Failed}, reboots={Reboots}",
            context.JobId,
            context.InstalledItems,
            context.FailedItems,
            context.RebootCount);

        if (!context.IsDryRun && context.Policy.ShutdownAfterComplete && !context.RequiresMaestroAttention)
        {
            try
            {
                await _reboot.ScheduleShutdownAsync(120, "UpdateAgent: 업데이트 완료 후 자동 종료", cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "자동 종료 예약에 실패했습니다.");
            }
        }
    }

    private static string BuildCompletionMessage(JobContext context)
    {
        var baseMessage = context.IsDryRun
            ? $"DRY RUN 시뮬레이션 완료(실제 설치 없음): 설치 예정 {context.InstalledItems}건, 실패/제외 {context.FailedItems}건, 재부팅 {context.RebootCount}회."
            : $"업데이트 완료: 설치 {context.InstalledItems}건, 실패/제외 {context.FailedItems}건, 재부팅 {context.RebootCount}회.";

        if (context.RequiresMaestroAttention)
        {
            baseMessage += " Maestro에서 '현재 상태 저장' 후 '자동 복구'를 다시 활성화하세요.";
        }

        return baseMessage;
    }

    private void MergeItems(JobContext context, IReadOnlyList<UpdateItemInfo> selected)
    {
        var now = UtcClock.Now;
        var selectedIds = selected.Select(u => u.UpdateId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var update in selected)
        {
            var existing = FindItem(context, update.UpdateId);
            if (existing is null)
            {
                context.Items.Add(new JobItem
                {
                    UpdateId = update.UpdateId,
                    KbArticleIds = update.KbLabel,
                    Title = update.Title,
                    Category = update.Category,
                    State = UpdateItemState.Pending,
                });
                continue;
            }

            if (existing.State is UpdateItemState.Installed
                or UpdateItemState.Skipped
                or UpdateItemState.Cancelled)
            {
                continue;
            }

            var index = context.Items.FindIndex(i =>
                string.Equals(i.UpdateId, existing.UpdateId, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                context.Items[index] = existing with
                {
                    KbArticleIds = string.IsNullOrWhiteSpace(existing.KbArticleIds) ? update.KbLabel : existing.KbArticleIds,
                    Title = string.IsNullOrWhiteSpace(existing.Title) ? update.Title : existing.Title,
                    Category = update.Category,
                };
            }
        }

        // WUA 검색 결과에서 사라진 항목을 설치 성공으로 기록하면 실제 실패가 은폐된다.
        for (var i = 0; i < context.Items.Count; i++)
        {
            var item = context.Items[i];
            if (selectedIds.Contains(item.UpdateId)) continue;

            if (item.State is UpdateItemState.Downloading or UpdateItemState.Installing)
            {
                // 진행 중이던 항목이 사라진 경우 성공으로 처리하지 않는다. 설치 여부를
                // 확인할 수 없으므로 재시도하고, 계속 사라지면 확인 불가로 격리한다.
                var failureCount = item.FailureCount + 1;
                var retry = failureCount < Math.Max(1, context.Policy.MaxItemFailureCount);
                context.Items[i] = item with
                {
                    FailureCount = failureCount,
                    State = retry ? UpdateItemState.Pending : UpdateItemState.Skipped,
                    CompletedAt = retry ? null : now,
                    Error = "WUA 검색 결과에서 항목이 사라져 설치 여부를 확인할 수 없습니다. " +
                            (retry ? "재시도합니다." : "확인할 수 없어 제외했습니다."),
                };
                continue;
            }

            // 숨김/거부/메타데이터 변경 등 원인을 알 수 없는 대기 항목은 건너뜀으로 표시한다.
            if (item.State is not (UpdateItemState.Pending or UpdateItemState.Downloaded)) continue;

            context.Items[i] = item with
            {
                State = UpdateItemState.Skipped,
                CompletedAt = now,
                Error = "WUA 검색 결과에서 항목이 사라져 설치 여부를 확인할 수 없습니다.",
            };
        }
    }

    private static JobItem? FindItem(JobContext context, string updateId) =>
        context.Items.FirstOrDefault(i =>
            string.Equals(i.UpdateId, updateId, StringComparison.OrdinalIgnoreCase));

    private static void SetItemState(JobContext context, string updateId, UpdateItemState state)
    {
        var index = context.Items.FindIndex(i =>
            string.Equals(i.UpdateId, updateId, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return;

        context.Items[index] = context.Items[index] with
        {
            State = state,
            CompletedAt = state is UpdateItemState.Installed or UpdateItemState.Failed or UpdateItemState.Skipped
                ? UtcClock.Now
                : context.Items[index].CompletedAt,
        };
    }

    private static void MarkFailure(JobContext context, JobItem? item, string error)
    {
        if (item is null) return;

        var index = context.Items.FindIndex(i =>
            string.Equals(i.UpdateId, item.UpdateId, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return;

        var failureCount = item.FailureCount + 1;
        var state = failureCount >= context.Policy.MaxItemFailureCount
            ? UpdateItemState.Skipped
            : UpdateItemState.Pending;

        context.Items[index] = item with
        {
            FailureCount = failureCount,
            State = state,
            Error = error,
            CompletedAt = state == UpdateItemState.Skipped ? UtcClock.Now : null,
        };

        if (state == UpdateItemState.Skipped)
        {
            context.Message = $"{item.KbArticleIds}은(는) {failureCount}회 실패하여 제외했습니다.";
        }
    }

    /// <summary>
    /// DateTimeOffset.AddHours는 int 최댓값처럼 큰 시간에서 오버플로한다.
    /// 표현 가능한 범위까지만 더해 마감 시각을 안전하게 계산한다.
    /// </summary>
    private static DateTimeOffset AddHoursSafely(DateTimeOffset value, int hours)
    {
        if (hours <= 0) return value;
        // double 시간 계산의 반올림으로 경계를 넘지 않도록 1시간 여유를 두고 딱 잘라낸다.
        var availableHours = (DateTimeOffset.MaxValue - value).TotalHours;
        return hours >= availableHours - 1 ? DateTimeOffset.MaxValue : value.AddHours(hours);
    }

    private static DateTimeOffset SubtractHoursSafely(DateTimeOffset value, int hours)
    {
        if (hours <= 0) return value;
        var availableHours = (value - DateTimeOffset.MinValue).TotalHours;
        return hours >= availableHours - 1 ? DateTimeOffset.MinValue : value.AddHours(-hours);
    }

    private void CheckRuntimeLimit(JobContext context)
    {
        if (context.Policy.MaxRuntimeHours <= 0) return;
        // TimeSpan.FromHours(int.MaxValue)는 오버플로 예외를 던져 정상 작업을 실패시킨다.
        // 시간 제한이 없는 매우 큰 값은 double 비교로 안전하게 처리한다.
        var elapsedHours = (UtcClock.Now - context.StartedAt).TotalHours;
        if (elapsedHours > context.Policy.MaxRuntimeHours)
        {
            throw new JobFailureException(
                $"작업 제한 시간({context.Policy.MaxRuntimeHours}시간)을 초과했습니다.");
        }
    }

    private async Task PersistAsync(JobContext context, CancellationToken cancellationToken)
    {
        context.UpdatedAt = UtcClock.Now;
        var detail = context.ToDetail();

        await _persistLock.WaitAsync(CancellationToken.None);
        try
        {
            await _store.SaveJobAsync(detail, cancellationToken);
            _current = detail;
        }
        catch (OperationCanceledException)
        {
            // 취소 중에도 최종 상태는 저장한다.
            await _store.SaveJobAsync(detail, CancellationToken.None);
            _current = detail;
        }
        finally
        {
            _persistLock.Release();
        }

        try
        {
            await _sink.OnProgressAsync(detail, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "진행 상황 전송 실패");
        }

        try
        {
            ProgressChanged?.Invoke(this, detail);
        }
        catch (Exception ex)
        {
            // UI 구독자의 예외가 이미 저장된 작업을 실패로 바꾸지 않게 한다.
            _logger.LogWarning(ex, "진행 상황 구독자 알림 실패");
        }
    }

    private async Task PersistSafeAsync(JobContext context)
    {
        try
        {
            await PersistAsync(context, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "최종 상태 저장 실패");
        }
    }

    private void EnsureInitialized()
    {
        if (_os is null)
        {
            throw new InvalidOperationException("UpdateJobRunner.InitializeAsync를 먼저 호출하세요.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        // 서비스 종료는 사용자 취소가 아니다. 플래그를 먼저 세팅해 런루프가
        // 재개 가능한 상태로 저장하도록 구분한다.
        _shutdownRequested = true;
        try
        {
            _runCts?.Cancel();
        }
        catch
        {
            // ignore
        }

        if (_runTask is not null)
        {
            try
            {
                await Task.WhenAny(_runTask, Task.Delay(TimeSpan.FromSeconds(5)));
            }
            catch
            {
                // ignore
            }
        }

        _runCts?.Dispose();
        _control.Dispose();
        _persistLock.Dispose();
    }

    private sealed class JobFailureException : Exception
    {
        public JobFailureException(string message) : base(message)
        {
        }
    }

    private sealed class JobContext
    {
        public required string JobId { get; init; }
        public required string AgentId { get; init; }
        public required string AgentName { get; init; }
        public required UpdatePolicy Policy { get; set; }

        /// <summary>시뮬레이션 작업은 실제 설치로 보고하지 않는다.</summary>
        public bool IsDryRun { get; init; }

        public List<PreflightCheck> PreflightChecks { get; init; } = [];
        public List<JobItem> Items { get; init; } = [];

        public UpdateJobState State { get; set; } = UpdateJobState.Pending;
        public string Phase { get; set; } = "대기";
        public string Message { get; set; } = "";
        public string? CurrentKb { get; set; }
        public PauseReason PauseReason { get; set; }
        public DateTimeOffset? PauseUntil { get; set; }
        public int RebootCount { get; set; }
        public int MaxRebootCount { get; set; } = 5;
        public DateTimeOffset StartedAt { get; set; } = UtcClock.Now;
        public DateTimeOffset UpdatedAt { get; set; } = UtcClock.Now;
        public DateTimeOffset? CompletedAt { get; set; }
        public string? LastError { get; set; }
        public bool RestoreVerified { get; set; }
        public bool RequiresMaestroAttention { get; set; }
        public int PassesWithoutProgress { get; set; }
        public MarkerVerificationResult? MarkerVerification { get; set; }

        public bool IsTerminal => JobStateRules.IsTerminal(State.ToString());

        public int TotalItems => Items.Count;
        public int InstalledItems => Items.Count(i => i.State == UpdateItemState.Installed);
        public int FailedItems => Items.Count(i =>
            i.State == UpdateItemState.Skipped || i.State == UpdateItemState.Failed);
        public int CompletedItems => Items.Count(i =>
            i.State is UpdateItemState.Installed or UpdateItemState.Skipped or UpdateItemState.Failed);

        public int ProgressPercent => ProgressGuard.Percent(
            State == UpdateJobState.Completed, CompletedItems, TotalItems);

        public JobSummary ToSummary() => new()
        {
            JobId = JobId,
            AgentId = AgentId,
            AgentName = AgentName,
            State = State,
            IsDryRun = IsDryRun,
            Phase = Phase,
            ProgressPercent = ProgressPercent,
            TotalItems = TotalItems,
            CompletedItems = CompletedItems,
            InstalledItems = InstalledItems,
            FailedItems = FailedItems,
            RebootCount = RebootCount,
            MaxRebootCount = MaxRebootCount,
            PassesWithoutProgress = PassesWithoutProgress,
            CurrentKb = CurrentKb,
            Message = Message,
            PauseReason = PauseReason,
            PauseUntil = PauseUntil,
            StartedAt = StartedAt,
            UpdatedAt = UpdatedAt,
            CompletedAt = CompletedAt,
            LastError = LastError,
            RestoreVerified = RestoreVerified,
            RequiresMaestroAttention = RequiresMaestroAttention,
        };

        public JobDetail ToDetail() => new()
        {
            Summary = ToSummary(),
            Items = Items.ToList(),
            Policy = Policy,
            PreflightChecks = PreflightChecks.ToList(),
            MarkerVerification = MarkerVerification,
        };

        public static JobContext FromDetail(JobDetail detail) => new()
        {
            JobId = detail.Summary.JobId,
            AgentId = detail.Summary.AgentId,
            AgentName = detail.Summary.AgentName,
            IsDryRun = detail.Summary.IsDryRun,
            Policy = detail.Policy,
            PreflightChecks = detail.PreflightChecks.ToList(),
            Items = detail.Items.ToList(),
            State = detail.Summary.State,
            Phase = detail.Summary.Phase,
            Message = detail.Summary.Message,
            CurrentKb = detail.Summary.CurrentKb,
            PauseReason = detail.Summary.PauseReason,
            PauseUntil = detail.Summary.PauseUntil,
            RebootCount = detail.Summary.RebootCount,
            MaxRebootCount = detail.Summary.MaxRebootCount,
            PassesWithoutProgress = detail.Summary.PassesWithoutProgress,
            StartedAt = detail.Summary.StartedAt,
            UpdatedAt = detail.Summary.UpdatedAt,
            CompletedAt = detail.Summary.CompletedAt,
            LastError = detail.Summary.LastError,
            RestoreVerified = detail.Summary.RestoreVerified,
            RequiresMaestroAttention = detail.Summary.RequiresMaestroAttention,
            MarkerVerification = detail.MarkerVerification,
        };
    }
}
