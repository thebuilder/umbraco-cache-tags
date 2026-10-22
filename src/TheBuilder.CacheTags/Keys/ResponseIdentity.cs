using System.Security.Cryptography;
using System.Text;

namespace TheBuilder.CacheTags.Keys;

/// <summary>
///     What makes two Delivery API requests return the same response. Two requests with the same
///     identity get the same <c>response-</c> key on every server.
/// </summary>
/// <param name="Path">The request path, lowercased.</param>
/// <param name="Query">The normalised query string (see <see cref="ResponseIdentity.NormaliseQuery" />).</param>
/// <param name="Culture">The request culture, lowercased, or empty.</param>
/// <param name="Segment">The requested segment, or empty.</param>
/// <param name="StartItem">The <c>Start-Item</c> header, lowercased, or empty.</param>
internal sealed record ResponseIdentity(string Path, string Query, string Culture, string Segment, string StartItem)
{
    /// <summary>
    ///     The Delivery API's query parameters that select what a response covers. Its others
    ///     (<c>expand</c>, <c>fields</c>, <c>sort</c>, <c>skip</c>, <c>take</c>) only change how it is
    ///     presented, and any other parameter is ignored, so they are left out: requests that differ
    ///     only in them share a key. That is safe because the mapping only ever adds keys: the shared
    ///     key covers everything any of them showed, so a purge reaches each. It keeps input such as
    ///     <c>?fields=properties[&lt;random&gt;]</c> or <c>?skip=&lt;n&gt;</c> from making a new key per request.
    /// </summary>
    public static readonly IReadOnlySet<string> DeliveryParameters = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id", "fetch", "filter" };

    /// <summary>The identity of a request; <paramref name="parameters" /> are the query parameters that select content, the Delivery API's by default.</summary>
    public static ResponseIdentity Create(string path, IEnumerable<KeyValuePair<string, string?>> query, string? culture, string? segment, string? startItem, IReadOnlySet<string>? parameters = null)
        => new(
            path.ToLowerInvariant(),
            NormaliseQuery(query, parameters),
            culture?.ToLowerInvariant() ?? string.Empty,
            segment ?? string.Empty,
            startItem?.ToLowerInvariant() ?? string.Empty);

    /// <summary>
    ///     Keeps the parameters that select content, sorts them by name, then value (so repeated
    ///     parameters keep a stable order), drops empty and repeated values, and encodes the rest the
    ///     same way every time. Names and values are lowercased, and an id is written in one form
    ///     (<c>D</c>), as is the id after a <c>:</c> (<c>children:&lt;id&gt;</c>): Umbraco reads ids,
    ///     paths and aliases regardless of case or form, so the variants of a request share a key.
    ///     Where a value's case did matter, sharing a key is still safe, for the reason given at
    ///     <see cref="DeliveryParameters" />.
    /// </summary>
    public static string NormaliseQuery(IEnumerable<KeyValuePair<string, string?>> query, IReadOnlySet<string>? parameters = null)
    {
        IReadOnlySet<string> selecting = parameters ?? DeliveryParameters;
        var normalised = query
            .Where(pair => string.IsNullOrEmpty(pair.Value) is false && selecting.Contains(pair.Key))
            .Select(pair => (Name: pair.Key.ToLowerInvariant(), Value: Canonical(pair.Value!)))
            .Distinct()
            .OrderBy(pair => pair.Name, StringComparer.Ordinal)
            .ThenBy(pair => pair.Value, StringComparer.Ordinal)
            .Select(pair => Uri.EscapeDataString(pair.Name) + "=" + Uri.EscapeDataString(pair.Value));
        return string.Join("&", normalised);
    }

    private static string Canonical(string value)
    {
        if (Guid.TryParse(value, out Guid id))
        {
            return id.ToString("D");
        }

        var colon = value.IndexOf(':', StringComparison.Ordinal);
        return colon >= 0 && Guid.TryParse(value.AsSpan(colon + 1), out id)
            ? value[..colon].ToLowerInvariant() + ":" + id.ToString("D")
            : value.ToLowerInvariant();
    }

    /// <summary>
    ///     Whether every field fits the mapping's columns. Segment and start item come from request
    ///     headers as sent, so a request can make them any length; such a response is not tagged.
    /// </summary>
    public bool Fits => Path.Length <= 1000 && Culture.Length <= 50 && Segment.Length <= 100 && StartItem.Length <= 200;

    /// <summary>
    ///     <c>response-</c> and the first 32 hex characters of the SHA-256 of the identity's fields,
    ///     joined by newlines.
    /// </summary>
    public string ToKey()
    {
        var text = string.Join('\n', Path, Query, Culture, Segment, StartItem);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return CacheKeys.ResponsePrefix + Convert.ToHexStringLower(hash)[..32];
    }
}
