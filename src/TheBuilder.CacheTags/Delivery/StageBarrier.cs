namespace TheBuilder.CacheTags.Delivery;

/// <summary>
///     Whether a change may move on to its next stage, and which servers it would move on without. A
///     stage waits for some servers to finish (apply the change, or index it), or for its timeout. A
///     server that has stopped (no heartbeat within the server timeout) serves nothing, so nothing
///     waits for it.
/// </summary>
internal sealed record StageBarrier(bool Open, bool TimedOut, IReadOnlyList<string> Waiting)
{
    /// <summary>A stage that waits for nothing.</summary>
    public static readonly StageBarrier Opened = new(true, false, []);

    /// <summary>
    ///     A change is released once every active server that was already running when it was made
    ///     has acknowledged it; one that started later built its caches after the change.
    /// </summary>
    public static StageBarrier ForAcks(
        DateTime changedUtc,
        DateTime nowUtc,
        IEnumerable<ServerPresence> servers,
        IReadOnlySet<string> acknowledgedBy,
        TimeSpan ackTimeout,
        TimeSpan serverTimeout)
        => Decide(
            Active(servers, nowUtc, serverTimeout).Where(server => server.FirstSeenUtc <= changedUtc),
            acknowledgedBy,
            changedUtc,
            nowUtc,
            ackTimeout);

    /// <summary>A released change's listings are purged once every active server that acknowledged it has indexed it.</summary>
    public static StageBarrier ForIndex(
        DateTime releasedUtc,
        DateTime nowUtc,
        IEnumerable<ServerPresence> servers,
        IReadOnlySet<string> acknowledgedBy,
        IReadOnlySet<string> indexedBy,
        TimeSpan indexTimeout,
        TimeSpan serverTimeout)
        => Decide(
            Active(servers, nowUtc, serverTimeout).Where(server => acknowledgedBy.Contains(server.Server)),
            indexedBy,
            releasedUtc,
            nowUtc,
            indexTimeout);

    private static IEnumerable<ServerPresence> Active(IEnumerable<ServerPresence> servers, DateTime nowUtc, TimeSpan serverTimeout)
        => servers.Where(server => server.LastSeenUtc >= nowUtc - serverTimeout);

    private static StageBarrier Decide(IEnumerable<ServerPresence> mustFinish, IReadOnlySet<string> finished, DateTime sinceUtc, DateTime nowUtc, TimeSpan timeout)
    {
        var waiting = mustFinish
            .Select(server => server.Server)
            .Where(server => finished.Contains(server) is false)
            .Order(StringComparer.Ordinal)
            .ToList();
        if (waiting.Count == 0)
        {
            return Opened;
        }

        var timedOut = nowUtc - sinceUtc >= timeout;
        return new StageBarrier(timedOut, timedOut, waiting);
    }
}
