using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TheBuilder.CacheTags.Configuration;

namespace TheBuilder.CacheTags.Delivery;

/// <summary>
///     The built-in target: the signed purge webhook (docs/design.md). The JSON body says which keys
///     to expire and which to mark stale, and is signed with HMAC-SHA256 over the method, path,
///     timestamp and body, joined by newlines.
/// </summary>
internal sealed class WebhookTarget(IHttpClientFactory httpClientFactory, TimeProvider time) : IPurgeTarget
{
    public const string TypeName = "webhook";
    public const string HttpClientName = "TheBuilder.CacheTags";
    public const string TimestampHeader = "X-Cache-Tags-Timestamp";
    public const string SignatureHeader = "X-Cache-Tags-Signature";

    /// <summary>The webhook body's version. A receiver that sees a version it does not know should not purge.</summary>
    public const int Version = 1;

    private static readonly string UserAgent = "TheBuilder.CacheTags/" + (typeof(WebhookTarget).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0");

    public const string ValidationMessage = "Each enabled CacheTags:Targets entry of type webhook needs an absolute https Url (http only for localhost) and a Secret of at least 32 characters.";

    public string Type => TypeName;

    /// <summary>Whether a target is usable, if it is an enabled webhook. Other types validate themselves.</summary>
    public static bool IsValid(CacheTagsTargetOptions target)
        => target.Enabled is false
            || string.Equals(target.Type, TypeName, StringComparison.OrdinalIgnoreCase) is false
            || (Uri.TryCreate(target.Url, UriKind.Absolute, out Uri? url)
                && (url.Scheme == Uri.UriSchemeHttps || (url.Scheme == Uri.UriSchemeHttp && url.IsLoopback))
                && target.Secret.Length >= 32);

    public async Task<string?> SendAsync(PurgeRequest request, CacheTagsTargetOptions target, CancellationToken cancellationToken)
    {
        using HttpRequestMessage message = Request(new Uri(target.Url), target.Secret, request, time.GetUtcNow().ToUnixTimeSeconds());
        try
        {
            using HttpResponseMessage response = await httpClientFactory.CreateClient(HttpClientName).SendAsync(message, cancellationToken);
            return response.IsSuccessStatusCode ? null : $"{(int)response.StatusCode} {response.ReasonPhrase}";
        }
        catch (Exception exception) when (exception is HttpRequestException || (exception is TaskCanceledException && cancellationToken.IsCancellationRequested is false))
        {
            return exception.Message;
        }
    }

    public static HttpRequestMessage Request(Uri url, string secret, PurgeRequest request, long timestamp)
    {
        var body = Body(request);
        var message = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        message.Headers.Add(TimestampHeader, timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
        message.Headers.Add(SignatureHeader, Sign(secret, "POST", url.AbsolutePath, timestamp, body));
        message.Headers.UserAgent.ParseAdd(UserAgent);
        return message;
    }

    public static string Body(PurgeRequest request)
        => JsonSerializer.Serialize(
            new WebhookBody(Version, request.Id.ToString("D"), request.ChangeId.ToString("D"), request.Expire, request.Stale),
            WebhookJson.Default.WebhookBody);

    public static string Sign(string secret, string method, string path, long timestamp, string body)
    {
        var signed = $"{method}\n{path}\n{timestamp}\n{body}";
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(signed));
        return "sha256=" + Convert.ToHexStringLower(mac);
    }
}

internal sealed record WebhookBody(int Version, string Id, string ChangeId, IReadOnlyList<string> Expire, IReadOnlyList<string> Stale);

[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(WebhookBody))]
internal sealed partial class WebhookJson : System.Text.Json.Serialization.JsonSerializerContext;
