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
///     keys on write. The package skips rewriting a render whose keys it wrote within the last hour, and
///     touches a response at most once an hour, so a key's last render time and a response's last seen
///     time can each be up to an hour behind. The cutoffs the package passes to <c>PruneAsync</c> and
///     <c>ForgetAsync</c> already allow for that, so a store compares against them as they are.
/// </remarks>
public interface ICacheTagStore
{
    /// <summary>Adds or refreshes rendered responses and the keys they cover.</summary>
    /// <remarks>
    ///     Must throw when any response could not be recorded, even if the others were: those responses
    ///     are out with their keys already, so the package writes each of them again (one by one, then
    ///     after 2, 4, 8 ... seconds) until it succeeds. A store that swallows a failure loses that
    ///     response's mapping, and no purge reaches it. Because of these retries a response can be
    ///     recorded more than once, so recording must be an idempotent, additive upsert.
    /// </remarks>
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
    ///     Removes these responses, and their keys, where they were neither rendered nor served since
    ///     <paramref name="cutoffUtc" />. A purge has expired every copy from before then, so no frontend
    ///     keeps one that needs the mapping. Returns how many responses and keys were removed together,
    ///     for logging only. Optional: a store that keeps them until <see cref="PruneAsync" /> removes
    ///     them only purges more than it needs to, which is the default.
    /// </summary>
    Task<int> ForgetAsync(IReadOnlyCollection<string> responseKeys, DateTime cutoffUtc, CancellationToken cancellationToken = default)
        => Task.FromResult(0);

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
