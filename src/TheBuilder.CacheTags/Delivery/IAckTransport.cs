namespace TheBuilder.CacheTags.Delivery;

/// <summary>
///     How servers tell each other they are running and have applied a change. The default keeps
///     both in the Umbraco database, so every server needs write access to the package's tables; a
///     site can register its own (for example over Redis) instead.
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

    /// <summary>The servers that have acknowledged each of the given changes.</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlySet<string>>> GetAcksAsync(IReadOnlyCollection<Guid> changeIds, CancellationToken cancellationToken = default);

    /// <summary>Removes servers not seen since <paramref name="cutoffUtc" />, and acks of changes released before it.</summary>
    Task PruneAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default);
}

/// <summary>A server running the package, as its heartbeats report it.</summary>
/// <param name="Server">The server's identity for this process.</param>
/// <param name="FirstSeenUtc">When this process started.</param>
/// <param name="LastSeenUtc">Its latest heartbeat.</param>
public sealed record ServerPresence(string Server, DateTime FirstSeenUtc, DateTime LastSeenUtc);
