using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NPoco;
using TheBuilder.CacheTags.Configuration;
using TheBuilder.CacheTags.Delivery;
using TheBuilder.CacheTags.Ledger;
using TheBuilder.CacheTags.Storage;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.PublishedCache;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Services.Changes;
using Umbraco.Cms.Core.Strings;

namespace TheBuilder.CacheTags.Tests;

/// <summary>Umbraco's notifications, through to the change recorded in the ledger and its plan.</summary>
public sealed class SourceChangeHandlersTests : IDisposable
{
    private static readonly Guid ArticleType = new("e3000000-0000-0000-0000-000000000003");
    private static readonly Guid Site = new("10000000-0000-0000-0000-000000000001");
    private static readonly Guid C = new("10000000-0000-0000-0000-00000000000c");
    private static readonly IShortStringHelper Strings = new DefaultShortStringHelper(new DefaultShortStringHelperConfig().WithDefault(new RequestHandlerSettings()));

    private readonly TestDatabase _database = new();
    private readonly Database _sql;
    private readonly LedgerStore _ledger;
    private readonly SourceChangeHandlers _handlers;

    // The types as they are in the database, before the save.
    private readonly Dictionary<Guid, string> _persisted = new() { [ArticleType] = "article" };

    public SourceChangeHandlersTests()
    {
        _sql = _database.Sql;
        PackageDatabase database = _database.Package;
        _ledger = new LedgerStore(database);
        IOptionsMonitor<CacheTagsOptions> options = Fake.Of<IOptionsMonitor<CacheTagsOptions>>((method, _) => method.Name is "get_CurrentValue" or "Get" ? new CacheTagsOptions() : null);
        var recorder = new ChangeRecorder(
            new PurgePlanner(new PurgePlannerTests.Tree()),
            database,
            _ledger,
            new ServerIdentity(TimeProvider.System),
            new DeliverySignal(),
            options,
            NullLogger<ChangeRecorder>.Instance);

        _handlers = new SourceChangeHandlers(
            recorder,
            new PendingOperations(),
            Fake.Of<IIdKeyMap>((method, args) => method.Name == nameof(IIdKeyMap.GetKeyForId) && args is [1059, _] ? Attempt<Guid>.Succeed(Site) : null),
            Fake.Of<IDocumentUrlService>(),
            Fake.Of<IDefaultCultureAccessor>(),
            Strings,
            new UrlSegmentProviderCollection(() => []),
            Fake.Of<IContentTypeService>((method, args) => method.Name == "Get" && args is [Guid key] ? Persisted<IContentType>(key) : null),
            Fake.Of<IMediaTypeService>((method, args) => method.Name == "Get" && args is [Guid key] ? Persisted<IMediaType>(key) : null),
            TimeProvider.System,
            NullLogger<SourceChangeHandlers>.Instance);
    }

    public void Dispose() => _database.Dispose();

    [Fact]
    public void Unpublishing_an_item_reaches_the_listings_its_descendants_leave_with_it()
    {
        // C, under the site, has a guide and a parking page under it (PurgePlannerTests.Tree).
        var category = new ContentType(Strings, -1) { Id = 1050, Alias = "category" };
        var content = new Content("Getting around", 1059, category) { Id = 1060, Key = C, Path = "-1,1059,1060" };

        _handlers.Handle(new ContentUnpublishedNotification(content, new EventMessages()));

        LedgerEntry recorded = Assert.Single(_ledger.Latest(10));
        Assert.Equal(
            [
                "children-10000000-0000-0000-0000-000000000001",
                "content-10000000-0000-0000-0000-00000000000c",
                "content-10000000-0000-0000-0000-000000000013",
                "content-10000000-0000-0000-0000-000000000014",
                "search-deliveryapicontentindex",
                "type-category",
                "type-guidepage",
                "type-parkingpage",
            ],
            recorded.Plan.Keys);
    }

    [Fact]
    public void Renaming_a_content_types_alias_plans_both_aliases_and_waits_for_the_index_to_drop_the_old_one()
    {
        var type = new ContentType(Strings, -1) { Id = 1060, Key = ArticleType, Alias = "article" };
        type.ResetDirtyProperties(false);
        type.Alias = "post";

        _handlers.Handle(new ContentTypeSavingNotification(type, new EventMessages()));
        _handlers.Handle(new ContentTypeChangedNotification(new ContentTypeChange<IContentType>(type, ContentTypeChangeTypes.RefreshMain), new EventMessages()));

        LedgerEntry recorded = Assert.Single(_ledger.Latest(10));
        Assert.Equal(["article", "post"], recorded.Change.TypeAliases);
        Assert.Equal(["article"], recorded.Change.RenamedAliases);
        Assert.Equal(["schema-article", "schema-post", "type-article", "type-post"], recorded.Plan.Keys);
    }

    [Fact]
    public void Renaming_a_media_types_alias_plans_both_aliases_but_waits_for_no_index()
    {
        // Media listings walk the media cache.
        var type = new MediaType(Strings, -1) { Id = 1061, Key = ArticleType, Alias = "article" };
        type.ResetDirtyProperties(false);
        type.Alias = "photo";

        _handlers.Handle(new MediaTypeSavingNotification(type, new EventMessages()));
        _handlers.Handle(new MediaTypeChangedNotification(new ContentTypeChange<IMediaType>(type, ContentTypeChangeTypes.RefreshMain), new EventMessages()));

        LedgerEntry recorded = Assert.Single(_ledger.Latest(10));
        Assert.Equal(["article", "photo"], recorded.Change.TypeAliases);
        Assert.Null(recorded.Change.RenamedAliases);
    }

    [Fact]
    public void Saving_a_type_without_renaming_it_plans_its_alias_only()
    {
        var type = new ContentType(Strings, -1) { Id = 1060, Key = ArticleType, Alias = "article" };
        type.ResetDirtyProperties(false);
        type.Name = "Blog post";

        _handlers.Handle(new ContentTypeSavingNotification(type, new EventMessages()));
        _handlers.Handle(new ContentTypeChangedNotification(new ContentTypeChange<IContentType>(type, ContentTypeChangeTypes.RefreshMain), new EventMessages()));

        LedgerEntry recorded = Assert.Single(_ledger.Latest(10));
        Assert.Equal(["article"], recorded.Change.TypeAliases);
        Assert.Null(recorded.Change.RenamedAliases);
    }

    private T? Persisted<T>(Guid key)
        where T : class, IContentTypeComposition
        => _persisted.TryGetValue(key, out var alias) ? Fake.Of<T>((method, _) => method.Name == "get_Alias" ? alias : null) : null;
}
