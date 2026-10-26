using Microsoft.Extensions.Logging.Abstractions;
using TheBuilder.CacheTags.Configuration;
using TheBuilder.CacheTags.Delivery;
using TheBuilder.CacheTags.Ledger;
using TheBuilder.CacheTags.Storage;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.PublishedCache;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Services.Changes;
using Umbraco.Cms.Core.Strings;
using Umbraco.Cms.Core.Sync;

namespace TheBuilder.CacheTags.Tests;

/// <summary>What a server does with the cache refreshes it applies, against the ledger in SQLite.</summary>
public sealed class AckProcessorTests : IDisposable
{
    private static readonly DateTime Noon = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Item = new("10000000-0000-0000-0000-00000000000a");

    private readonly TestDatabase _database = new();
    private readonly ManualTime _time = new(Noon);
    private readonly ServerIdentity _identity;
    private readonly LedgerStore _ledger;
    private readonly List<string> _evicted = [];
    private readonly AckProcessor _acks;
    private readonly RefresherHandlers _refreshers;
    private readonly SourceChangeHandlers _changes;

    public AckProcessorTests()
    {
        _identity = new ServerIdentity(_time);
        _ledger = new LedgerStore(_database.Package);

        IDeliveryApiOutputCacheManager outputCache = Fake.Of<IDeliveryApiOutputCacheManager>((method, args) =>
        {
            if (method.Name == nameof(IDeliveryApiOutputCacheManager.EvictByTagAsync))
            {
                _evicted.Add((string)args[0]!);
            }

            return null;
        });
        IServiceProvider services = Fake.Of<IServiceProvider>((_, args) => (Type)args[0]! == typeof(IDeliveryApiOutputCacheManager) ? outputCache : null);
        _acks = new AckProcessor(
            _ledger,
            new OutputCacheEviction(services),
            new DatabaseAckTransport(_database.Package),
            _identity,
            new DeliverySignal(),
            _time,
            NullLogger<AckProcessor>.Instance);
        _refreshers = new RefresherHandlers(_acks, NullLogger<RefresherHandlers>.Instance);

        // Looking an id up is slow, as it is for a large branch: each lookup takes a second.
        var recorder = new ChangeRecorder(new PurgePlanner(new NoDescendants()), _database.Package, _ledger, _identity, new DeliverySignal(), new TestOptions(new CacheTagsOptions()), NullLogger<ChangeRecorder>.Instance);
        _changes = new SourceChangeHandlers(
            recorder,
            new PendingOperations(),
            Fake.Of<IIdKeyMap>((_, _) =>
            {
                _time.UtcNow += TimeSpan.FromSeconds(1);
                return null;
            }),
            Fake.Of<IDocumentUrlService>(),
            Fake.Of<IDefaultCultureAccessor>(),
            Fake.Of<IShortStringHelper>(),
            new UrlSegmentProviderCollection(() => []),
            Fake.Of<IContentTypeService>((method, _) => method.Name == nameof(IContentTypeService.GetAll) ? new[] { TypeWith(1051, "page", DataType) } : null),
            Fake.Of<IMediaTypeService>((method, _) => method.Name == nameof(IMediaTypeService.GetAll) ? Array.Empty<IMediaType>() : null),
            _time,
            NullLogger<SourceChangeHandlers>.Instance);
    }

    private static readonly Guid DataType = new("d0000000-0000-0000-0000-000000000001");

    private sealed class NoDescendants : IDescendants
    {
        public IEnumerable<Guid> Of(Guid key) => [];

        public IEnumerable<string> TypesOf(IReadOnlyCollection<Guid> keys, bool media) => [];
    }

    private static IContentType TypeWith(int id, string alias, Guid dataType)
    {
        IPropertyType property = Fake.Of<IPropertyType>((method, _) => method.Name == "get_DataTypeKey" ? dataType : null);
        return Fake.Of<IContentType>((method, _) => method.Name switch
        {
            "get_Id" => id,
            "get_Alias" => alias,
            "get_CompositionPropertyTypes" => new[] { property },
            _ => null,
        });
    }

