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

    /// <summary>
    ///     Where purges are sent once every server has applied a change. A change is still planned
    ///     and recorded without any.
    /// </summary>
    public List<CacheTagsTargetOptions> Targets { get; set; } = [];

    /// <summary>
    ///     How long a change waits for every active server to acknowledge it before it is sent anyway.
    ///     Defaults to 60 seconds.
    /// </summary>
    public TimeSpan AckTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    ///     How long since its last heartbeat a server still counts as active. Defaults to 2 minutes;
    ///     servers send one every 30 seconds.
    /// </summary>
    public TimeSpan ServerTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>How often each server looks for changes to release and purges to send. Defaults to 1 second.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How many times a purge is tried before it is given up and kept for inspection. Defaults to 10.</summary>
    public int MaxAttempts { get; set; } = 10;

    /// <summary>
    ///     Listings come from Umbraco's Delivery API index, which is updated in the background after
    ///     a change, so a listing purged at once can be refetched before the index has the change.
    ///     Listing responses are purged again after each of these delays. Defaults to 5, 20 and 45
    ///     seconds: the index usually takes a few seconds, and over half a minute right after a
    ///     restart. Empty turns the re-purge off.
    /// </summary>
    public List<TimeSpan> ListingRepurgeDelays { get; set; } = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(45)];

    /// <summary>How long released changes and sent purges are kept for the lookup API. Defaults to 7 days.</summary>
    public TimeSpan LedgerRetention { get; set; } = TimeSpan.FromDays(7);
}

/// <summary>
///     One place purges are sent: a URL that receives the signed purge webhook (see docs/design.md).
/// </summary>
public sealed class CacheTagsTargetOptions
{
    /// <summary>A name for logs and the lookup API, unique among the targets.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The absolute URL the webhook is posted to.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>The secret the webhook is signed with (HMAC-SHA256). At least 16 characters.</summary>
    public string Secret { get; set; } = string.Empty;

    /// <summary>Whether purges are sent to this target. Defaults to <c>true</c>.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The most keys one request carries; more are split over several requests. Defaults to 500.</summary>
    public int MaxKeysPerRequest { get; set; } = 500;
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
