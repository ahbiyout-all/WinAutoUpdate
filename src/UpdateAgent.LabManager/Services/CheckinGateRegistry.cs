using System.Collections.Concurrent;

namespace UpdateAgent.LabManager.Services;

/// <summary>
/// 같은 PC의 체크인을 직렬화하는 게이트를 개수 제한과 함께 관리한다.
/// 게이트 생성은 잠금 안에서 GetOrAdd로 수행해 동시 요청이 상한을 넘지 않게 한다.
/// 오래 사용하지 않은 게이트는 정리해 공유 토큰으로 ID를 무한 생성하는 것을 막는다.
/// </summary>
public sealed class CheckinGateRegistry
{
    private const int MaxAgents = 200;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);

    private readonly object _sync = new();
    private readonly ConcurrentDictionary<string, Gate> _gates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>게이트를 획득한다. 상한에 도달했으면 null(429)을 반환한다.</summary>
    public async Task<GateLease?> TryAcquireAsync(
        string agentId,
        CancellationToken cancellationToken = default)
    {
        Gate gate;
        lock (_sync)
        {
            RemoveIdleGates(DateTimeOffset.UtcNow);
            if (!_gates.ContainsKey(agentId) && _gates.Count >= MaxAgents)
            {
                return null;
            }

            // ContainsKey → GetOrAdd 사이에 다른 요청이 끼어들 수 없도록 잠금 안에서 추가한다.
            gate = _gates.GetOrAdd(agentId, static _ => new Gate());
            gate.ActiveCount++;
        }

        try
        {
            await gate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref gate.LastUsedTicks, DateTimeOffset.UtcNow.UtcTicks);
            return new GateLease(this, gate);
        }
        catch
        {
            Release(gate);
            throw;
        }
    }

    private void Release(Gate gate)
    {
        lock (_sync)
        {
            gate.ActiveCount--;
        }
        gate.Semaphore.Release();
    }

    private void RemoveIdleGates(DateTimeOffset now)
    {
        var idleBefore = now.UtcTicks - IdleTimeout.Ticks;
        foreach (var (agentId, gate) in _gates)
        {
            // 대기 중인 요청이 있으면 제거하지 않는다. 제거하면 같은 PC에 두 게이트가 생길 수 있다.
            if (gate.ActiveCount == 0 && Volatile.Read(ref gate.LastUsedTicks) < idleBefore)
            {
                _gates.TryRemove(new KeyValuePair<string, Gate>(agentId, gate));
            }
        }
    }

    internal sealed class Gate
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);

        public int ActiveCount;

        public long LastUsedTicks = DateTimeOffset.UtcNow.UtcTicks;
    }

    /// <summary>using 블록이 끝나면 게이트를 해제한다.</summary>
    public sealed class GateLease : IDisposable
    {
        private readonly CheckinGateRegistry _registry;
        private readonly Gate _gate;
        private bool _disposed;

        internal GateLease(CheckinGateRegistry registry, Gate gate)
        {
            _registry = registry;
            _gate = gate;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Volatile.Write(ref _gate.LastUsedTicks, DateTimeOffset.UtcNow.UtcTicks);
            _registry.Release(_gate);
        }
    }
}