    private static T Entity<T>(int id, Guid key = default)
        where T : class
        => Fake.Of<T>((method, _) => method.Name switch
        {
            "get_Id" => id,
            "get_Key" => key,
            _ => null,
        });

    public void Dispose() => _database.Dispose();

    private Change Record(Change change)
    {
        _ledger.Add(change, new PurgePlan([$"content-{Item:N}"], Everything: false), "backoffice");
        return change;
    }

    private IReadOnlyList<string> AcknowledgedBy(Guid changeId)
        => _database.Sql.Fetch<string>("SELECT Server FROM theBuilderCacheTagsAck WHERE EventId = @0", changeId);

    private async Task ApplyAsync(params ObservedRefresh[] refreshes)
    {
        _acks.Observe(refreshes);
        await _acks.ProcessAsync(CancellationToken.None);
    }

    private static ObservedRefresh PublishOf(Guid key) => new(AckSignal.Content, key, TreeChangeTypes.RefreshNode, HasPublishedCultures: true);

    [Fact]
    public async Task A_server_that_applies_a_change_after_it_was_released_without_it_still_evicts_its_responses()
    {
        Change change = Record(new Change(Guid.CreateVersion7(), Noon, ChangeKind.Published, [new ChangedItem(Item, "page", Guid.Empty)], [], null));
        _ledger.Release(change.Id, ChangeStage.Released, [_identity.Name], Noon.AddMinutes(1), []);

        _time.UtcNow = Noon.AddMinutes(3);
        await ApplyAsync(PublishOf(Item));

        Assert.Contains($"content-{Item:N}", _evicted);

        // Too late to count: the purge has gone, so nothing waits for this server's ack.
        Assert.Empty(AcknowledgedBy(change.Id));
    }

    [Fact]
    public async Task A_change_released_without_another_server_is_not_evicted_again_here()
    {
        Change change = Record(new Change(Guid.CreateVersion7(), Noon, ChangeKind.Published, [new ChangedItem(Item, "page", Guid.Empty)], [], null));
        _ledger.Release(change.Id, ChangeStage.Released, ["another-server/1234abcd"], Noon.AddMinutes(1), []);

        _time.UtcNow = Noon.AddMinutes(3);
        await ApplyAsync(PublishOf(Item));

        Assert.Empty(_evicted);
    }

    [Fact]
    public async Task A_type_change_is_acknowledged_by_the_refresh_of_its_own_type_not_another()
    {
        Change change = Record(new Change(Guid.CreateVersion7(), Noon, ChangeKind.TypesChanged, [], ["page"], null, "content types changed", Sources: ["contenttype:1051"]));
        _time.UtcNow = Noon.AddSeconds(1);

        await _refreshers.HandleAsync(
            new ContentTypeCacheRefresherNotification(new[] { new ContentTypeCacheRefresher.JsonPayload("IContentType", 1052, ContentTypeChangeTypes.RefreshMain) }, MessageType.RefreshByPayload),
            CancellationToken.None);
        await _acks.ProcessAsync(CancellationToken.None);
        Assert.Empty(AcknowledgedBy(change.Id));

        await _refreshers.HandleAsync(
            new ContentTypeCacheRefresherNotification(new[] { new ContentTypeCacheRefresher.JsonPayload("IContentType", 1051, ContentTypeChangeTypes.RefreshMain) }, MessageType.RefreshByPayload),
            CancellationToken.None);
        await _acks.ProcessAsync(CancellationToken.None);
        Assert.Equal([_identity.Name], AcknowledgedBy(change.Id));
    }

