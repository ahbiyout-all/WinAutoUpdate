using UpdateAgent.Shared.Models;

namespace UpdateAgent.Core.Orchestration;

/// <summary>작업 진행 상황을 오버레이/원격/CLI로 전달한다.</summary>
public interface IJobProgressSink
{
    Task OnProgressAsync(JobDetail detail, CancellationToken cancellationToken = default);
}

public sealed class NullJobProgressSink : IJobProgressSink
{
    public static NullJobProgressSink Instance { get; } = new();

    public Task OnProgressAsync(JobDetail detail, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

public sealed class DelegateJobProgressSink : IJobProgressSink
{
    private readonly Func<JobDetail, CancellationToken, Task> _callback;

    public DelegateJobProgressSink(Func<JobDetail, CancellationToken, Task> callback)
    {
        _callback = callback;
    }

    public Task OnProgressAsync(JobDetail detail, CancellationToken cancellationToken = default) =>
        _callback(detail, cancellationToken);
}

/// <summary>여러 sink로 동시에 전달하되, 하나의 실패가 작업을 막지 않게 한다.</summary>
public sealed class CompositeJobProgressSink : IJobProgressSink
{
    private readonly IReadOnlyList<IJobProgressSink> _sinks;

    public CompositeJobProgressSink(params IJobProgressSink[] sinks)
    {
        _sinks = sinks;
    }

    public async Task OnProgressAsync(JobDetail detail, CancellationToken cancellationToken = default)
    {
        foreach (var sink in _sinks)
        {
            try
            {
                await sink.OnProgressAsync(detail, cancellationToken);
            }
            catch
            {
                // 진행 보고 실패는 작업을 중단시키지 않는다.
            }
        }
    }
}
