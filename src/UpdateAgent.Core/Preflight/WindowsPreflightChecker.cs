using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using UpdateAgent.Core.Windows;
using UpdateAgent.Core.Windows.Update;
using UpdateAgent.Shared.Models;

namespace UpdateAgent.Core.Preflight;

public interface IPreflightChecker
{
    Task<PreflightResult> CheckAsync(
        UpdatePolicy policy,
        OsInfo os,
        MaestroDetectionResult maestro,
        bool maestroConfirmed,
        CancellationToken cancellationToken = default);
}

/// <summary>작업 시작 전 환경/안전 점검.</summary>
public sealed class WindowsPreflightChecker : IPreflightChecker
{
    private readonly IWuaClient _wua;
    private readonly bool _dryRun;

    public WindowsPreflightChecker(IWuaClient wua, bool dryRun = false)
    {
        _wua = wua;
        _dryRun = dryRun;
    }

    public async Task<PreflightResult> CheckAsync(
        UpdatePolicy policy,
        OsInfo os,
        MaestroDetectionResult maestro,
        bool maestroConfirmed,
        CancellationToken cancellationToken = default)
    {
        var checks = new List<PreflightCheck>
        {
            CheckAdministrator(_dryRun),
            CheckOs(os),
            CheckDisk(policy),
            CheckPower(),
            CheckNetwork(),
            CheckWsus(),
            CheckMaestro(maestro, maestroConfirmed, policy),
            CheckBitLocker(),
            CheckSecureBootState(),
        };

        try
        {
            var status = await _wua.GetSystemStatusAsync(cancellationToken);
            checks.Add(status.InstallerBusy
                ? PreflightCheck.Block(
                    "Windows Update 사용 중",
                    "다른 Windows Update/Maestro 업데이트 설치가 진행 중입니다. 완료 후 다시 시도하세요.")
                : PreflightCheck.Pass("Windows Update 상태", "다른 설치 작업이 없습니다."));

            checks.Add(status.RebootRequired
                ? PreflightCheck.Warn(
                    "재부팅 대기",
                    "이전 업데이트 설치로 재부팅이 필요합니다. 작업이 재부팅을 포함해 진행됩니다.")
                : PreflightCheck.Pass("재부팅 상태", "대기 중인 재부팅이 없습니다."));
        }
        catch (Exception ex)
        {
            checks.Add(PreflightCheck.Warn("Windows Update 상태", $"WUA 상태를 확인할 수 없습니다: {ex.Message}"));
        }

        return new PreflightResult { Checks = checks };
    }

    private static PreflightCheck CheckAdministrator(bool dryRun)
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        var isAdmin = principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

        if (isAdmin)
            return PreflightCheck.Pass("관리자 권한", "관리자 권한으로 실행 중입니다.");

