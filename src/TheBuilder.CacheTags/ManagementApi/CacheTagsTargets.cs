using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using TheBuilder.CacheTags.Configuration;
using TheBuilder.CacheTags.Delivery;
using TheBuilder.CacheTags.Keys;
using TheBuilder.CacheTags.Targets;

namespace TheBuilder.CacheTags.ManagementApi;

/// <summary>
///     What the targets API answers. Public only because the controller's constructor takes it; its
///     members are internal, so nothing outside the package can implement or call it.
/// </summary>
public interface ICacheTagsTargets
{
    internal TargetsView List();

    internal TargetSaveResult Add(TargetDefinition target);

    internal TargetSaveResult Update(string name, TargetDefinition target);

    internal bool Delete(string name);

    internal Task<TargetTestResult> TestAsync(TargetDefinition target, CancellationToken cancellationToken);
}

/// <summary>Every target, and the types a target can have.</summary>
internal sealed record TargetsView(IReadOnlyList<string> Types, IReadOnlyList<TargetView> Targets);

/// <summary>
///     One target. A configured one shows only the names of its headers and settings, never their
///     values or its secret: those are the site's configuration, not the backoffice's.
/// </summary>
internal sealed record TargetView(
    string Name,
    string Type,
    bool Enabled,
    bool FromConfiguration,
    string Url,
    int MaxKeysPerRequest,
    TargetValue? Secret,
    IReadOnlyList<NamedValue> Headers,
    IReadOnlyList<NamedValue> Settings,
    string? Problem);

internal sealed record TargetSaveResult(bool Found, string? Error)
{
    public static readonly TargetSaveResult Saved = new(true, null);
    public static readonly TargetSaveResult NotFound = new(false, null);

    public static TargetSaveResult Invalid(string error) => new(true, error);
}

internal sealed record TargetTestResult(bool Success, string? Error);

internal sealed class CacheTagsTargets(
    TargetStore store,
    PurgeTargets targets,
    IEnumerable<IPurgeTarget> senders,
    IConfiguration configuration,
    IOptionsMonitor<CacheTagsOptions> options) : ICacheTagsTargets
{
    /// <summary>The one key a test purge names. Nothing is tagged with it, so purging it changes nothing.</summary>
    public const string TestKey = "x-cache-tags-test";

    public TargetsView List()
        => new(
            targets.Types,
            targets.Current().Values.Select(View).OrderBy(target => target.Name, StringComparer.OrdinalIgnoreCase).ToList());

    private static TargetView View(ResolvedTarget target)
        => target.Definition is { } stored
            ? new TargetView(stored.Name, stored.Type, stored.Enabled, FromConfiguration: false, stored.Url, stored.MaxKeysPerRequest, stored.Secret, stored.Headers, stored.Settings, target.Problem)
            : new TargetView(
                target.Name,
                target.Options.Type,
                target.Options.Enabled,
                FromConfiguration: true,
                WithoutSecrets(target.Options.Url),
                target.Options.MaxKeysPerRequest,
                Secret: null,
                target.Options.Headers.Keys.Select(name => new NamedValue(name)).ToList(),
                target.Options.Settings.Keys.Select(name => new NamedValue(name)).ToList(),
                Problem: null);

    // A configured URL can carry a token in its query or user info; neither is the backoffice's to show.
    internal static string WithoutSecrets(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            ? new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty, Query = string.Empty, Fragment = string.Empty }.Uri.ToString()
            : string.Empty;

    public TargetSaveResult Add(TargetDefinition target)
    {
        target = Normalise(target);
        if (Invalid(target) is { } error)
        {
            return TargetSaveResult.Invalid(error);
        }

        if (store.Add(target) is false)
        {
            return TargetSaveResult.Invalid($"A target named {target.Name} exists.");
        }

        targets.Invalidate();
        return TargetSaveResult.Saved;
    }

    public TargetSaveResult Update(string name, TargetDefinition target)
    {
        // The name is what queued purges are sent under, so it never changes.
        target = Normalise(target with { Name = name });
        if (Invalid(target) is { } error)
        {
            return TargetSaveResult.Invalid(error);
        }

        if (store.Update(target) is false)
        {
            return TargetSaveResult.NotFound;
        }

        targets.Invalidate();
        return TargetSaveResult.Saved;
    }

    public bool Delete(string name)
    {
        var deleted = store.Delete(name, keepPurges: options.CurrentValue.Targets.ContainsKey(name));
        targets.Invalidate();
        return deleted;
    }

    /// <summary>
    ///     Sends a target, saved or not, a purge of <see cref="TestKey" /> straight away, outside the
    ///     ledger, and says whether it was accepted.
    /// </summary>
    public async Task<TargetTestResult> TestAsync(TargetDefinition target, CancellationToken cancellationToken)
    {
        target = Normalise(target);
        if (target.Name.Length == 0)
        {
            // A draft can be tested before it is named.
            target = target with { Name = "test" };
        }

        if (TargetValidator.Validate(target, targets.Types) is { } invalid)
        {
            return new TargetTestResult(false, invalid);
        }

        CacheTagsOptions settings = options.CurrentValue;
        ResolvedTarget resolved = TargetResolver.Resolve(target with { Enabled = true }, targets.Types, configuration, settings.AllowPrivateNetworkTargets);
        if (resolved.Problem is not null)
        {
            return new TargetTestResult(false, resolved.Problem);
        }

        IPurgeTarget sender = senders.First(sender => string.Equals(sender.Type, target.Type, StringComparison.OrdinalIgnoreCase));
        var request = new PurgeRequest
        {
            Id = Guid.NewGuid(),
            ChangeId = Guid.Empty,
            TargetName = target.Name,
            Expire = [CacheKeys.WithPrefix(settings.KeyPrefix, TestKey)],
            Stale = [],
        };
        try
        {
            var error = await sender.SendAsync(request, resolved.Options, cancellationToken);
            return new TargetTestResult(error is null, error);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || cancellationToken.IsCancellationRequested is false)
        {
            return new TargetTestResult(false, exception.Message);
        }
    }

    private string? Invalid(TargetDefinition target)
        => options.CurrentValue.Targets.ContainsKey(target.Name)
            ? $"CacheTags:Targets in configuration already has a target named {target.Name}."
            : TargetValidator.Validate(target, targets.Types);

    // Trimmed names, the registered type's own spelling, and an empty value or setting as none.
    private TargetDefinition Normalise(TargetDefinition target) => target with
    {
        Name = (target.Name ?? string.Empty).Trim(),
        Type = targets.Types.FirstOrDefault(type => string.Equals(type, (target.Type ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase)) ?? (target.Type ?? string.Empty).Trim(),
        Url = (target.Url ?? string.Empty).Trim(),
        Secret = target.Secret is { } secret ? new TargetValue(Empty(secret.Value), Empty(secret.Setting?.Trim())) : null,
        Headers = (target.Headers ?? []).Where(header => header is not null).Select(Normalise).ToList(),
        Settings = (target.Settings ?? []).Where(setting => setting is not null).Select(Normalise).ToList(),
    };

    private static NamedValue Normalise(NamedValue value) => new((value.Name ?? string.Empty).Trim(), Empty(value.Value), Empty(value.Setting?.Trim()));

    private static string? Empty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
