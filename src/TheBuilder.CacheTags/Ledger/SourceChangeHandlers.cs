using System.Reflection;
using Microsoft.Extensions.Logging;
using TheBuilder.CacheTags.Delivery;
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
///     tell a draft save from a sort, which is why the ledger is built
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
        INotificationHandler<ContentTypeSavingNotification>,
        INotificationHandler<MediaTypeSavingNotification>,
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
        => Guard(_ =>
        {
            foreach (IContent content in notification.PublishedEntities)
            {
                pending.RememberSegments(content.Key, Cultures(content).ToDictionary(culture => culture, culture => documentUrlService.GetUrlSegment(content.Key, culture, isDraft: false)));
            }
        });

    public void Handle(ContentPublishedNotification notification)
    {
        Guard(at =>
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
                var cultureUnpublished = unpublished?.TryGetValue(content.Key, out var gone) is true && gone.Count > 0;
                return Item(content, routeChanged: routeChanged || cultureUnpublished);
            }).ToList();

            Record(at, ChangeKind.Published, items, Cultures(entities.Select(c => c.Key), published, unpublished));
        });
    }

    // Raised when the whole document is unpublished, even when it was asked to unpublish one
    // (mandatory) culture; unpublishing one culture of several is raised as a publish of the rest.
    public void Handle(ContentUnpublishedNotification notification)
        => Guard(at => Record(
            at,
            ChangeKind.Unpublished,
            notification.UnpublishedEntities.Select(content => Item(content, routeChanged: true)).ToList()));

    public void Handle(ContentMovedNotification notification)
        => Guard(at => Record(
            at,
            ChangeKind.Moved,
            notification.MoveInfoCollection.Where(move => move.Entity.Published).Select(move => Item(move.Entity, OldParent(move.OriginalPath, UmbracoObjectTypes.Document))).ToList()));

    public void Handle(ContentMovedToRecycleBinNotification notification)
        => Guard(at => Record(
            at,
            ChangeKind.Trashed,
            notification.MoveInfoCollection.Select(move => Item(move.Entity, OldParent(move.OriginalPath, UmbracoObjectTypes.Document))).ToList()));

    // An item deleted from the recycle bin was purged when it was trashed.
    public void Handle(ContentDeletedNotification notification)
        => Guard(at => Record(at, ChangeKind.Deleted, notification.DeletedEntities.Where(content => content.Trashed is false).Select(content => Item(content)).ToList()));

    public void Handle(ContentSortingNotification notification)
        => Guard(_ => pending.MarkSorting(notification.SortedEntities.Where(content => content.Published).Select(content => content.Key)));

    public void Handle(ContentSortedNotification notification)
        => Guard(at => Record(at, ChangeKind.Sorted, notification.SortedEntities.Where(content => content.Published).Select(content => Item(content)).ToList()));

    public void Handle(MediaSavedNotification notification)
        => Guard(at => Record(at, ChangeKind.MediaSaved, notification.SavedEntities.Select(media => Item(media)).ToList()));

    public void Handle(MediaMovedNotification notification)
        => Guard(at => Record(
            at,
            ChangeKind.MediaMoved,
            notification.MoveInfoCollection.Select(move => Item(move.Entity, OldParent(move.OriginalPath, UmbracoObjectTypes.Media))).ToList()));

    public void Handle(MediaMovedToRecycleBinNotification notification)
        => Guard(at => Record(
            at,
            ChangeKind.MediaTrashed,
            notification.MoveInfoCollection.Select(move => Item(move.Entity, OldParent(move.OriginalPath, UmbracoObjectTypes.Media))).ToList()));

    public void Handle(MediaDeletedNotification notification)
        => Guard(at => Record(at, ChangeKind.MediaDeleted, notification.DeletedEntities.Where(media => media.Trashed is false).Select(media => Item(media)).ToList()));

    // Before a type is saved: its alias as it was, which the changed notification no longer has.
    // Responses rendered with the old alias are keyed by it.
    public void Handle(ContentTypeSavingNotification notification)
        => Guard(_ => RememberAliases(notification.SavedEntities, key => contentTypeService.Get(key)));

    public void Handle(MediaTypeSavingNotification notification)
        => Guard(_ => RememberAliases(notification.SavedEntities, key => mediaTypeService.Get(key)));

    // A renamed content type's documents are indexed again under the new alias, which listings
    // filtered by type wait for. Media listings walk the media cache.
    public void Handle(ContentTypeChangedNotification notification)
        => Guard(at => RecordTypes(at, notification.Changes.Select(change => change.Item), "content types changed", indexed: true));

    public void Handle(MediaTypeChangedNotification notification)
        => Guard(at => RecordTypes(at, notification.Changes.Select(change => change.Item), "media types changed", indexed: false));

    // A data type's configuration changes how every property using it renders.
    public void Handle(DataTypeSavedNotification notification)
    {
        Guard(at =>
        {
            var keys = notification.SavedEntities.Select(dataType => dataType.Key).ToHashSet();
            var aliases = contentTypeService.GetAll().Cast<IContentTypeComposition>()
                .Concat(mediaTypeService.GetAll())
                .Where(type => type.CompositionPropertyTypes.Any(property => keys.Contains(property.DataTypeKey)))
                .Select(type => type.Alias);
            RecordTypes(at, aliases, "data types saved", keys.Select(key => AckSignal.Source(AckSignal.DataTypes, key)));
        });
    }

    public void Handle(DomainSavedNotification notification)
        => Guard(at => RecordEverything(at, "domains saved", notification.SavedEntities.Select(domain => AckSignal.Source(AckSignal.Domains, domain.Id))));

    public void Handle(DomainDeletedNotification notification)
        => Guard(at => RecordEverything(at, "domains deleted", notification.DeletedEntities.Select(domain => AckSignal.Source(AckSignal.Domains, domain.Id))));

    public void Handle(LanguageSavedNotification notification)
        => Guard(at => RecordEverything(at, "languages saved", notification.SavedEntities.Select(language => AckSignal.Source(AckSignal.Languages, language.Id))));

    public void Handle(LanguageDeletedNotification notification)
        => Guard(at => RecordEverything(at, "languages deleted", notification.DeletedEntities.Select(language => AckSignal.Source(AckSignal.Languages, language.Id))));

    // Protecting a page changes what the Delivery API returns for it and every page under it.
    public void Handle(PublicAccessEntrySavedNotification notification) => Guard(at => RecordEverything(at, "public access saved", [AckSignal.Source(AckSignal.PublicAccess)]));

    public void Handle(PublicAccessEntryDeletedNotification notification) => Guard(at => RecordEverything(at, "public access removed", [AckSignal.Source(AckSignal.PublicAccess)]));

    // A throwing handler would stop Umbraco from publishing the operation's remaining notifications,
    // including the one that refreshes every server's cache. Missing a purge is the lesser failure.
    // The change is timed as the handler is entered: the refresh this server applies for it can come
    // first, and building the items (URL segments, ancestors) can take a while for a large branch.
    private void Guard(Action<DateTime> handle)
    {
        try
        {
            handle(time.GetUtcNow().UtcDateTime);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Cache tags could not record a change; its purge is not planned.");
        }
    }

    private void Record(DateTime at, ChangeKind kind, IReadOnlyList<ChangedItem> items, IReadOnlyList<string>? cultures = null)
    {
        if (items.Count > 0)
        {
            recorder.Record(new Change(Guid.CreateVersion7(), at, kind, items, [], cultures));
        }
    }

    // Each type's alias, and the one it had before a rename: responses rendered before it are keyed by
    // that. Umbraco refreshes each changed content or media type by its id.
    private void RecordTypes(DateTime at, IEnumerable<IContentTypeComposition> types, string reason, bool indexed)
    {
        var changed = types.Select(type => (type.Id, type.Alias, Previous: pending.TakeAlias(type.Key))).ToList();
        var renamed = changed.Where(type => type.Previous is not null).Select(type => type.Previous!).ToList();
        RecordTypes(
            at,
            changed.Select(type => type.Alias).Concat(renamed),
            reason,
            changed.Select(type => AckSignal.Source(AckSignal.ContentTypes, type.Id)),
            indexed && renamed.Count > 0 ? renamed : null);
    }

    private void RecordTypes(DateTime at, IEnumerable<string> aliases, string reason, IEnumerable<string> sources, IReadOnlyList<string>? renamed = null)
    {
        var distinct = aliases.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (distinct.Count > 0)
        {
            recorder.Record(new Change(Guid.CreateVersion7(), at, ChangeKind.TypesChanged, [], distinct, null, reason, RenamedAliases: renamed, Sources: sources.Distinct().ToList()));
        }
    }

    private void RememberAliases(IEnumerable<IContentTypeComposition> types, Func<Guid, IContentTypeComposition?> persisted)
    {
        foreach (IContentTypeComposition type in types.Where(type => type.HasIdentity && type.IsPropertyDirty(nameof(IContentTypeComposition.Alias))))
        {
            if (persisted(type.Key)?.Alias is { } alias && alias.Equals(type.Alias, StringComparison.OrdinalIgnoreCase) is false)
            {
                pending.RememberAlias(type.Key, alias);
            }
        }
    }

    private void RecordEverything(DateTime at, string reason, IEnumerable<string> sources)
        => recorder.Record(new Change(Guid.CreateVersion7(), at, ChangeKind.Everything, [], [], null, reason, Sources: sources.Distinct().ToList()));

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
