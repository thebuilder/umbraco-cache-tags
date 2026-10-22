namespace TheBuilder.CacheTags.Delivery;

/// <summary>Whether a change may be sent yet, and which servers it would be sent without.</summary>
internal sealed record ReleaseDecision(bool Release, bool TimedOut, IReadOnlyList<string> Missing)
{
    /// <summary>
    ///     A change is released once every server that must apply it has acknowledged it, or once
    ///     <paramref name="ackTimeout" /> has passed. A server must apply it when it is active (a
    ///     heartbeat within <paramref name="serverTimeout" />) and was already running when the change
    ///     was made; one that started later built its caches after the change.
    /// </summary>
    public static ReleaseDecision Decide(
        DateTime changedUtc,
        DateTime nowUtc,
        IEnumerable<ServerPresence> servers,
        IReadOnlySet<string> acknowledgedBy,
        TimeSpan ackTimeout,
        TimeSpan serverTimeout)
    {
        var missing = servers
            .Where(server => server.LastSeenUtc >= nowUtc - serverTimeout && server.FirstSeenUtc <= changedUtc)
            .Select(server => server.Server)
            .Where(server => acknowledgedBy.Contains(server) is false)
            .Order(StringComparer.Ordinal)
            .ToList();

        if (missing.Count == 0)
        {
            return new ReleaseDecision(true, false, []);
        }

        var timedOut = nowUtc - changedUtc >= ackTimeout;
        return new ReleaseDecision(timedOut, timedOut, missing);
    }
}

/// <summary>When a failed purge is tried again.</summary>
internal static class Backoff
{
    private static readonly TimeSpan Longest = TimeSpan.FromMinutes(10);

    /// <summary>2, 4, 8 ... seconds after the given number of failed attempts, at most 10 minutes.</summary>
    public static TimeSpan After(int failedAttempts)
        => failedAttempts >= 10 ? Longest : TimeSpan.FromSeconds(Math.Min(Longest.TotalSeconds, Math.Pow(2, failedAttempts)));
}
