namespace TheBuilder.CacheTags.Delivery;

/// <summary>
///     How servers tell each other they are running and have applied a change. The default keeps
///     both in the Umbraco database, so every server needs write access to the package's tables; a
///     site can register its own (for example over Redis) instead. Registered as a singleton and
///     called from several threads at once.
/// </summary>
public interface IAckTransport
{
    /// <summary>Records that <paramref name="server" /> is running, and since when.</summary>
    Task HeartbeatAsync(string server, DateTime firstSeenUtc, DateTime nowUtc, CancellationToken cancellationToken = default);

    /// <summary>Removes a server that is shutting down, so no change waits for it.</summary>
    Task LeaveAsync(string server, CancellationToken cancellationToken = default);

    /// <summary>Every server that has sent a heartbeat and not left.</summary>
    Task<IReadOnlyCollection<ServerPresence>> GetServersAsync(CancellationToken cancellationToken = default);

    /// <summary>Records that <paramref name="server" /> has applied the given changes.</summary>
    Task AckAsync(string server, IReadOnlyCollection<Guid> changeIds, DateTime ackedUtc, CancellationToken cancellationToken = default);

    /// <summary>The acknowledgements of each of the given changes, with whether each server has indexed it.</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<ServerAck>>> GetAcksAsync(IReadOnlyCollection<Guid> changeIds, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Records that <paramref name="server" />'s Delivery API index has the given changes, which it
    ///     has acknowledged. A change's listings are purged once every server that acknowledged it has.
    /// </summary>
    Task IndexedAsync(string server, IReadOnlyCollection<Guid> changeIds, DateTime indexedUtc, CancellationToken cancellationToken = default);

    /// <summary>Removes servers not seen since <paramref name="cutoffUtc" />, and acks recorded before it.</summary>
    Task PruneAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default);
}

/// <summary>A server running the package, as its heartbeats report it.</summary>
/// <param name="Server">The server's identity for this process.</param>
/// <param name="FirstSeenUtc">When this process started.</param>
/// <param name="LastSeenUtc">Its latest heartbeat.</param>
public sealed record ServerPresence(string Server, DateTime FirstSeenUtc, DateTime LastSeenUtc);

/// <summary>One server's acknowledgement of a change.</summary>
/// <param name="Server">The server.</param>
/// <param name="AckedUtc">When it applied the change.</param>
/// <param name="IndexedUtc">When its Delivery API index had the change, for a change whose listings wait for it.</param>
public sealed record ServerAck(string Server, DateTime AckedUtc, DateTime? IndexedUtc);

/// <summary>Reading acknowledgements by change.</summary>
internal static class ServerAcks
{
    public static IReadOnlySet<string> AcknowledgedBy(this IReadOnlyDictionary<Guid, IReadOnlyList<ServerAck>> acks, Guid changeId)
        => (acks.GetValueOrDefault(changeId) ?? []).Select(ack => ack.Server).ToHashSet(StringComparer.Ordinal);

    public static IReadOnlySet<string> IndexedBy(this IReadOnlyDictionary<Guid, IReadOnlyList<ServerAck>> acks, Guid changeId)
        => (acks.GetValueOrDefault(changeId) ?? []).Where(ack => ack.IndexedUtc is not null).Select(ack => ack.Server).ToHashSet(StringComparer.Ordinal);
}
