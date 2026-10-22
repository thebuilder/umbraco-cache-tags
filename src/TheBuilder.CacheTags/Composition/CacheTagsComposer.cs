using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using TheBuilder.CacheTags.Collection;
using TheBuilder.CacheTags.Configuration;
using TheBuilder.CacheTags.Delivery;
using TheBuilder.CacheTags.Ledger;
using TheBuilder.CacheTags.ManagementApi;
using TheBuilder.CacheTags.Migrations;
using TheBuilder.CacheTags.Storage;
using TheBuilder.CacheTags.Targets;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Web.Common.ApplicationBuilder;
using Umbraco.Extensions;

namespace TheBuilder.CacheTags.Composition;

/// <summary>
///     Installs the package: tags Delivery API responses, records what each covers, and plans the purge
///     for every change made on this server. Settings are under <c>CacheTags</c>.
/// </summary>
public sealed class CacheTagsComposer : IComposer
{
    /// <inheritdoc />
    public void Compose(IUmbracoBuilder builder)
    {
        builder.PackageMigrationPlans().Add(typeof(CacheTagsMigrationPlan));
        builder.Services.AddOptions<CacheTagsOptions>()
            .Bind(builder.Config.GetSection(CacheTagsOptions.SectionName))
            .Validate(options => options.HeaderName.Length > 0 && options.HeaderName.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'), "CacheTags:HeaderName must be a header name of letters, digits and hyphens.")
            .Validate(options => options.KeyPrefix.Length == 0 || (options.KeyPrefix.EndsWith('-') && options.KeyPrefix.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-')), "CacheTags:KeyPrefix may contain only lowercase letters, digits and hyphens, and must end in a hyphen.")
            .Validate(options => options.MaxItemKeys > 0, "CacheTags:MaxItemKeys must be above 0.")
            .Validate(options => options.MaxDescendants >= 0, "CacheTags:MaxDescendants must be 0 or more.")
            .Validate(options => options.MappingRetention > TimeSpan.Zero && options.LedgerRetention > TimeSpan.Zero, "CacheTags:MappingRetention and LedgerRetention must be above zero.")
            .Validate(options => options.IndexTimeout > TimeSpan.Zero, "CacheTags:IndexTimeout must be above zero.")
            .Validate(options => options.Targets.Values.All(t => t.MaxKeysPerRequest > 0), "CacheTags:Targets MaxKeysPerRequest must be above 0.")
            .Validate(options => options.Targets.Values.All(WebhookTarget.IsValid), WebhookTarget.ValidationMessage)
            .Validate(options => options.Targets.Values.All(CloudflareTarget.IsValid), CloudflareTarget.ValidationMessage)
            .Validate(options => options.AckTimeout > TimeSpan.Zero && options.ServerTimeout > Heartbeat.Interval && options.PollInterval > TimeSpan.Zero && options.MaxAttempts > 0, "CacheTags:AckTimeout, PollInterval and MaxAttempts must be above zero, and ServerTimeout above 30 seconds.")
            .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<CacheTagsOptions>, ConfiguredTargetHeaders>();
        builder.Services.TryAddSingleton(TimeProvider.System);

        // Collecting.
        builder.Services.AddSingleton<ReferenceReader>();
        builder.Services.AddScoped<ResponseCollector>();
        builder.Services.AddScoped<CacheTagsResponse>();
        builder.Services.AddScoped<ICacheTagsResponse>(services => services.GetRequiredService<CacheTagsResponse>());
        builder.Services.AddScoped<CacheKeyResultFilter>();
        builder.Services.Configure<MvcOptions>(options => options.Filters.AddService<CacheKeyResultFilter>());
        builder.Services.AddSingleton<IDeliveryApiOutputCacheTagProvider, OutputCacheTags>();
        builder.Services.Configure<UmbracoPipelineOptions>(options => options.AddFilter(SeenPipeline.Filter()));

        // The mapping.
        builder.Services.AddSingleton<PackageDatabase>();
        builder.Services.TryAddSingleton<ICacheTagStore, UmbracoCacheTagStore>();
        builder.Services.AddSingleton<MappingWriter>();
        builder.Services.AddHostedService(services => services.GetRequiredService<MappingWriter>());
        builder.Services.AddRecurringBackgroundJob<MappingPruneJob>();

        // The ledger, on the server that makes each change.
        builder.Services.AddSingleton<IDescendants, NavigationDescendants>();
        builder.Services.AddSingleton<PurgePlanner>();
        builder.Services.AddSingleton<LedgerStore>();
        builder.Services.AddSingleton<ChangeRecorder>();
        builder.Services.AddSingleton<ICacheTagsChanges, CacheTagsChanges>();
        builder.Services.AddSingleton<PendingOperations>();
        builder
            .AddNotificationHandler<ContentPublishingNotification, SourceChangeHandlers>()
            .AddNotificationHandler<ContentPublishedNotification, SourceChangeHandlers>()
            .AddNotificationHandler<ContentUnpublishedNotification, SourceChangeHandlers>()
            .AddNotificationHandler<ContentMovedNotification, SourceChangeHandlers>()
            .AddNotificationHandler<ContentMovedToRecycleBinNotification, SourceChangeHandlers>()
            .AddNotificationHandler<ContentDeletedNotification, SourceChangeHandlers>()
            .AddNotificationHandler<ContentSortingNotification, SourceChangeHandlers>()
            .AddNotificationHandler<ContentSortedNotification, SourceChangeHandlers>()
            .AddNotificationHandler<MediaSavedNotification, SourceChangeHandlers>()
            .AddNotificationHandler<MediaMovedNotification, SourceChangeHandlers>()
            .AddNotificationHandler<MediaMovedToRecycleBinNotification, SourceChangeHandlers>()
            .AddNotificationHandler<MediaDeletedNotification, SourceChangeHandlers>()
            .AddNotificationHandler<ContentTypeChangedNotification, SourceChangeHandlers>()
            .AddNotificationHandler<MediaTypeChangedNotification, SourceChangeHandlers>()
            .AddNotificationHandler<DataTypeSavedNotification, SourceChangeHandlers>()
            .AddNotificationHandler<DomainSavedNotification, SourceChangeHandlers>()
            .AddNotificationHandler<DomainDeletedNotification, SourceChangeHandlers>()
            .AddNotificationHandler<LanguageSavedNotification, SourceChangeHandlers>()
            .AddNotificationHandler<LanguageDeletedNotification, SourceChangeHandlers>()
            .AddNotificationHandler<PublicAccessEntrySavedNotification, SourceChangeHandlers>()
            .AddNotificationHandler<PublicAccessEntryDeletedNotification, SourceChangeHandlers>();

        // Delivery: heartbeats, acks from every server's cache refreshes, and the purge outbox.
        builder.Services.AddSingleton<ServerIdentity>();
        builder.Services.AddSingleton<DeliverySignal>();
        builder.Services.TryAddSingleton<IAckTransport, DatabaseAckTransport>();
        builder.Services.AddSingleton<PurgeResolver>();
        builder.Services.AddSingleton<OutputCacheEviction>();
        builder.Services.AddSingleton<AckProcessor>();
        builder.Services.AddSingleton<LocalDeliveryIndex>();
        builder.Services.AddSingleton<IndexStage>();
        builder.ContentIndexHandlers().Add<IndexStamp>();
        builder.Services.AddSingleton<Releaser>();
        builder.Services.AddSingleton<PurgeSender>();
        builder.Services.AddHostedService<Heartbeat>();
        builder.Services.AddHostedService<DeliveryWorker>();
        builder.Services.AddSingleton<IPurgeTarget, WebhookTarget>();
        builder.Services.AddSingleton<IPurgeTarget, CloudflareTarget>();
        builder.Services.AddHttpClient(WebhookTarget.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(10))
            // A redirect would carry the signed purge to another host; a target must answer itself.
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        builder.Services.AddHttpClient(WebhookTarget.PublicHttpClientName, client => client.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(TargetNetwork.PublicOnlyHandler);

        // Targets added in the backoffice, merged with those in configuration.
        builder.Services.AddSingleton<TargetStore>();
        builder.Services.AddSingleton<PurgeTargets>();
        builder
            .AddNotificationAsyncHandler<ContentCacheRefresherNotification, RefresherHandlers>()
            .AddNotificationAsyncHandler<MediaCacheRefresherNotification, RefresherHandlers>()
            .AddNotificationAsyncHandler<ContentTypeCacheRefresherNotification, RefresherHandlers>()
            .AddNotificationAsyncHandler<DataTypeCacheRefresherNotification, RefresherHandlers>()
            .AddNotificationAsyncHandler<DomainCacheRefresherNotification, RefresherHandlers>()
            .AddNotificationAsyncHandler<LanguageCacheRefresherNotification, RefresherHandlers>()
            .AddNotificationAsyncHandler<PublicAccessCacheRefresherNotification, RefresherHandlers>();

        // The lookup API.
        builder.Services.AddSingleton<ICacheTagsLookup, CacheTagsLookup>();
        builder.Services.AddSingleton<ICacheTagsOutbox, CacheTagsOutbox>();
        builder.Services.AddSingleton<ICacheTagsTargets, CacheTagsTargets>();
        builder.Services.AddSingleton<IAuthorizationHandler, AdministratorHandler>();
        builder.Services.AddAuthorization(options => options.AddPolicy(
            AdministratorRequirement.Policy,
            policy => policy.AddRequirements(new AdministratorRequirement())));
    }
}
