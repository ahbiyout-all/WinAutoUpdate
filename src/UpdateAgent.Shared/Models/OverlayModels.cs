namespace UpdateAgent.Shared.Models;

/// <summary>서비스 → SessionHelper 오버레이 메시지.</summary>
public sealed record OverlayMessage
{
    public OverlayMessageKind Kind { get; init; } = OverlayMessageKind.Update;
    public JobSummary? Summary { get; init; }
    public bool ShowKbDetails { get; init; } = true;
    public string? Notice { get; init; }

    public static OverlayMessage Show(JobSummary summary, bool showDetails = true) => new()
    {
        Kind = OverlayMessageKind.Show,
        Summary = summary,
        ShowKbDetails = showDetails,
    };

    public static OverlayMessage Update(JobSummary summary, bool showDetails = true) => new()
    {
        Kind = OverlayMessageKind.Update,
        Summary = summary,
        ShowKbDetails = showDetails,
    };

    public static OverlayMessage Hide(string? notice = null) => new()
    {
        Kind = OverlayMessageKind.Hide,
        Notice = notice,
    };

    public static OverlayMessage Ping() => new() { Kind = OverlayMessageKind.Ping };
}

public enum OverlayMessageKind
{
    Show = 0,
    Update = 1,
    Hide = 2,
    Ping = 3,
}

/// <summary>SessionHelper → 서비스 응답.</summary>
public sealed record OverlayReply
{
    public bool Ok { get; init; } = true;
    public string Message { get; init; } = "";
    public int SessionId { get; init; }
    public DateTimeOffset Timestamp { get; init; } = UpdateAgent.Guards.UtcClock.Now;
}
