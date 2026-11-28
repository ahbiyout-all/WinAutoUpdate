using Serilog.Core;
using Serilog.Events;

namespace UpdateAgent.LabManager.Observability;

/// <summary>
/// 기존 Serilog 파이프라인에 추가하는 구조화 이벤트 싱크. 콘솔/일반 파일 로깅은 그대로 둔다.
/// observabilityEvent 표시가 있는 이벤트만 기록하고, 허용된 메타데이터 외의 속성은 버린다.
/// </summary>
public sealed class JsonLineLogSink : ILogEventSink
{
    private readonly JsonLineLogWriter _writer;

    public JsonLineLogSink(JsonLineLogWriter writer)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    }

    public void Emit(LogEvent logEvent)
    {
        if (logEvent is null ||
            !TryGetString(logEvent, "observabilityEvent", out var eventName) ||
            string.IsNullOrWhiteSpace(eventName))
        {
            return;
        }

        _writer.Write(new ObservabilityEvent(
            logEvent.Timestamp.UtcDateTime,
            logEvent.Level.ToString(),
            eventName!,
            GetString(logEvent, "jobId"),
            GetString(logEvent, "agentId"),
            GetString(logEvent, "correlationId")));
    }

    private static bool TryGetString(LogEvent logEvent, string key, out string? value)
    {
        value = null;
        if (!logEvent.Properties.TryGetValue(key, out var property) ||
            property is not ScalarValue { Value: string text })
        {
            return false;
        }
        value = text;
        return true;
    }

    private static string? GetString(LogEvent logEvent, string key) =>
        TryGetString(logEvent, key, out var value) ? value : null;
}
