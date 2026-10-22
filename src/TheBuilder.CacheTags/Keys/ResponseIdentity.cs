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
    ///     The query parameters that select what a response covers. The Delivery API's others
    ///     (<c>expand</c>, <c>fields</c>, <c>sort</c>, <c>skip</c>, <c>take</c>) only change how it is
    ///     presented, and any other parameter is ignored, so they are left out: requests that differ
    ///     only in them share a key. That is safe because the mapping only ever adds keys: the shared
    ///     key covers everything any of them showed, so a purge reaches each. It keeps input such as
    ///     <c>?fields=properties[&lt;random&gt;]</c> or <c>?skip=&lt;n&gt;</c> from making a new key per request.
    /// </summary>
    private static readonly HashSet<string> Parameters = new(StringComparer.OrdinalIgnoreCase) { "id", "fetch", "filter" };

    public static ResponseIdentity Create(string path, IEnumerable<KeyValuePair<string, string?>> query, string? culture, string? segment, string? startItem)
        => new(
            path.ToLowerInvariant(),
            NormaliseQuery(query),
            culture?.ToLowerInvariant() ?? string.Empty,
            segment ?? string.Empty,
            startItem?.ToLowerInvariant() ?? string.Empty);

    /// <summary>
    ///     Keeps the parameters that select content, sorts them by name, then value (so repeated
    ///     parameters keep a stable order), drops empty values, and encodes the rest the same way
    ///     every time. Parameter
    ///     names are lowercased; values keep their case, since filters and paths can depend on it.
    /// </summary>
    public static string NormaliseQuery(IEnumerable<KeyValuePair<string, string?>> query)
    {
        var parameters = query
            .Where(pair => string.IsNullOrEmpty(pair.Value) is false && Parameters.Contains(pair.Key))
            .Select(pair => (Name: pair.Key.ToLowerInvariant(), Value: pair.Value!))
            .OrderBy(pair => pair.Name, StringComparer.Ordinal)
            .ThenBy(pair => pair.Value, StringComparer.Ordinal)
            .Select(pair => Uri.EscapeDataString(pair.Name) + "=" + Uri.EscapeDataString(pair.Value));
        return string.Join("&", parameters);
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
