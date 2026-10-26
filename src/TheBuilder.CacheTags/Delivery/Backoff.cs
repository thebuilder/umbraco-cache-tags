namespace TheBuilder.CacheTags.Delivery;

/// <summary>When a failed purge is tried again.</summary>
internal static class Backoff
{
    private static readonly TimeSpan Longest = TimeSpan.FromMinutes(10);

    /// <summary>2, 4, 8 ... seconds after the given number of failed attempts, at most 10 minutes.</summary>
    public static TimeSpan After(int failedAttempts) => After(failedAttempts, Longest);

    /// <summary>2, 4, 8 ... seconds after the given number of failed attempts, at most <paramref name="longest" />.</summary>
    public static TimeSpan After(int failedAttempts, TimeSpan longest)
        => TimeSpan.FromSeconds(Math.Min(longest.TotalSeconds, Math.Pow(2, Math.Min(failedAttempts, 30))));
}
