using System.Collections.Concurrent;
using Examine;
using Examine.Search;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TheBuilder.CacheTags.Collection;
using TheBuilder.CacheTags.Configuration;
using TheBuilder.CacheTags.Ledger;
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
    IContentService contentService,
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

    private readonly ConcurrentDictionary<Guid, Watched> _watched = new();

    public bool HasWatched => _watched.IsEmpty is false;

    /// <summary>Starts watching for a change this server just acknowledged.</summary>
    public void Watch(LedgerEntry entry)
    {
        if (entry.ReadChange() is { } change && IndexCheck.WaitsFor(change.Kind, entry.Plan))
        {
            _watched.TryAdd(entry.Id, new Watched(entry.Id, change.Items.ToList(), entry.Plan, time.GetUtcNow().UtcDateTime));
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
            else
            {
                // An item stays indexed once it matched: a later edit is a change of its own, watched on its own.
                watched.Pending.RemoveAll(item => index is null || HasIndexed(index, item));
                if (watched.Pending.Count == 0)
                {
                    indexed.Add(watched);
                }
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

    private bool HasIndexed(IIndex index, ChangedItem item)
    {
        // This server applied the change before watching for it, so the database has it.
        IContent? content = contentService.GetById(item.Key);
        if (content is null)
        {
            // Deleted: without its id there is nothing to look up, and Umbraco removes its documents.
            return true;
        }

        // A trashed item, or one of a type the index skips, has no documents.
        var state = content.Trashed || deliveryApi.CurrentValue.IsAllowedContentType(content.ContentType.Alias) is false
            ? null
            : IndexStamp.For(content);

        var documents = index.Searcher
            .CreateQuery()
            .Field("id", content.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Execute(QueryOptions.SkipTake(0, 1000))
            .Select(result => new IndexedDocument(
                result.Values.TryGetValue("culture", out var culture) ? culture : "none",
                result.Values.TryGetValue(IndexStamp.FieldName, out var stamp) ? stamp : null))
            .ToList();
        return IndexCheck.HasIndexed(state, documents);
    }

    private sealed record Watched(Guid ChangeId, List<ChangedItem> Pending, PurgePlan Plan, DateTime AddedUtc);
}
