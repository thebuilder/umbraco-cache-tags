using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Sync;

namespace TheBuilder.CacheTags.Delivery;

/// <summary>
///     Turns this server's cache refreshes into acknowledgements. Umbraco raises these after it has
///     updated the published cache, navigation and routing on this server. A failure is logged, never thrown: a throwing handler would stop Umbraco's
///     remaining notifications for the refresh.
/// </summary>
internal sealed class RefresherHandlers(AckProcessor processor, ILogger<RefresherHandlers> logger)
    : INotificationAsyncHandler<ContentCacheRefresherNotification>,
        INotificationAsyncHandler<MediaCacheRefresherNotification>,
        INotificationAsyncHandler<ContentTypeCacheRefresherNotification>,
        INotificationAsyncHandler<DataTypeCacheRefresherNotification>,
        INotificationAsyncHandler<DomainCacheRefresherNotification>,
        INotificationAsyncHandler<LanguageCacheRefresherNotification>,
        INotificationAsyncHandler<PublicAccessCacheRefresherNotification>
{
    public Task HandleAsync(ContentCacheRefresherNotification notification, CancellationToken cancellationToken)
        => Observe(notification, () => notification.MessageType == MessageType.RefreshByPayload && notification.MessageObject is ContentCacheRefresher.JsonPayload[] payloads
            ? payloads.Where(p => p.Blueprint is false).Select(p => new ObservedRefresh(
                AckSignal.Content,
                p.ChangeTypes.HasFlag(Umbraco.Cms.Core.Services.Changes.TreeChangeTypes.RefreshAll) ? null : p.Key,
                p.ChangeTypes,
                p.PublishedCultures is not null,
                p.UnpublishedCultures is not null)).ToList()
            : [new ObservedRefresh(AckSignal.Content, null)]);

    public Task HandleAsync(MediaCacheRefresherNotification notification, CancellationToken cancellationToken)
        => Observe(notification, () => notification.MessageType == MessageType.RefreshByPayload && notification.MessageObject is MediaCacheRefresher.JsonPayload[] payloads
            ? payloads.Select(p => new ObservedRefresh(
                AckSignal.Media,
                p.ChangeTypes.HasFlag(Umbraco.Cms.Core.Services.Changes.TreeChangeTypes.RefreshAll) ? null : p.Key,
                p.ChangeTypes)).ToList()
            : [new ObservedRefresh(AckSignal.Media, null)]);

    // Type and site-wide refreshes name the item they refreshed (see AckSignal.Source); one without a
    // payload refreshes every item of its refresher.
    public Task HandleAsync(ContentTypeCacheRefresherNotification notification, CancellationToken cancellationToken)
        => Observe(notification, () => Sources(notification, AckSignal.Types, AckSignal.ContentTypes, (ContentTypeCacheRefresher.JsonPayload payload) => payload.Id));

    public Task HandleAsync(DataTypeCacheRefresherNotification notification, CancellationToken cancellationToken)
        => Observe(notification, () => Sources(notification, AckSignal.Types, AckSignal.DataTypes, (DataTypeCacheRefresher.JsonPayload payload) => payload.Key));

    public Task HandleAsync(DomainCacheRefresherNotification notification, CancellationToken cancellationToken)
        => Observe(notification, () => Sources(notification, AckSignal.Everything, AckSignal.Domains, (DomainCacheRefresher.JsonPayload payload) => payload.Id));

    public Task HandleAsync(LanguageCacheRefresherNotification notification, CancellationToken cancellationToken)
        => Observe(notification, () => Sources(notification, AckSignal.Everything, AckSignal.Languages, (LanguageCacheRefresher.JsonPayload payload) => payload.Id));

    public Task HandleAsync(PublicAccessCacheRefresherNotification notification, CancellationToken cancellationToken)
        => Observe(notification, () => [new ObservedRefresh(AckSignal.Everything, null, Source: AckSignal.Source(AckSignal.PublicAccess))]);

    private static IReadOnlyCollection<ObservedRefresh> Sources<TPayload>(CacheRefresherNotification notification, string kind, string refresher, Func<TPayload, object> id)
        => notification.MessageType == MessageType.RefreshByPayload && notification.MessageObject is TPayload[] payloads
            ? payloads.Select(payload => new ObservedRefresh(kind, null, Source: AckSignal.Source(refresher, id(payload)))).ToList()
            : [new ObservedRefresh(kind, null, Source: AckSignal.Source(refresher))];

    private Task Observe(CacheRefresherNotification notification, Func<IReadOnlyCollection<ObservedRefresh>> read)
    {
        try
        {
            processor.Observe(read());
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Cache tags could not acknowledge a {Refresher}; changes it completes are sent after the ack timeout.", notification.GetType().Name);
        }

        return Task.CompletedTask;
    }
}
