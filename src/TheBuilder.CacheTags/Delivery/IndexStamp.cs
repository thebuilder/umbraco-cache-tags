using System.Globalization;
#if NET11_0_OR_GREATER
using System.Security.Cryptography;
using System.Text;
#endif
using Umbraco.Cms.Core.DeliveryApi;
using Umbraco.Cms.Core.Models;
#if NET11_0_OR_GREATER
using Umbraco.Extensions;
#endif

namespace TheBuilder.CacheTags.Delivery;

/// <summary>
///     Stamps each document Umbraco builds for the Delivery API content index with the state of the
///     item it was built from, so a server can tell whether its index has caught up with a change
///     (<see cref="LocalDeliveryIndex" />): when the stamp matches the item's state in the database now, the index
///     has every change made to it so far. On Umbraco 19 the Delivery API passes its index handlers on to
///     Umbraco Search, so the stamp lands in every culture's document of its content indexes.
/// </summary>
internal sealed class IndexStamp : IContentIndexHandler
{
    public const string FieldName = "theBuilderCacheTagsState";

    public IEnumerable<IndexFieldValue> GetFieldValues(IContent content, string? culture)
        => [new IndexFieldValue { FieldName = FieldName, Values = [For(content)] }];

    public IEnumerable<IndexField> GetFields()
        => [new IndexField { FieldName = FieldName, FieldType = FieldType.StringRaw, VariesByCulture = false }];

#if NET11_0_OR_GREATER
    /// <summary>
    ///     What Umbraco Search's published content index records about an item, for listings: which
    ///     version is published in which cultures, where it is, and its order. A publish, unpublish,
    ///     move or sort changes it; a draft save does not, because that index only holds published
    ///     content and is not updated for one. Hashed into one token, so it reads back the same from a
    ///     search provider that analyzes keyword fields.
    /// </summary>
    public static string For(IContent content)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(
            '|',
            content.PublishedVersionId.ToString(CultureInfo.InvariantCulture),
            string.Join(',', content.PublishedCultures.Order(StringComparer.OrdinalIgnoreCase)),
            content.Path,
            content.SortOrder.ToString(CultureInfo.InvariantCulture)))))[..32];

    /// <summary>
    ///     The state the published content index should hold an item in, or <c>null</c> when it should
    ///     hold no document of it. Umbraco Search indexes a document there only where it can be routed:
    ///     it is published, not trashed, and every ancestor is published, in a culture they share when
    ///     both vary by culture. Unlike Umbraco 17 and 18's Delivery API index, it also holds the content
    ///     types the Delivery API is configured to skip.
    /// </summary>
    public static string? Expected(IContent? content, Func<int, IContent?> ancestor)
    {
        if (content is null || content.Trashed || content.Published is false)
        {
            return null;
        }

        var variant = content.ContentType.VariesByCulture();
        var cultures = new HashSet<string>(content.PublishedCultures, StringComparer.OrdinalIgnoreCase);
        foreach (var id in AncestorIds(content.Path))
        {
            IContent? parent = ancestor(id);
            if (parent is null || parent.Published is false)
            {
                return null;
            }

            if (variant && parent.ContentType.VariesByCulture())
            {
                cultures.IntersectWith(parent.PublishedCultures);
            }

            if (variant && cultures.Count == 0)
            {
                return null;
            }
        }

        return For(content);
    }

    // The path is "-1,<root id>,...,<own id>": the ids between the tree root and the item itself.
    private static IEnumerable<int> AncestorIds(string path)
        => path.Split(',').Skip(1).SkipLast(1).Select(id => int.Parse(id, CultureInfo.InvariantCulture));
#else
    /// <summary>
    ///     What the index records about an item, for listings: whether and which version is published
    ///     in which cultures, where it is, and its order. A save, publish, unpublish, move or sort
    ///     changes it.
    /// </summary>
    public static string For(IContent content)
        => string.Join(
            '|',
            content.Published ? "published" : "draft",
            content.VersionId.ToString(CultureInfo.InvariantCulture),
            content.PublishedVersionId.ToString(CultureInfo.InvariantCulture),
            string.Join(',', content.PublishedCultures.Order(StringComparer.OrdinalIgnoreCase)),
            content.Path,
            content.SortOrder.ToString(CultureInfo.InvariantCulture));
#endif
}
