using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TheBuilder.CacheTags.Delivery;

/// <summary>
///     This process, as the other servers see it. A restart is a new identity, and the new process
///     counts as having every change made before it started, since it built its caches after them.
/// </summary>
internal sealed class ServerIdentity(TimeProvider time)
{
    public string Name { get; } = $"{Environment.MachineName}/{Guid.NewGuid().ToString("N")[..8]}";

    public DateTime StartedUtc { get; } = time.GetUtcNow().UtcDateTime;

    /// <summary>
    ///     The running servers, with this one among them even before its first heartbeat is recorded:
    ///     a server must never release a change without waiting for itself.
    /// </summary>
    public IReadOnlyCollection<ServerPresence> Among(IEnumerable<ServerPresence> servers, DateTime nowUtc)
        => [.. servers.Where(server => server.Server != Name), new ServerPresence(Name, StartedUtc, nowUtc)];
}

/// <summary>Sends this server's heartbeat every 30 seconds, and leaves on a clean shutdown.</summary>
internal sealed class Heartbeat(IAckTransport transport, Storage.PackageDatabase database, ServerIdentity identity, IOptionsMonitor<Configuration.CacheTagsOptions> options, TimeProvider time, ILogger<Heartbeat> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // On a first boot the package's tables come a moment after this starts: beat as soon as they exist.
        while (database.IsInstalled() is false)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), time, stoppingToken);
        }

        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            if (options.CurrentValue.Enabled is false)
            {
                continue;
            }

            try
            {
                await transport.HeartbeatAsync(identity.Name, identity.StartedUtc, time.GetUtcNow().UtcDateTime, stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Cache tags heartbeat failed; the next one tries again.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        try
        {
            await transport.LeaveAsync(identity.Name, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Cache tags could not remove this server on shutdown; it ages out instead.");
        }
    }
}
