using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Services.Changes;

namespace TheBuilder.CacheTags.Spike.Probe;

/// <summary>
///     Logs each content and media cache refresher notification this server handles, with the
///     order in which the async handlers for it are registered (and so run).
/// </summary>
internal sealed class RefresherProbe(IServiceProvider services)
    : INotificationAsyncHandler<ContentCacheRefresherNotification>, INotificationAsyncHandler<MediaCacheRefresherNotification>
{
    public Task HandleAsync(ContentCacheRefresherNotification notification, CancellationToken cancellationToken)
    {
        object payload = notification.MessageObject is ContentCacheRefresher.JsonPayload[] payloads
            ? payloads.Select(p => new
            {
                p.Id,
                p.Key,
                ChangeTypes = p.ChangeTypes.ToString(),
                p.Blueprint,
                p.PublishedCultures,
                p.UnpublishedCultures,
            }).ToArray()
            : notification.MessageObject.ToString() ?? string.Empty;

        Record("content", notification.MessageType.ToString(), payload, HandlerOrder<ContentCacheRefresherNotification>());
        return Task.CompletedTask;
    }

    public Task HandleAsync(MediaCacheRefresherNotification notification, CancellationToken cancellationToken)
    {
        object payload = notification.MessageObject is MediaCacheRefresher.JsonPayload[] payloads
            ? payloads.Select(p => new { p.Id, p.Key, ChangeTypes = p.ChangeTypes.ToString() }).ToArray()
            : notification.MessageObject.ToString() ?? string.Empty;

        Record("media", notification.MessageType.ToString(), payload, HandlerOrder<MediaCacheRefresherNotification>());
        return Task.CompletedTask;
    }

    private string[] HandlerOrder<T>()
        where T : INotification
        => services.GetServices<INotificationAsyncHandler<T>>().Select(h => h.GetType().Name).ToArray();

    private static void Record(string refresher, string messageType, object payload, string[] order)
        => ProbeLog.Refreshers.Enqueue(new RefresherEntry(ProbeLog.Next(), ProbeLog.CurrentOperation, refresher, messageType, payload, order));
}

/// <summary>
///     Logs what the server making a change sees: the tree change notification (with cultures)
///     and the publish notification, for comparison with the refresher payloads.
/// </summary>
internal sealed class SourceProbe
    : INotificationHandler<ContentTreeChangeNotification>, INotificationHandler<ContentPublishedNotification>, INotificationHandler<ContentSavedNotification>,
        INotificationHandler<ContentUnpublishedNotification>, INotificationHandler<ContentSortedNotification>, INotificationHandler<ContentMovedNotification>,
        INotificationHandler<ContentMovedToRecycleBinNotification>
{
    public void Handle(ContentUnpublishedNotification notification)
        => Record("source:ContentUnpublished", new
        {
            Entities = notification.UnpublishedEntities.Select(c => new { c.Key, c.Name }).ToArray(),
            notification.UnpublishedCultures,
        });

    public void Handle(ContentSortedNotification notification)
        => Record("source:ContentSorted", notification.SortedEntities.Select(c => new { c.Key, c.Name, c.SortOrder }).ToArray());

    public void Handle(ContentMovedNotification notification)
        => Record("source:ContentMoved", notification.MoveInfoCollection.Select(m => new { m.Entity.Key, m.OriginalPath, NewParentId = m.NewParentId }).ToArray());

    public void Handle(ContentMovedToRecycleBinNotification notification)
        => Record("source:ContentMovedToRecycleBin", notification.MoveInfoCollection.Select(m => new { m.Entity.Key, m.OriginalPath }).ToArray());

    public void Handle(ContentTreeChangeNotification notification)
        => Record("source:ContentTreeChange", notification.Changes.Select(c => new
        {
            c.Item.Key,
            c.Item.Name,
            ChangeTypes = c.ChangeTypes.ToString(),
            c.PublishedCultures,
            c.UnpublishedCultures,
        }).ToArray());

    // Logs both the per-key culture map on the notification (17.6+) and IContent.PublishedCultures,
    // which is every culture the item has published, not what this operation published.
    public void Handle(ContentPublishedNotification notification)
        => Record("source:ContentPublished", new
        {
            Entities = notification.PublishedEntities.Select(c => new
            {
                c.Key,
                c.Name,
                ContentType = c.ContentType.Alias,
                ItemPublishedCultures = c.PublishedCultures.ToArray(),
            }).ToArray(),
            NotificationPublishedCultures = notification.PublishedCultures,
            NotificationUnpublishedCultures = notification.UnpublishedCultures,
        });

    public void Handle(ContentSavedNotification notification)
        => Record("source:ContentSaved", notification.SavedEntities.Select(c => new { c.Key, c.Name, c.Published }).ToArray());

    private static void Record(string kind, object payload)
        => ProbeLog.Refreshers.Enqueue(new RefresherEntry(ProbeLog.Next(), ProbeLog.CurrentOperation, kind, "-", payload, []));
}
