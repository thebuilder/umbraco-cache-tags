using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
        builder.Services.AddCacheTagsOptions(builder.Config);
        builder.Services.TryAddSingleton(TimeProvider.System);

        // Collecting.
        builder.Services.AddSingleton<ReferenceReader>();
        builder.Services.AddSingleton<KnownNames>();
        builder.Services.AddScoped<ResponseCollector>();
        builder.Services.AddScoped<CacheTagsResponse>();
        builder.Services.AddScoped<ICacheTagsResponse>(services => services.GetRequiredService<CacheTagsResponse>());
        builder.Services.AddScoped<CacheKeyResultFilter>();
        builder.Services.Configure<MvcOptions>(options => options.Filters.AddService<CacheKeyResultFilter>());
        builder.Services.AddSingleton<IDeliveryApiOutputCacheTagProvider, OutputCacheTags>();

        // The package's tables.
        builder.Services.AddSingleton<PackageDatabase>();
        builder.Services.AddRecurringBackgroundJob<LedgerPruneJob>();

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
            .AddNotificationHandler<ContentTypeSavingNotification, SourceChangeHandlers>()
            .AddNotificationHandler<MediaTypeSavingNotification, SourceChangeHandlers>()
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

        // The backoffice's Changes view.
        builder.Services.AddSingleton<ICacheTagsChangeLog, CacheTagsChangeLog>();
        builder.Services.AddSingleton<ICacheTagsOutbox, CacheTagsOutbox>();
        builder.Services.AddSingleton<ICacheTagsTargets, CacheTagsTargets>();
        builder.Services.AddSingleton<IAuthorizationHandler, AdministratorHandler>();
        builder.Services.AddAuthorization(options => options.AddPolicy(
            AdministratorRequirement.Policy,
            policy => policy.AddRequirements(new AdministratorRequirement())));
    }
}
