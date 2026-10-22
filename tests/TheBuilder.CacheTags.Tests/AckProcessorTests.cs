using Microsoft.Extensions.Logging.Abstractions;
using TheBuilder.CacheTags.Delivery;
using TheBuilder.CacheTags.Ledger;
using TheBuilder.CacheTags.Storage;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Services.Changes;
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
        ICacheTagStore store = Fake.Of<ICacheTagStore>((method, _) => method.Name == nameof(ICacheTagStore.FindResponseKeysAsync)
            ? Task.FromResult<IReadOnlyCollection<string>>(["response-page"])
            : null);

        _acks = new AckProcessor(
            _ledger,
            new PurgeResolver(store),
            new OutputCacheEviction(services),
            new DatabaseAckTransport(_database.Package),
            _identity,
            new DeliverySignal(),
            _time,
            NullLogger<AckProcessor>.Instance);
        _refreshers = new RefresherHandlers(_acks, NullLogger<RefresherHandlers>.Instance);
    }

    public void Dispose() => _database.Dispose();

    private Change Record(Change change)
    {
        _ledger.Add(change, new PurgePlan([$"content-{Item:N}"], Everything: false, Cultures: null), "backoffice");
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

        Assert.Contains("response-page", _evicted);

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
}
