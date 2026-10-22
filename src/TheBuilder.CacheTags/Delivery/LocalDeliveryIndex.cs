using System.Globalization;
using Examine;
using Examine.Search;
using Microsoft.Extensions.Options;
using TheBuilder.CacheTags.Ledger;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Extensions;

namespace TheBuilder.CacheTags.Delivery;

/// <summary>This server's Delivery API content index, as listings query it.</summary>
internal sealed class LocalDeliveryIndex(IExamineManager examine, IContentService contentService, IOptionsMonitor<DeliveryApiSettings> deliveryApi)
{
    /// <summary>The Delivery API content index (<c>Constants.UmbracoIndexes.DeliveryApiContentIndexName</c>).</summary>
    public const string IndexName = "DeliveryApiContentIndex";

    /// <summary>The field <c>filter=contentType:</c> matches (Umbraco's internal <c>ContentTypeFilterIndexer.FieldName</c>).</summary>
    private const string ContentTypeField = "contentType";

    /// <summary>
    ///     Whether the index has every item of a change this server has applied: each item's documents
    ///     were built from its state in the database now (<see cref="IndexStamp" />), and no document is
    ///     left of a content type alias it renamed. Without the index (the Delivery API is off), nothing
    ///     can wait for it.
    /// </summary>
    public bool Has(Change change)
        => examine.TryGetIndex(IndexName, out IIndex index) is false
            || (change.Items.All(item => Has(index, item)) && (change.RenamedAliases ?? []).All(alias => Gone(index, alias)));

    private static bool Gone(IIndex index, string alias)
        => index.Searcher.CreateQuery().Field(ContentTypeField, alias).Execute(QueryOptions.SkipTake(0, 1)).TotalItemCount == 0;

    private bool Has(IIndex index, ChangedItem item)
    {
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
            .Field("id", content.Id.ToString(CultureInfo.InvariantCulture))
            .Execute(QueryOptions.SkipTake(0, 1000))
            .Select(result => new IndexedDocument(
                result.Values.TryGetValue("culture", out var culture) ? culture : "none",
                result.Values.TryGetValue(IndexStamp.FieldName, out var stamp) ? stamp : null))
            .ToList();
        return IndexCheck.HasIndexed(state, documents);
    }
}
