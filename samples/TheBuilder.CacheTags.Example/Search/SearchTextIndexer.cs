using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Umbraco.Cms.Core.DeliveryApi;
using Umbraco.Cms.Core.Models;
using Umbraco.Extensions;
using EditorAliases = Umbraco.Cms.Core.Constants.PropertyEditors.Aliases;

namespace TheBuilder.CacheTags.Example.Search;

/// <summary>
///     Puts each item's published text (its name, text fields and rich text) into the Delivery API
///     content index as analyzed text, per culture, for <see cref="SearchFilter" />. The Delivery API
///     index is an Examine (Lucene) index, so this is full-text search without a second index, and
///     search results stay Delivery API responses that TheBuilder.CacheTags tags and purges.
/// </summary>
internal sealed partial class SearchTextIndexer : IContentIndexHandler
{
    public const string FieldName = "searchText";

    private static readonly HashSet<string> TextEditors = new(StringComparer.Ordinal)
    {
        EditorAliases.TextBox,
        EditorAliases.TextArea,
        EditorAliases.RichText,
    };

    public IEnumerable<IndexFieldValue> GetFieldValues(IContent content, string? culture)
    {
        var texts = new List<string> { content.GetPublishName(culture) ?? content.GetCultureName(culture) ?? content.Name ?? string.Empty };
        foreach (IProperty property in content.Properties.Where(property => TextEditors.Contains(property.PropertyType.PropertyEditorAlias)))
        {
            // Published values only: a draft's text must not be findable.
            if (property.GetValue(property.PropertyType.VariesByCulture() ? culture : null, published: true) is string { Length: > 0 } value)
            {
                texts.Add(property.PropertyType.PropertyEditorAlias == EditorAliases.RichText ? PlainText(value) : value);
            }
        }

        return [new IndexFieldValue { FieldName = FieldName, Values = [string.Join(' ', texts)] }];
    }

    public IEnumerable<IndexField> GetFields()
        => [new IndexField { FieldName = FieldName, FieldType = FieldType.StringAnalyzed, VariesByCulture = true }];

    // Rich text is stored as JSON with the markup in "markup" (and as plain markup before Umbraco 14).
    private static string PlainText(string value)
    {
        var markup = value;
        if (value.TrimStart().StartsWith('{'))
        {
            try
            {
                using var json = JsonDocument.Parse(value);
                markup = json.RootElement.TryGetProperty("markup", out JsonElement found) ? found.GetString() ?? string.Empty : string.Empty;
            }
            catch (JsonException)
            {
            }
        }

        return WebUtility.HtmlDecode(Tags().Replace(markup, " "));
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();
}
