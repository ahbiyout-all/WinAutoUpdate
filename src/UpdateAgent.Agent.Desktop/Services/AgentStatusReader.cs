using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using UpdateAgent.Core.Orchestration;
using UpdateAgent.Core.Storage;
using UpdateAgent.Shared.Models;

namespace UpdateAgent.Agent.Desktop.Services;

/// <summary>학생용 데스크톱 앱이 보여주는 상태 스냅샷.</summary>
public sealed record AgentStatusSnapshot(
    string ServiceStatus,
    string LabManagerUrl,
    string AgentId,
    string MachineName,
    JobSummary? ActiveJob,
    IReadOnlyList<JobSummary> RecentJobs,
    string? Error);

/// <summary>
/// 로컬 작업 저장소와 서비스 상태를 읽는다. 관리자 권한이 필요 없는 읽기 전용 조회만 수행한다.
/// </summary>
public sealed class AgentStatusReader
{
    // sc.exe 출력의 STATE 줄만 읽는다. WIN32_EXIT_CODE 같은 다른 숫자 필드의 값을
    // 상태 코드로 오인하지 않도록 상태 문구(RUNNING/STOPPED 등)가 붙은 줄만 매칭한다.
    private static readonly Regex ServiceStateLinePattern = new(
        @"^[ \t]*[^:\r\n]*:[ \t]*(?<state>[0-9]+)[ \t]+(?:RUNNING|STOPPED|PAUSED|PAUSE_PENDING|CONTINUE_PENDING|START_PENDING|STOP_PENDING)\b",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public async Task<AgentStatusSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        JobSummary? active = null;
        IReadOnlyList<JobSummary> recent = [];
        string? error = null;
        var agentId = "unknown";

        try
        {
            var store = new SqliteJobStore();
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);

            agentId = AgentIdentity.GetAgentId();
            var detail = await store.GetActiveJobAsync(agentId, cancellationToken).ConfigureAwait(false);
            active = detail?.Summary;
            recent = await store.GetRecentJobsAsync(agentId, 10, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        return new AgentStatusSnapshot(
            ReadServiceStatus(),
            ReadLabManagerUrl(),
            agentId,
            Environment.MachineName,
            active,
            recent,
            error);
    }

    private static string ReadServiceStatus()
    {
        try
        {
            var sc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sc.exe");
            var startInfo = new ProcessStartInfo(sc, "query " + UpdateAgent.Shared.UpdateAgentConstants.ServiceName)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var process = Process.Start(startInfo);
            if (process is null) return "확인 불가";

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            return ResolveServiceStatus(output, process.ExitCode);
        }
        catch
        {
            return "확인 불가";
        }
    }

    /// <summary>
    /// sc.exe 질의 결과에서 서비스 상태를 해석한다. STATE 줄의 숫자만 사용하므로
    /// WIN32_EXIT_CODE가 1/4여도 실행/중지 상태가 뒤집히지 않는다.
    /// </summary>
    internal static string ResolveServiceStatus(string output, int exitCode)
    {
        if (exitCode != 0) return "설치되지 않음";

        var match = ServiceStateLinePattern.Match(output);
        if (!match.Success) return "상태 확인 필요";

        return match.Groups["state"].Value switch
        {
            "4" => "실행 중",
            "1" => "중지됨",
            _ => "상태 확인 필요",
        };
    }

    private static string ReadLabManagerUrl() => ReadLabManagerUrl(DefaultCandidatePaths());

    private static IEnumerable<string> DefaultCandidatePaths() =>
    [
        UpdateAgent.Shared.UpdateAgentConstants.ConfigFilePath,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "UpdateAgent", "Service", "appsettings.json"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "UpdateAgent", "Service", "appsettings.json"),
    ];

    /// <summary>
    /// 설정 파일에서 실제 LabManager URL을 읽는다. 후보를 하나도 읽지 못하면 기본값을
    /// 추측해 보여주지 않고 "확인 불가"로 표시한다.
    /// </summary>
    internal static string ReadLabManagerUrl(IEnumerable<string> candidatePaths)
    {
        foreach (var path in candidatePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!File.Exists(path)) continue;
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (document.RootElement.TryGetProperty("Agent", out var agent) &&
                    agent.TryGetProperty("LabManagerUrl", out var url) &&
                    url.ValueKind == JsonValueKind.String)
                {
                    var value = url.GetString();
                    if (!string.IsNullOrWhiteSpace(value)) return value!;
                }
            }
            catch
            {
                // 다음 후보 경로를 확인한다.
            }
        }

        return "확인 불가";
    }
}
