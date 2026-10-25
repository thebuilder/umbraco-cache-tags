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
    public const string SchemaPrefix = "schema-";
    public const string All = "umbraco";

    /// <summary>The prefix of keys a site names for its own output (<c>x-dictionary-da-dk</c>).</summary>
    public const string CustomPrefix = "x-";

    /// <summary>The longest custom key, so it fits the mapping with a key prefix.</summary>
    public const int MaxCustomKeyLength = 200;

    public static string Content(Guid key) => ContentPrefix + key.ToString("D");

    public static string Media(Guid key) => MediaPrefix + key.ToString("D");

    public static string Type(string alias) => TypePrefix + alias.ToLowerInvariant();

    /// <summary>A response that renders an item, element or media of this type: its shape changes with the type.</summary>
    public static string Schema(string alias) => SchemaPrefix + alias.ToLowerInvariant();

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
    ///     Whether a (prefixed) key names one response, item or piece of custom output, which a purge
    ///     expires at once, rather than a listing selector or everything, which a purge marks stale.
    /// </summary>
    public static bool Expires(string key, string prefix)
    {
        var bare = WithoutPrefix(prefix, key);
        return IsResponse(bare) || IsItem(bare) || IsCustom(bare);
    }

    /// <summary>Whether a key names one response (<c>response-</c>).</summary>
    public static bool IsResponse(string key) => key.StartsWith(ResponsePrefix, StringComparison.Ordinal);

    /// <summary>Whether a key is one a site named for its own output (<c>x-</c>).</summary>
    public static bool IsCustom(string key) => key.StartsWith(CustomPrefix, StringComparison.Ordinal);

    /// <summary>
    ///     Whether a site may name this key: a custom key (<c>x-</c> and lowercase letters, digits and
    ///     hyphens, at most <see cref="MaxCustomKeyLength" /> characters), a <c>content-</c> or
    ///     <c>media-</c> key of an item, or <c>umbraco</c> (everything).
    /// </summary>
    public static bool IsNameable(string key)
    {
        if (key == All)
        {
            return true;
        }

        if (IsCustom(key))
        {
            return key.Length > CustomPrefix.Length
                && key.Length <= MaxCustomKeyLength
                && key.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');
        }

        var prefix = key.StartsWith(ContentPrefix, StringComparison.Ordinal) ? ContentPrefix
            : key.StartsWith(MediaPrefix, StringComparison.Ordinal) ? MediaPrefix
            : null;
        return prefix is not null && Guid.TryParseExact(key[prefix.Length..], "D", out Guid id) && key == prefix + id.ToString("D");
    }

    /// <summary>Whether a key is the catch-all selector of an index or of media listings (<c>search-</c>).</summary>
    public static bool IsSearch(string key) => key.StartsWith(SearchPrefix, StringComparison.Ordinal);

    /// <summary>Whether a key names a type whose items a response renders (<c>schema-</c>).</summary>
    public static bool IsSchema(string key) => key.StartsWith(SchemaPrefix, StringComparison.Ordinal);

    /// <summary>Applies the configured prefix to a key, for the header and the purge payload.</summary>
    public static string WithPrefix(string prefix, string key) => prefix.Length == 0 ? key : prefix + key;

    /// <summary>A key as sent, with the configured prefix taken off.</summary>
    public static string WithoutPrefix(string prefix, string key) => key.StartsWith(prefix, StringComparison.Ordinal) ? key[prefix.Length..] : key;
}
