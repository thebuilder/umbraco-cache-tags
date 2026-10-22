using Microsoft.Extensions.Logging.Abstractions;
using TheBuilder.CacheTags.Configuration;
using TheBuilder.CacheTags.Delivery;
using TheBuilder.CacheTags.Ledger;
using TheBuilder.CacheTags.Storage;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.PublishedCache;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Services.Changes;
using Umbraco.Cms.Core.Strings;
using Umbraco.Cms.Core.Sync;
using Umbraco.Extensions;

namespace TheBuilder.CacheTags.Tests;

/// <summary>
///     A content change on the server that makes it, from Umbraco's notification to the change recorded,
///     and back from the cache refresh Umbraco sends every server for it to this server's ack. The
///     refreshes are built by Umbraco itself (<see cref="DistributedCache" />) from the tree change
///     ContentService raises for each operation, so a change to their shape in an Umbraco release
///     shows up here.
/// </summary>
public sealed class ContentRefreshTests : IDisposable
{
    private static readonly IShortStringHelper Strings = new DefaultShortStringHelper(new DefaultShortStringHelperConfig().WithDefault(new RequestHandlerSettings()));
    private static readonly Guid Key = new("10000000-0000-0000-0000-00000000000a");

    private readonly TestDatabase _database = new();
    private readonly ServerIdentity _identity = new(TimeProvider.System);
    private readonly LedgerStore _ledger;
    private readonly AckProcessor _acks;
    private readonly RefresherHandlers _refreshers;
    private readonly SourceChangeHandlers _changes;
    private readonly Content _article;

    public ContentRefreshTests()
    {
        _ledger = new LedgerStore(_database.Package);
        _acks = new AckProcessor(
            _ledger,
            new PurgeResolver(Fake.Of<ICacheTagStore>((method, _) => method.Name == nameof(ICacheTagStore.FindResponseKeysAsync) ? Task.FromResult<IReadOnlyCollection<string>>([]) : null)),
            new OutputCacheEviction(Fake.Of<IServiceProvider>()),
            new DatabaseAckTransport(_database.Package),
            _identity,
            new DeliverySignal(),
            TimeProvider.System,
            NullLogger<AckProcessor>.Instance);
        _refreshers = new RefresherHandlers(_acks, NullLogger<RefresherHandlers>.Instance);
        _changes = new SourceChangeHandlers(
            new ChangeRecorder(new PurgePlanner(new PurgePlannerTests.Tree()), _database.Package, _ledger, _identity, new DeliverySignal(), new TestOptions(new CacheTagsOptions()), NullLogger<ChangeRecorder>.Instance),
            new PendingOperations(),
            Fake.Of<IIdKeyMap>(),
            Fake.Of<IDocumentUrlService>(),
            Fake.Of<IDefaultCultureAccessor>(),
            Strings,
            new UrlSegmentProviderCollection(() => []),
            Fake.Of<IContentTypeService>(),
            Fake.Of<IMediaTypeService>(),
            TimeProvider.System,
            NullLogger<SourceChangeHandlers>.Instance);

        var type = new ContentType(Strings, -1) { Id = 1050, Alias = "article" };
        _article = new Content("Harbour mornings", 1059, type) { Id = 1061, Key = Key, Path = "-1,1059,1061", Published = true };
    }

    public void Dispose() => _database.Dispose();

    /// <summary>The payloads Umbraco's distributed cache sends every server for the tree change.</summary>
    private static ContentCacheRefresher.JsonPayload[] Refresh(TreeChange<IContent> change)
    {
        var sent = new List<object>();
        IServerMessenger messenger = Fake.Of<IServerMessenger>((method, args) =>
        {
            if (method.Name == nameof(IServerMessenger.QueueRefresh))
            {
                sent.Add(args[1]!);
            }

            return null;
        });
        ICacheRefresher refresher = Fake.Of<ICacheRefresher>((method, _) => method.Name == "get_RefresherUniqueId" ? ContentCacheRefresher.UniqueId : null);
        new DistributedCache(messenger, new CacheRefresherCollection(() => [refresher])).RefreshContentCache([change]);
        return Assert.IsType<ContentCacheRefresher.JsonPayload[]>(Assert.Single(sent));
    }

    /// <summary>Applies the refresh on this server, as Umbraco's content cache refresher announces it, and returns who acknowledged the change.</summary>
    private async Task<IReadOnlyList<string>> AcknowledgedAfterAsync(TreeChange<IContent> change)
    {
        LedgerEntry recorded = Assert.Single(_ledger.InStage(ChangeStage.Pending, 10));
        await _refreshers.HandleAsync(new ContentCacheRefresherNotification(Refresh(change), MessageType.RefreshByPayload), CancellationToken.None);
        await _acks.ProcessAsync(CancellationToken.None);
        return _database.Sql.Fetch<string>("SELECT Server FROM theBuilderCacheTagsAck WHERE EventId = @0", recorded.Id);
    }

