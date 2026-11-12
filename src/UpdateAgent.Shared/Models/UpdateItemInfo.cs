namespace UpdateAgent.Shared.Models;

/// <summary>WUA 검색 결과의 단일 업데이트.</summary>
public sealed record UpdateItemInfo
{
    public required string UpdateId { get; init; }
    public string RevisionNumber { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public UpdateCategory Category { get; init; }
    public string CategoryName { get; init; } = "";
    public bool IsSecurity { get; init; }
    public bool IsFeatureUpdate { get; init; }
    public bool IsDriver { get; init; }
    public bool IsDownloaded { get; init; }
    public bool IsMandatory { get; init; }
    public double MaxDownloadSizeBytes { get; init; }
    public IReadOnlyList<string> KbArticleIds { get; init; } = [];

    /// <summary>선택 정책에 포함되었는지 여부.</summary>
    public bool Selected { get; init; } = true;

    public string KbLabel =>
        KbArticleIds.Count == 0 ? "KB-" : string.Join(", ", KbArticleIds.Select(k => $"KB{k}"));

    public string DisplayName =>
        string.IsNullOrWhiteSpace(Title) ? KbLabel : $"{KbLabel} · {Title}";

    public double MaxDownloadSizeMb => Math.Round(MaxDownloadSizeBytes / 1024d / 1024d, 1);

    public string SizeLabel
    {
        get
        {
            if (MaxDownloadSizeBytes <= 0) return "";
            var mb = MaxDownloadSizeBytes / 1024d / 1024d;
            return mb >= 1024
                ? $"{mb / 1024d:N1} GB"
                : $"{mb:N1} MB";
        }
    }
}

/// <summary>작업에 포함된 항목의 처리 상태.</summary>
public sealed record JobItem
{
    public required string UpdateId { get; init; }
    public string KbArticleIds { get; init; } = "";
    public string Title { get; init; } = "";
    public UpdateCategory Category { get; init; }
    public UpdateItemState State { get; init; } = UpdateItemState.Pending;
    public int FailureCount { get; init; }
    public string? Error { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
}
