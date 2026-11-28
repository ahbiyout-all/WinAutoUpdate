using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using UpdateAgent.Guards;
using UpdateAgent.LabManager.Services;

namespace UpdateAgent.LabManager.Observability;

/// <summary>
/// JSONL 한 줄에 담는 관측 메타데이터. 본문/헤더/토큰을 담을 필드는 아예 두지 않는다.
/// </summary>
public sealed record ObservabilityEvent(
    DateTimeOffset TimestampUtc,
    string Level,
    string EventName,
    string? JobId = null,
    string? AgentId = null,
    string? CorrelationId = null)
{
    /// <summary>토큰·암호·헤더처럼 보이는 값을 마커로 바꾼다. 길이는 자르지 않는다.</summary>
    public static string? MaskSecrets(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        var forbidden = new[]
        {
            "token", "password", "passwd", "secret", "authorization", "cookie", "requestbody", "body=",
        };
        return forbidden.Any(word => trimmed.Contains(word, StringComparison.OrdinalIgnoreCase)) ||
               trimmed.Any(char.IsControl)
            ? "확인 불가"
            : trimmed;
    }

    /// <summary>JSONL 한 줄에 넣을 값은 마스킹한 뒤 128자로 제한한다.</summary>
    public static string? Sanitize(string? value)
    {
        var masked = MaskSecrets(value);
        return masked is null ? null : masked.Length > 128 ? masked[..128] : masked;
    }

    public string ToJsonLine()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   buffer,
                   new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteString("timestampUtc", UtcTimestamps.Format(TimestampUtc));
            writer.WriteString("level", Sanitize(Level) ?? "정보");
            writer.WriteString("event", Sanitize(EventName) ?? "확인 불가");
            WriteOptional(writer, "jobId", JobId);
            WriteOptional(writer, "agentId", AgentId);
            WriteOptional(writer, "correlationId", CorrelationId);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteOptional(Utf8JsonWriter writer, string name, string? value)
    {
        var safe = Sanitize(value);
        if (safe is not null) writer.WriteString(name, safe);
    }
}
