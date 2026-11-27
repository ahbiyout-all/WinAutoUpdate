using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using UpdateAgent.Core.Maestro;
using UpdateAgent.Core.Orchestration;
using UpdateAgent.Core.Preflight;
using UpdateAgent.Core.Reboot;
using UpdateAgent.Core.Sessions;
using UpdateAgent.Core.Storage;
using UpdateAgent.Core.Windows.Update;
using UpdateAgent.Shared.Models;
using UpdateAgent.Shared.Serialization;

namespace UpdateAgent.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;

        var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "help";
        var options = ParseOptions(args.Skip(1).ToArray());

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("\n중단 요청됨...");
            cts.Cancel();
        };

        try
        {
            return command switch
            {
                "scan" => await RunScanAsync(options, cts.Token),
                "preflight" => await RunPreflightAsync(options, cts.Token),
                "run" => await RunJobAsync(options, cts.Token),
                "resume" => await RunJobAsync(options, cts.Token, resumeOnly: true),
                "status" => await ShowStatusAsync(cts.Token),
                "maestro" => await ShowMaestroAsync(cts.Token),
                "pause" => await PauseAsync(cts.Token),
                "cancel" => await CancelAsync(cts.Token),
                "help" or "--help" or "-h" => ShowHelp(),
                _ => Unknown(command),
            };
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("취소되었습니다.");
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"오류: {ex.Message}");
            return 1;
        }
    }

    private static List<string> _lastKeys = [];

    private static async Task<int> RunScanAsync(Dictionary<string, string?> options, CancellationToken ct)
    {
        using var wua = new WuaClient();
        var scanner = new UpdateScanner(wua);
        var policy = BuildPolicy(options);

        Console.WriteLine($"PC: {Environment.MachineName}");
        Console.WriteLine($"OS: {scanner.Os.SupportSummary}");
        Console.WriteLine("Windows Update 검색 중... (네트워크에 따라 수 분 소요)");
        Console.WriteLine();

        var report = await scanner.ScanAsync(policy, ct);

        Console.WriteLine($"검색 시각: {report.ScannedAt:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine($"재부팅 필요: {(report.SystemStatus.RebootRequired ? "예" : "아니오")}");
        Console.WriteLine($"WU 사용 중: {(report.SystemStatus.IsBusy ? "예" : "아니오")}");
        Console.WriteLine();

        if (options.ContainsKey("json"))
        {
            Console.WriteLine(UpdateAgentJson.Serialize(report));
            return 0;
        }

        PrintUpdates("설치 대상", report.Selected);
        Console.WriteLine();
        PrintUpdates("제외", report.Excluded);

        if (report.Selected.Count == 0)
        {
            Console.WriteLine("설치할 업데이트가 없습니다.");
        }

        return 0;
    }

    private static async Task<int> RunPreflightAsync(Dictionary<string, string?> options, CancellationToken ct)
    {
        using var wua = new WuaClient();
        var runner = CreateRunner(wua, options, NullJobProgressSink.Instance);
        await runner.InitializeAsync(ct);

        var policy = BuildPolicy(options);
        var confirmed = options.ContainsKey("confirm-maestro");
        var result = await runner.RunPreflightAsync(policy, confirmed, ct);

        Console.WriteLine($"OS: {runner.Os?.SupportSummary}");
        foreach (var check in result.Checks)
        {
            var mark = check.Status switch
            {
                PreflightStatus.Pass => "통과",
                PreflightStatus.Warn => "경고",
                _ => "차단",
            };
            Console.WriteLine($"[{mark}] {check.Name}: {check.Message}");
        }

        Console.WriteLine();
        Console.WriteLine(result.CanStart ? "작업을 시작할 수 있습니다." : "작업을 시작할 수 없습니다.");
        return result.CanStart ? 0 : 2;
    }

    private static async Task<int> RunJobAsync(
        Dictionary<string, string?> options,
        CancellationToken ct,
        bool resumeOnly = false)
    {
        using var wua = new WuaClient();
        var runner = CreateRunner(wua, options, new DelegateJobProgressSink(PrintProgress));
        await runner.InitializeAsync(ct);

        if (resumeOnly)
        {
            var resumed = await runner.ResumeIfNeededAsync(ct);
            if (resumed is null)
            {
                Console.WriteLine("재개할 작업이 없습니다.");
                return 0;
            }

            Console.WriteLine($"작업 재개: {resumed.JobId}");
        }
        else
        {
            var policy = BuildPolicy(options);
            var start = await runner.StartAsync(
                policy,
                options.ContainsKey("confirm-maestro"),
                "cli",
                cancellationToken: ct);

            PrintPreflight(start.Preflight, onlyProblems: true);

            if (!start.Started)
            {
                Console.Error.WriteLine(start.Message);
                return 2;
            }

            Console.WriteLine($"작업 시작: {start.Job?.JobId}");
        }

        // 재부팅이 예약되면 프로세스는 종료된다.
        var lastState = string.Empty;
        while (true)
        {
            if (ct.IsCancellationRequested)
            {
                await runner.CancelAsync(CancellationToken.None);
                break;
            }

            var current = runner.Current;
            if (current is null)
            {
                await Task.Delay(1000, ct);
                continue;
            }

            if (current.Summary.State == UpdateJobState.Rebooting)
            {
                Console.WriteLine();
                Console.WriteLine("재부팅이 예약되었습니다. 부팅 후 서비스 또는 'resume' 명령으로 자동 재개됩니다.");
                return 0;
            }

            if (current.Summary.State == UpdateJobState.Paused)
            {
                Console.WriteLine();
                Console.WriteLine($"일시중지: {current.Summary.Message}");
                if (current.Summary.PauseReason == PauseReason.UserLoggedOn)
                {
                    Console.WriteLine("사용자 로그오프 후 자동 재개됩니다. 계속 대기하려면 Ctrl+C를 누르세요.");
                }
            }

            if (current.Summary.IsTerminal)
            {
                Console.WriteLine();
                Console.WriteLine($"최종 상태: {current.Summary.StateLabel}");
                Console.WriteLine(current.Summary.Message);
                return current.Summary.State == UpdateJobState.Completed ? 0 : 1;
            }

            await Task.Delay(1500, ct);
        }

        return 0;
    }

    private static async Task<int> ShowStatusAsync(CancellationToken ct)
    {
        var store = new SqliteJobStore();
        await store.InitializeAsync(ct);
        var detail = await store.GetActiveJobAsync(AgentIdentity.GetAgentId(), ct);

        if (detail is null)
        {
            Console.WriteLine("진행 중인 작업이 없습니다. 최근 작업:");
            var recent = await store.GetRecentJobsAsync(AgentIdentity.GetAgentId(), 10, ct);
            if (recent.Count == 0)
            {
                Console.WriteLine("  (기록 없음)");
                return 0;
            }

            foreach (var job in recent)
            {
                Console.WriteLine(
                    $"  {job.JobId}  {job.StateLabel,-8} {job.ProgressPercent,3}%  {job.UpdatedAt:MM-dd HH:mm}  {job.Message}");
            }

            return 0;
        }

        PrintJobDetail(detail);
        return 0;
    }

    private static async Task<int> ShowMaestroAsync(CancellationToken ct)
    {
        var provider = new ManualMaestroProvider();
        var detection = await provider.DetectAsync(ct);

        var detectionText = detection.DetectionFailed
            ? "확인 불가(감지 오류)"
            : detection.Installed ? "설치됨" : "감지되지 않음";
        Console.WriteLine($"Maestro 감지: {detectionText}");
        Console.WriteLine($"Windows Update 기능: {detection.WuFeatureStatus}");
        foreach (var evidence in detection.Evidence)
        {
            Console.WriteLine($"  - {evidence}");
        }

        var active = new SqliteJobStore();
        await active.InitializeAsync(ct);
        var job = await active.GetActiveJobAsync(AgentIdentity.GetAgentId(), ct);
        if (job is not null && detection.Installed)
        {
            var checklist = await provider.GetChecklistAsync(job.Summary.JobId, ct);
            Console.WriteLine();
            Console.WriteLine("작업 전 확인:");
            foreach (var message in checklist.BeforeStartMessages) Console.WriteLine($"  - {message}");
            Console.WriteLine("작업 후 확인:");
            foreach (var message in checklist.AfterCompleteMessages) Console.WriteLine($"  - {message}");
        }

        return 0;
    }

    private static async Task<int> PauseAsync(CancellationToken ct)
    {
        using var wua = new WuaClient();
        var runner = CreateRunner(wua, [], NullJobProgressSink.Instance);
        await runner.InitializeAsync(ct);
        var summary = await runner.PauseAsync(PauseReason.Manual, ct);
        Console.WriteLine(summary is null ? "일시중지할 작업이 없습니다." : "일시중지를 요청했습니다.");
        return 0;
    }

    private static async Task<int> CancelAsync(CancellationToken ct)
    {
        using var wua = new WuaClient();
        var runner = CreateRunner(wua, [], NullJobProgressSink.Instance);
        await runner.InitializeAsync(ct);
        var summary = await runner.CancelAsync(ct);
        Console.WriteLine(summary is null ? "취소할 작업이 없습니다." : "작업을 취소했습니다.");
        return 0;
    }

    private static UpdateJobRunner CreateRunner(
        IWuaClient wua,
        Dictionary<string, string?> options,
        IJobProgressSink sink)
    {
        var store = new SqliteJobStore();
        var dryRun = options.ContainsKey("dry-run");
        var preflight = new WindowsPreflightChecker(wua, dryRun);
        var maestro = new ManualMaestroProvider();
        var reboot = new WindowsRebootManager();
        var sessions = new UserSessionProbe();

        var runnerOptions = new UpdateJobRunnerOptions
        {
            DryRun = dryRun,
        };

        return new UpdateJobRunner(
            wua,
            store,
            preflight,
            maestro,
            reboot,
            sessions,
            sink,
            NullLogger<UpdateJobRunner>.Instance,
            runnerOptions);
    }

    /// <summary>명령줄 옵션을 실행 정책으로 변환한다. 테스트에서도 호출할 수 있도록 internal로 둔다.</summary>
    internal static UpdatePolicy BuildPolicy(Dictionary<string, string?> options)
    {
        var pauseOnUser = options.ContainsKey("pause-on-user");
        var noUserPause = options.ContainsKey("no-user-pause");
        if (pauseOnUser && noUserPause)
        {
            // 모순된 옵션은 추측하지 않고 거부한다(페일 클로즈).
            throw new InvalidOperationException(
                "--pause-on-user와 --no-user-pause는 동시에 지정할 수 없습니다.");
        }

        var policy = UpdatePolicy.Default;

        if (options.TryGetValue("max-reboots", out var maxReboots)
            && int.TryParse(maxReboots, out var max))
        {
            policy = policy with { MaxRebootCount = Math.Max(0, max) };
        }

        if (options.ContainsKey("optional"))
            policy = policy with { IncludeOptional = true };

        if (options.ContainsKey("drivers"))
            policy = policy with { IncludeDrivers = true };

        if (noUserPause)
        {
            policy = policy with { PauseOnUserLogon = false };
        }
        else if (pauseOnUser)
        {
            policy = policy with { PauseOnUserLogon = true };
        }

        if (options.ContainsKey("no-overlay"))
            policy = policy with { OverlayEnabled = false };

        if (options.ContainsKey("reboot-never"))
            policy = policy with { RebootPolicy = RebootPolicy.Never };

        if (options.ContainsKey("shutdown"))
            policy = policy with { ShutdownAfterComplete = true };

        return policy;
    }

    private static Task PrintProgress(JobDetail detail, CancellationToken _)
    {
        var summary = detail.Summary;
        var key = $"{summary.State}:{summary.ProgressPercent}:{summary.CurrentKb}:{summary.Phase}";
        if (_lastKeys.Contains(key)) return Task.CompletedTask;
        _lastKeys.Add(key);
        if (_lastKeys.Count > 50) _lastKeys.RemoveAt(0);

        var kb = string.IsNullOrWhiteSpace(summary.CurrentKb) ? "" : $" [{summary.CurrentKb}]";
        Console.WriteLine(
            $"{summary.StateLabel,-10} {summary.ProgressPercent,3}%  " +
            $"설치 {summary.InstalledItems}/{summary.TotalItems}  재부팅 {summary.RebootCount}/{summary.MaxRebootCount}  {summary.Phase}{kb}");

        return Task.CompletedTask;
    }

    private static void PrintPreflight(PreflightResult result, bool onlyProblems)
    {
        foreach (var check in result.Checks)
        {
            if (onlyProblems && check.Status == PreflightStatus.Pass) continue;
            var mark = check.Status switch
            {
                PreflightStatus.Pass => "통과",
                PreflightStatus.Warn => "경고",
                _ => "차단",
            };
            Console.WriteLine($"[{mark}] {check.Name}: {check.Message}");
        }
    }

    private static void PrintUpdates(string title, IReadOnlyList<UpdateItemInfo> updates)
    {
        Console.WriteLine($"== {title}: {updates.Count}건 ==");
        foreach (var update in updates)
        {
            var size = string.IsNullOrWhiteSpace(update.SizeLabel) ? "" : $" {update.SizeLabel,10}";
            Console.WriteLine($"  {update.KbLabel,-16} {update.Category,-12}{size}  {update.Title}");
        }
    }

    private static void PrintJobDetail(JobDetail detail)
    {
        var summary = detail.Summary;
        Console.WriteLine($"작업: {summary.JobId}");
        Console.WriteLine($"상태: {summary.StateLabel} ({summary.ProgressPercent}%)");
        Console.WriteLine($"단계: {summary.Phase}");
        Console.WriteLine($"PC: {summary.AgentName}");
        Console.WriteLine($"시작: {summary.StartedAt:yyyy-MM-dd HH:mm:ss}  갱신: {summary.UpdatedAt:HH:mm:ss}");
        Console.WriteLine($"항목: 전체 {summary.TotalItems}, 설치 {summary.InstalledItems}, 실패/제외 {summary.FailedItems}");
        Console.WriteLine($"재부팅: {summary.RebootCount}/{summary.MaxRebootCount}  복원 검증: {(summary.RestoreVerified ? "통과" : "미검증")}");
        Console.WriteLine($"메시지: {summary.Message}");
        if (!string.IsNullOrWhiteSpace(summary.LastError)) Console.WriteLine($"오류: {summary.LastError}");

        Console.WriteLine();
        foreach (var item in detail.Items)
        {
            var error = string.IsNullOrWhiteSpace(item.Error) ? "" : $" — {item.Error}";
            Console.WriteLine($"  [{item.State,-10}] {item.KbArticleIds,-16} {item.Title}{error}");
        }
    }

    private static Dictionary<string, string?> ParseOptions(string[] args)
    {
        var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith('-')) continue;

            var name = arg.TrimStart('-');
            string? value = null;
            if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
            {
                value = args[i + 1];
                i++;
            }

            options[name] = value;
        }

        return options;
    }

    private static int ShowHelp()
    {
        Console.WriteLine("""
            UpdateAgent CLI

            사용법:
              UpdateAgent.Cli scan       [--drivers] [--optional] [--json]
              UpdateAgent.Cli preflight  [--confirm-maestro]
              UpdateAgent.Cli run        [--confirm-maestro] [--max-reboots 5] [--dry-run]
                                         [--optional] [--drivers] [--pause-on-user]
                                         [--no-user-pause] [--no-overlay] [--reboot-never] [--shutdown]
              UpdateAgent.Cli resume
              UpdateAgent.Cli status
              UpdateAgent.Cli maestro
              UpdateAgent.Cli pause
              UpdateAgent.Cli cancel

            주의:
              - Windows Update 설치는 관리자 권한이 필요합니다.
              - Maestro 사용 시 작업 전 '보호 중지/수동 복구' 전환 후 --confirm-maestro를 지정하세요.
              - run은 재부팅이 예약되면 종료됩니다. 부팅 후 서비스 또는 resume으로 이어집니다.
              - --pause-on-user는 사용자 로그온 시 작업을 일시중지하고, --no-user-pause는 기본값(일시중지 안 함)을 명시합니다.
              - 두 플래그는 함께 지정할 수 없습니다.
            """);
        return 0;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"알 수 없는 명령: {command}");
        return ShowHelp();
    }
}
