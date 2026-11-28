using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using UpdateAgent.Service.Configuration;

namespace UpdateAgent.Service.Integrity;

/// <summary>구성 지문 계산 결과. 실패 시 원문 대신 null과 확인 불가 플래그를 돌려준다.</summary>
public sealed record ConfigFingerprintResult(string? Fingerprint, bool Unavailable)
{
    public static ConfigFingerprintResult Failed { get; } = new(null, true);
}

/// <summary>
/// 체크인에 실어 보낼 구성 지문. 설정 파일 원문이나 토큰/암호 키는 절대 읽지 않고,
/// 명시적 허용 목록의 값만 정규화해 해시한다. 해시에는 어떤 원문 값도 포함되지 않는다.
/// </summary>
public static class ConfigFingerprint
{
    public const string Prefix = "cfg-sha256:";
    private const string CanonicalVersion = "v1";
    private const int MaxValueLength = 512;

    // 비밀성이 없는 설정만 허용한다. 토큰/암호/자격 증명 키는 의도적으로 absent.
    private static readonly string[] AllowlistedKeys =
    [
        "LabManagerUrl",
        "AgentId",
        "CheckInEnabled",
        "OverlayEnabled",
        "OverlayCompletionHoldSeconds",
        "PollIntervalSeconds",
        "MaxCommandsPerCheckIn",
        "ResumeOnStartup",
        "DryRun",
    ];

    private static readonly Regex SecretLike = new(
        @"(?i)(token|password|passwd|pwd|secret)",
        RegexOptions.CultureInvariant);

    public static ConfigFingerprintResult Compute(
        string configPath,
        string agentId,
        AgentOptions? effectiveOptions = null)
    {
        if (string.IsNullOrWhiteSpace(configPath) || string.IsNullOrWhiteSpace(agentId))
        {
            return ConfigFingerprintResult.Failed;
        }

        try
        {
            if (!File.Exists(configPath)) return ConfigFingerprintResult.Failed;

            using var document = JsonDocument.Parse(File.ReadAllText(configPath));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("Agent", out var agentSection) ||
                agentSection.ValueKind != JsonValueKind.Object)
            {
                return ConfigFingerprintResult.Failed;
            }

            var canonical = new StringBuilder();
            canonical.Append(CanonicalVersion).Append('\n');
            foreach (var key in AllowlistedKeys)
            {
                if (SecretLike.IsMatch(key)) return ConfigFingerprintResult.Failed;
                if (!agentSection.TryGetProperty(key, out var value)) continue;
                if (!TryNormalize(value, out var normalized)) return ConfigFingerprintResult.Failed;

                canonical.Append("Agent.").Append(key).Append('=').Append(normalized).Append('\n');
            }

            // AgentId는 PC 식별자이며 실행 중 서비스가 이미 알고 있는 값이다.
            if (!TryNormalizeAgentId(agentId, out var normalizedId))
            {
                return ConfigFingerprintResult.Failed;
            }
            canonical.Append("runtime.AgentId=").Append(normalizedId).Append('\n');

            // 머신 설정 파일만 보면 appsettings.json/환경 변수로 바꾼 유효 설정을 놓친다.
            // 같은 허용 목록의 유효 값도 함께 해시해 실제 동작 변경을 탐지한다.
            if (effectiveOptions is not null)
            {
                AppendEffective(canonical, "effective.LabManagerUrl", effectiveOptions.LabManagerUrl);
                AppendEffective(canonical, "effective.CheckInEnabled", Bool(effectiveOptions.CheckInEnabled));
                AppendEffective(canonical, "effective.OverlayEnabled", Bool(effectiveOptions.OverlayEnabled));
                AppendEffective(canonical, "effective.OverlayCompletionHoldSeconds",
                    effectiveOptions.OverlayCompletionHoldSeconds.ToString());
                AppendEffective(canonical, "effective.PollIntervalSeconds",
                    effectiveOptions.PollIntervalSeconds.ToString());
                AppendEffective(canonical, "effective.MaxCommandsPerCheckIn",
                    effectiveOptions.MaxCommandsPerCheckIn.ToString());
                AppendEffective(canonical, "effective.ResumeOnStartup", Bool(effectiveOptions.ResumeOnStartup));
                AppendEffective(canonical, "effective.DryRun", Bool(effectiveOptions.DryRun));
            }

            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
            return new ConfigFingerprintResult(
                Prefix + Convert.ToHexString(hash).ToLowerInvariant(), Unavailable: false);
        }
        catch
        {
            // 읽기/파싱 실패는 조용히 확인 불가로 보고한다. 원문을 로그에 남기지 않는다.
            return ConfigFingerprintResult.Failed;
        }
    }

    private static void AppendEffective(StringBuilder canonical, string key, string? value)
    {
        if (!TryNormalizeText(value, out var normalized)) throw new FormatException();
        canonical.Append(key).Append('=').Append(normalized).Append('\n');
    }

    private static string Bool(bool value) => value ? "true" : "false";

    private static bool TryNormalizeText(string? value, out string normalized)
    {
        normalized = (value ?? "").Trim();
        return normalized.Length <= MaxValueLength && !SecretLike.IsMatch(normalized);
    }

    private static bool TryNormalizeAgentId(string agentId, out string normalized)
    {
        normalized = agentId.Trim();
        return normalized.Length > 0 &&
               normalized.Length <= MaxValueLength &&
               !SecretLike.IsMatch(normalized);
    }

    private static bool TryNormalize(JsonElement value, out string normalized)
    {
        normalized = "";
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                normalized = value.GetString() ?? "";
                break;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                normalized = value.GetRawText();
                break;
            default:
                // 배열/개체 원문은 설정 값으로 해시하지 않는다.
                return false;
        }

        normalized = normalized.Trim();
        return normalized.Length <= MaxValueLength && !SecretLike.IsMatch(normalized);
    }
}
