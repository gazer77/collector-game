using System.Collections.Concurrent;

namespace Collector.Server.Game;

/// <summary>Serializes requests per player so concurrent tabs or retries can't interleave on one save.</summary>
public sealed class PlayerLocks
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public async Task<IDisposable> AcquireAsync(Guid playerId, CancellationToken cancellationToken)
    {
        var semaphore = _locks.GetOrAdd(playerId, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken);
        return new Releaser(semaphore);
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        public void Dispose() => semaphore.Release();
    }
}
