using System.Globalization;
using UpdateAgent.Shared.Models;

namespace UpdateAgent.LabManager.Services;

/// <summary>
/// 작업 시작 요청에서 읽은 단계적 롤아웃/유지보수 창 설정.
/// 잘못된 값은 기본값으로 조용히 되돌리지 않고 거부한다(fail-closed).
/// </summary>
public sealed record RolloutConfiguration
{
    private RolloutConfiguration(
        int batchSize,
        int intervalSeconds,
        TimeOnly? windowStart,
        TimeOnly? windowEnd)
    {
        BatchSize = batchSize;
        IntervalSeconds = intervalSeconds;
        WindowStart = windowStart;
        WindowEnd = windowEnd;
    }

    /// <summary>0이면 모든 대상을 한 번에 시작한다.</summary>
    public int BatchSize { get; }

    /// <summary>배치 사이 대기 시간(초). 0이면 즉시 전송한다.</summary>
    public int IntervalSeconds { get; }

    public TimeOnly? WindowStart { get; }

    public TimeOnly? WindowEnd { get; }

    public bool HasWindow => WindowStart is not null && WindowEnd is not null;

    /// <summary>시작 시각이 종료 시각보다 늦으면 자정을 넘어가는 창이다.</summary>
    public bool CrossesMidnight => HasWindow && WindowStart! > WindowEnd!;

    /// <summary>대상 수에 따른 배치 수. 배치 옵션이 없으면 항상 1이다.</summary>
    public int BatchCount(int targetCount)
    {
        if (targetCount <= 0) return 1;
        if (BatchSize <= 0) return 1;
        return (targetCount + BatchSize - 1) / BatchSize;
    }

    /// <summary>0부터 시작하는 대상 순번이 속한 배치 번호(1부터 시작).</summary>
    public int BatchIndexOf(int zeroBasedTargetIndex) =>
        BatchSize <= 0 ? 1 : zeroBasedTargetIndex / BatchSize + 1;

    /// <summary>서버 로컬 시각이 유지보수 창 안인지 확인한다. 창이 없으면 항상 true다.</summary>
    public bool IsWithinWindow(DateTimeOffset localNow)
    {
        if (!HasWindow) return true;

        var time = TimeOnly.FromTimeSpan(localNow.TimeOfDay);
        var start = WindowStart!.Value;
        var end = WindowEnd!.Value;

        return CrossesMidnight
            ? time >= start || time < end
            : time >= start && time < end;
    }

    /// <summary>사용자에게 보여줄 유지보수 창 문구.</summary>
    public string WindowLabel => HasWindow
        ? $"{WindowStart!.Value:HH:mm}~{WindowEnd!.Value:HH:mm}"
        : "설정 없음";

    /// <summary>
    /// 정책 값을 검증한다. 하나라도 잘못되면 오류 목록을 채우고 false를 돌려
    /// 호출부가 작업/명령/감사 생성 전에 요청을 거부하도록 한다.
    /// </summary>
    public static bool TryCreate(
        UpdatePolicy? policy,
        out RolloutConfiguration configuration,
        out IReadOnlyList<string> errors)
    {
        var messages = new List<string>();
        policy ??= UpdatePolicy.Default;

        if (policy.RolloutBatchSize < 0)
            messages.Add("배치 크기는 0 이상이어야 합니다. 0이면 모든 PC를 즉시 시작합니다.");
        if (policy.RolloutIntervalSeconds < 0)
            messages.Add("배치 간격은 0 이상이어야 합니다.");

        var hasStart = !string.IsNullOrWhiteSpace(policy.MaintenanceWindowStart);
        var hasEnd = !string.IsNullOrWhiteSpace(policy.MaintenanceWindowEnd);
        if (hasStart != hasEnd)
        {
            messages.Add("유지보수 시작 시간과 종료 시간은 함께 설정해야 합니다.");
        }

        TimeOnly? start = null;
        TimeOnly? end = null;
        if (hasStart && !TryParseTime(policy.MaintenanceWindowStart, out start))
            messages.Add("유지보수 시작 시간은 HH:mm 형식(예: 22:00)이어야 합니다.");
        if (hasEnd && !TryParseTime(policy.MaintenanceWindowEnd, out end))
            messages.Add("유지보수 종료 시간은 HH:mm 형식(예: 06:00)이어야 합니다.");

        if (start is not null && end is not null && start == end)
            messages.Add("유지보수 시작 시간과 종료 시간이 같을 수 없습니다. 자정을 넘기는 창도 종료 시간을 다르게 지정하세요.");

        configuration = new RolloutConfiguration(
            policy.RolloutBatchSize < 0 ? 0 : policy.RolloutBatchSize,
            policy.RolloutIntervalSeconds < 0 ? 0 : policy.RolloutIntervalSeconds,
            start,
            end);
        errors = messages;
        return messages.Count == 0;
    }

    private static bool TryParseTime(string? value, out TimeOnly? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(value)) return false;

        // 앞뒤 공백만 정리하고 나머지 형식은 엄격하게 검사한다.
        var text = value.Trim();
        if (!TimeOnly.TryParseExact(
                text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            return false;
        }

        parsed = time;
        return true;
    }

    /// <summary>저장/전송할 정책에 검증된 롤아웃 값을 다시 적용한다.</summary>
    public UpdatePolicy ApplyTo(UpdatePolicy policy) => policy with
    {
        RolloutBatchSize = BatchSize,
        RolloutIntervalSeconds = IntervalSeconds,
        MaintenanceWindowStart = WindowStart?.ToString("HH:mm"),
        MaintenanceWindowEnd = WindowEnd?.ToString("HH:mm"),
    };
}
