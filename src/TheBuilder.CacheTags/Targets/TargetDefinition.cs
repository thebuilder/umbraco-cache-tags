using System.Text.Json.Serialization;

namespace TheBuilder.CacheTags.Targets;

/// <summary>
///     A target added in the backoffice, as stored. A secret is never stored by the package: a value
///     is either typed in (and stored as typed, in plain text) or names an app setting, which is
///     read when a purge is sent.
/// </summary>
internal sealed record TargetDefinition
{
    /// <summary>Lowercase letters, digits and hyphens. The name purges are queued under, so it never changes.</summary>
    public string Name { get; init; } = string.Empty;

    public string Type { get; init; } = string.Empty;

    public bool Enabled { get; init; } = true;

    /// <summary>For a webhook: where purges are posted.</summary>
    public string Url { get; init; } = string.Empty;

    /// <summary>For a webhook: the signing secret.</summary>
    public TargetValue? Secret { get; init; }

    public int MaxKeysPerRequest { get; init; } = 500;

    /// <summary>For a webhook: request headers sent with every purge.</summary>
    public IReadOnlyList<NamedValue> Headers { get; init; } = [];

    /// <summary>For other types: their settings, such as Cloudflare's ZoneId and ApiToken.</summary>
    public IReadOnlyList<NamedValue> Settings { get; init; } = [];
}

/// <summary>A value typed in, or the name of the app setting that holds it. Exactly one is set.</summary>
internal sealed record TargetValue(string? Value = null, string? Setting = null);

/// <summary>A named <see cref="TargetValue" />: a request header, or a target type's setting.</summary>
internal sealed record NamedValue(string Name, string? Value = null, string? Setting = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TargetDefinition))]
internal sealed partial class TargetJson : JsonSerializerContext;
