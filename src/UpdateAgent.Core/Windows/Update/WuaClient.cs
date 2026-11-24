using System.Runtime.InteropServices;
using UpdateAgent.Shared.Models;

namespace UpdateAgent.Core.Windows.Update;

/// <summary>WUA(Windows Update Agent) COM API 래퍼.</summary>
public interface IWuaClient
{
    Task<WuaScanResult> ScanAsync(bool includeDrivers, CancellationToken cancellationToken = default);
    Task<WuaOperationResult> DownloadAsync(WuaUpdate update, CancellationToken cancellationToken = default);
    Task<WuaInstallResult> InstallAsync(WuaUpdate update, CancellationToken cancellationToken = default);
    Task<WuaSystemStatus> GetSystemStatusAsync(CancellationToken cancellationToken = default);
    Task<bool> IsBusyAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// IUpdateSearcher / IUpdateDownloader / IUpdateInstaller를 late binding(dynamic)으로 호출한다.
/// COM interop 어셈블리를 배포하지 않아도 되고, WUA가 있는 모든 Windows에서 동작한다.
/// </summary>
public sealed class WuaClient : IWuaClient, IDisposable
{
    private const string SoftwareSearch = "IsInstalled=0 and IsHidden=0 and Type='Software'";
    private const string DriverSearch = "IsInstalled=0 and IsHidden=0 and Type='Driver'";

    private readonly StaExecutor _sta;
    private readonly bool _ownsExecutor;

    public WuaClient() : this(new StaExecutor(), ownsExecutor: true)
    {
    }

    internal WuaClient(StaExecutor executor, bool ownsExecutor)
    {
        _sta = executor;
        _ownsExecutor = ownsExecutor;
    }

    public Task<WuaScanResult> ScanAsync(bool includeDrivers, CancellationToken cancellationToken = default)
    {
        return _sta.InvokeAsync(() => ScanCore(includeDrivers), cancellationToken);
    }

    private WuaScanResult ScanCore(bool includeDrivers)
    {
        try
        {
            dynamic session = CreateCom("Microsoft.Update.Session");
            dynamic searcher = session.CreateUpdateSearcher();
            TrySet(() => searcher.Online = true);
            TrySet(() => searcher.SearchScope = 1); // ssMachine

            var updates = new List<WuaUpdate>();
            Collect(searcher, SoftwareSearch, updates);

            if (includeDrivers)
            {
                try
                {
                    Collect(searcher, DriverSearch, updates);
                }
                catch (Exception ex) when (ex is COMException or InvalidCastException)
                {
                    // 드라이버 검색 실패는 전체 검색을 실패로 만들지 않는다.
                }
            }

            return new WuaScanResult
            {
                Updates = updates,
                Status = GetSystemStatusCore(),
                ScannedAt = UpdateAgent.Guards.UtcClock.Now,
            };
        }
        catch (WuaException)
        {
            throw;
        }
        catch (COMException ex)
        {
            throw new WuaException(DescribeHResult(ex.ErrorCode), ex.ErrorCode, ex);
        }
        catch (Exception ex)
        {
            throw new WuaException($"Windows Update 검색 실패: {ex.Message}", 0, ex);
        }
    }

    private static void Collect(dynamic searcher, string criteria, List<WuaUpdate> updates)
    {
        dynamic result = searcher.Search(criteria);
        int count = result.Updates.Count;

        for (var i = 0; i < count; i++)
        {
            dynamic update = result.Updates.Item(i);
            var info = MapUpdate(update);
            updates.Add(new WuaUpdate { Info = info, Native = update });
        }
    }

    public Task<WuaOperationResult> DownloadAsync(WuaUpdate update, CancellationToken cancellationToken = default)
    {
        return _sta.InvokeAsync(() => DownloadCore(update), cancellationToken);
    }

    private WuaOperationResult DownloadCore(WuaUpdate update)
    {
        try
        {
            if (update.Info.IsDownloaded)
            {
                return WuaOperationResult.Ok("이미 다운로드됨");
            }

            dynamic session = CreateCom("Microsoft.Update.Session");
            dynamic downloader = session.CreateUpdateDownloader();
            dynamic coll = CreateCom("Microsoft.Update.UpdateColl");
            coll.Add(update.Native);
            downloader.Updates = coll;

            dynamic result = downloader.Download();
            int resultCode = result.ResultCode;
            int hResult = result.HResult;

            if (resultCode is 2 or 3)
            {
                return WuaOperationResult.Ok();
            }

            return WuaOperationResult.Fail(
                resultCode,
                hResult,
                $"다운로드 실패: {DescribeHResult(hResult)} (code {resultCode})");
        }
        catch (COMException ex)
        {
            return WuaOperationResult.Fail(4, ex.ErrorCode, $"다운로드 오류: {DescribeHResult(ex.ErrorCode)}");
        }
        catch (Exception ex)
        {
            return WuaOperationResult.Fail(4, 0, $"다운로드 오류: {ex.Message}");
        }
    }