        // DRY RUN은 실제 설치를 하지 않으므로 관리자 권한 없이도 흐름을 검증할 수 있다.
        return dryRun
            ? PreflightCheck.Warn("관리자 권한", "DRY RUN: 관리자 권한 없이 흐름만 검증합니다.")
            : PreflightCheck.Block("관리자 권한", "Windows Update 설치에는 관리자 권한이 필요합니다.");
    }

    private static PreflightCheck CheckOs(OsInfo os)
    {
        if (os.Build == 0)
            return PreflightCheck.Warn("운영체제", "OS 버전을 확인할 수 없습니다.");

        if (os.IsServer && os.Build < 14393)
            return PreflightCheck.Block("운영체제", $"지원되지 않는 서버 버전입니다: {os.SupportSummary}");

        if (!os.IsServer && os.Build < 10240)
            return PreflightCheck.Block("운영체제", $"지원되지 않는 Windows 버전입니다: {os.SupportSummary}");

        if (os.IsServerCore)
            return PreflightCheck.Pass("운영체제", $"{os.SupportSummary} — 헤드리스 모드로 진행합니다.");

        if (os.IsWindows10)
            return PreflightCheck.Warn("운영체제",
                $"{os.SupportSummary}. Windows 10은 ESU 라이선스가 필요하며 기능 업데이트는 지원하지 않습니다.");

        if (os.IsLtsc)
            return PreflightCheck.Warn("운영체제", $"{os.SupportSummary}. 품질/보안 업데이트만 처리합니다.");

        return PreflightCheck.Pass("운영체제", os.SupportSummary);
    }

    private static PreflightCheck CheckDisk(UpdatePolicy policy)
    {
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            var drive = new DriveInfo(root);
            var freeMb = drive.AvailableFreeSpace / 1024 / 1024;
            return freeMb >= policy.MinimumFreeDiskMb
                ? PreflightCheck.Pass("디스크 공간", $"시스템 드라이브 여유 공간 {freeMb:N0} MB")
                : PreflightCheck.Block("디스크 공간",
                    $"여유 공간이 부족합니다. {freeMb:N0} MB / 최소 {policy.MinimumFreeDiskMb:N0} MB");
        }
        catch (Exception ex)
        {
            return PreflightCheck.Warn("디스크 공간", $"디스크 공간을 확인할 수 없습니다: {ex.Message}");
        }
    }

    private static PreflightCheck CheckPower()
    {
        try
        {
            if (!GetSystemPowerStatus(out var status)) return PreflightCheck.Warn("전원", "전원 상태를 확인할 수 없습니다.");

            const byte noSystemBattery = 128;
            if ((status.BatteryFlag & noSystemBattery) != 0)
                return PreflightCheck.Pass("전원", "AC 전원 (배터리 없음)");

            if (status.ACLineStatus == 1)
                return PreflightCheck.Pass("전원", $"AC 전원 연결됨 (배터리 {status.BatteryLifePercent}%)");

            return PreflightCheck.Block("전원", "배터리 전원입니다. 업데이트/재부팅 중 전원이 꺼질 수 있어 AC 전원 연결이 필요합니다.");
        }
        catch (Exception ex)
        {
            return PreflightCheck.Warn("전원", ex.Message);
        }
    }

    private static PreflightCheck CheckNetwork()
    {
        return System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable()
            ? PreflightCheck.Pass("네트워크", "네트워크에 연결되어 있습니다.")
            : PreflightCheck.Block("네트워크", "네트워크 연결이 없습니다. Windows Update를 받을 수 없습니다.");
    }

    private static PreflightCheck CheckWsus()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU");
            var useWuServer = key?.GetValue("UseWUServer");
            if (useWuServer is int i && i == 1)
            {
                return PreflightCheck.Warn("WSUS/정책",
                    "WSUS 또는 중앙 정책이 적용된 PC입니다. 승인된 업데이트만 설치되며 정책을 우회할 수 없습니다.");
            }

            using var wuKey = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate");
            if (wuKey?.GetValue("TargetReleaseVersion") is int target && target == 1)
            {
                return PreflightCheck.Warn("WSUS/정책",
                    "TargetReleaseVersion 정책이 설정되어 있습니다. 기능 업데이트 대상이 제한됩니다.");
            }

            return PreflightCheck.Pass("WSUS/정책", "중앙 Windows Update 정책이 감지되지 않았습니다.");
        }
        catch
        {
            return PreflightCheck.Warn("WSUS/정책", "Windows Update 정책을 확인할 수 없습니다.");
        }
    }

    private static PreflightCheck CheckMaestro(
        MaestroDetectionResult maestro,
        bool maestroConfirmed,
        UpdatePolicy policy)
    {
        if (!maestro.Installed)
            return PreflightCheck.Pass("Maestro 복원", "Maestro Network 7이 감지되지 않았습니다.");

        if (maestro.WuFeatureStatus == MaestroWuFeatureStatus.Enabled)
        {
            return PreflightCheck.Warn("Maestro Windows Update",
                "Maestro의 Windows Update 기능이 켜져 있습니다. 작업 중 수동 실행하지 마세요.");
        }

        if (!policy.RequireMaestroManualConfirmation || maestroConfirmed)
        {
            return PreflightCheck.Pass("Maestro 복원",
                "교사가 복원 보호 해제를 확인했습니다. (수동 확인)");
        }

        return PreflightCheck.Block("Maestro 복원",
            "Maestro 복원을 '매 부팅 자동 복구'에서 '수동 복구/해제'로 전환한 뒤 확인란을 체크해야 합니다.");
    }

    private static PreflightCheck CheckBitLocker()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\cimv2\security\microsoftvolumeencryption",
                "SELECT DriveLetter, ProtectionStatus, ConversionStatus FROM Win32_EncryptableVolume");
            foreach (var item in searcher.Get())
            {
                using (item)
                {
                    var drive = item["DriveLetter"]?.ToString();
                    var protection = Convert.ToInt32(item["ProtectionStatus"] ?? 0);
                    if (protection is 1 or 2)
                    {
                        return PreflightCheck.Warn("BitLocker",
                            $"{drive} 드라이브에 BitLocker가 켜져 있습니다. PIN/복구 키 입력이 필요하면 재부팅 후 자동 재개가 불가능합니다.");
                    }
                }
            }

            return PreflightCheck.Pass("BitLocker", "BitLocker 보호가 감지되지 않았습니다.");
        }
        catch
        {
            return PreflightCheck.Warn("BitLocker", "BitLocker 상태를 확인할 수 없습니다.");
        }
    }

    private static PreflightCheck CheckSecureBootState()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
            var enabled = key?.GetValue("UEFISecureBootEnabled");
            return enabled is int i && i == 1
                ? PreflightCheck.Pass("보안 부팅", "UEFI 보안 부팅이 활성화되어 있습니다.")
                : PreflightCheck.Warn("보안 부팅", "보안 부팅이 비활성화되어 있거나 확인할 수 없습니다.");
        }
        catch
        {
            return PreflightCheck.Warn("보안 부팅", "보안 부팅 상태를 확인할 수 없습니다.");
        }
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
