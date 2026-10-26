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

    /// <summary>
    ///     The most keys one response header lists, most needed first. Defaults to 128, the most tags
    ///     Next.js keeps per cache entry. Raise it for a target that limits tags by size instead, such as
    ///     Cloudflare in front of Umbraco.
    /// </summary>
    public int MaxHeaderKeys { get; set; } = 128;

    /// <summary>
    ///     Where purges are sent once every server has applied a change, by name
    ///     (<c>CacheTags:Targets:nextjs:Url</c>). A change is still planned and recorded without any.
    /// </summary>
    public Dictionary<string, CacheTagsTargetOptions> Targets { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Whether targets added in the backoffice may connect to private and reserved network
    ///     addresses (localhost, 10.0.0.0/8, link-local and so on). Defaults to <c>false</c>, so an
    ///     administrator cannot point a target at a service inside the server's own network. Targets
    ///     in configuration are not limited.
    /// </summary>
    public bool AllowPrivateNetworkTargets { get; set; }
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
