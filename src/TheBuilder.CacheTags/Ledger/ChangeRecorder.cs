using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TheBuilder.CacheTags.Configuration;
using TheBuilder.CacheTags.Delivery;

namespace TheBuilder.CacheTags.Ledger;

/// <summary>
///     Plans each change and records it in the ledger, on the server that made it. The delivery
///     worker sends the purge once every server has acknowledged the change. A change that cannot be
///     recorded (the database is busy, say) is kept in memory and recorded again by the worker, so
///     its purge comes late rather than never.
/// </summary>
internal sealed class ChangeRecorder(
    PurgePlanner planner,
    LedgerStore ledger,
    ServerIdentity identity,
    DeliverySignal signal,
    IOptionsMonitor<CacheTagsOptions> options,
    ILogger<ChangeRecorder> logger)
{
    private readonly ConcurrentQueue<(Change Change, PurgePlan Plan)> _unrecorded = new();

    /// <summary>Whether some changes are waiting to be recorded again.</summary>
    public bool HasUnrecorded => _unrecorded.IsEmpty is false;

    public void Record(Change change)
    {
        CacheTagsOptions settings = options.CurrentValue;
        if (settings.Enabled is false)
        {
            return;
        }

        PurgePlan plan = planner.Plan(change, settings.MaxDescendants);

        // This server applied the change's cache refresh before this handler ran, so recording it as
        // the change's server also counts as its acknowledgement.
        try
        {
            ledger.Add(change, plan, identity.Name);
        }
        catch (Exception exception)
        {
            _unrecorded.Enqueue((change, plan));
            logger.LogWarning(exception, "Cache tags change {ChangeId} could not be recorded; it is tried again shortly.", change.Id);
        }

        signal.Wake();

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Cache tags change {ChangeId} ({Kind}) plans {Plan}.",
                change.Id,
                change.Kind,
                plan.Everything ? "every response" : string.Join(", ", plan.Keys));
        }
    }

    /// <summary>Records the changes that failed before, in order, until one fails again.</summary>
    public void RecordUnrecorded()
    {
        while (_unrecorded.TryPeek(out var unrecorded))
        {
            ledger.Add(unrecorded.Change, unrecorded.Plan, identity.Name);
            _unrecorded.TryDequeue(out _);
        }
    }
}
