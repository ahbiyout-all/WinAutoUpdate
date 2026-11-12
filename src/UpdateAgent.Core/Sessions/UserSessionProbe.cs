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

public interface IUserSessionProbe
{
    IReadOnlyList<InteractiveSession> GetInteractiveSessions();
    bool HasInteractiveUser();
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

    public IReadOnlyList<InteractiveSession> GetInteractiveSessions()
    {
        var results = new List<InteractiveSession>();
        IntPtr buffer = IntPtr.Zero;
        var count = 0;

        try
        {
            if (!WTSEnumerateSessions((IntPtr)WtsCurrentServerHandle, 0, 1, out buffer, out count))
                return results;

            var size = Marshal.SizeOf<WtsSessionInfo>();
            for (var i = 0; i < count; i++)
            {
                var infoPtr = IntPtr.Add(buffer, i * size);
                var info = Marshal.PtrToStructure<WtsSessionInfo>(infoPtr);

                var state = QueryInt(info.SessionID, WtsConnectState);
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
        }
        catch
        {
            // WTS 조회 실패는 "사용자 없음"으로 처리한다. 서비스 환경에서 발생할 수 있다.
        }
        finally
        {
            if (buffer != IntPtr.Zero) WTSFreeMemory(buffer);
        }

        return results;
    }

    public bool HasInteractiveUser() => GetInteractiveSessions().Count > 0;

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

    [StructLayout(LayoutKind.Sequential)]
    private struct WtsSessionInfo
    {
        public int SessionID;

        [MarshalAs(UnmanagedType.LPStr)]
        public string pWinStationName;

        public int State;
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnumerateSessions(
        IntPtr hServer, int reserved, int version, out IntPtr ppSessionInfo, out int pCount);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(
        IntPtr hServer, int sessionId, int wtsInfoClass, out IntPtr ppBuffer, out int pBytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
}
