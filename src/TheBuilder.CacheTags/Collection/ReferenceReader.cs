using TheBuilder.CacheTags.Keys;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Extensions;

namespace TheBuilder.CacheTags.Collection;

/// <summary>
///     The documents and media an item's published values reference, found with the reference
///     factories behind Umbraco's relations. Source values are never cached away the way converted
///     Delivery API values are, so this sees pickers, blocks and rich text on every request.
/// </summary>
internal sealed class ReferenceReader(PropertyEditorCollection editors, DataValueReferenceFactoryCollection factories)
{
    /// <param name="item">The published item.</param>
    /// <param name="culture">The request culture. A culture-variant property contributes this culture's value only.</param>
    /// <param name="onlyProperties">Read only these properties, when given.</param>
    public IEnumerable<ReferencedKey> Read(IPublishedContent item, string? culture, Func<string, bool>? onlyProperties = null)
    {
        foreach (IPublishedProperty property in item.Properties)
        {
            if ((onlyProperties is not null && onlyProperties(property.Alias) is false)
                || editors.TryGet(property.PropertyType.EditorAlias, out IDataEditor? editor) is false)
            {
                continue;
            }

            var value = property.PropertyType.VariesByCulture()
                ? property.GetSourceValue(culture)
                : property.GetSourceValue();
            if (value is null)
            {
                continue;
            }

            foreach (var reference in factories.GetReferences(editor, value))
            {
                if (reference.Udi is not GuidUdi udi)
                {
                    continue;
                }

                if (udi.EntityType == Umbraco.Cms.Core.Constants.UdiEntityType.Document)
                {
                    yield return new ReferencedKey(CacheKeys.Content(udi.Guid), udi.Guid, property.Alias, IsMedia: false);
                }
                else if (udi.EntityType == Umbraco.Cms.Core.Constants.UdiEntityType.Media)
                {
                    yield return new ReferencedKey(CacheKeys.Media(udi.Guid), udi.Guid, property.Alias, IsMedia: true);
                }
            }
        }
    }
}

/// <summary>A referenced document or media item, and the property that references it.</summary>
internal sealed record ReferencedKey(string Key, Guid Id, string PropertyAlias, bool IsMedia);
