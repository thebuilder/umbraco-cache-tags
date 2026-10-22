using Microsoft.Extensions.Configuration;
using TheBuilder.CacheTags.Configuration;

namespace TheBuilder.CacheTags.Targets;

/// <summary>A target ready to send to, or what stops it on this server.</summary>
/// <param name="Name">The name purges are queued under.</param>
/// <param name="Options">The target, with every app setting it names read.</param>
/// <param name="FromConfiguration">Whether it is configured in appsettings rather than the backoffice.</param>
/// <param name="Problem">Why purges to it fail on this server, or null. Never names an app setting.</param>
internal sealed record ResolvedTarget(string Name, CacheTagsTargetOptions Options, bool FromConfiguration, string? Problem = null);

/// <summary>Turns a backoffice target into the options its <see cref="Delivery.IPurgeTarget" /> sends with.</summary>
internal static class TargetResolver
{
    public static ResolvedTarget Resolve(TargetDefinition target, IEnumerable<string> types, IConfiguration configuration, bool allowPrivateNetwork)
    {
        var problems = new List<string>();
        string Read(string what, string? value, string? setting)
        {
            if (value is not null)
            {
                return value;
            }

            var configured = configuration[setting!];
            if (string.IsNullOrEmpty(configured))
            {
                // Not the setting's name: an error is shown in the backoffice and kept with the purge,
                // and the setting field is where a secret gets pasted by mistake.
                problems.Add($"{what} names an app setting that is not set on this server.");
                return string.Empty;
            }

            if (configured.Contains('\r') || configured.Contains('\n'))
            {
                problems.Add($"{what} reads an app setting with a line break in it.");
                return string.Empty;
            }

            return configured;
        }

        var options = new CacheTagsTargetOptions
        {
            Type = target.Type,
            Enabled = target.Enabled,
            Url = target.Url,
            Secret = target.Secret is { } secret ? Read("The signing secret", secret.Value, secret.Setting) : string.Empty,
            MaxKeysPerRequest = target.MaxKeysPerRequest,
            PublicNetworkOnly = allowPrivateNetwork is false,
        };
        foreach (NamedValue header in target.Headers)
        {
            options.Headers[header.Name] = Read($"The request header '{header.Name}'", header.Value, header.Setting);
        }

        foreach (NamedValue setting in target.Settings)
        {
            options.Settings[setting.Name] = Read($"The setting '{setting.Name}'", setting.Value, setting.Setting);
        }

        // Validated again before every purge: the rules may have changed since the target was saved.
        var invalid = TargetValidator.Validate(target, types);
        if (invalid is null && target.Secret is { Setting: not null } && options.Secret.Length is > 0 and < 32)
        {
            invalid = "The signing secret's app setting holds fewer than 32 characters.";
        }

        return new ResolvedTarget(target.Name, options, FromConfiguration: false, invalid ?? problems.FirstOrDefault());
    }
}
