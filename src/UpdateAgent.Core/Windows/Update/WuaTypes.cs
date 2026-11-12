using UpdateAgent.Shared.Models;

namespace UpdateAgent.Core.Windows.Update;

/// <summary>WUA COM 호출에서 발생한 오류.</summary>
public sealed class WuaException : Exception
{
    public WuaException(string message, int hResult = 0, Exception? inner = null)
        : base(message, inner)
    {
        WuaHResult = hResult;
    }

    public int WuaHResult { get; }

    public string HResultHex => $"0x{WuaHResult:X8}";
}

/// <summary>검색된 업데이트 하나. COM 객체는 STA 스레드에서만 사용해야 한다.</summary>
public sealed class WuaUpdate
{
    public required UpdateItemInfo Info { get; init; }
    internal object Native { get; init; } = null!;

    /// <summary>재검색 없이 설치/다운로드할 때 사용하는 원본 객체. UI에 노출하지 않는다.</summary>
    public object NativeObject => Native;
}

public sealed record WuaScanResult
{
    public IReadOnlyList<WuaUpdate> Updates { get; init; } = [];
    public WuaSystemStatus Status { get; init; } = new();
    public DateTimeOffset ScannedAt { get; init; } = DateTimeOffset.Now;

    public int TotalCount => Updates.Count;
}

public sealed record WuaSystemStatus
{
    public bool RebootRequired { get; init; }
    public bool InstallerBusy { get; init; }
    public bool IsWuServiceRunning { get; init; }
    public string? LastError { get; init; }

    public bool IsBusy => InstallerBusy || IsWuServiceRunning;
}

public record WuaOperationResult
{
    public bool Success { get; init; }
    public int ResultCode { get; init; }
    public int HResult { get; init; }
    public string Message { get; init; } = "";

    public string HResultHex => $"0x{HResult:X8}";

    public static WuaOperationResult Ok(string message = "성공") => new()
    {
        Success = true,
        ResultCode = 2,
        Message = message,
    };

    public static WuaOperationResult Fail(int resultCode, int hResult, string message) => new()
    {
        Success = false,
        ResultCode = resultCode,
        HResult = hResult,
        Message = message,
    };
}

public sealed record WuaInstallResult : WuaOperationResult
{
    public bool RebootRequired { get; init; }

    public static WuaInstallResult Ok(bool rebootRequired, string message = "설치 완료") => new()
    {
        Success = true,
        ResultCode = 2,
        Message = message,
        RebootRequired = rebootRequired,
    };

    public static WuaInstallResult Failed(int resultCode, int hResult, string message) => new()
    {
        Success = false,
        ResultCode = resultCode,
        HResult = hResult,
        Message = message,
    };
}
