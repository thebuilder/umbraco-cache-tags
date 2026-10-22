using System.Reflection;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.PublishedCache;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;
using Umbraco.Extensions;

namespace TheBuilder.CacheTags.Ledger;

/// <summary>
///     Turns edits into <see cref="Change" />s on the server that makes them. Only changes to published
///     output count: a draft save raises none of these notifications. Cache refresher payloads cannot
///     tell a draft save from a sort (spike/FINDINGS.md, section 4), which is why the ledger is built
///     here, at the source.
/// </summary>
internal sealed class SourceChangeHandlers(
    ChangeRecorder recorder,
    PendingOperations pending,
    IIdKeyMap idKeyMap,
    IDocumentUrlService documentUrlService,
    IDefaultCultureAccessor defaultCulture,
    IShortStringHelper shortStringHelper,
    UrlSegmentProviderCollection urlSegmentProviders,
    IContentTypeService contentTypeService,
    IMediaTypeService mediaTypeService,
    TimeProvider time,
    ILogger<SourceChangeHandlers> logger)
    : INotificationHandler<ContentPublishingNotification>,
        INotificationHandler<ContentPublishedNotification>,
        INotificationHandler<ContentUnpublishedNotification>,
        INotificationHandler<ContentMovedNotification>,
        INotificationHandler<ContentMovedToRecycleBinNotification>,
        INotificationHandler<ContentDeletedNotification>,
        INotificationHandler<ContentSortingNotification>,
        INotificationHandler<ContentSortedNotification>,
        INotificationHandler<MediaSavedNotification>,
        INotificationHandler<MediaMovedNotification>,
        INotificationHandler<MediaMovedToRecycleBinNotification>,
        INotificationHandler<MediaDeletedNotification>,
        INotificationHandler<ContentTypeChangedNotification>,
        INotificationHandler<MediaTypeChangedNotification>,
        INotificationHandler<DataTypeSavedNotification>,
        INotificationHandler<DomainSavedNotification>,
        INotificationHandler<DomainDeletedNotification>,
        INotificationHandler<LanguageSavedNotification>,
        INotificationHandler<LanguageDeletedNotification>,
        INotificationHandler<PublicAccessEntrySavedNotification>,
        INotificationHandler<PublicAccessEntryDeletedNotification>
{
    // The per-item culture maps on these notifications arrived in Umbraco 17.6 and 18.1. Read them
    // when present; on older versions every publish counts as all cultures, which only over-purges.
    private static readonly PropertyInfo? PublishedCulturesProperty = typeof(ContentPublishedNotification).GetProperty("PublishedCultures");
    private static readonly PropertyInfo? PublishedUnpublishedCulturesProperty = typeof(ContentPublishedNotification).GetProperty("UnpublishedCultures");

    // Before a publish: the URL segments now live, to tell afterwards whether the route changed.
    public void Handle(ContentPublishingNotification notification)
        => Guard(() =>
        {
            foreach (IContent content in notification.PublishedEntities)
            {
                pending.RememberSegments(content.Key, Cultures(content).ToDictionary(culture => culture, culture => documentUrlService.GetUrlSegment(content.Key, culture, isDraft: false)));
            }
        });

    public void Handle(ContentPublishedNotification notification)
    {
        Guard(() =>
        {
            var published = CultureMap(PublishedCulturesProperty, notification);
            var unpublished = CultureMap(PublishedUnpublishedCulturesProperty, notification);

            // A sort publishes the sorted items again; the sort is recorded on its own.
            var entities = notification.PublishedEntities.Where(content => pending.TakeSorting(content.Key) is false).ToList();
            var items = entities.Select(content =>
            {
                var routeChanged = pending.TakeSegments(content.Key) is { } live && live.Any(pair =>
                    pair.Value is not null && pair.Value != content.GetUrlSegment(shortStringHelper, urlSegmentProviders, Variant(content) ? pair.Key : null));

                // Unpublishing one culture is raised as a publish of the rest; that culture's route is gone.
                routeChanged |= unpublished?.TryGetValue(content.Key, out var gone) is true && gone.Count > 0;
                return Item(content, routeChanged: routeChanged);
            }).ToList();

            Record(ChangeKind.Published, items, Cultures(entities.Select(c => c.Key), published, unpublished));
        });
    }

    // Raised when the whole document is unpublished, even when it was asked to unpublish one
    // (mandatory) culture; unpublishing one culture of several is raised as a publish of the rest.
    public void Handle(ContentUnpublishedNotification notification)
        => Guard(() => Record(
            ChangeKind.Unpublished,
            notification.UnpublishedEntities.Select(content => Item(content, routeChanged: true)).ToList()));

    public void Handle(ContentMovedNotification notification)
        => Guard(() => Record(
            ChangeKind.Moved,
            notification.MoveInfoCollection.Where(move => move.Entity.Published).Select(move => Item(move.Entity, OldParent(move.OriginalPath, UmbracoObjectTypes.Document))).ToList()));

    public void Handle(ContentMovedToRecycleBinNotification notification)
        => Guard(() => Record(
            ChangeKind.Trashed,
            notification.MoveInfoCollection.Select(move => Item(move.Entity, OldParent(move.OriginalPath, UmbracoObjectTypes.Document))).ToList()));

    // An item deleted from the recycle bin was purged when it was trashed.
    public void Handle(ContentDeletedNotification notification)
        => Guard(() => Record(ChangeKind.Deleted, notification.DeletedEntities.Where(content => content.Trashed is false).Select(content => Item(content)).ToList()));

    public void Handle(ContentSortingNotification notification)
        => Guard(() => pending.MarkSorting(notification.SortedEntities.Where(content => content.Published).Select(content => content.Key)));

    public void Handle(ContentSortedNotification notification)
        => Guard(() => Record(ChangeKind.Sorted, notification.SortedEntities.Where(content => content.Published).Select(content => Item(content)).ToList()));

    public void Handle(MediaSavedNotification notification)
        => Guard(() => Record(ChangeKind.MediaSaved, notification.SavedEntities.Select(media => Item(media)).ToList()));

    public void Handle(MediaMovedNotification notification)
        => Guard(() => Record(
            ChangeKind.MediaMoved,
            notification.MoveInfoCollection.Select(move => Item(move.Entity, OldParent(move.OriginalPath, UmbracoObjectTypes.Media))).ToList()));

    public void Handle(MediaMovedToRecycleBinNotification notification)
        => Guard(() => Record(
            ChangeKind.MediaTrashed,
            notification.MoveInfoCollection.Select(move => Item(move.Entity, OldParent(move.OriginalPath, UmbracoObjectTypes.Media))).ToList()));

    public void Handle(MediaDeletedNotification notification)
        => Guard(() => Record(ChangeKind.MediaDeleted, notification.DeletedEntities.Where(media => media.Trashed is false).Select(media => Item(media)).ToList()));

    public void Handle(ContentTypeChangedNotification notification)
        => Guard(() => RecordTypes(notification.Changes.Select(change => change.Item.Alias), "content types changed"));

    public void Handle(MediaTypeChangedNotification notification)
        => Guard(() => RecordTypes(notification.Changes.Select(change => change.Item.Alias), "media types changed"));

    // A data type's configuration changes how every property using it renders.
    public void Handle(DataTypeSavedNotification notification)
    {
        Guard(() =>
        {
            var keys = notification.SavedEntities.Select(dataType => dataType.Key).ToHashSet();
            var aliases = contentTypeService.GetAll().Cast<IContentTypeComposition>()
                .Concat(mediaTypeService.GetAll())
                .Where(type => type.CompositionPropertyTypes.Any(property => keys.Contains(property.DataTypeKey)))
                .Select(type => type.Alias);
            RecordTypes(aliases, "data types saved");
        });
    }

    public void Handle(DomainSavedNotification notification) => Guard(() => RecordEverything("domains saved"));

    public void Handle(DomainDeletedNotification notification) => Guard(() => RecordEverything("domains deleted"));

    public void Handle(LanguageSavedNotification notification) => Guard(() => RecordEverything("languages saved"));

    public void Handle(LanguageDeletedNotification notification) => Guard(() => RecordEverything("languages deleted"));

    // Protecting a page changes what the Delivery API returns for it and every page under it.
    public void Handle(PublicAccessEntrySavedNotification notification) => Guard(() => RecordEverything("public access saved"));

    public void Handle(PublicAccessEntryDeletedNotification notification) => Guard(() => RecordEverything("public access removed"));

    // A throwing handler would stop Umbraco from publishing the operation's remaining notifications,
    // including the one that refreshes every server's cache. Missing a purge is the lesser failure.
    private void Guard(Action handle)
    {
        try
        {
            handle();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Cache tags could not record a change; its purge is not planned.");
        }
    }

    private void Record(ChangeKind kind, IReadOnlyList<ChangedItem> items, IReadOnlyList<string>? cultures = null)
    {
        if (items.Count > 0)
        {
            recorder.Record(new Change(Guid.CreateVersion7(), Now(), kind, items, [], cultures));
        }
    }

    private void RecordTypes(IEnumerable<string> aliases, string reason)
    {
        var distinct = aliases.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (distinct.Count > 0)
        {
            recorder.Record(new Change(Guid.CreateVersion7(), Now(), ChangeKind.TypesChanged, [], distinct, null, reason));
        }
    }

    private void RecordEverything(string reason)
        => recorder.Record(new Change(Guid.CreateVersion7(), Now(), ChangeKind.Everything, [], [], null, reason));

    private DateTime Now() => time.GetUtcNow().UtcDateTime;

    private ChangedItem Item(IContentBase item, Guid? oldParent = null, bool routeChanged = false)
    {
        UmbracoObjectTypes type = item is IMedia ? UmbracoObjectTypes.Media : UmbracoObjectTypes.Document;

        // A path is "-1,1069,1077,<id>": the ancestors are every id between the root and the item.
        var ids = item.Path.Split(',');
        var ancestors = ids.Skip(1).Take(ids.Length - 2)
            .Select(id => int.TryParse(id, out var ancestor) ? KeyOf(ancestor, type) : Guid.Empty)
            .Where(key => key != Guid.Empty)
            .ToList();
        return new ChangedItem(item.Key, item.ContentType.Alias, KeyOf(item.ParentId, type), oldParent, routeChanged, ancestors);
    }

    // A path is "-1,1069,1077": the parent is the next-to-last id.
    private Guid? OldParent(string? originalPath, UmbracoObjectTypes type)
    {
        var ids = originalPath?.Split(',') ?? [];
        return ids.Length >= 2 && int.TryParse(ids[^2], out var parentId) ? KeyOf(parentId, type) : null;
    }

    private Guid KeyOf(int id, UmbracoObjectTypes type)
        => id <= 0 ? Guid.Empty : idKeyMap.GetKeyForId(id, type) is { Success: true } attempt ? attempt.Result : Guid.Empty;

    private bool Variant(IContent content) => content.ContentType.VariesByCulture();

    private IEnumerable<string> Cultures(IContent content)
        => Variant(content) ? content.PublishedCultures : [defaultCulture.DefaultCulture];

    private static IReadOnlyDictionary<Guid, IReadOnlyCollection<string>>? CultureMap(PropertyInfo? property, object notification)
        => property?.GetValue(notification) as IReadOnlyDictionary<Guid, IReadOnlyCollection<string>>;

    // The cultures a change touched, or null for all: when a map is missing (before 17.6), when an
    // item has none in it, or when any item changed every culture ("*").
    private static IReadOnlyList<string>? Cultures(
        IEnumerable<Guid> keys,
        IReadOnlyDictionary<Guid, IReadOnlyCollection<string>>? first,
        IReadOnlyDictionary<Guid, IReadOnlyCollection<string>>? second)
    {
        if (first is null && second is null)
        {
            return null;
        }

        var cultures = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Guid key in keys)
        {
            var found = false;
            foreach (var map in new[] { first, second })
            {
                if (map?.TryGetValue(key, out var values) is true && values.Count > 0)
                {
                    cultures.UnionWith(values);
                    found = true;
                }
            }

            if (found is false || cultures.Contains("*"))
            {
                return null;
            }
        }

        return cultures.ToList();
    }
}
