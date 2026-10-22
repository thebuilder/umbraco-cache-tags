namespace TheBuilder.CacheTags;

/// <summary>
///     Tags the responses of a site's own API action (or every action of a controller) the way the
///     package tags Delivery API responses: a <c>response-</c> key in the response header, recorded with
///     the keys the response covers. The action names those keys with <see cref="ICacheTagsResponse" />;
///     the Delivery API items in its result are found on their own. A change recorded with
///     <see cref="ICacheTagsChanges" /> purges it.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true)]
public sealed class CacheTaggedAttribute : Attribute
{
    /// <summary>
    ///     The query parameters that select what a response covers, such as <c>locale</c>. Only these
    ///     are part of its <c>response-</c> key; requests that differ in any other parameter share it.
    ///     None by default: list only parameters with a bounded set of values, or every value sent
    ///     becomes a key of its own.
    /// </summary>
    public string[] QueryParameters { get; set; } = [];

    /// <summary>
    ///     Whether responses are recorded under their <c>response-</c> key. <c>false</c> makes each
    ///     response carry its keys in the header itself, unrecorded, for an action whose input varies
    ///     without end, such as a search. Defaults to <c>true</c>.
    /// </summary>
    public bool Recorded { get; set; } = true;
}
