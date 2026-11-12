namespace UpdateAgent.Shared.Models;

/// <summary>단일 사전 점검 항목.</summary>
public sealed record PreflightCheck
{
    public required string Name { get; init; }
    public PreflightStatus Status { get; init; }
    public string Message { get; init; } = "";
    public bool BlocksJob => Status == PreflightStatus.Block;

    public static PreflightCheck Pass(string name, string message) =>
        new() { Name = name, Status = PreflightStatus.Pass, Message = message };

    public static PreflightCheck Warn(string name, string message) =>
        new() { Name = name, Status = PreflightStatus.Warn, Message = message };

    public static PreflightCheck Block(string name, string message) =>
        new() { Name = name, Status = PreflightStatus.Block, Message = message };
}

/// <summary>사전 점검 전체 결과.</summary>
public sealed record PreflightResult
{
    public IReadOnlyList<PreflightCheck> Checks { get; init; } = [];

    public bool CanStart => Checks.All(c => !c.BlocksJob);
    public IReadOnlyList<PreflightCheck> BlockingChecks => Checks.Where(c => c.BlocksJob).ToList();
    public IReadOnlyList<PreflightCheck> Warnings =>
        Checks.Where(c => c.Status == PreflightStatus.Warn).ToList();
}