    [Fact]
    public async Task A_language_change_is_acknowledged_by_the_refresh_of_that_language_not_another_or_a_domain()
    {
        Change change = Record(new Change(Guid.CreateVersion7(), Noon, ChangeKind.Everything, [], [], null, "languages saved", Sources: ["language:2"]));
        _time.UtcNow = Noon.AddSeconds(1);

        await _refreshers.HandleAsync(
            new LanguageCacheRefresherNotification(new[] { new LanguageCacheRefresher.JsonPayload(3, "da-DK", LanguageCacheRefresher.JsonPayload.LanguageChangeType.Update) }, MessageType.RefreshByPayload),
            CancellationToken.None);
        await _refreshers.HandleAsync(
            new DomainCacheRefresherNotification(new[] { new DomainCacheRefresher.JsonPayload(2, DomainChangeTypes.Refresh) }, MessageType.RefreshByPayload),
            CancellationToken.None);
        await _acks.ProcessAsync(CancellationToken.None);
        Assert.Empty(AcknowledgedBy(change.Id));

        await _refreshers.HandleAsync(
            new LanguageCacheRefresherNotification(new[] { new LanguageCacheRefresher.JsonPayload(2, "en-US", LanguageCacheRefresher.JsonPayload.LanguageChangeType.Update) }, MessageType.RefreshByPayload),
            CancellationToken.None);
        await _acks.ProcessAsync(CancellationToken.None);
        Assert.Equal([_identity.Name], AcknowledgedBy(change.Id));
    }

    [Fact]
    public async Task A_server_whose_clock_is_behind_evicts_a_change_once_it_is_released_without_it()
    {
        Change change = Record(new Change(Guid.CreateVersion7(), Noon, ChangeKind.Published, [new ChangedItem(Item, "page", Guid.Empty)], [], null));

        // This server's clock is 3 seconds behind: its refresh looks older than the change, and does not count.
        _time.UtcNow = Noon.AddSeconds(-3);
        await ApplyAsync(PublishOf(Item));
        Assert.Empty(AcknowledgedBy(change.Id));

        _time.UtcNow = Noon.AddSeconds(70);
        _ledger.Release(change.Id, ChangeStage.Released, [_identity.Name], Noon.AddSeconds(60), []);
        await _acks.ProcessAsync(CancellationToken.None);

        Assert.Contains($"content-{Item:N}", _evicted);
        Assert.Empty(AcknowledgedBy(change.Id));
    }

    [Fact]
    public async Task Evicting_a_change_released_without_this_server_does_not_use_up_the_refresh_of_the_next_change()
    {
        Change missed = Record(new Change(Guid.CreateVersion7(), Noon, ChangeKind.Published, [new ChangedItem(Item, "page", Guid.Empty)], [], null));
        _ledger.Release(missed.Id, ChangeStage.Released, [_identity.Name], Noon.AddSeconds(60), []);

        // The item is published again; its refresh comes moments before the change is recorded.
        _time.UtcNow = Noon.AddSeconds(70);
        await ApplyAsync(PublishOf(Item));
        Change next = Record(new Change(Guid.CreateVersion7(), Noon.AddSeconds(70.5), ChangeKind.Published, [new ChangedItem(Item, "page", Guid.Empty)], [], null));
        _time.UtcNow = Noon.AddSeconds(71);
        await _acks.ProcessAsync(CancellationToken.None);

        Assert.Contains($"content-{Item:N}", _evicted);
        Assert.Equal([_identity.Name], AcknowledgedBy(next.Id));
    }

    [Fact]
    public async Task A_change_this_server_acknowledged_is_not_evicted_again_when_it_is_released_without_its_ack_seen()
    {
        Change change = Record(new Change(Guid.CreateVersion7(), Noon, ChangeKind.Published, [new ChangedItem(Item, "page", Guid.Empty)], [], null));
        _time.UtcNow = Noon.AddSeconds(1);
        await ApplyAsync(PublishOf(Item));
        Assert.Equal([_identity.Name], AcknowledgedBy(change.Id));

        // Another server released it on a list of acks read just before this one's.
        _ledger.Release(change.Id, ChangeStage.Released, [_identity.Name], Noon.AddSeconds(2), []);
        _time.UtcNow = Noon.AddSeconds(10);
        await ApplyAsync(PublishOf(Item));

        Assert.Single(_evicted, tag => tag == $"content-{Item:N}");
    }

