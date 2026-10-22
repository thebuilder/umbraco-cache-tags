using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TheBuilder.CacheTags.Configuration;
using TheBuilder.CacheTags.Ledger;
using TheBuilder.CacheTags.Storage;

namespace TheBuilder.CacheTags.Delivery;

/// <summary>
///     The loop every server runs: record the changes that failed to record before, acknowledge the
///     refreshes it applied, release the changes every
///     server has applied, and send the purges that are due. Leases make sure only one server
///     releases each change and sends each purge.
/// </summary>
internal sealed class DeliveryWorker(
    PackageDatabase database,
    ChangeRecorder recorder,
    AckProcessor acks,
    Releaser releaser,
    PurgeSender sender,
    DeliverySignal signal,
    IOptionsMonitor<CacheTagsOptions> options,
    ILogger<DeliveryWorker> logger) : BackgroundService
{
    /// <summary>How often an idle server looks for work it was not woken for (another server's purges, say).</summary>
    private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (stoppingToken.IsCancellationRequested is false)
        {
            CacheTagsOptions settings = options.CurrentValue;
            var busy = settings.Enabled && database.IsInstalled() && await RunOnceAsync(stoppingToken);

            // While there is work, look again soon; otherwise sleep until a change or refresh wakes us.
            await signal.WaitAsync(busy ? settings.PollInterval : IdleInterval, stoppingToken);
        }
    }

    /// <summary>One pass. Returns whether there is more work to do soon.</summary>
    private async Task<bool> RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            recorder.RecordUnrecorded();
            await acks.ProcessAsync(cancellationToken);
            var waiting = await releaser.ReleaseAsync(cancellationToken);
            var pending = await sender.SendAsync(cancellationToken);
            return recorder.HasUnrecorded || acks.HasQueued || waiting > 0 || pending > 0;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Cache tags delivery pass failed; it is tried again shortly.");
            return true;
        }
    }
}
