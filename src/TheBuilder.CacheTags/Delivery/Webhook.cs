using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TheBuilder.CacheTags.Delivery;

/// <summary>
///     The purge webhook's wire format (docs/design.md): a JSON body of the purge id and keys,
///     signed with HMAC-SHA256 over the method, path, timestamp and body, joined by newlines.
/// </summary>
internal static class Webhook
{
    public const string TimestampHeader = "X-Cache-Tags-Timestamp";
    public const string SignatureHeader = "X-Cache-Tags-Signature";

    /// <summary>The signed POST of one purge to a target.</summary>
    public static HttpRequestMessage Request(Uri url, string secret, Guid id, IReadOnlyCollection<string> keys, long timestamp)
    {
        var body = Body(id, keys);
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(TimestampHeader, timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add(SignatureHeader, Sign(secret, "POST", url.AbsolutePath, timestamp, body));
        return request;
    }

    public static string Body(Guid id, IReadOnlyCollection<string> keys)
        => JsonSerializer.Serialize(new WebhookBody(id.ToString("D"), keys), WebhookJson.Default.WebhookBody);

    public static string Sign(string secret, string method, string path, long timestamp, string body)
    {
        var signed = $"{method}\n{path}\n{timestamp}\n{body}";
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(signed));
        return "sha256=" + Convert.ToHexStringLower(mac);
    }

    /// <summary>Splits keys over requests of at most <paramref name="perRequest" /> keys, keeping their order.</summary>
    public static IReadOnlyList<string[]> Chunk(IReadOnlyCollection<string> keys, int perRequest)
        => keys.Chunk(Math.Max(1, perRequest)).ToList();
}

internal sealed record WebhookBody(string Id, IReadOnlyCollection<string> Keys);

[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(WebhookBody))]
internal sealed partial class WebhookJson : System.Text.Json.Serialization.JsonSerializerContext;
