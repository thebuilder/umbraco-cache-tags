using System.Text.RegularExpressions;
using TheBuilder.CacheTags.Configuration;
using TheBuilder.CacheTags.Delivery;

namespace TheBuilder.CacheTags.Targets;

/// <summary>
///     The rules a target must meet: for one added in the backoffice, on save and again before every
///     purge; for headers in configuration, at startup. Each rule answers with what is wrong, or null.
/// </summary>
internal static partial class TargetValidator
{
    public const int MaxNameLength = 100;
    public const int MaxHeaders = 20;
    public const int MaxSettings = 20;
    public const int MaxValueLength = 1000;
    public const int MaxSettingNameLength = 200;

    /// <summary>
    ///     Where a backoffice target's secrets are read from. A target names a secret, never a whole
    ///     configuration key, so an administrator can only send values someone put here.
    /// </summary>
    public const string SecretsSection = "CacheTags:Secrets";
    public const int MaxKeysPerRequestLimit = 10_000;

    /// <summary>Headers the purge request sets itself.</summary>
    private static readonly HashSet<string> ManagedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection",
        "Content-Length",
        "Content-Type",
        "Host",
        "Transfer-Encoding",
        "Upgrade",
        "User-Agent",
        WebhookTarget.TimestampHeader,
        WebhookTarget.SignatureHeader,
    };

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex TargetName();

    // RFC 9110's token: what a header name may contain.
    [GeneratedRegex("^[!#$%&'*+.^_`|~0-9A-Za-z-]+$")]
    private static partial Regex HeaderName();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9]*$")]
    private static partial Regex SettingName();

    // A secret's name under CacheTags:Secrets. No colon, so it cannot reach outside that section.
    [GeneratedRegex("^[A-Za-z0-9_.-]+$")]
    private static partial Regex SecretName();

    /// <summary>What is wrong with a backoffice target, or null. <paramref name="types" /> are the registered target types.</summary>
    public static string? Validate(TargetDefinition target, IEnumerable<string> types)
    {
        if (target.Name.Length > MaxNameLength || TargetName().IsMatch(target.Name) is false)
        {
            return $"The name must be lowercase letters, digits and hyphens, starting with a letter or digit, at most {MaxNameLength} characters.";
        }

        if (types.Contains(target.Type, StringComparer.OrdinalIgnoreCase) is false)
        {
            return $"No target type '{target.Type}' is registered.";
        }

        if (target.MaxKeysPerRequest is < 1 or > MaxKeysPerRequestLimit)
        {
            return $"Keys per request must be from 1 to {MaxKeysPerRequestLimit}.";
        }

        if (Is(target, WebhookTarget.TypeName))
        {
            return ValidateWebhook(target);
        }

        if (target.Headers.Count > 0 && Is(target, CloudflareTarget.TypeName))
        {
            return "A Cloudflare target sends no request headers.";
        }

        return ValidateHeaders(target.Headers) ?? ValidateSettings(target);
    }

    private static string? ValidateWebhook(TargetDefinition target)
    {
        if (Uri.TryCreate(target.Url, UriKind.Absolute, out Uri? url) is false
            || (url.Scheme != Uri.UriSchemeHttps && (url.Scheme != Uri.UriSchemeHttp || url.IsLoopback is false)))
        {
            return "The URL must be an absolute https URL (http only for localhost).";
        }

        if (target.Settings.Count > 0)
        {
            return "A webhook has no settings; use request headers.";
        }

        if (target.Secret is null)
        {
            return "The webhook needs a signing secret.";
        }

        return ValidateValue("The signing secret", target.Secret.Value, target.Secret.Setting)
            ?? (target.Secret.Value is { Length: < 32 } ? "The signing secret must be at least 32 characters." : null)
            ?? ValidateHeaders(target.Headers);
    }

    private static string? ValidateSettings(TargetDefinition target)
    {
        if (target.Settings.Count > MaxSettings)
        {
            return $"A target has at most {MaxSettings} settings.";
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (NamedValue setting in target.Settings)
        {
            if (setting.Name.Length > MaxNameLength || SettingName().IsMatch(setting.Name) is false)
            {
                return $"Setting names are letters and digits, starting with a letter, at most {MaxNameLength} characters.";
            }

            if (seen.Add(setting.Name) is false)
            {
                return $"The setting '{setting.Name}' is there twice.";
            }

            if (ValidateValue($"The setting '{setting.Name}'", setting.Value, setting.Setting) is { } error)
            {
                return error;
            }
        }

        if (Is(target, CloudflareTarget.TypeName))
        {
            foreach (var required in new[] { CloudflareTarget.ZoneIdSetting, CloudflareTarget.ApiTokenSetting })
            {
                if (seen.Contains(required) is false)
                {
                    return $"A Cloudflare target needs the setting {required}.";
                }
            }
        }

        return null;
    }

    /// <summary>What is wrong with a list of request headers, or null. Values may be typed in or name a secret.</summary>
    public static string? ValidateHeaders(IReadOnlyCollection<NamedValue> headers)
    {
        if (headers.Count > MaxHeaders)
        {
            return $"A target sends at most {MaxHeaders} request headers.";
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (NamedValue header in headers)
        {
            if (header.Name.Length > MaxNameLength || HeaderName().IsMatch(header.Name) is false)
            {
                return $"'{header.Name}' is not a header name: use letters, digits and hyphens, at most {MaxNameLength} characters.";
            }

            if (ManagedHeaders.Contains(header.Name) || CanAddToRequest(header.Name) is false)
            {
                return $"The request header '{header.Name}' is set by the purge itself.";
            }

            if (seen.Add(header.Name) is false)
            {
                return $"The request header '{header.Name}' is there twice.";
            }

            if (ValidateValue($"The request header '{header.Name}'", header.Value, header.Setting) is { } error)
            {
                return error;
            }
        }

        return null;
    }

    /// <summary>What is wrong with the headers of the targets in configuration, or null.</summary>
    public static string? ValidateConfigured(IReadOnlyDictionary<string, CacheTagsTargetOptions> targets)
        => targets
            .Select(target => ValidateHeaders(target.Value.Headers.Select(header => new NamedValue(header.Key, header.Value)).ToList()) is { } error
                ? $"CacheTags:Targets:{target.Key}: {error}"
                : null)
            .FirstOrDefault(error => error is not null);

    /// <summary>A value is typed in or names a secret, not both, and a typed value fits on one line.</summary>
    public static string? ValidateValue(string what, string? value, string? setting)
    {
        if (string.IsNullOrEmpty(value) == string.IsNullOrEmpty(setting))
        {
            return $"{what} needs a value or the name of a secret, not both.";
        }

        if (value is not null)
        {
            return value.Length > MaxValueLength || value.Contains('\r') || value.Contains('\n')
                ? $"{what} must be one line of at most {MaxValueLength} characters."
                : null;
        }

        return setting!.Length > MaxSettingNameLength || SecretName().IsMatch(setting) is false
            ? $"{what} names a secret with characters a secret's name cannot have. Use letters, digits, dots, hyphens and underscores."
            : null;
    }

    private static bool Is(TargetDefinition target, string type) => string.Equals(target.Type, type, StringComparison.OrdinalIgnoreCase);

    // Content headers, such as Content-Encoding, cannot go on the request itself.
    private static bool CanAddToRequest(string name)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://localhost");
        return request.Headers.TryAddWithoutValidation(name, "value");
    }
}

/// <summary>Stops startup when a configured target has request headers the package cannot send, saying which.</summary>
internal sealed class ConfiguredTargetHeaders : Microsoft.Extensions.Options.IValidateOptions<CacheTagsOptions>
{
    public Microsoft.Extensions.Options.ValidateOptionsResult Validate(string? name, CacheTagsOptions options)
        => TargetValidator.ValidateConfigured(options.Targets) is { } error
            ? Microsoft.Extensions.Options.ValidateOptionsResult.Fail(error)
            : Microsoft.Extensions.Options.ValidateOptionsResult.Success;
}
