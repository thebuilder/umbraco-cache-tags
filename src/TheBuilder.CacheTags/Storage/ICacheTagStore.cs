namespace TheBuilder.CacheTags.Storage;

/// <summary>
///     Where the package keeps which keys each Delivery API response covers. The default stores it in
///     the Umbraco database; a site can register its own (for example Redis) instead.
/// </summary>
/// <remarks>
///     Writes are additive: recording a response adds or refreshes its keys and never removes one.
///     A key a response no longer covers ages out after <c>MappingRetention</c>. A spare key costs an
///     extra purge; a missing one leaves a cached response stale, so implementations must never drop
///     keys on write.
/// </remarks>
public interface ICacheTagStore
{
    /// <summary>Adds or refreshes rendered responses and the keys they cover.</summary>
    Task RecordAsync(IReadOnlyCollection<CachedResponse> responses, CancellationToken cancellationToken = default);

    /// <summary>Marks responses as served (for example from an output cache) at <paramref name="seenUtc" />.</summary>
    Task TouchAsync(IReadOnlyCollection<string> responseKeys, DateTime seenUtc, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The response keys that cover any of <paramref name="keys" />. With <paramref name="cultures" />,
    ///     only responses requested in one of those cultures, or with no culture.
    /// </summary>
    Task<IReadOnlyCollection<string>> FindResponseKeysAsync(IReadOnlyCollection<string> keys, IReadOnlyCollection<string>? cultures, CancellationToken cancellationToken = default);

    /// <summary>A recorded response and every key it currently covers, or <c>null</c>.</summary>
    Task<CachedResponse?> GetAsync(string responseKey, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Removes responses not served since <paramref name="cutoffUtc" />, and keys that a response
    ///     stopped covering before then. Returns the number of rows removed.
    /// </summary>
    Task<int> PruneAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default);
}

/// <summary>
///     A rendered Delivery API response: what was requested, and the keys it covers.
/// </summary>
/// <param name="ResponseKey">The <c>response-</c> key.</param>
/// <param name="Path">The request path.</param>
/// <param name="Query">The normalised query string.</param>
/// <param name="Culture">The requested culture, lowercased, or empty.</param>
/// <param name="Segment">The requested segment, or empty.</param>
/// <param name="StartItem">The <c>Start-Item</c> header, or empty.</param>
/// <param name="Keys">The <c>content-</c>, <c>media-</c> and selector keys the response covers.</param>
/// <param name="RenderedUtc">When it was rendered (or, when read back, last rendered).</param>
/// <param name="SeenUtc">When it was last served. Equal to <paramref name="RenderedUtc" /> on record.</param>
public sealed record CachedResponse(
    string ResponseKey,
    string Path,
    string Query,
    string Culture,
    string Segment,
    string StartItem,
    IReadOnlyCollection<string> Keys,
    DateTime RenderedUtc,
    DateTime SeenUtc);
