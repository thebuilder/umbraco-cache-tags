using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using TheBuilder.CacheTags.Configuration;

namespace TheBuilder.CacheTags.Delivery;

/// <summary>
///     Purges a Cloudflare zone by cache tag, for a site where Cloudflare caches the responses that carry
///     the package's keys (with <c>CacheTags:HeaderName</c> set to <c>Cache-Tag</c> on whatever origin
///     Cloudflare caches). Configured with <c>"Type": "cloudflare"</c> and the settings
///     <c>ZoneId</c> and <c>ApiToken</c> (a token with the Zone > Cache Purge permission). Cloudflare
///     has no "serve stale" purge, so stale keys are purged too.
/// </summary>
internal sealed class CloudflareTarget(IHttpClientFactory httpClientFactory) : IPurgeTarget
{
    public const string TypeName = "cloudflare";
    public const string ZoneIdSetting = "ZoneId";
    public const string ApiTokenSetting = "ApiToken";

    /// <summary>The most tags Cloudflare purges in one call.</summary>
    public const int MaxTagsPerCall = 100;

    public const string ValidationMessage = "Each enabled CacheTags:Targets entry of type cloudflare needs Settings:ZoneId and Settings:ApiToken.";

    private static readonly Uri Api = new("https://api.cloudflare.com/client/v4/");

    public string Type => TypeName;

    /// <summary>Whether a target is usable, if it is an enabled Cloudflare target. Other types validate themselves.</summary>
    public static bool IsValid(CacheTagsTargetOptions target)
        => target.Enabled is false
            || string.Equals(target.Type, TypeName, StringComparison.OrdinalIgnoreCase) is false
            || (Setting(target, ZoneIdSetting) is not null && Setting(target, ApiTokenSetting) is not null);

    public async Task<string?> SendAsync(PurgeRequest request, CacheTagsTargetOptions target, CancellationToken cancellationToken)
    {
        HttpClient client = httpClientFactory.CreateClient(WebhookTarget.HttpClientName);
        foreach (HttpRequestMessage message in Requests(request, Setting(target, ZoneIdSetting)!, Setting(target, ApiTokenSetting)!))
        {
            using (message)
            {
                try
                {
                    using HttpResponseMessage response = await client.SendAsync(message, cancellationToken);
                    if (response.IsSuccessStatusCode is false)
                    {
                        return $"Cloudflare answered {(int)response.StatusCode}: {await Errors(response, cancellationToken)}";
                    }
                }
                catch (Exception exception) when (exception is HttpRequestException || (exception is TaskCanceledException && cancellationToken.IsCancellationRequested is false))
                {
                    return exception.Message;
                }
            }
        }

        return null;
    }

    /// <summary>The purge calls for a request: every key (expire and stale) as tags, at most <see cref="MaxTagsPerCall" /> per call.</summary>
    public static IEnumerable<HttpRequestMessage> Requests(PurgeRequest request, string zoneId, string apiToken)
        => request.Expire.Concat(request.Stale)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Chunk(MaxTagsPerCall)
            .Select(tags =>
            {
                var message = new HttpRequestMessage(HttpMethod.Post, new Uri(Api, $"zones/{zoneId}/purge_cache"))
                {
                    Content = JsonContent.Create(new CloudflarePurge(tags), CloudflareJson.Default.CloudflarePurge),
                };
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
                return message;
            });

    private static string? Setting(CacheTagsTargetOptions target, string name)
        => target.Settings.TryGetValue(name, out var value) && string.IsNullOrWhiteSpace(value) is false ? value : null;

    // Cloudflare explains a failure in errors[].message; anything else is cut short.
    private static async Task<string> Errors(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            CloudflareResult? result = JsonSerializer.Deserialize(body, CloudflareJson.Default.CloudflareResult);
            if (result?.Errors is { Count: > 0 } errors)
            {
                return string.Join("; ", errors.Select(error => $"{error.Code} {error.Message}"));
            }
        }
        catch (JsonException)
        {
        }

        return body.Length > 200 ? body[..200] : body;
    }
}

internal sealed record CloudflarePurge(IReadOnlyList<string> Tags);

internal sealed record CloudflareResult(bool Success, IReadOnlyList<CloudflareError>? Errors);

internal sealed record CloudflareError(int Code, string Message);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CloudflarePurge))]
[JsonSerializable(typeof(CloudflareResult))]
internal sealed partial class CloudflareJson : JsonSerializerContext;
