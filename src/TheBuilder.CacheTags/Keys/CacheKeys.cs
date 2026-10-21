namespace TheBuilder.CacheTags.Keys;

/// <summary>
///     Builds the package's cache keys. Every key is lowercase. See docs/design.md.
/// </summary>
internal static class CacheKeys
{
    public const string ResponsePrefix = "response-";
    public const string ContentPrefix = "content-";
    public const string MediaPrefix = "media-";
    public const string TypePrefix = "type-";
    public const string ChildrenPrefix = "children-";
    public const string SearchPrefix = "search-";
    public const string All = "umbraco";

    public static string Content(Guid key) => ContentPrefix + key.ToString("D");

    public static string Media(Guid key) => MediaPrefix + key.ToString("D");

    public static string Type(string alias) => TypePrefix + alias.ToLowerInvariant();

    public static string Children(Guid parentKey) => ChildrenPrefix + parentKey.ToString("D");

    public static string Search(string indexName) => SearchPrefix + indexName.ToLowerInvariant();

    /// <summary>Whether a key names one item (expired at once) rather than a set of responses.</summary>
    public static bool IsItem(string key)
        => key.StartsWith(ContentPrefix, StringComparison.Ordinal) || key.StartsWith(MediaPrefix, StringComparison.Ordinal);

    /// <summary>Whether a key selects listings (<c>type-</c>, <c>children-</c>, <c>search-</c>).</summary>
    public static bool IsSelector(string key)
        => key.StartsWith(TypePrefix, StringComparison.Ordinal)
            || key.StartsWith(ChildrenPrefix, StringComparison.Ordinal)
            || key.StartsWith(SearchPrefix, StringComparison.Ordinal);

    /// <summary>
    ///     Whether a (prefixed) key names one response or item, which a purge expires at once, rather
    ///     than a listing selector or everything, which a purge marks stale.
    /// </summary>
    public static bool Expires(string key, string prefix)
    {
        var bare = key.StartsWith(prefix, StringComparison.Ordinal) ? key[prefix.Length..] : key;
        return bare.StartsWith(ResponsePrefix, StringComparison.Ordinal) || IsItem(bare);
    }

    /// <summary>Applies the configured prefix to a key, for the header and the purge payload.</summary>
    public static string WithPrefix(string prefix, string key) => prefix.Length == 0 ? key : prefix + key;
}
