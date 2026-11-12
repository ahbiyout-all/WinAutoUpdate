using System.Runtime.InteropServices;
using Microsoft.Win32;
using UpdateAgent.Shared.Models;

namespace UpdateAgent.Core.Windows;

/// <summary>레지스트리와 RtlGetVersion으로 정확한 Windows 버전/에디션을 감지한다.</summary>
public static class WindowsOsInfoProvider
{
    private const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    private const int Windows11Build = 22000;
    private const int Windows10Build = 10240;

    public static OsInfo GetCurrent()
    {
        var productName = ReadString("ProductName") ?? "Windows";
        var editionId = ReadString("EditionID") ?? "";
        var displayVersion = ReadString("DisplayVersion") ?? ReadString("ReleaseId") ?? "";
        var buildLab = ReadString("BuildLabEx") ?? "";
        var installationType = ReadString("InstallationType") ?? "";
        var ubr = ReadInt("UBR") ?? 0;

        var osVersion = GetVersionEx();
        var build = osVersion?.dwBuildNumber ?? ParseBuild(ReadString("CurrentBuildNumber")) ?? 0;
        var revision = ubr;

        var isServer = IsServerEdition(productName, installationType);
        var isServerCore = installationType.Equals("Server Core", StringComparison.OrdinalIgnoreCase);
        var isLtsc = IsLtscEdition(editionId, productName);

        return new OsInfo
        {
            ProductName = NormalizeProductName(productName.Trim(), build),
            EditionId = editionId,
            DisplayVersion = string.IsNullOrWhiteSpace(displayVersion)
                ? build >= Windows11Build ? "Windows 11" : "Windows 10"
                : displayVersion,
            BuildLab = buildLab,
            Build = build,
            Revision = revision,
            Architecture = RuntimeInformation.OSArchitecture.ToString(),
            IsServer = isServer,
            IsLtsc = isLtsc,
            IsWindows10 = !isServer && build is >= Windows10Build and < Windows11Build,
            IsServerCore = isServerCore,
        };
    }

    /// <summary>테스트에서 레지스트리 없이 OsInfo를 만들기 위한 팩토리.</summary>
    public static OsInfo Create(
        string productName,
        string editionId,
        string displayVersion,
        int build,
        int revision = 0,
        string installationType = "Client")
    {
        var isServer = IsServerEdition(productName, installationType);
        return new OsInfo
        {
            ProductName = NormalizeProductName(productName, build),
            EditionId = editionId,
            DisplayVersion = displayVersion,
            Build = build,
            Revision = revision,
            Architecture = "X64",
            IsServer = isServer,
            IsLtsc = IsLtscEdition(editionId, productName),
            IsWindows10 = !isServer && build is >= Windows10Build and < Windows11Build,
            IsServerCore = installationType.Equals("Server Core", StringComparison.OrdinalIgnoreCase),
        };
    }

    private static bool IsServerEdition(string productName, string installationType) =>
        installationType.StartsWith("Server", StringComparison.OrdinalIgnoreCase)
        || productName.Contains("Server", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Windows 11에서도 레지스트리 ProductName이 "Windows 10 ..."으로 남아 있는 경우가 많다.
    /// 빌드 번호가 22000 이상이면 Windows 11로 정규화한다.
    /// </summary>
    private static string NormalizeProductName(string productName, int build)
    {
        if (build >= Windows11Build
            && productName.StartsWith("Windows 10", StringComparison.OrdinalIgnoreCase))
        {
            return "Windows 11" + productName["Windows 10".Length..];
        }

        return productName;
    }

    private static bool IsLtscEdition(string editionId, string productName)
    {
        var text = $"{editionId} {productName}";
        return text.Contains("LTSC", StringComparison.OrdinalIgnoreCase)
            || text.Contains("EnterpriseS", StringComparison.OrdinalIgnoreCase)
            || text.Contains("IoTEnterpriseS", StringComparison.OrdinalIgnoreCase);
    }

    private static int? ParseBuild(string? value) =>
        int.TryParse(value, out var build) ? build : null;

    private static string? ReadString(string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(CurrentVersionKey);
            return key?.GetValue(name) as string;
        }
        catch
        {
            return null;
        }
    }

    private static int? ReadInt(string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(CurrentVersionKey);
            var value = key?.GetValue(name);
            return value is int i ? i : int.TryParse(value?.ToString(), out var parsed) ? parsed : null;
        }
        catch
        {
            return null;
        }
    }

    private static RtlOsVersionInfo? GetVersionEx()
    {
        try
        {
            var info = new RtlOsVersionInfo
            {
                dwOSVersionInfoSize = Marshal.SizeOf<RtlOsVersionInfo>(),
            };
            return RtlGetVersion(ref info) == 0 ? info : null;
        }
        catch
        {
            return null;
        }
    }

    [DllImport("ntdll.dll", SetLastError = true)]
    private static extern int RtlGetVersion(ref RtlOsVersionInfo versionInfo);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RtlOsVersionInfo
    {
        public int dwOSVersionInfoSize;
        public int dwMajorVersion;
        public int dwMinorVersion;
        public int dwBuildNumber;
        public int dwPlatformId;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szCSDVersion;

        public ushort wServicePackMajor;
        public ushort wServicePackMinor;
        public ushort wSuiteMask;
        public byte wProductType;
        public byte wReserved;
    }
}
