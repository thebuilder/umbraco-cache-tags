using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models.DeliveryApi;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Extensions;

namespace TheBuilder.CacheTags.Spike.Probe;

// Strategy B, "objects": walk the action result's object graph after the controller ran.
// Strategy C, "json": walk the serialized response body.
// Both read what the response actually contains, so Umbraco's property value cache cannot hide
// items from them.

internal static partial class MarkupScanner
{
    [GeneratedRegex("<a\\s[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex AnchorTag();

    [GeneratedRegex("data-destination-id=\"(?<id>[0-9a-fA-F-]{36})\"")]
    private static partial Regex DestinationId();

    [GeneratedRegex("data-link-type=\"(?<type>\\w+)\"")]
    private static partial Regex LinkType();

    public static void Scan(string text, HashSet<string> into)
    {
        if (text.Contains("data-destination-id", StringComparison.Ordinal) is false)
        {
            return;
        }

        foreach (Match tag in AnchorTag().Matches(text))
        {
            Match id = DestinationId().Match(tag.Value);
            if (id.Success is false)
            {
                continue;
            }

            if (Guid.TryParse(id.Groups["id"].Value, out Guid key) is false)
            {
                continue;
            }

            Match type = LinkType().Match(tag.Value);
            var prefix = type.Success && type.Groups["type"].Value == "Media" ? "m:" : "c:";
            into.Add(prefix + key);
        }
    }
}

internal static class ObjectWalker
{
    public static void Walk(object? value, HashSet<string> into)
        => Walk(value, into, new HashSet<object>(ReferenceEqualityComparer.Instance), 0);

    private static void Walk(object? value, HashSet<string> into, HashSet<object> seen, int depth)
    {
        if (value is null || depth > 64)
        {
            return;
        }

        if (value is string text)
        {
            MarkupScanner.Scan(text, into);
            return;
        }

        Type type = value.GetType();
        if (type.IsPrimitive || type.IsEnum || value is Guid or DateTime or DateTimeOffset or decimal or Uri || seen.Add(value) is false)
        {
            return;
        }

        switch (value)
        {
            case IApiContent content:
                into.Add("c:" + content.Id);
                break;
            case IApiMedia media:
                into.Add("m:" + media.Id);
                break;
            case IApiElement element:
                into.Add("e:" + element.Id);
                break;
            case ApiLink { DestinationId: { } destinationId } link:
                into.Add((link.LinkType == Umbraco.Cms.Core.Models.LinkType.Media ? "m:" : "c:") + destinationId);
                break;
        }

        if (value is IDictionary dictionary)
        {
            // Rich text elements (JSON output) carry links as attributes.
            if (dictionary.Contains("destinationId") && dictionary["destinationId"] is string attributeId && Guid.TryParse(attributeId, out Guid parsed))
            {
                into.Add((dictionary.Contains("linkType") && dictionary["linkType"] as string == "Media" ? "m:" : "c:") + parsed);
            }

            foreach (DictionaryEntry entry in dictionary)
            {
                Walk(entry.Value, into, seen, depth + 1);
            }

            return;
        }

        if (value is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                Walk(item, into, seen, depth + 1);
            }

            return;
        }

        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            object? child;
            try
            {
                child = property.GetValue(value);
            }
            catch
            {
                continue;
            }

            Walk(child, into, seen, depth + 1);
        }
    }
}

internal static class JsonWalker
{
    public static void Walk(JsonElement element, HashSet<string> into)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                Classify(element, into);
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    Walk(property.Value, into);
                }

                break;
            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    Walk(item, into);
                }

                break;
            case JsonValueKind.String:
                MarkupScanner.Scan(element.GetString() ?? string.Empty, into);
                break;
        }
    }

    private static void Classify(JsonElement element, HashSet<string> into)
    {
        if (element.TryGetProperty("destinationId", out JsonElement destination) && destination.TryGetGuid(out Guid destinationId))
        {
            var media = element.TryGetProperty("linkType", out JsonElement linkType) && linkType.ValueKind == JsonValueKind.String && linkType.GetString() == "Media";
            into.Add((media ? "m:" : "c:") + destinationId);
        }

        if (element.TryGetProperty("id", out JsonElement idElement) is false || idElement.TryGetGuid(out Guid id) is false)
        {
            return;
        }

        if (element.TryGetProperty("route", out _))
        {
            into.Add("c:" + id);
        }
        else if (element.TryGetProperty("mediaType", out _))
        {
            into.Add("m:" + id);
        }
        else if (element.TryGetProperty("contentType", out _))
        {
            into.Add("e:" + id);
        }
    }
}

// Strategy D, "refs": the references Umbraco's own relation tracking finds in the published
// source values of the resolved items (depth 1), and of those referenced items (depth 2).
// With a culture, a culture-variant property contributes that culture's value only ("refsCulture");
// without one, every culture's value ("refs").
internal static class ReferenceWalker
{
    public static IEnumerable<string> References(IPublishedContent item, PropertyEditorCollection editors, DataValueReferenceFactoryCollection factories, string? culture = null)
    {
        foreach (IPublishedProperty property in item.Properties)
        {
            if (editors.TryGet(property.PropertyType.EditorAlias, out IDataEditor? editor) is false)
            {
                continue;
            }

            var values = new List<object?>();
            if (property.PropertyType.VariesByCulture() is false)
            {
                values.Add(property.GetSourceValue());
            }
            else if (culture is not null)
            {
                values.Add(property.GetSourceValue(culture));
            }
            else
            {
                values.AddRange(item.Cultures.Keys.Where(c => c.Length > 0).Select(c => property.GetSourceValue(c)));
            }

            foreach (var reference in factories.GetReferences(editor, values))
            {
                if (reference.Udi is GuidUdi udi)
                {
                    yield return udi.EntityType switch
                    {
                        Constants.UdiEntityType.Document => "c:" + udi.Guid,
                        Constants.UdiEntityType.Media => "m:" + udi.Guid,
                        _ => udi.EntityType + ":" + udi.Guid,
                    };
                }
            }
        }
    }
}
