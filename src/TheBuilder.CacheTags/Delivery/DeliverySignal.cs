namespace TheBuilder.CacheTags.Delivery;

/// <summary>Wakes the delivery worker when there is work: a change was recorded, or a cache refresh observed.</summary>
internal sealed class DeliverySignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Wake()
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

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => _signal.WaitAsync(timeout, cancellationToken);
}
