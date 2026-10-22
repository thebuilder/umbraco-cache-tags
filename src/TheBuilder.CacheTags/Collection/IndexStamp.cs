using System.Globalization;
using Umbraco.Cms.Core.DeliveryApi;
using Umbraco.Cms.Core.Models;

namespace TheBuilder.CacheTags.Collection;

/// <summary>
///     Stamps each document Umbraco builds for the Delivery API content index with the state of the
///     item it was built from, so a server can tell whether its index has caught up with a change
///     (<c>IndexWatcher</c>): when the stamp matches the item's state in the database now, the index
///     has every change made to it so far.
/// </summary>
internal sealed class IndexStamp : IContentIndexHandler
{
    public const string FieldName = "theBuilderCacheTagsState";

    public IEnumerable<IndexFieldValue> GetFieldValues(IContent content, string? culture)
        => [new IndexFieldValue { FieldName = FieldName, Values = [For(content)] }];

    public IEnumerable<IndexField> GetFields()
        => [new IndexField { FieldName = FieldName, FieldType = FieldType.StringRaw, VariesByCulture = false }];

    /// <summary>
    ///     What the index records about an item, for listings: which version is published in which
    ///     cultures, where it is, and its order. A save, publish, unpublish, move or sort changes it.
    /// </summary>
    public static string For(IContent content)
        => string.Join(
            '|',
            content.VersionId.ToString(CultureInfo.InvariantCulture),
            content.PublishedVersionId.ToString(CultureInfo.InvariantCulture),
            string.Join(',', content.PublishedCultures.Order(StringComparer.OrdinalIgnoreCase)),
            content.Path,
            content.SortOrder.ToString(CultureInfo.InvariantCulture));
}
