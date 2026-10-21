using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TheBuilder.CacheTags.Delivery;

/// <summary>
///     This process, as the other servers see it. A restart is a new identity, and the new process
///     counts as having every change made before it started, since it built its caches after them.
/// </summary>
internal sealed class ServerIdentity(TimeProvider time)
{
    public string Name { get; } = $"{Environment.MachineName}/{Guid.NewGuid().ToString("N")[..8]}";

    public DateTime StartedUtc { get; } = time.GetUtcNow().UtcDateTime;
}

/// <summary>Sends this server's heartbeat every 30 seconds, and leaves on a clean shutdown.</summary>
internal sealed class Heartbeat(IAckTransport transport, ServerIdentity identity, Storage.DatabaseGate gate, TimeProvider time, ILogger<Heartbeat> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            try
            {
                await gate.RunAsync(() => transport.HeartbeatAsync(identity.Name, identity.StartedUtc, time.GetUtcNow().UtcDateTime, stoppingToken), stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Usually the package's tables are not installed yet; the next beat tries again.
                logger.LogDebug(exception, "Cache tags heartbeat failed.");
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
