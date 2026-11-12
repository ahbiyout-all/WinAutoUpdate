using System.Runtime.InteropServices;

namespace UpdateAgent.Core.Windows;

/// <summary>서비스 상태를 sc.exe 없이 조회한다.</summary>
public static class ServiceProbe
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStatusRunning = 0x00000004;

    public static bool IsRunning(string serviceName)
    {
        var scm = OpenSCManager(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero) return false;

        try
        {
            var service = OpenService(scm, serviceName, ServiceQueryStatus);
            if (service == IntPtr.Zero) return false;

            try
            {
                var status = new ServiceStatus();
                if (!QueryServiceStatus(service, ref status)) return false;
                return status.dwCurrentState == ServiceStatusRunning;
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    public static bool IsTrustedInstallerRunning() =>
        IsRunning("TrustedInstaller") || IsRunning("TiWorker");

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenService(IntPtr scManager, string serviceName, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatus(IntPtr service, ref ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