    private ChangeKind Recorded() => Assert.Single(_ledger.Latest(10)).Change.Kind;

    [Fact]
    public async Task A_publish_is_acknowledged_by_the_refresh_of_its_published_cultures()
    {
        _changes.Handle(new ContentPublishedNotification(_article, new EventMessages()));

        Assert.Equal(ChangeKind.Published, Recorded());
        Assert.Equal([_identity.Name], await AcknowledgedAfterAsync(new TreeChange<IContent>(_article, TreeChangeTypes.RefreshNode, ["*"], null)));
    }

    [Fact]
    public async Task A_draft_save_of_the_same_item_does_not_acknowledge_its_publish()
    {
        _changes.Handle(new ContentPublishedNotification(_article, new EventMessages()));

        Assert.Empty(await AcknowledgedAfterAsync(new TreeChange<IContent>(_article, TreeChangeTypes.RefreshNode)));
    }

    [Fact]
    public async Task An_unpublish_is_acknowledged_by_the_refresh_of_its_unpublished_cultures_and_not_by_a_draft_save()
    {
        _changes.Handle(new ContentUnpublishedNotification(_article, new EventMessages()));

        Assert.Equal(ChangeKind.Unpublished, Recorded());
        Assert.Empty(await AcknowledgedAfterAsync(new TreeChange<IContent>(_article, TreeChangeTypes.RefreshNode)));
        Assert.Equal([_identity.Name], await AcknowledgedAfterAsync(new TreeChange<IContent>(_article, TreeChangeTypes.RefreshBranch, null, ["*"])));
    }

    [Fact]
    public async Task A_move_is_acknowledged_by_the_refresh_of_the_branch_and_not_of_the_item_alone()
    {
        // Umbraco 18 made this constructor obsolete in favour of one 17 does not have.
#pragma warning disable CS0618
        var move = new MoveEventInfo<IContent>(_article, "-1,1060,1061", 1059);
#pragma warning restore CS0618
        _changes.Handle(new ContentMovedNotification(move, new EventMessages()));

        Assert.Equal(ChangeKind.Moved, Recorded());
        Assert.Empty(await AcknowledgedAfterAsync(new TreeChange<IContent>(_article, TreeChangeTypes.RefreshNode)));
        Assert.Equal([_identity.Name], await AcknowledgedAfterAsync(new TreeChange<IContent>(_article, TreeChangeTypes.RefreshBranch)));
    }

    [Fact]
    public async Task A_sort_is_recorded_once_and_acknowledged_by_the_refresh_of_a_sorted_item()
    {
        // A sort publishes the sorted items again, which is not recorded as a publish of its own.
        _changes.Handle(new ContentSortingNotification([_article], new EventMessages()));
        _changes.Handle(new ContentPublishedNotification(_article, new EventMessages()));
        _changes.Handle(new ContentSortedNotification([_article], new EventMessages()));

        Assert.Equal(ChangeKind.Sorted, Recorded());
        Assert.Equal([_identity.Name], await AcknowledgedAfterAsync(new TreeChange<IContent>(_article, TreeChangeTypes.RefreshNode)));
    }

    [Fact]
    public void A_publish_of_one_culture_is_recorded_for_that_culture_where_umbraco_says_which()
    {
        // Umbraco 17.6 and 18.1 added the per-item culture maps to the notification. Before them a
        // publish counts as every culture (null).
        Version umbraco = typeof(IContent).Assembly.GetName().Version!;
        var hasCultures = umbraco >= new Version(18, 1) || (umbraco.Major == 17 && umbraco >= new Version(17, 6));
        IReadOnlyDictionary<Guid, IReadOnlyCollection<string>> published = new Dictionary<Guid, IReadOnlyCollection<string>> { [Key] = ["da-DK"] };
        System.Reflection.ConstructorInfo? withCultures = typeof(ContentPublishedNotification).GetConstructor(
            [typeof(IContent), typeof(EventMessages), typeof(IReadOnlyDictionary<Guid, IReadOnlyCollection<string>>), typeof(IReadOnlyDictionary<Guid, IReadOnlyCollection<string>>)]);
        Assert.Equal(hasCultures, withCultures is not null);

        _changes.Handle(withCultures is null
            ? new ContentPublishedNotification(_article, new EventMessages())
            : (ContentPublishedNotification)withCultures.Invoke([_article, new EventMessages(), published, null]));

        Assert.Equal(hasCultures ? ["da-DK"] : null, Assert.Single(_ledger.Latest(10)).Change.Cultures);
    }
}
