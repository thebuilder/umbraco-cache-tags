using System.Collections.Concurrent;
using Examine;
using Examine.Search;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TheBuilder.CacheTags.Collection;
using TheBuilder.CacheTags.Configuration;
using TheBuilder.CacheTags.Ledger;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Extensions;

namespace TheBuilder.CacheTags.Delivery;

/// <summary>
///     Watches this server's Delivery API index for the changes it acknowledged whose listings wait
///     for it. Once the index has every item of a change, it evicts the change's listing responses
///     from this server's output cache and records the change as indexed here.
/// </summary>
internal sealed class IndexWatcher(
    IExamineManager examine,
    IIdKeyMap idKeyMap,
    IOptionsMonitor<DeliveryApiSettings> deliveryApi,
    IAckTransport transport,
    ServerIdentity identity,
    PurgeResolver resolver,
    OutputCacheEviction eviction,
    IOptionsMonitor<CacheTagsOptions> options,
    TimeProvider time,
    ILogger<IndexWatcher> logger)
{
    /// <summary>The Delivery API content index (<c>Constants.UmbracoIndexes.DeliveryApiContentIndexName</c>).</summary>
    public const string IndexName = "DeliveryApiContentIndex";

    // Umbraco's indexing and the package's ack are both started by the same refresh, so a document
    // can be built a moment before the refresh is recorded here.
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(1);

    private readonly ConcurrentDictionary<Guid, Watched> _watched = new();

    public bool HasWatched => _watched.IsEmpty is false;

    /// <summary>Starts watching for a change this server just acknowledged, applied at <paramref name="appliedUtc" />.</summary>
    public void Watch(LedgerEntry entry, DateTime appliedUtc)
    {
        if (entry.ReadChange() is { } change && IndexCheck.WaitsFor(change.Kind, entry.Plan))
        {
            _watched.TryAdd(entry.Id, new Watched(entry.Id, change.Kind, change.Items, entry.Plan, appliedUtc - Slack, time.GetUtcNow().UtcDateTime));
        }
    }

    /// <summary>Records the watched changes the index now has.</summary>
    public async Task ProcessAsync(CancellationToken cancellationToken)
    {
        if (_watched.IsEmpty)
        {
            return;
        }

        DateTime now = time.GetUtcNow().UtcDateTime;
        TimeSpan timeout = options.CurrentValue.IndexTimeout;
        IIndex? index = examine.TryGetIndex(IndexName, out IIndex found) ? found : null;
        var indexed = new List<Watched>();
        foreach (Watched watched in _watched.Values)
        {
            if (now - watched.AddedUtc >= timeout)
            {
                // The releasing server has stopped waiting for this one by now.
                _watched.TryRemove(watched.ChangeId, out _);
                logger.LogWarning("Cache tags change {ChangeId} was not in this server's Delivery API index after {Timeout}.", watched.ChangeId, timeout);
            }
            else if (index is null || watched.Items.All(item => HasIndexed(index, watched, item)))
            {
                indexed.Add(watched);
            }
        }

        if (indexed.Count == 0)
        {
            return;
        }

        if (eviction.Enabled)
        {
            foreach (Watched watched in indexed)
            {
                await eviction.EvictAsync((await resolver.ResolveAsync(watched.Plan, cancellationToken)).Listings);
            }
        }

        await transport.IndexedAsync(identity.Name, indexed.Select(watched => watched.ChangeId).ToList(), now, cancellationToken);
        foreach (Watched watched in indexed)
        {
            _watched.TryRemove(watched.ChangeId, out _);
        }
    }

    private bool HasIndexed(IIndex index, Watched watched, ChangedItem item)
    {
        Attempt<int> id = idKeyMap.GetIdForKey(item.Key, UmbracoObjectTypes.Document);
        if (id.Success is false)
        {
            // Deleted since: nothing of it can be left to index.
            return true;
        }

        var documents = index.Searcher
            .CreateQuery()
            .Field("id", id.Result.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Execute(QueryOptions.SkipTake(0, 1000))
            .Select(result => new IndexedDocument(
                result.Values.TryGetValue("culture", out var culture) ? culture : "none",
                IndexStamp.Read(result.Values.TryGetValue(IndexStamp.FieldName, out var stamp) ? stamp : null)))
            .ToList();
        return IndexCheck.HasIndexed(watched.Kind, documents, watched.SinceUtc, deliveryApi.CurrentValue.IsAllowedContentType(item.TypeAlias));
    }

    private sealed record Watched(Guid ChangeId, ChangeKind Kind, IReadOnlyList<ChangedItem> Items, PurgePlan Plan, DateTime SinceUtc, DateTime AddedUtc);
}