    public Task<WuaInstallResult> InstallAsync(WuaUpdate update, CancellationToken cancellationToken = default)
    {
        return _sta.InvokeAsync(() => InstallCore(update), cancellationToken);
    }

    private WuaInstallResult InstallCore(WuaUpdate update)
    {
        try
        {
            dynamic native = update.Native;
            TrySet(() =>
            {
                if (!native.EulaAccepted) native.AcceptEula();
            });

            dynamic session = CreateCom("Microsoft.Update.Session");
            dynamic installer = session.CreateUpdateInstaller();
            TrySet(() => installer.ForceQuiet = true);

            if (installer.IsBusy)
            {
                return WuaInstallResult.Failed(5, 0, "다른 Windows Update 설치가 진행 중입니다. 잠시 후 재시도합니다.");
            }

            dynamic coll = CreateCom("Microsoft.Update.UpdateColl");
            coll.Add(update.Native);
            installer.Updates = coll;

            dynamic result = installer.Install();
            int resultCode = result.ResultCode;
            bool rebootRequired = false;
            TrySet(() => rebootRequired = installer.RebootRequired || result.RebootRequired);

            int hResult = 0;
            int updateResultCode = resultCode;
            try
            {
                dynamic itemResult = result.GetUpdateResult(0);
                updateResultCode = itemResult.ResultCode;
                hResult = itemResult.HResult;
                TrySet(() => rebootRequired = rebootRequired || itemResult.RebootRequired);
            }
            catch
            {
                hResult = SafeHResult(result);
            }

            if (updateResultCode is 2 or 3)
            {
                return WuaInstallResult.Ok(rebootRequired);
            }

            return WuaInstallResult.Failed(
                updateResultCode,
                hResult,
                $"설치 실패: {DescribeHResult(hResult)} (code {updateResultCode})");
        }
        catch (COMException ex)
        {
            return WuaInstallResult.Failed(4, ex.ErrorCode, $"설치 오류: {DescribeHResult(ex.ErrorCode)}");
        }
        catch (Exception ex)
        {
            return WuaInstallResult.Failed(4, 0, $"설치 오류: {ex.Message}");
        }
    }

    public Task<WuaSystemStatus> GetSystemStatusAsync(CancellationToken cancellationToken = default)
    {
        return _sta.InvokeAsync(GetSystemStatusCore, cancellationToken);
    }

