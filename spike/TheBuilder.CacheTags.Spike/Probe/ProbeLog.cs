using System.Collections.Concurrent;
using Umbraco.Cms.Core.Models.PublishedContent;

namespace TheBuilder.CacheTags.Spike.Probe;

/// <summary>
///     One Delivery API request as the probe saw it: the keys each collection strategy found.
/// </summary>
public sealed record ProbeEntry(
    int Seq,
    string Label,
    string Path,
    string Query,
    int Status,
    bool ControllerRan,
    string? RenderStamp,
    string RichText,
    IReadOnlyDictionary<string, string> Headers,
    IReadOnlyDictionary<string, string[]> Sets);

/// <summary>
///     A cache refresher notification as this server handled it.
/// </summary>
public sealed record RefresherEntry(int Seq, string Label, string Refresher, string MessageType, object Payload, string[] HandlerOrder);

/// <summary>
///     In-memory log the spike endpoints read back.
/// </summary>
public static class ProbeLog
{
    private static int _seq;

    public static ConcurrentQueue<ProbeEntry> Requests { get; } = new();

    public static ConcurrentQueue<RefresherEntry> Refreshers { get; } = new();

    /// <summary>The label of the operation the driver is running, attached to refresher entries.</summary>
    public static string CurrentOperation { get; set; } = "startup";

    public static int Next() => Interlocked.Increment(ref _seq);

    public static void Clear()
    {
        Requests.Clear();
        Refreshers.Clear();
    }
}

/// <summary>
///     Per-request key sets, keyed by strategy name, kept in <see cref="HttpContext.Items" />.
/// </summary>
public static class Collect
{
    private const string ItemsKey = "spike.sets";

    public static Dictionary<string, HashSet<string>> Sets(HttpContext context)
    {
        if (context.Items[ItemsKey] is not Dictionary<string, HashSet<string>> sets)
        {
            sets = new Dictionary<string, HashSet<string>>();
            context.Items[ItemsKey] = sets;
        }

        return sets;
    }

    public static void Add(HttpContext? context, string strategy, string value)
    {
        if (context is null)
        {
            return;
        }

        Dictionary<string, HashSet<string>> sets = Sets(context);
        if (sets.TryGetValue(strategy, out HashSet<string>? set) is false)
        {
            set = [];
            sets[strategy] = set;
        }

        set.Add(value);
    }

    public static void Add(HttpContext? context, string strategy, IPublishedElement? item)
    {
        if (item is null)
        {
            return;
        }

        Add(context, strategy, Format(item));
    }

    public static string Format(IPublishedElement item) => item switch
    {
        IPublishedContent { ItemType: PublishedItemType.Media } => "m:" + item.Key,
        IPublishedContent { ItemType: PublishedItemType.Content } => "c:" + item.Key,
        _ => "e:" + item.Key,
    };
}
