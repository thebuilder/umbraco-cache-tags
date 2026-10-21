using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TheBuilder.CacheTags.Collection;
using TheBuilder.CacheTags.Configuration;
using TheBuilder.CacheTags.Ledger;
using TheBuilder.CacheTags.ManagementApi;
using TheBuilder.CacheTags.Migrations;
using TheBuilder.CacheTags.Storage;
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
            .Validate(options => options.KeyPrefix.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'), "CacheTags:KeyPrefix may contain only lowercase letters, digits and hyphens.")
            .Validate(options => options.DirectKeyBudget > 0, "CacheTags:DirectKeyBudget must be above 0.")
            .Validate(options => options.MappingRetention > TimeSpan.Zero, "CacheTags:MappingRetention must be above zero.")
            .Validate(options => options.DescendantBudget >= 0, "CacheTags:DescendantBudget must be 0 or more.")
            .ValidateOnStart();
        builder.Services.TryAddSingleton(TimeProvider.System);

        // Collecting.
        builder.Services.AddSingleton<ReferenceReader>();
        builder.Services.Configure<MvcOptions>(options => options.Filters.Add(new CacheKeyResultFilter()));
        builder.Services.AddSingleton<IDeliveryApiOutputCacheTagProvider, ResponseKeyOutputCacheTags>();
        builder.Services.Configure<UmbracoPipelineOptions>(options => options.AddFilter(SeenPipeline.Filter()));

        // The mapping.
        builder.Services.TryAddScoped<ICacheTagStore, UmbracoCacheTagStore>();
        builder.Services.AddSingleton<MappingWriter>();
        builder.Services.AddHostedService(services => services.GetRequiredService<MappingWriter>());
        builder.Services.AddRecurringBackgroundJob<MappingPruneJob>();

        // The ledger, on the server that makes each change.
        builder.Services.AddSingleton<IDescendants, NavigationDescendants>();
        builder.Services.AddSingleton<PurgePlanner>();
        builder.Services.AddSingleton<ChangeRecorder>();
        builder
            .AddNotificationHandler<ContentPublishingNotification, SourceChangeHandlers>()
            .AddNotificationHandler<ContentPublishedNotification, SourceChangeHandlers>()
            .AddNotificationHandler<ContentUnpublishedNotification, SourceChangeHandlers>()
            .AddNotificationHandler<ContentMovedNotification, SourceChangeHandlers>()
            .AddNotificationHandler<ContentMovedToRecycleBinNotification, SourceChangeHandlers>()
            .AddNotificationHandler<ContentDeletedNotification, SourceChangeHandlers>()
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
            .AddNotificationHandler<LanguageDeletedNotification, SourceChangeHandlers>();

        // The lookup API.
        builder.Services.AddSingleton<IAuthorizationHandler, AdministratorHandler>();
        builder.Services.AddAuthorization(options => options.AddPolicy(
            AdministratorRequirement.Policy,
            policy => policy.AddRequirements(new AdministratorRequirement())));
    }
}
