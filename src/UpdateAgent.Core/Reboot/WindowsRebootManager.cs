using System.Diagnostics;

namespace UpdateAgent.Core.Reboot;

public interface IRebootManager
{
    Task ScheduleRebootAsync(int delaySeconds, string reason, CancellationToken cancellationToken = default);
    Task CancelRebootAsync(CancellationToken cancellationToken = default);
    Task ScheduleShutdownAsync(int delaySeconds, string reason, CancellationToken cancellationToken = default);
    bool IsRebootPending();
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

    public bool IsRebootPending()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
            if (key is not null) return true;

            using var wuKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            if (wuKey is not null) return true;

            using var sessionKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Session Manager");
            if (sessionKey?.GetValue("PendingFileRenameOperations") is string[] { Length: > 0 })
                return true;
        }
        catch
        {
            // 확인 실패는 "대기 아님"으로 처리한다.
        }

        return false;
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
