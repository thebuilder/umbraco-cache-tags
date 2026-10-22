namespace TheBuilder.CacheTags.Storage;

/// <summary>
///     Where the package keeps which keys each Delivery API response covers. The default stores it in
///     the Umbraco database; a site can register its own (for example Redis) instead.
/// </summary>
/// <remarks>
///     Registered as a singleton and called from several threads at once. Writes come from a
///     background queue, never the request path.
///     Writes are additive: recording a response adds or refreshes its keys and never removes one.
///     A key a response no longer covers ages out after <c>MappingRetention</c>. A spare key costs an
///     extra purge; a missing one leaves a cached response stale, so implementations must never drop
///     keys on write. The package skips rewriting a render whose keys it wrote within the last hour, so
///     a key's last render time can be up to an hour behind; the default store prunes dropped keys an
///     hour later than <c>PruneAsync</c>'s cutoff for that reason.
/// </remarks>
public interface ICacheTagStore
{
    /// <summary>
    ///     Adds or refreshes rendered responses and the keys they cover. Throws when any of them could not
    ///     be recorded: those responses are out with their keys already, so the package writes the batch
    ///     again until it succeeds.
    /// </summary>
    Task RecordAsync(IReadOnlyCollection<CachedResponse> responses, CancellationToken cancellationToken = default);

    /// <summary>Marks responses as served (for example from an output cache) at <paramref name="seenUtc" />.</summary>
    Task TouchAsync(IReadOnlyCollection<string> responseKeys, DateTime seenUtc, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The response keys that cover any of <paramref name="keys" />. With <paramref name="cultures" />,
    ///     only responses served in one of those cultures (compared case-insensitively), or with no
    ///     culture; <c>null</c> or a <c>*</c> among them means every culture.
    /// </summary>
    Task<IReadOnlyCollection<string>> FindResponseKeysAsync(IReadOnlyCollection<string> keys, IReadOnlyCollection<string>? cultures, CancellationToken cancellationToken = default);

    /// <summary>A recorded response and every key it currently covers, or <c>null</c>.</summary>
    Task<CachedResponse?> GetAsync(string responseKey, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Recorded keys, and responses, whose key, path or query contain <paramref name="text" />, for
    ///     the backoffice's search, at most <paramref name="take" /> of each. The responses' keys are
    ///     left empty. Optional: a store that cannot search finds nothing, which is the default.
    /// </summary>
    Task<CacheSearchResult> SearchAsync(string text, int take, CancellationToken cancellationToken = default)
        => Task.FromResult(CacheSearchResult.Empty);

    /// <summary>
    ///     Removes responses not served since <paramref name="cutoffUtc" />, and keys that a response
    ///     stopped covering before then. Returns how many responses and keys were removed together, for
    ///     logging only.
    /// </summary>
    Task<int> PruneAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default);
}

/// <summary>
///     A rendered Delivery API response: what was requested, and the keys it covers. Keys and
///     cultures are lowercase.
/// </summary>
public sealed class CachedResponse
{
    /// <summary>The <c>response-</c> key, without the configured prefix.</summary>
    public required string ResponseKey { get; init; }

    /// <summary>The request path, lowercased.</summary>
    public required string Path { get; init; }

    /// <summary>The normalised query string: the Delivery API's own parameters, sorted.</summary>
    public required string Query { get; init; }

    /// <summary>The culture the response was served in, lowercased, or empty for an invariant one.</summary>
    public required string Culture { get; init; }

    /// <summary>The requested segment, or empty.</summary>
    public required string Segment { get; init; }

    /// <summary>The <c>Start-Item</c> header, lowercased, or empty.</summary>
    public required string StartItem { get; init; }

    /// <summary>The <c>content-</c>, <c>media-</c> and selector keys the response covers, without the configured prefix.</summary>
    public required IReadOnlyCollection<string> Keys { get; init; }

    /// <summary>When it was rendered (or, when read back, last rendered).</summary>
    public required DateTime RenderedUtc { get; init; }

    /// <summary>When it was last served. Equal to <see cref="RenderedUtc" /> on record.</summary>
    public required DateTime SeenUtc { get; init; }
}

/// <summary>What <see cref="ICacheTagStore.SearchAsync" /> found.</summary>
public sealed class CacheSearchResult
{
    /// <summary>Nothing found.</summary>
    public static CacheSearchResult Empty { get; } = new() { Keys = [], Responses = [] };

    /// <summary>Recorded keys containing the text.</summary>
    public required IReadOnlyList<string> Keys { get; init; }

    /// <summary>Recorded responses whose key, path or query contain the text, most recently served first, without their keys.</summary>
    public required IReadOnlyList<CachedResponse> Responses { get; init; }
}
