namespace TheBuilder.CacheTags.Configuration;

/// <summary>
///     Settings under <c>CacheTags</c> in configuration.
/// </summary>
public sealed class CacheTagsOptions
{
    /// <summary>The configuration section the options bind to.</summary>
    public const string SectionName = "CacheTags";

    /// <summary>Whether Delivery API responses are tagged and changes are planned. Defaults to <c>true</c>.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     Prefixed to every key, for a site that shares a CDN zone with another app using the same key
    ///     names. Lowercase letters, digits and hyphens. Defaults to none.
    /// </summary>
    public string KeyPrefix { get; set; } = string.Empty;

    /// <summary>Which keys the response header carries. Defaults to <see cref="CacheKeyMode.Response" />.</summary>
    public CacheKeyMode Mode { get; set; } = CacheKeyMode.Response;

    /// <summary>In <see cref="CacheKeyMode.Direct" />, the most item keys one response lists before it falls back to type keys.</summary>
    public int DirectKeyBudget { get; set; } = 64;

    /// <summary>How long a response's mapping is kept after it was last served. Defaults to 7 days.</summary>
    public TimeSpan MappingRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    ///     The most descendants a route change (rename, move, unpublish) purges one by one before it
    ///     purges every response instead. Defaults to 500.
    /// </summary>
    public int DescendantBudget { get; set; } = 500;
}

/// <summary>
///     Which keys a Delivery API response carries in its header.
/// </summary>
public enum CacheKeyMode
{
    /// <summary>One <c>response-</c> key per response; the package records what it covers.</summary>
    Response = 0,

    /// <summary>The <c>content-</c> and <c>media-</c> keys of every item the response renders, up to a budget.</summary>
    Direct = 1,
}
