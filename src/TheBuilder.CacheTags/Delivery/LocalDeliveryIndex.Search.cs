#if NET11_0_OR_GREATER
using Microsoft.Extensions.Options;
using TheBuilder.CacheTags.Ledger;
using TheBuilder.CacheTags.Storage;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Search;
using Umbraco.Cms.Core.Search.Querying;
using Umbraco.Cms.Core.Search.Querying.Filtering;
using Umbraco.Cms.Core.Services;
using UmbracoSearch = Umbraco.Cms.Core.Constants.Search;

namespace TheBuilder.CacheTags.Delivery;

/// <summary>
///     This server's Delivery API content index, as listings query it: Umbraco Search's published
///     content index on Umbraco 19, which replaced Examine's <c>DeliveryApiContentIndex</c>. Queried
///     through <see cref="ISearcher" />, so it works with whichever search provider the site runs.
/// </summary>
internal sealed class LocalDeliveryIndex(
    ISearcherResolver searchers,
    IContentService contentService,
    ILanguageService languageService,
    PackageDatabase database,
    IOptionsMonitor<DeliveryApiSettings> deliveryApi)
{
    /// <summary>The index the Delivery API queries outside preview.</summary>
    public const string IndexAlias = UmbracoSearch.IndexAliases.PublishedContent;

    // Each batch is one filter of item keys, well under Lucene's 1024 clauses per query.
    private const int BatchSize = 200;

    /// <summary>
    ///     Whether the index has every item of a change this server has applied: each item's documents
    ///     were built from its state in the database now (<see cref="IndexStamp" />), and none is left of
    ///     an item it should not hold. A content type rename waits for nothing here: Umbraco Search
    ///     indexes a document's type by key, and the Delivery API turns a type filter's alias into that
    ///     key when it queries. Without the index, or with the Delivery API off, nothing can wait for it.
    /// </summary>
    public async Task<bool> HasAsync(Change change, CancellationToken cancellationToken)
    {
        if (change.Items.Count == 0 || deliveryApi.CurrentValue.Enabled is false || searchers.GetSearcher(IndexAlias) is not { } searcher)
        {
            return true;
        }

        // Runs on the delivery worker while editors may be saving (see PackageDatabase.ReadUmbraco).
        // Only the database reads hold the lock; the index is searched after.
        (Dictionary<Guid, string?> expected, string?[] cultures) = database.ReadUmbraco(() =>
        {
            var states = new Dictionary<Guid, string?>();
            foreach (ChangedItem item in change.Items)
            {
                states[item.Key] = IndexStamp.Expected(contentService.GetById(item.Key), contentService.GetById);
            }

            // Every culture rather than each item's own: a document can be left in a culture an item no longer has.
            string?[] isoCodes = [.. languageService.GetAllAsync().GetAwaiter().GetResult().Select(language => language.IsoCode)];
            return (states, isoCodes.Length == 0 ? [null] : isoCodes);
        });

        foreach (Dictionary<Guid, string?> batch in expected.Chunk(BatchSize).Select(chunk => chunk.ToDictionary()))
        {
            if (await HasAsync(searcher, batch, cultures, cancellationToken) is false)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    ///     Whether the index has a batch of items in the states <paramref name="expected" /> gives. A search
    ///     for one culture also finds the documents of items that do not vary by culture.
    /// </summary>
    internal static async Task<bool> HasAsync(ISearcher searcher, IReadOnlyDictionary<Guid, string?> expected, string?[] cultures, CancellationToken cancellationToken)
    {
        var absent = expected.Where(item => item.Value is null).Select(item => item.Key).ToArray();
        var present = expected.Where(item => item.Value is not null).ToArray();
        var states = present.Select(item => item.Value!).ToArray();
        var current = new HashSet<Guid>();
        long leftover = 0, stale = 0;
        foreach (var culture in cultures)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (absent.Length > 0)
            {
                leftover += (await SearchAsync(searcher, culture, [Ids(absent)], 1)).Total;
            }

            if (present.Length > 0)
            {
                Filter ids = Ids(present.Select(item => item.Key));
                stale += (await SearchAsync(searcher, culture, [ids, new KeywordFilter(IndexStamp.FieldName, states, Negate: true)], 1)).Total;
                SearchResult built = await SearchAsync(searcher, culture, [ids, new KeywordFilter(IndexStamp.FieldName, states, Negate: false)], present.Length);
                current.UnionWith(built.Documents.Select(document => document.Id));
            }

            if (leftover > 0 || stale > 0)
            {
                return false;
            }
        }

        return IndexCheck.HasIndexed(expected, leftover, stale, current);
    }

    private static Filter Ids(IEnumerable<Guid> keys)
        => new KeywordFilter(UmbracoSearch.FieldNames.Id, [.. keys.Select(key => key.ToString("D"))], Negate: false);

    // Protected content is indexed too, and listings for members find it, so the check sees past protection.
    // Counts take one document: Umbraco's Examine provider reports a total of 0 for a search that takes none.
    private static Task<SearchResult> SearchAsync(ISearcher searcher, string? culture, Filter[] filters, int take)
        => searcher.SearchAsync(IndexAlias, filters: filters, culture: culture, accessContext: AccessContext.BypassProtection(), take: take);
}
#endif
