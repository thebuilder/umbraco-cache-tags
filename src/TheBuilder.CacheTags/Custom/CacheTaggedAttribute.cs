namespace TheBuilder.CacheTags;

/// <summary>
///     Tags the responses of a site's own API action (or every action of a controller) the way the
///     package tags Delivery API responses: the keys of what a response shows, in its header. The
///     action names its own keys with <see cref="ICacheTagsResponse" />; the Delivery API items in its
///     result are found on their own. A change recorded with <see cref="ICacheTagsChanges" /> purges it.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true)]
public sealed class CacheTaggedAttribute : Attribute;
