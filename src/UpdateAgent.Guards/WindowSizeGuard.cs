namespace UpdateAgent.Guards;

/// <summary>브라우저 앱 모드 창 크기에 적용하는 순수 clamping 규칙.</summary>
public static class WindowSizeGuard
{
    public const int DefaultWidth = 1320;
    public const int DefaultHeight = 820;
    public const int MinWidth = 640;
    public const int MinHeight = 480;
    public const int MaxWidth = 3840;
    public const int MaxHeight = 2160;

    /// <summary>NaN/Infinity/극단값을 안전한 범위로 정규화한다.</summary>
    public static (int Width, int Height) Normalize(double width, double height) =>
        (Clamp(width, DefaultWidth, MinWidth, MaxWidth),
         Clamp(height, DefaultHeight, MinHeight, MaxHeight));

    private static int Clamp(double value, int fallback, int min, int max)
    {
        if (!double.IsFinite(value)) return fallback;
        var integer = value is > int.MaxValue or < int.MinValue
            ? fallback
            : (int)value;
        return Math.Clamp(integer, min, max);
    }
}
