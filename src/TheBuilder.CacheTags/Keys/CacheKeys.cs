namespace TheBuilder.CacheTags.Keys;

/// <summary>
///     Builds the package's cache keys. Every key is lowercase. See docs/design.md.
/// </summary>
internal static class CacheKeys
{
    public const string ContentPrefix = "content-";
    public const string MediaPrefix = "media-";
    public const string TypePrefix = "type-";
    public const string ChildrenPrefix = "children-";
    public const string SearchPrefix = "search-";
    public const string All = "umbraco";

    /// <summary>
    ///     The prefix of a stored set of items (<c>set-3f9a…</c>): a response showing more items than
    ///     its header lists carries one key for all of them, and the package stores which items it holds.
    /// </summary>
    public const string SetPrefix = "set-";

    /// <summary>The prefix of keys a site names for its own output (<c>x-dictionary-da-dk</c>).</summary>
    public const string CustomPrefix = "x-";

    /// <summary>The longest custom key, so it fits a frontend's tag limit (Next.js keeps 256 characters) with a key prefix.</summary>
    public const int MaxCustomKeyLength = 200;

    public static string Content(Guid key) => ContentPrefix + key.ToString("D");

    public static string Media(Guid key) => MediaPrefix + key.ToString("D");

    public static string Type(string alias) => TypePrefix + alias.ToLowerInvariant();

    /// <summary>
    ///     A type's key within one site root (<c>type-article-&lt;root&gt;</c>): listings confined to the
    ///     root's tree, and the types a response under it lists in place of its items. A change to an item
    ///     plans its root's key as well as the global one, so another root's changes never reach it.
    /// </summary>
    public static string Type(string alias, Guid root) => Type(alias) + "-" + root.ToString("D");

    public static string Children(Guid parentKey) => ChildrenPrefix + parentKey.ToString("D");

    public static string Search(string indexName) => SearchPrefix + indexName.ToLowerInvariant();

    /// <summary>The catch-all key within one site root (<c>search-deliveryapicontentindex-&lt;root&gt;</c>), as <see cref="Type(string, Guid)" />.</summary>
    public static string Search(string indexName, Guid root) => Search(indexName) + "-" + root.ToString("D");

    /// <summary>Whether a key names one item (expired at once) rather than a set of responses.</summary>
    public static bool IsItem(string key)
        => key.StartsWith(ContentPrefix, StringComparison.Ordinal) || key.StartsWith(MediaPrefix, StringComparison.Ordinal);

    /// <summary>Whether a key names a stored set of items (<c>set-</c>).</summary>
    public static bool IsSet(string key) => key.StartsWith(SetPrefix, StringComparison.Ordinal);

    /// <summary>Whether a key selects listings (<c>type-</c>, <c>children-</c>, <c>search-</c>).</summary>
    public static bool IsSelector(string key)
        => key.StartsWith(TypePrefix, StringComparison.Ordinal)
            || key.StartsWith(ChildrenPrefix, StringComparison.Ordinal)
            || key.StartsWith(SearchPrefix, StringComparison.Ordinal);

    /// <summary>
    ///     Whether a (prefixed) key names items or custom output (an item, a set of items, an <c>x-</c>
    ///     key), which a purge expires at once, rather than a listing selector or everything, which a
    ///     purge marks stale.
    /// </summary>
    public static bool Expires(string key, string prefix)
    {
        var bare = key.StartsWith(prefix, StringComparison.Ordinal) ? key[prefix.Length..] : key;
        return IsItem(bare) || IsSet(bare) || IsCustom(bare);
    }

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

    /// <summary>
    ///     Whether a change may purge this key: one a site may name (<see cref="IsNameable" />), or a
    ///     stored set's <c>set-</c> key as a response carries it (<c>set-</c> and 32 lowercase hex
    ///     digits), which an administrator may copy from a response header to purge it by hand.
    /// </summary>
    public static bool IsPurgeable(string key)
        => IsNameable(key)
            || (IsSet(key)
                && key.Length == SetPrefix.Length + 32
                && key.AsSpan(SetPrefix.Length).ContainsAnyExcept("0123456789abcdef") is false);

    /// <summary>Applies the configured prefix to a key, for the header and the purge payload.</summary>
    public static string WithPrefix(string prefix, string key) => prefix.Length == 0 ? key : prefix + key;
}
