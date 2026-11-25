using System.Management;
using System.Runtime.InteropServices;

namespace UpdateAgent.Core.Windows;

public sealed record HardwareSnapshot
{
    public bool HasBattery { get; init; }
    public bool IsLaptop { get; init; }

    /// <summary>노트북 여부를 조회하지 못했는지. false를 "데스크톱"으로 단정하지 않는다.</summary>
    public bool LaptopDetectionFailed { get; init; }

    public bool OnAcPower { get; init; } = true;
    public int FreeDiskMb { get; init; }
}

/// <summary>배터리/노트북/디스크 여유 공간 등 하드웨어 요약.</summary>
public static class HardwareInfo
{
    public static HardwareSnapshot GetSnapshot()
    {
        var power = GetPowerStatus();
        var (isLaptop, laptopDetectionFailed) = DetectLaptop();
        return new HardwareSnapshot
        {
            HasBattery = power.HasBattery,
            OnAcPower = power.OnAcPower,
            IsLaptop = isLaptop,
            LaptopDetectionFailed = laptopDetectionFailed,
            FreeDiskMb = GetFreeDiskMb(),
        };
    }

    public static int GetFreeDiskMb()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            var drive = new DriveInfo(root);
            return (int)(drive.AvailableFreeSpace / 1024 / 1024);
        }
        catch
        {
            // 조회 실패를 0MB(디스크 가득 참)로 보고하지 않는다. UI는 음수를 "확인 불가"로 표시한다.
            return -1;
        }
    }

    public static (bool HasBattery, bool OnAcPower) GetPowerStatus()
    {
        try
        {
            if (!GetSystemPowerStatus(out var status)) return (false, true);

            const byte noSystemBattery = 128;
            var hasBattery = (status.BatteryFlag & noSystemBattery) == 0;
            return (hasBattery, status.ACLineStatus == 1);
        }
        catch
        {
            return (false, true);
        }
    }

    private static (bool IsLaptop, bool Failed) DetectLaptop()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ChassisTypes FROM Win32_SystemEnclosure");
            foreach (var item in searcher.Get())
            {
                using (item)
                {
                    if (item["ChassisTypes"] is not ushort[] types) continue;
                    foreach (var type in types)
                    {
                        // 8=Portable, 9=Laptop, 10=Notebook, 11=Hand Held, 12=Docking, 14=Sub Notebook,
                        // 18=Expansion Chassis, 21=Desktop
                        if (type is 8 or 9 or 10 or 11 or 12 or 14 or 18) return (true, false);
                    }
                }
            }
        }
        catch
        {
            return (false, true);
        }

        return (false, false);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
