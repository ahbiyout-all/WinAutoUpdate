namespace UpdateAgent.LabManager.Live;

/// <summary>
/// 에이전트 체크인/작업 변경을 실시간 모니터 연결에 알리는 인메모리 버스.
/// 구독자마다 현재 대기 작업을 공유하므로 한 신호가 모든 연결에 전달된다.
/// </summary>
public sealed class LiveEventBus
{
    private readonly object _gate = new();
    private long _version;
    private TaskCompletionSource _changed = CreateCompletionSource();

    public long Version
    {
        get { lock (_gate) return _version; }
    }

    public void Signal()
    {
        TaskCompletionSource pending;
        lock (_gate)
        {
            _version++;
            pending = _changed;
            _changed = CreateCompletionSource();
        }

        pending.TrySetResult();
    }

    /// <summary>지정한 버전 이후 변경이 없으면 다음 신호(또는 취소)까지 기다린다.</summary>
    public Task WaitAsync(long sinceVersion, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return _version != sinceVersion
                ? Task.CompletedTask
                : _changed.Task.WaitAsync(cancellationToken);
        }
    }

    private static TaskCompletionSource CreateCompletionSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
