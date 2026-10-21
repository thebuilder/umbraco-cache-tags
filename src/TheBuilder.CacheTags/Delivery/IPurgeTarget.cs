using TheBuilder.CacheTags.Configuration;

namespace TheBuilder.CacheTags.Delivery;

/// <summary>
///     Sends purges to one kind of target. The package has <c>webhook</c>; a site registers more (a
///     CDN's purge API, say) as singletons, and configures targets of that <see cref="Type" />.
/// </summary>
public interface IPurgeTarget
{
    /// <summary>The <see cref="CacheTagsTargetOptions.Type" /> this sends for.</summary>
    string Type { get; }

    /// <summary>Sends one purge. Returns null on success, or what went wrong (the purge is retried with backoff).</summary>
    Task<string?> SendAsync(PurgeRequest request, CacheTagsTargetOptions target, CancellationToken cancellationToken);
}

/// <summary>One purge request to one target.</summary>
public sealed class PurgeRequest
{
    /// <summary>The purge's id. A target can use it to ignore a request it already handled.</summary>
    public required Guid Id { get; init; }

    /// <summary>The change it purges for. One change can send several purges.</summary>
    public required Guid ChangeId { get; init; }

    /// <summary>The configured target's name.</summary>
    public required string TargetName { get; init; }

    /// <summary>Keys whose cached entries must be refetched before they are served again (<c>response-</c>, <c>content-</c>, <c>media-</c>).</summary>
    public required IReadOnlyList<string> Expire { get; init; }

    /// <summary>
    ///     Keys whose cached entries may be served once more while they refresh (<c>type-</c>,
    ///     <c>children-</c>, <c>search-</c>, <c>umbraco</c>): listings that change with an index, or
    ///     everything. A target without stale-while-revalidate expires them too.
    /// </summary>
    public required IReadOnlyList<string> Stale { get; init; }
}
