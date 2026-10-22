using System.Text.Json;
using Umbraco.Cms.Core;

namespace TheBuilder.CacheTags.Example.Seeding;

/// <summary>A block's content: its element type and invariant property values.</summary>
internal sealed record Block(Guid Key, Guid ContentTypeKey, IReadOnlyDictionary<string, object?> Values);

/// <summary>A block in a block grid, with the blocks in each of its areas.</summary>
internal sealed record GridItem(Block Block, int ColumnSpan, IReadOnlyDictionary<Guid, GridItem[]>? Areas = null);

/// <summary>A media pick's crop, as fractions cut from each edge.</summary>
internal sealed record Crop(string Alias, int Width, int Height, double X1, double Y1, double X2, double Y2);

/// <summary>
///     Property values in the format Umbraco 17 stores them, which is what the backoffice saves. A value
///     in the wrong shape is not an error: the Delivery API just leaves it out, so check the output.
/// </summary>
internal static class SeedValues
{
    /// <summary>Block keys are fixed too: <c>e2000000-0000-0000-0000-&lt;n&gt;</c>.</summary>
    public static Guid BlockKey(int n) => new($"e2000000-0000-0000-0000-{n:x12}");

    public static string Udi(Guid key) => new GuidUdi(Constants.UdiEntityType.Document, key).ToString();

    public static string Udis(params Guid[] keys) => string.Join(',', keys.Select(Udi));

    public static string MediaUdi(Guid key) => new GuidUdi(Constants.UdiEntityType.Media, key).ToString();

    /// <summary>A media picker value with one image.</summary>
    public static string Image(Guid mediaKey) => Media((mediaKey, Constants.Conventions.MediaTypes.Image, []));

    /// <summary>A media picker value; each pick may carry crops of its own.</summary>
    public static string Media(params (Guid MediaKey, string MediaTypeAlias, Crop[] Crops)[] picks) =>
        JsonSerializer.Serialize(picks.Select((pick, index) => new
        {
            // Stable pick keys, so the stored value is the same on every machine.
            key = new Guid($"{pick.MediaKey.ToString()[..^4]}{index:x4}"),
            mediaKey = pick.MediaKey,
            mediaTypeAlias = pick.MediaTypeAlias,
            crops = pick.Crops.Select(crop => new
            {
                alias = crop.Alias,
                width = crop.Width,
                height = crop.Height,
                coordinates = new { x1 = crop.X1, y1 = crop.Y1, x2 = crop.X2, y2 = crop.Y2 },
            }),
            focalPoint = pick.Crops.Length > 0 ? new { left = 0.5, top = 0.4 } : null,
        }));

    /// <summary>Tags or a dropdown: a JSON array of strings.</summary>
    public static string Strings(params string[] values) => JsonSerializer.Serialize(values);

    public static string RichText(string markup) => JsonSerializer.Serialize(new { markup, blocks = (object?)null });

    /// <summary>
    ///     Rich text with blocks. The markup holds an <c>umb-rte-block</c> placeholder per block, where it
    ///     sits in the text.
    /// </summary>
    public static string RichText(string markup, params Block[] blocks) => JsonSerializer.Serialize(new
    {
        markup,
        blocks = BlockValue("Umbraco.RichText", blocks.Select(block => (object)new { contentKey = block.Key }), blocks),
    });

    public static string RichTextBlock(Block block) => $"<umb-rte-block data-content-key=\"{block.Key}\"></umb-rte-block>";

    public static string BlockList(params Block[] blocks) => JsonSerializer.Serialize(BlockListValue(blocks));

    /// <summary>A block list value as an object, for a block list inside a block.</summary>
    public static object BlockListValue(params Block[] blocks) =>
        BlockValue("Umbraco.BlockList", blocks.Select(block => (object)new { contentKey = block.Key }), blocks);

    public static string BlockGrid(params GridItem[] items)
    {
        object Layout(GridItem item) => new
        {
            contentKey = item.Block.Key,
            columnSpan = item.ColumnSpan,
            rowSpan = 1,
            areas = (item.Areas ?? new Dictionary<Guid, GridItem[]>())
                .Select(area => new { key = area.Key, items = area.Value.Select(Layout) }),
        };

        IEnumerable<Block> All(GridItem item) =>
            (item.Areas ?? new Dictionary<Guid, GridItem[]>()).Values.SelectMany(area => area).SelectMany(All).Prepend(item.Block);

        return JsonSerializer.Serialize(BlockValue("Umbraco.BlockGrid", items.Select(Layout), items.SelectMany(All).ToArray()));
    }

    /// <summary>A multi URL picker value: documents, media and external links.</summary>
    public static string Links(params Link[] links) => JsonSerializer.Serialize(links.Select(link => new
    {
        name = link.Name,
        target = (string?)null,
        type = link.EntityType ?? "external",
        unique = link.Key,
        udi = link.Key is { } key ? new GuidUdi(link.EntityType!, key).ToString() : null,
        url = link.Url,
        queryString = link.QueryString,
    }));

    // Every block needs an expose entry, or the Delivery API leaves it out.
    private static object BlockValue(string layoutAlias, IEnumerable<object> layout, Block[] blocks) => new
    {
        layout = new Dictionary<string, object> { [layoutAlias] = layout },
        contentData = blocks.Select(block => new
        {
            contentTypeKey = block.ContentTypeKey,
            key = block.Key,
            values = block.Values.Select(value => new { alias = value.Key, culture = (string?)null, segment = (string?)null, value = value.Value }),
        }),
        settingsData = Array.Empty<object>(),
        expose = blocks.Select(block => new { contentKey = block.Key, culture = (string?)null, segment = (string?)null }),
    };
}

/// <summary>A multi URL picker link: a document or media item by key, or an external URL.</summary>
internal sealed record Link(string Name, string? EntityType = null, Guid? Key = null, string? Url = null, string? QueryString = null)
{
    public static Link Document(string name, Guid key, string? queryString = null) => new(name, Constants.UdiEntityType.Document, key, QueryString: queryString);

    public static Link Media(string name, Guid key) => new(name, Constants.UdiEntityType.Media, key);

    public static Link External(string name, string url) => new(name, Url: url);
}
