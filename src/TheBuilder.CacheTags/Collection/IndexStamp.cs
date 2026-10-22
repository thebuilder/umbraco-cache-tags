using System.Globalization;
using Umbraco.Cms.Core.DeliveryApi;
using Umbraco.Cms.Core.Models;

namespace TheBuilder.CacheTags.Collection;

/// <summary>
///     Stamps each document Umbraco builds for the Delivery API content index with when it was built,
///     so a server can tell whether its index has caught up with a change (<c>IndexWatcher</c>).
///     Umbraco builds a document from the database, so one built after a server applied a change has it.
/// </summary>
internal sealed class IndexStamp(TimeProvider time) : IContentIndexHandler
{
    public const string FieldName = "theBuilderCacheTagsBuiltUtc";

    public IEnumerable<IndexFieldValue> GetFieldValues(IContent content, string? culture)
        => [new IndexFieldValue { FieldName = FieldName, Values = [time.GetUtcNow().UtcTicks.ToString("D19", CultureInfo.InvariantCulture)] }];

    public IEnumerable<IndexField> GetFields()
        => [new IndexField { FieldName = FieldName, FieldType = FieldType.StringRaw, VariesByCulture = false }];

    /// <summary>A stamp read back from a document, or <c>null</c> when it has none (built before the package was installed).</summary>
    public static DateTime? Read(string? value)
        => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) ? new DateTime(ticks, DateTimeKind.Utc) : null;
}
