using System.Globalization;
using System.Text;
using UpdateAgent.LabManager.Models;
using UpdateAgent.LabManager.Observability;
using UpdateAgent.LabManager.Storage;

namespace UpdateAgent.LabManager.Services;

/// <summary>
/// 작업 리포트의 절단/누락 표현을 한 곳에서 정한다. 실제로 조회한 값보다 추정값을
/// 우선하지 않으며, 없는 값은 빈 문자열이 아니라 한국어 마커로 반환한다.
/// </summary>
public static class JobReportBuilder
{
    public const string Absent = "없음";
    public const string Unknown = "확인 불가";
    public const string TimelineRecorded = "기록됨";
    public const int DefaultLimit = 100;
    public const int MaxLimit = 500;

    public static int NormalizeLimit(int? limit) => Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

    /// <summary>from/to는 UTC ISO-8601을 받는다. 날짜만 주면 to는 그 날의 마지막 순간으로 본다.</summary>
    public static bool TryParseRange(
        string? from,
        string? to,
        out DateTimeOffset? fromUtc,
        out DateTimeOffset? toUtc,
        out string? error)
    {
        fromUtc = null;
        toUtc = null;
        error = null;

        if (!TryParseBoundary(from, isEnd: false, out fromUtc))
        {
            error = "from은 UTC ISO-8601 시각이어야 합니다.";
            return false;
        }
        if (!TryParseBoundary(to, isEnd: true, out toUtc))
        {
            error = "to는 UTC ISO-8601 시각이어야 합니다.";
            return false;
        }
        if (fromUtc is { } start && toUtc is { } end && start > end)
        {
            error = "from이 to보다 늦습니다.";
            return false;
        }
        return true;
    }

    private static bool TryParseBoundary(string? value, bool isEnd, out DateTimeOffset? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (!DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var result))
        {
            return false;
        }

        var dateOnly = value.Length == 10 && value[4] == '-' && value[7] == '-';
        parsed = (isEnd && dateOnly ? result.AddDays(1).AddTicks(-1) : result).ToUniversalTime();
        return true;
    }

    public static JobReportResponse Build(
        IReadOnlyList<JobReportRow> rows,
        int totalCount,
        IReadOnlyDictionary<string, JobTransitionSummary>? transitions,
        int limit,
        DateTimeOffset? from,
        DateTimeOffset? to,
        DateTimeOffset? generatedAt = null)
    {
        var entries = new List<JobReportEntry>(rows.Count);
        foreach (var row in rows)
        {
            var summary = transitions is not null &&
                          transitions.TryGetValue(row.JobId, out var found)
                ? found
                : new JobTransitionSummary(0, null, null);
            entries.Add(new JobReportEntry(
                Text(row.JobId),
                Text(row.AgentId),
                Text(row.MachineName),
                Text(row.State),
                Text(row.Phase),
                Text(row.Message),
                Stamp(row.StartedAt),
                Stamp(row.UpdatedAt),
                Text(row.CorrelationId),
                summary.Count,
                Stamp(summary.FirstAt),
                Stamp(summary.LastAt),
                transitions is null ? Unknown : summary.Count > 0 ? TimelineRecorded : Absent));
        }

        // 전체 건수는 DB에서 따로 센다. 잘린 목록 길이를 전체로 취급하지 않는다.
        var omitted = Math.Max(0, totalCount - rows.Count);
        return new JobReportResponse(
            generatedAt ?? UtcTimestamps.Now,
            from,
            to,
            NormalizeLimit(limit),
            Math.Max(0, totalCount),
            rows.Count,
            omitted > 0,
            omitted,
            entries);
    }

    public static string ToCsv(JobReportResponse report)
    {
        var builder = new StringBuilder();
        builder.AppendJoin(',', new[]
        {
            "jobId", "agentId", "machineName", "state", "phase", "message",
            "startedAt", "updatedAt", "correlationId", "transitionCount",
            "firstTransitionAt", "lastTransitionAt", "timeline",
            "generatedAt", "from", "to", "limit", "totalCount", "returnedCount",
            "truncated", "omittedCount",
        });
        builder.Append('\n');

        var metadata = new[]
        {
            Stamp(report.GeneratedAt),
            Stamp(report.From),
            Stamp(report.To),
            report.Limit.ToString(CultureInfo.InvariantCulture),
            report.TotalCount.ToString(CultureInfo.InvariantCulture),
            report.ReturnedCount.ToString(CultureInfo.InvariantCulture),
            report.Truncated ? "true" : "false",
            report.OmittedCount.ToString(CultureInfo.InvariantCulture),
        };

        if (report.Jobs.Count == 0)
        {
            AppendRow(builder, new[] { Absent, Absent, Absent, Absent, Absent, Absent, Absent, Absent, Absent, "0", Absent, Absent, Absent }.Concat(metadata));
            return builder.ToString();
        }

        foreach (var job in report.Jobs)
        {
            AppendRow(builder, new[]
            {
                job.JobId, job.AgentId, job.MachineName, job.State, job.Phase, job.Message,
                job.StartedAt, job.UpdatedAt, job.CorrelationId,
                job.TransitionCount.ToString(CultureInfo.InvariantCulture),
                job.FirstTransitionAt, job.LastTransitionAt, job.Timeline,
            }.Concat(metadata));
        }
        return builder.ToString();
    }

    private static void AppendRow(StringBuilder builder, IEnumerable<string> fields)
    {
        var first = true;
        foreach (var field in fields)
        {
            if (!first) builder.Append(',');
            builder.Append('"').Append(field.Replace("\"", "\"\"")).Append('"');
            first = false;
        }
        builder.Append('\n');
    }

    private static string Text(string? value) =>
        ObservabilityEvent.MaskSecrets(value) ?? Absent;

    private static string Stamp(DateTimeOffset? value) =>
        value is null ? Absent : UtcTimestamps.Format(value.Value);
}
