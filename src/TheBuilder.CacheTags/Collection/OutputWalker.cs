using System.Collections;
using System.Text.RegularExpressions;
using TheBuilder.CacheTags.Keys;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.DeliveryApi;

namespace TheBuilder.CacheTags.Collection;

/// <summary>
///     Collects the documents and media a Delivery API result shows, and the types of everything it
///     renders (documents, media and block elements), by walking the result objects.
///     Only the Delivery API's own model types are walked, so a custom value converter is covered when
///     it emits them. Block elements are walked into but not collected: they are not publish targets.
/// </summary>
internal static partial class OutputWalker
{
    private const int MaxDepth = 64;

    public static void Collect(object? value, ISet<string> keys) => Walk(value, keys, 0);

    private static void Walk(object? value, ISet<string> keys, int depth)
    {
        if (value is null || depth > MaxDepth)
        {
            return;
        }

        switch (value)
        {
            case string text:
                ScanMarkup(text, keys);
                return;
            case IApiContent content:
                keys.Add(CacheKeys.Content(content.Id));
                WalkProperties(content.Properties, keys, depth);
                return;
            case IApiMedia media:
                keys.Add(CacheKeys.Media(media.Id));
                WalkProperties(media.Properties, keys, depth);
                return;
            case IApiElement element:
                WalkProperties(element.Properties, keys, depth);
                return;
            case ApiLink link:
                if (link.DestinationId is { } destination)
                {
                    keys.Add(link.LinkType == LinkType.Media ? CacheKeys.Media(destination) : CacheKeys.Content(destination));
                }

                return;
            case ApiBlockListModel blockList:
                Walk(blockList.Items, keys, depth + 1);
                return;
            case ApiBlockGridModel blockGrid:
                Walk(blockGrid.Items, keys, depth + 1);
                return;
            case ApiBlockGridItem gridItem:
                Walk(gridItem.Content, keys, depth + 1);
                Walk(gridItem.Settings, keys, depth + 1);
                foreach (ApiBlockGridArea area in gridItem.Areas)
                {
                    Walk(area.Items, keys, depth + 1);
                }

                return;
            case ApiBlockItem block:
                Walk(block.Content, keys, depth + 1);
                Walk(block.Settings, keys, depth + 1);
                return;
            case RichTextModel richText:
                ScanMarkup(richText.Markup, keys);
                Walk(richText.Blocks, keys, depth + 1);
                return;
            case RichTextRootElement root:
                Walk(root.Elements, keys, depth + 1);
                Walk(root.Blocks, keys, depth + 1);
                return;
            case RichTextGenericElement element:
                CollectLinkAttributes(element.Attributes, keys);
                Walk(element.Elements, keys, depth + 1);
                return;
            case IDictionary<string, object?> dictionary:
                WalkProperties(dictionary, keys, depth);
                return;
            case IEnumerable sequence:
                foreach (var item in sequence)
                {
                    Walk(item, keys, depth + 1);
                }

                return;
        }

        // A paged listing (PagedViewModel<T>): walk its items.
        Type type = value.GetType();
        if (type.IsGenericType && type.GetProperty("Items")?.GetValue(value) is IEnumerable items)
        {
            Walk(items, keys, depth + 1);
        }
    }

    private static void WalkProperties(IDictionary<string, object?> properties, ISet<string> keys, int depth)
    {
        foreach (var property in properties.Values)
        {
            Walk(property, keys, depth + 1);
        }
    }

    // Rich text as JSON: a link element carries destinationId and linkType attributes.
    private static void CollectLinkAttributes(Dictionary<string, object> attributes, ISet<string> keys)
    {
        if (attributes.TryGetValue("destinationId", out var id) && Guid.TryParse(id as string, out Guid destination))
        {
            var media = attributes.TryGetValue("linkType", out var type) && type as string == nameof(LinkType.Media);
            keys.Add(media ? CacheKeys.Media(destination) : CacheKeys.Content(destination));
        }
    }

    // Rich text as markup: a link carries data-destination-id and data-link-type attributes.
    private static void ScanMarkup(string text, ISet<string> keys)
    {
        if (text.Contains("data-destination-id", StringComparison.Ordinal) is false)
        {
            return;
        }

        foreach (Match anchor in AnchorTag().Matches(text))
        {
            Match id = DestinationId().Match(anchor.Value);
            if (id.Success is false || Guid.TryParse(id.Groups["id"].Value, out Guid destination) is false)
            {
                continue;
            }

            Match type = DestinationLinkType().Match(anchor.Value);
            keys.Add(type.Success && type.Groups["type"].Value == nameof(LinkType.Media)
                ? CacheKeys.Media(destination)
                : CacheKeys.Content(destination));
        }
    }

    [GeneratedRegex("<a\\s[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex AnchorTag();

    [GeneratedRegex("data-destination-id=[\"'](?<id>[0-9a-fA-F-]{36})[\"']")]
    private static partial Regex DestinationId();

    [GeneratedRegex("data-link-type=[\"'](?<type>\\w+)[\"']")]
    private static partial Regex DestinationLinkType();
}
