namespace UpdateAgent.Guards;

/// <summary>식별자와 감사 로그 텍스트에 적용하는 길이/문자 규칙.</summary>
public static class TextGuard
{
    /// <summary>에이전트 ID와 작업 ID의 최대 길이.</summary>
    public const int MaxIdentifierLength = 128;

    /// <summary>감사 로그 한 행에 저장하는 텍스트의 최대 길이.</summary>
    public const int MaxAuditTextLength = 512;

    /// <summary>컴퓨터 이름 등 감사 대상 라벨의 최대 길이.</summary>
    public const int MaxLabelLength = 128;

    /// <summary>영문/숫자/-/_로만 구성된 짧은 ID인지 확인한다.</summary>
    public static bool IsSafeIdentifier(string? value, int maxLength = MaxIdentifierLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength) return false;
        // char.IsLetterOrDigit는 한글/전각 숫자도 허용한다. ID는 명세대로 ASCII만 허용한다.
        return value.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z'
            or >= '0' and <= '9' or '-' or '_');
    }

    /// <summary>클라이언트가 보낸 텍스트를 감사 로그에 저장하기 전에 잘라낸다.</summary>
    public static string Truncate(string? value, int maxLength)
    {
        if (maxLength <= 0) return string.Empty;
        var text = value ?? string.Empty;
        return text.Length <= maxLength ? text : text[..maxLength];
    }
}
