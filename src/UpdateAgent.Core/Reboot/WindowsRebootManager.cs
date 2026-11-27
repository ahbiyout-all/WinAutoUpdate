using System.Diagnostics;

namespace UpdateAgent.Core.Reboot;

/// <summary>
/// 재부팅 대기 여부 조회 결과. Pending이 null이면 조회 실패이므로 false로 단정할 수 없다.
/// </summary>
public sealed record RebootPendingProbeResult
{
    public bool? Pending { get; init; }
    public bool ProbeFailed => Pending is null;
    public string? Error { get; init; }

    public static RebootPendingProbeResult Ok(bool pending) => new() { Pending = pending };

    public static RebootPendingProbeResult Failed(string error) => new()
    {
        Pending = null,
        Error = error,
    };
}

public interface IRebootManager
{
    Task ScheduleRebootAsync(int delaySeconds, string reason, CancellationToken cancellationToken = default);
    Task CancelRebootAsync(CancellationToken cancellationToken = default);
    Task ScheduleShutdownAsync(int delaySeconds, string reason, CancellationToken cancellationToken = default);
    bool IsRebootPending();

    /// <summary>기존 구현/목은 tri-state를 모르므로 기본 구현으로 호환을 유지한다.</summary>
    RebootPendingProbeResult ProbeRebootPending() => RebootPendingProbeResult.Ok(IsRebootPending());
}

/// <summary>shutdown.exe를 사용해 계획된 재부팅/종료를 수행한다.</summary>
public sealed class WindowsRebootManager : IRebootManager
{
    public Task ScheduleRebootAsync(int delaySeconds, string reason, CancellationToken cancellationToken = default)
    {
        delaySeconds = Math.Max(10, delaySeconds);
        var safeReason = SanitizeReason(reason);
        return RunAsync(
            "shutdown.exe",
            $"/r /t {delaySeconds} /f /d p:2:4 /c \"{safeReason}\"",
            cancellationToken);
    }

    public Task CancelRebootAsync(CancellationToken cancellationToken = default) =>
        RunAsync("shutdown.exe", "/a", cancellationToken);

    public Task ScheduleShutdownAsync(int delaySeconds, string reason, CancellationToken cancellationToken = default)
    {
        delaySeconds = Math.Max(10, delaySeconds);
        var safeReason = SanitizeReason(reason);
        return RunAsync(
            "shutdown.exe",
            $"/s /t {delaySeconds} /f /d p:2:4 /c \"{safeReason}\"",
            cancellationToken);
    }

    public bool IsRebootPending() => ProbeRebootPending().Pending ?? false;

    public RebootPendingProbeResult ProbeRebootPending()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
            if (key is not null) return RebootPendingProbeResult.Ok(true);

            using var wuKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            if (wuKey is not null) return RebootPendingProbeResult.Ok(true);

            using var sessionKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Session Manager");
            if (sessionKey?.GetValue("PendingFileRenameOperations") is string[] { Length: > 0 })
                return RebootPendingProbeResult.Ok(true);

            return RebootPendingProbeResult.Ok(false);
        }
        catch (Exception ex)
        {
            // 확인 실패는 "대기 아님"이 아니라 "확인 불가"로 보고한다.
            return RebootPendingProbeResult.Failed(
                $"재부팅 대기 상태를 확인할 수 없습니다: {ex.Message}");
        }
    }

    private static async Task RunAsync(string fileName, string arguments, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        using var process = Process.Start(psi)
                            ?? throw new InvalidOperationException($"{fileName} 실행에 실패했습니다.");

        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch
            {
                // ignore
            }
        });

        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{fileName} {arguments} 실행 실패 (exit code {process.ExitCode})");
        }
    }

    private static string SanitizeReason(string reason)
    {
        var safe = reason.Replace('"', '\'').Replace('\r', ' ').Replace('\n', ' ').Trim();
        return safe.Length <= 200 ? safe : safe[..200];
    }
}
