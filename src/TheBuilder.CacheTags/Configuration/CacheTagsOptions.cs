namespace TheBuilder.CacheTags.Configuration;

/// <summary>
///     Settings under <c>CacheTags</c> in configuration.
/// </summary>
public sealed class CacheTagsOptions
{
    /// <summary>The configuration section the options bind to.</summary>
    public const string SectionName = "CacheTags";

    /// <summary>Whether Delivery API responses are tagged and changes planned and sent. Defaults to <c>true</c>.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The response header that carries a response's keys. Defaults to <c>X-Umbraco-Cache-Keys</c>.</summary>
    public string HeaderName { get; set; } = "X-Umbraco-Cache-Keys";

    /// <summary>
    ///     Prefixed to every key, for a site that shares a CDN zone with another app using the same key
    ///     names: lowercase letters, digits and hyphens, ending in a hyphen (<c>shop-</c>). Defaults to none.
    /// </summary>
    public string KeyPrefix { get; set; } = string.Empty;

    /// <summary>Which keys the response header carries. Defaults to <see cref="CacheKeyMode.ResponseKey" />.</summary>
    public CacheKeyMode KeyMode { get; set; } = CacheKeyMode.ResponseKey;

    /// <summary>
    ///     In <see cref="CacheKeyMode.ItemKeys" />, the most item keys one response lists before it lists
    ///     their content types instead. Defaults to 64.
    /// </summary>
    public int MaxItemKeys { get; set; } = 64;

    /// <summary>
    ///     The most keys one response header lists, most needed first. Defaults to 128, the most tags
    ///     Next.js keeps per cache entry. Raise it for a target that limits tags by size instead, such as
    ///     Cloudflare in front of Umbraco.
    /// </summary>
    public int MaxHeaderKeys { get; set; } = 128;

    /// <summary>
    ///     The most descendants a route change (rename, move, unpublish) purges one by one before it
    ///     purges every response instead. Defaults to 500.
    /// </summary>
    public int MaxDescendants { get; set; } = 500;

    /// <summary>
    ///     How long a response's mapping (which keys it covers) is kept after the response was last
    ///     served. Defaults to 7 days. A frontend must not cache a response longer than this.
    /// </summary>
    public TimeSpan MappingRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    ///     Where purges are sent once every server has applied a change, by name
    ///     (<c>CacheTags:Targets:nextjs:Url</c>). A change is still planned and recorded without any.
    /// </summary>
    public Dictionary<string, CacheTagsTargetOptions> Targets { get; set; } = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>How often each server looks for changes to release and purges to send while there are some. Defaults to 1 second.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How many times a purge is tried before it is given up and kept for inspection. Defaults to 10.</summary>
    public int MaxAttempts { get; set; } = 10;

    /// <summary>
    ///     Listings come from each server's Delivery API index, which Umbraco updates in the background
    ///     after a change, so a change's listings are purged a second time once every server's index has
    ///     it. This is the longest that second purge waits for a server. Defaults to 5 minutes: the
    ///     index usually takes a second or two, and can take over a minute right after a restart.
    /// </summary>
    public TimeSpan IndexTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    ///     Whether targets added in the backoffice may connect to private and reserved network
    ///     addresses (localhost, 10.0.0.0/8, link-local and so on). Defaults to <c>false</c>, so an
    ///     administrator cannot point a target at a service inside the server's own network. Targets
    ///     in configuration are not limited.
    /// </summary>
    public bool AllowPrivateNetworkTargets { get; set; }

    /// <summary>How long released changes and sent purges are kept for the lookup API. Defaults to 7 days.</summary>
    public TimeSpan LedgerRetention { get; set; } = TimeSpan.FromDays(7);
}

/// <summary>
///     One place purges are sent. The built-in types are <c>webhook</c>, the signed purge webhook, and
///     <c>cloudflare</c>, purging a Cloudflare zone by cache tag (docs/design.md). A site adds other
///     types by registering an <see cref="Delivery.IPurgeTarget" />.
/// </summary>
public sealed class CacheTagsTargetOptions
{
    /// <summary>The target type, matched against the registered <see cref="Delivery.IPurgeTarget" />s. Defaults to <c>webhook</c>.</summary>
    public string Type { get; set; } = Delivery.WebhookTarget.TypeName;

    /// <summary>Whether purges are sent to this target. Defaults to <c>true</c>.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>For a webhook: the absolute URL it is posted to.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>For a webhook: the secret it is signed with (HMAC-SHA256). At least 32 characters.</summary>
    public string Secret { get; set; } = string.Empty;

    /// <summary>The most keys one request carries; more are split over several requests. Defaults to 500.</summary>
    public int MaxKeysPerRequest { get; set; } = 500;

    /// <summary>
    ///     For a webhook: request headers sent with every purge, such as a gateway's API key or
    ///     <c>x-vercel-protection-bypass</c>. Other types read them only if they choose to.
    /// </summary>
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Settings for other target types, which their <see cref="Delivery.IPurgeTarget" /> reads.</summary>
    public Dictionary<string, string> Settings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Whether this target may only connect to public network addresses: set for targets added in
    ///     the backoffice, unless <see cref="CacheTagsOptions.AllowPrivateNetworkTargets" />.
    /// </summary>
    internal bool PublicNetworkOnly { get; init; }
}

/// <summary>
///     Which keys a Delivery API response carries in its header.
/// </summary>
public enum CacheKeyMode
{
    /// <summary>One <c>response-</c> key per response; the package records what it covers.</summary>
    ResponseKey = 0,

    /// <summary>
    ///     The <c>content-</c> and <c>media-</c> keys of every item the response shows (up to
    ///     <see cref="CacheTagsOptions.MaxItemKeys" />), and its listing selectors, for a CDN in front of
    ///     Umbraco that purges by tag itself.
    /// </summary>
    ItemKeys = 1,
}
