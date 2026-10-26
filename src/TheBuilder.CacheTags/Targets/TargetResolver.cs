using Microsoft.Extensions.Configuration;
using TheBuilder.CacheTags.Configuration;

namespace TheBuilder.CacheTags.Targets;

/// <summary>A target ready to send to, or what stops it on this server.</summary>
/// <param name="Name">The name purges are queued under.</param>
/// <param name="Options">The target, with every secret it names read.</param>
/// <param name="FromConfiguration">Whether it is configured in appsettings rather than the backoffice.</param>
/// <param name="Problem">Why purges to it fail on this server, or null. Never names a secret.</param>
/// <param name="Definition">For a backoffice target, what is stored.</param>
internal sealed record ResolvedTarget(string Name, CacheTagsTargetOptions Options, bool FromConfiguration, string? Problem = null, TargetDefinition? Definition = null);

/// <summary>Turns a backoffice target into the options its <see cref="Delivery.IPurgeTarget" /> sends with.</summary>
internal static class TargetResolver
{
    public static ResolvedTarget Resolve(TargetDefinition target, IEnumerable<string> types, IConfiguration configuration, bool allowPrivateNetwork)
    {
        if (target.Unreadable)
        {
            return new ResolvedTarget(target.Name, new CacheTagsTargetOptions(), FromConfiguration: false, "The target's stored settings could not be read. Save it again.", target);
        }

        var problems = new List<string>();
        string Read(string what, string? value, string? setting)
        {
            if (value is not null)
            {
                return value;
            }

            if (string.IsNullOrEmpty(setting))
            {
                problems.Add($"{what} has no value and names no secret.");
                return string.Empty;
            }

            // Only under CacheTags:Secrets, so a target cannot read any other configuration value.
            var configured = configuration[$"{TargetValidator.SecretsSection}:{setting}"];
            if (string.IsNullOrEmpty(configured))
            {
                // Not the secret's name: an error is shown in the backoffice and kept with the purge,
                // and the name field is where a secret gets pasted by mistake.
                problems.Add($"{what} names a secret that is not in {TargetValidator.SecretsSection} on this server.");
                return string.Empty;
            }

            if (configured.Contains('\r') || configured.Contains('\n'))
            {
                problems.Add($"{what} reads a secret with a line break in it.");
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
            invalid = $"The signing secret in {TargetValidator.SecretsSection} holds fewer than 32 characters.";
        }

        return new ResolvedTarget(target.Name, options, FromConfiguration: false, invalid ?? problems.FirstOrDefault(), target);
    }
}
