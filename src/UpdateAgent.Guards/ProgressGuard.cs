namespace UpdateAgent.Guards;

/// <summary>작업 진행률 계산/표시에 적용하는 순수 clamp 규칙.</summary>
public static class ProgressGuard
{
    /// <summary>완료 상태는 100, 항목이 없으면 0, 그 외에는 0~99로 제한한다.</summary>
    public static int Percent(bool isCompleted, int completedItems, int totalItems)
    {
        if (isCompleted) return 100;
        if (totalItems <= 0 || completedItems <= 0) return 0;

        var percent = (int)Math.Round(completedItems * 100d / totalItems);
        return Math.Clamp(percent, 0, 99);
    }

    /// <summary>화면 표시용 진행률 clamp.</summary>
    public static int Display(int percent) => Math.Clamp(percent, 0, 100);
}
