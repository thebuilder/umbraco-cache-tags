using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using TheBuilder.CacheTags.Delivery;
using TheBuilder.CacheTags.Targets;

namespace TheBuilder.CacheTags.Configuration;

internal static class CacheTagsOptionsRegistration
{
    /// <summary>
    ///     Binds and validates <see cref="CacheTagsOptions" />. Invalid settings stop the site at start;
    ///     settings that become invalid on a reload are logged, and the last valid ones stay in use.
    /// </summary>
    public static IServiceCollection AddCacheTagsOptions(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<CacheTagsOptions>()
            .Bind(configuration.GetSection(CacheTagsOptions.SectionName))
            .Validate(options => options.HeaderName.Length > 0 && options.HeaderName.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'), "CacheTags:HeaderName must be a header name of letters, digits and hyphens.")
            .Validate(options => options.KeyPrefix.Length == 0 || (options.KeyPrefix.EndsWith('-') && options.KeyPrefix.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-')), "CacheTags:KeyPrefix may contain only lowercase letters, digits and hyphens, and must end in a hyphen.")
            .Validate(options => options.MaxItemKeys > 0, "CacheTags:MaxItemKeys must be above 0.")
            .Validate(options => options.MaxHeaderKeys > 2, "CacheTags:MaxHeaderKeys must be above 2.")
            .Validate(options => options.MaxDescendants >= 0, "CacheTags:MaxDescendants must be 0 or more.")
            .Validate(options => options.MappingRetention > TimeSpan.Zero && options.LedgerRetention > TimeSpan.Zero, "CacheTags:MappingRetention and LedgerRetention must be above zero.")
            .Validate(options => options.IndexTimeout > TimeSpan.Zero, "CacheTags:IndexTimeout must be above zero.")
            .Validate(options => options.Targets.Values.All(t => t.MaxKeysPerRequest > 0), "CacheTags:Targets MaxKeysPerRequest must be above 0.")
            .Validate(options => options.Targets.Values.All(WebhookTarget.IsValid), WebhookTarget.ValidationMessage)
            .Validate(options => options.Targets.Values.All(CloudflareTarget.IsValid), CloudflareTarget.ValidationMessage)
            .Validate(options => options.AckTimeout > TimeSpan.Zero && options.ServerTimeout > Heartbeat.Interval && options.PollInterval > TimeSpan.Zero && options.MaxAttempts > 0, "CacheTags:AckTimeout, PollInterval and MaxAttempts must be above zero, and ServerTimeout above 30 seconds.")
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<CacheTagsOptions>, ConfiguredTargetHeaders>();
        services.AddSingleton<IOptionsMonitor<CacheTagsOptions>, LastValidOptions<CacheTagsOptions>>();
        return services;
    }
}

/// <summary>
///     An options monitor that keeps the last valid options when a reload fails validation. Microsoft's
///     throws on every read after an invalid edit to appsettings, in requests and in the delivery
///     worker, until the file is fixed. This one logs the error once and keeps the options it had.
///     With no valid options yet (at start) a read throws as usual, so invalid settings stop the site.
/// </summary>
internal sealed class LastValidOptions<TOptions> : IOptionsMonitor<TOptions>, IDisposable
    where TOptions : class
{
    private readonly IOptionsFactory<TOptions> _factory;
    private readonly ILogger<LastValidOptions<TOptions>> _logger;
    private readonly ConcurrentDictionary<string, TOptions> _options = new();
    private readonly List<IDisposable> _registrations = [];
    private event Action<TOptions, string>? Changed;

    public LastValidOptions(IOptionsFactory<TOptions> factory, IEnumerable<IOptionsChangeTokenSource<TOptions>> sources, ILogger<LastValidOptions<TOptions>> logger)
    {
        _factory = factory;
        _logger = logger;
        foreach (IOptionsChangeTokenSource<TOptions> source in sources)
        {
            _registrations.Add(ChangeToken.OnChange(source.GetChangeToken, () => Reload(source.Name ?? Options.DefaultName)));
        }
    }

    public TOptions CurrentValue => Get(Options.DefaultName);

    public TOptions Get(string? name) => _options.GetOrAdd(name ?? Options.DefaultName, _factory.Create);

    public IDisposable? OnChange(Action<TOptions, string?> listener)
    {
        Action<TOptions, string> changed = (options, name) => listener(options, name);
        Changed += changed;
        return new Registration(() => Changed -= changed);
    }

    public void Dispose()
    {
        foreach (IDisposable registration in _registrations)
        {
            registration.Dispose();
        }
    }

    private void Reload(string name)
    {
        TOptions options;
        try
        {
            options = _factory.Create(name);
        }
        catch (Exception exception)
        {
            // Failed validation, or a value that does not bind ("ten" for a number).
            if (_options.ContainsKey(name))
            {
                _logger.LogError(exception, "The reloaded {Options} settings are invalid; the last valid settings stay in use until they are fixed.", typeof(TOptions).Name);
            }

            return;
        }

        _options[name] = options;
        Changed?.Invoke(options, name);
    }

    private sealed class Registration(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
