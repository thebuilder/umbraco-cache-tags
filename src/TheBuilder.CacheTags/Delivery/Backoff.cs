namespace TheBuilder.CacheTags.Delivery;

/// <summary>When a failed purge is tried again.</summary>
internal static class Backoff
{
    private static readonly TimeSpan Longest = TimeSpan.FromMinutes(10);

    /// <summary>2, 4, 8 ... seconds after the given number of failed attempts, at most 10 minutes.</summary>
    public static TimeSpan After(int failedAttempts)
        => failedAttempts >= 10 ? Longest : TimeSpan.FromSeconds(Math.Min(Longest.TotalSeconds, Math.Pow(2, failedAttempts)));
}