    [Fact]
    public async Task A_change_is_timed_when_it_is_raised_not_after_its_items_are_read()
    {
        IContent page = Fake.Of<IContent>((method, _) => method.Name switch
        {
            "get_Key" => Item,
            "get_Path" => "-1,1060,1061,1062",
            "get_ParentId" => 1061,
            "get_ContentType" => Fake.Of<ISimpleContentType>((type, _) => type.Name == "get_Alias" ? "page" : null),
            _ => null,
        });

        _changes.Handle(new ContentMovedToRecycleBinNotification(new MoveToRecycleBinEventInfo<IContent>(page, "-1,1060,1061,1062"), new EventMessages()));

        LedgerEntry recorded = Assert.Single(_ledger.Latest(10));
        Assert.Equal(Noon, recorded.CreatedUtc);
    }

    // What the server making a change records, and what each server's refresh shows, must name the same refresh.
    private async Task AssertAcknowledgedFromItsOwnRefreshAsync(Action record, Func<Task> refresh)
    {
        record();
        LedgerEntry change = Assert.Single(_ledger.InStage(ChangeStage.Pending, 10));
        await refresh();
        await _acks.ProcessAsync(CancellationToken.None);

        Assert.Equal([_identity.Name], AcknowledgedBy(change.Id));
    }

    [Fact]
    public Task A_content_type_change_is_acknowledged_by_its_refresh()
        => AssertAcknowledgedFromItsOwnRefreshAsync(
            () => _changes.Handle(new ContentTypeChangedNotification(new ContentTypeChange<IContentType>(TypeWith(1051, "page", DataType), ContentTypeChangeTypes.RefreshMain), new EventMessages())),
            () => _refreshers.HandleAsync(
                new ContentTypeCacheRefresherNotification(new[] { new ContentTypeCacheRefresher.JsonPayload("IContentType", 1051, ContentTypeChangeTypes.RefreshMain) }, MessageType.RefreshByPayload),
                CancellationToken.None));

    [Fact]
    public Task A_data_type_change_is_acknowledged_by_its_refresh()
        => AssertAcknowledgedFromItsOwnRefreshAsync(
            () => _changes.Handle(new DataTypeSavedNotification(Entity<IDataType>(3, DataType), new EventMessages())),
            () => _refreshers.HandleAsync(
                new DataTypeCacheRefresherNotification(new[] { new DataTypeCacheRefresher.JsonPayload(3, DataType, false) }, MessageType.RefreshByPayload),
                CancellationToken.None));

    [Fact]
    public Task A_language_change_is_acknowledged_by_its_refresh()
        => AssertAcknowledgedFromItsOwnRefreshAsync(
            () => _changes.Handle(new LanguageSavedNotification(Entity<ILanguage>(2), new EventMessages())),
            () => _refreshers.HandleAsync(
                new LanguageCacheRefresherNotification(new[] { new LanguageCacheRefresher.JsonPayload(2, "da-DK", LanguageCacheRefresher.JsonPayload.LanguageChangeType.Update) }, MessageType.RefreshByPayload),
                CancellationToken.None));

    [Fact]
    public Task A_domain_change_is_acknowledged_by_its_refresh()
        => AssertAcknowledgedFromItsOwnRefreshAsync(
            () => _changes.Handle(new DomainSavedNotification(Entity<IDomain>(5), new EventMessages())),
            () => _refreshers.HandleAsync(
                new DomainCacheRefresherNotification(new[] { new DomainCacheRefresher.JsonPayload(5, DomainChangeTypes.Refresh) }, MessageType.RefreshByPayload),
                CancellationToken.None));

    [Fact]
    public Task A_public_access_change_is_acknowledged_by_its_refresh()
        => AssertAcknowledgedFromItsOwnRefreshAsync(
            () => _changes.Handle(new PublicAccessEntrySavedNotification(Array.Empty<PublicAccessEntry>(), new EventMessages())),
            () => _refreshers.HandleAsync(new PublicAccessCacheRefresherNotification(new object(), MessageType.RefreshAll), CancellationToken.None));
}