    private WuaSystemStatus GetSystemStatusCore()
    {
        var rebootRequired = false;
        var busy = false;
        string? error = null;

        try
        {
            dynamic systemInfo = CreateCom("Microsoft.Update.SystemInfo");
            rebootRequired = systemInfo.RebootRequired;
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        try
        {
            dynamic session = CreateCom("Microsoft.Update.Session");
            dynamic installer = session.CreateUpdateInstaller();
            busy = installer.IsBusy;
        }
        catch (Exception ex)
        {
            error ??= ex.Message;
        }

        return new WuaSystemStatus
        {
            RebootRequired = rebootRequired,
            InstallerBusy = busy,
            IsWuServiceRunning = ServiceProbe.IsTrustedInstallerRunning(),
            LastError = error,
        };
    }

    public Task<bool> IsBusyAsync(CancellationToken cancellationToken = default) =>
        _sta.InvokeAsync(() =>
        {
            try
            {
                dynamic session = CreateCom("Microsoft.Update.Session");
                dynamic installer = session.CreateUpdateInstaller();
                return (bool)installer.IsBusy || ServiceProbe.IsTrustedInstallerRunning();
            }
            catch
            {
                return ServiceProbe.IsTrustedInstallerRunning();
            }
        }, cancellationToken);

    internal static UpdateItemInfo MapUpdate(dynamic update)
    {
        var kbIds = new List<string>();
        var categoryNames = new List<string>();

        TrySet(() =>
        {
            dynamic kbs = update.KBArticleIDs;
            for (var i = 0; i < kbs.Count; i++)
            {
                var kb = Convert.ToString(kbs.Item(i));
                if (!string.IsNullOrWhiteSpace(kb)) kbIds.Add(kb!);
            }
        });

        TrySet(() =>
        {
            dynamic categories = update.Categories;
            for (var i = 0; i < categories.Count; i++)
            {
                dynamic category = categories.Item(i);
                var name = Convert.ToString(category.Name);
                if (!string.IsNullOrWhiteSpace(name)) categoryNames.Add(name!);
            }
        });

        var categoryName = string.Join(", ", categoryNames);
        var title = TryGet<string>(() => update.Title) ?? "";
        var category = ClassifyCategory(categoryName, title);
        var isDriver = category == UpdateCategory.Driver;
        var isFeature = category == UpdateCategory.Upgrade
                        || title.Contains("Feature update", StringComparison.OrdinalIgnoreCase)
                        || title.Contains("기능 업데이트", StringComparison.OrdinalIgnoreCase);

        double size = 0;
        TrySet(() => size = Convert.ToDouble(update.MaxDownloadSize));

        bool downloaded = false;
        TrySet(() => downloaded = update.IsDownloaded);

        bool mandatory = false;
        TrySet(() => mandatory = update.IsMandatory);

        return new UpdateItemInfo
        {
            UpdateId = TryGet<string>(() => update.Identity.UpdateID) ?? Guid.NewGuid().ToString("B"),
            RevisionNumber = TryGet<object>(() => update.Identity.RevisionNumber)?.ToString() ?? "",
            Title = title,
            Description = TryGet<string>(() => update.Description) ?? "",
            Category = category,
            CategoryName = categoryName,
            IsSecurity = category == UpdateCategory.Security,
            IsFeatureUpdate = isFeature,
            IsDriver = isDriver,
            IsDownloaded = downloaded,
            IsMandatory = mandatory,
            MaxDownloadSizeBytes = size,
            KbArticleIds = kbIds,
        };
    }

    internal static UpdateCategory ClassifyCategory(string categoryName, string title)
    {
        var text = $"{categoryName} {title}";

        if (text.Contains("Driver", StringComparison.OrdinalIgnoreCase)) return UpdateCategory.Driver;
        if (text.Contains("Definition", StringComparison.OrdinalIgnoreCase)) return UpdateCategory.Definition;
        if (text.Contains("Upgrade", StringComparison.OrdinalIgnoreCase)) return UpdateCategory.Upgrade;
        if (text.Contains("Feature Pack", StringComparison.OrdinalIgnoreCase)) return UpdateCategory.FeaturePack;
        if (text.Contains("Service Pack", StringComparison.OrdinalIgnoreCase)) return UpdateCategory.ServicePack;
        if (text.Contains("Security", StringComparison.OrdinalIgnoreCase)) return UpdateCategory.Security;
        if (text.Contains("Critical", StringComparison.OrdinalIgnoreCase)) return UpdateCategory.Critical;
        if (text.Contains("Important", StringComparison.OrdinalIgnoreCase)) return UpdateCategory.Important;
        if (text.Contains("Moderate", StringComparison.OrdinalIgnoreCase)) return UpdateCategory.Moderate;
        if (text.Contains("Low", StringComparison.OrdinalIgnoreCase)) return UpdateCategory.Low;
        if (text.Contains("Unspecified", StringComparison.OrdinalIgnoreCase)) return UpdateCategory.Unspecified;
        return UpdateCategory.Other;
    }

    private static object CreateCom(string progId)
    {
        var type = Type.GetTypeFromProgID(progId);
        if (type is null)
        {
            throw new WuaException(
                $"COM ProgID '{progId}'를 찾을 수 없습니다. Windows Update 서비스가 비활성화되었을 수 있습니다.");
        }

        return Activator.CreateInstance(type)
               ?? throw new WuaException($"COM 객체 '{progId}'를 생성할 수 없습니다.");
    }

    private static void TrySet(Action action)
    {
        try
        {
            action();
        }
        catch
        {
            // 선택적 속성은 실패해도 무시한다.
        }
    }

    private static T? TryGet<T>(Func<object?> getter)
    {
        try
        {
            var value = getter();
            return value is T typed ? typed : default;
        }
        catch
        {
            return default;
        }
    }

    private static int SafeHResult(dynamic result)
    {
        try
        {
            return (int)result.HResult;
        }
        catch
        {
            return 0;
        }
    }

    internal static string DescribeHResult(int hresult) => ((uint)hresult) switch
    {
        0u => "정상",
        0x80240017u => "이 업데이트는 이 PC에 적용되지 않습니다 (WU_E_NOT_APPLICABLE).",
        0x80240024u => "설치할 업데이트가 없습니다 (WU_E_NO_UPDATE).",
        0x8024000Bu => "작업이 취소되었습니다 (WU_E_CALL_CANCELLED).",
        0x8024000Eu => "업데이트 정보를 읽을 수 없습니다 (WU_E_XML_INVALID).",
        0x80240020u => "다른 작업이 진행 중입니다 (WU_E_INTERACTIVE_CALLBACK).",
        0x8024002Eu => "Windows Update 서비스가 비활성화되었습니다 (WU_E_WU_DISABLED).",
        0x80240034u => "다운로드에 실패했습니다 (WU_E_DOWNLOAD_FAILED).",
        0x80240438u => "Windows Update 서버에 연결할 수 없습니다 (WU_E_PT_HTTP_STATUS_NOT_FOUND).",
        0x8024402Cu => "프록시/네트워크 오류입니다 (WU_E_PT_WINHTTP_NAME_NOT_RESOLVED).",
        0x80248007u => "업데이트 메타데이터를 찾을 수 없습니다 (WU_E_DS_NODATA).",
        0x80070422u => "Windows Update 서비스가 실행 중이 아닙니다 (service disabled).",
        0x800F0922u => "구성 요소 저장소 오류입니다 (CBS_E_INSTALLERS_FAILED).",
        0x80070005u => "액세스가 거부되었습니다. 관리자 권한으로 실행하세요.",
        _ => $"알 수 없는 오류 (0x{hresult:X8})",
    };

    public void Dispose()
    {
        if (_ownsExecutor) _sta.Dispose();
    }
}
