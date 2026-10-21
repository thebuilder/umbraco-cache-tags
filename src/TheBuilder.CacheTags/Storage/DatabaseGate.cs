namespace TheBuilder.CacheTags.Storage;

/// <summary>
///     Lets one of the package's background jobs use the database at a time in this process (the
///     mapping writer, the heartbeat, the delivery worker, the prune job). On SQLite, Umbraco's
///     default for small sites, concurrent transactions that each touch several tables can block
///     each other until they time out; one at a time, they cannot.
/// </summary>
internal sealed class DatabaseGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<T> RunAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await work();
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task RunAsync(Func<Task> work, CancellationToken cancellationToken = default)
        => RunAsync(async () =>
        {
            await work();
            return true;
        }, cancellationToken);
}

/// <summary>Wakes the delivery worker when there is work: a change was recorded, or a cache refresh observed.</summary>
internal sealed class DeliverySignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Wake()
    {
        if (_signal.CurrentCount == 0)
        {
            try
            {
                _signal.Release();
            }
            catch (SemaphoreFullException)
            {
                // Already woken.
            }
        }
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => _signal.WaitAsync(timeout, cancellationToken);
}
