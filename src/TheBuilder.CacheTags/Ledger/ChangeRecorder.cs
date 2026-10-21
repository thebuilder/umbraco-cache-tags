using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TheBuilder.CacheTags.Configuration;
using TheBuilder.CacheTags.Delivery;

namespace TheBuilder.CacheTags.Ledger;

/// <summary>
///     Plans each change and records it in the ledger, on the server that made it. The delivery
///     worker sends the purge once every server has acknowledged the change.
/// </summary>
internal sealed class ChangeRecorder(
    PurgePlanner planner,
    LedgerStore ledger,
    ServerIdentity identity,
    DeliverySignal signal,
    IOptionsMonitor<CacheTagsOptions> options,
    ILogger<ChangeRecorder> logger)
{
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
        ledger.Add(change, plan, identity.Name);
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
}
