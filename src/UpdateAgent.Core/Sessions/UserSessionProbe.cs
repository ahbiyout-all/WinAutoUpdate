using System.Runtime.InteropServices;

namespace UpdateAgent.Core.Sessions;

public sealed record InteractiveSession
{
    public int SessionId { get; init; }
    public string UserName { get; init; } = "";
    public string DomainName { get; init; } = "";
    public bool IsActive { get; init; }

    public string DisplayName => string.IsNullOrWhiteSpace(DomainName)
        ? UserName
        : $"{DomainName}\\{UserName}";
}

/// <summary>
/// 사용자 세션 조회 결과. ProbeFailed이면 세션이 없는 것이 아니라 확인 불가를 뜻한다.
/// </summary>
public sealed record UserSessionProbeResult
{
    public IReadOnlyList<InteractiveSession> Sessions { get; init; } = [];
    public bool ProbeFailed { get; init; }
    public string? Error { get; init; }

    public static UserSessionProbeResult Success(IReadOnlyList<InteractiveSession> sessions) => new()
    {
        Sessions = sessions,
    };

    public static UserSessionProbeResult Failed(string error) => new()
    {
        ProbeFailed = true,
        Error = error,
    };
}

public interface IUserSessionProbe
{
    IReadOnlyList<InteractiveSession> GetInteractiveSessions();
    bool HasInteractiveUser();

    /// <summary>기존 구현/목은 실패 여부를 모르므로 기본 구현으로 호환을 유지한다.</summary>
    UserSessionProbeResult Probe() => UserSessionProbeResult.Success(GetInteractiveSessions());
}

/// <summary>WTS API로 현재 로그온한 대화형 사용자를 확인한다.</summary>
public sealed class UserSessionProbe : IUserSessionProbe
{
    private const int WtsCurrentServerHandle = 0;
    private const int WtsUserName = 5;
    private const int WtsDomainName = 7;
    private const int WtsConnectState = 8;
    private const int WtsActive = 0;
    private const int WtsConnected = 1;

    public UserSessionProbeResult Probe()
    {
        var results = new List<InteractiveSession>();
        IntPtr buffer = IntPtr.Zero;
        var count = 0;

        try
        {
            if (!WTSEnumerateSessions((IntPtr)WtsCurrentServerHandle, 0, 1, out buffer, out count))
            {
                return UserSessionProbeResult.Failed(
                    $"WTS 세션을 확인할 수 없습니다 (오류 코드 {Marshal.GetLastWin32Error()}).");
            }

            var size = Marshal.SizeOf<WtsSessionInfo>();
            for (var i = 0; i < count; i++)
            {
                var infoPtr = IntPtr.Add(buffer, i * size);
                var info = Marshal.PtrToStructure<WtsSessionInfo>(infoPtr);

                var state = QueryInt(info.SessionID, WtsConnectState);
                if (state < 0)
                {
                    return UserSessionProbeResult.Failed(
                        $"WTS 세션 {info.SessionID}의 연결 상태를 확인할 수 없습니다.");
                }

                if (state is not (WtsActive or WtsConnected)) continue;

                var user = QueryString(info.SessionID, WtsUserName);
                if (string.IsNullOrWhiteSpace(user)) continue;

                results.Add(new InteractiveSession
                {
                    SessionId = info.SessionID,
                    UserName = user,
                    DomainName = QueryString(info.SessionID, WtsDomainName),
                    IsActive = state == WtsActive,
                });
            }

            return UserSessionProbeResult.Success(results);
        }
        catch (Exception ex)
        {
            // 조회 실패를 "사용자 없음"으로 보고하면 PauseOnUserLogon이 무력화된다.
            return UserSessionProbeResult.Failed($"WTS 세션을 확인할 수 없습니다: {ex.Message}");
        }
        finally
        {
            if (buffer != IntPtr.Zero) WTSFreeMemory(buffer);
        }
    }

    public IReadOnlyList<InteractiveSession> GetInteractiveSessions() => Probe().Sessions;

    public bool HasInteractiveUser()
    {
        var probe = Probe();
        return probe.ProbeFailed || probe.Sessions.Count > 0;
    }

    private static string QueryString(int sessionId, int infoClass)
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, infoClass, out buffer, out var bytes))
                return "";
            return bytes <= 1 ? "" : Marshal.PtrToStringUni(buffer) ?? "";
        }
        finally
        {
            if (buffer != IntPtr.Zero) WTSFreeMemory(buffer);
        }
    }

    private static int QueryInt(int sessionId, int infoClass)
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, infoClass, out buffer, out var bytes)
                || bytes < sizeof(int))
            {
                return -1;
            }

            return Marshal.ReadInt32(buffer);
        }
        finally
        {
            if (buffer != IntPtr.Zero) WTSFreeMemory(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WtsSessionInfo
    {
        public int SessionID;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string pWinStationName;

        public int State;
    }

    [DllImport("wtsapi32.dll", EntryPoint = "WTSEnumerateSessionsW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnumerateSessions(
        IntPtr hServer, int reserved, int version, out IntPtr ppSessionInfo, out int pCount);

    [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(
        IntPtr hServer, int sessionId, int wtsInfoClass, out IntPtr ppBuffer, out int pBytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
}
