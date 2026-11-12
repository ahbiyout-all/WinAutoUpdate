using System.Collections.Concurrent;

namespace UpdateAgent.Core.Windows.Update;

/// <summary>
/// WUA COM은 STA를 요구한다. 모든 COM 호출을 전용 STA 스레드로 직렬화한다.
/// 한 번에 하나의 작업만 실행되므로 동시 호출로 인한 잠금 오류를 피할 수 있다.
/// </summary>
public sealed class StaExecutor : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private bool _disposed;

    public StaExecutor(string name = "UpdateAgent-WUA-STA")
    {
        _thread = new Thread(RunLoop)
        {
            IsBackground = true,
            Name = name,
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void RunLoop()
    {
        foreach (var work in _queue.GetConsumingEnumerable())
        {
            try
            {
                work();
            }
            catch
            {
                // 작업 자체가 예외를 tcs로 전달하므로 여기서는 무시한다.
            }
        }
    }

    public Task<T> InvokeAsync<T>(Func<T> func, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (cancellationToken.IsCancellationRequested)
        {
            tcs.TrySetCanceled(cancellationToken);
            return tcs.Task;
        }

        _queue.Add(() =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                tcs.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                tcs.TrySetResult(func());
            }
            catch (OperationCanceledException)
            {
                tcs.TrySetCanceled(cancellationToken);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        return tcs.Task;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _queue.CompleteAdding();
        }
        catch
        {
            // ignore
        }
    }
}
