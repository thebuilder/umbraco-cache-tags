using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TheBuilder.CacheTags.Collection;
using TheBuilder.CacheTags.Configuration;
using TheBuilder.CacheTags.Delivery;
using TheBuilder.CacheTags.Ledger;
using TheBuilder.CacheTags.ManagementApi;
using TheBuilder.CacheTags.Storage;
using TheBuilder.CacheTags.Targets;
using Umbraco.Cms.Core.DeliveryApi;
using Umbraco.Cms.Core.Models.DeliveryApi;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.PublishedCache;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Infrastructure.Scoping;

namespace TheBuilder.CacheTags.Tests;

/// <summary>
///     A response showing more items than its header lists carries one stored <c>set-</c> key, and a
///     change to one of its items purges it: rendered through the result filter, released through
///     the ledger in SQLite.
/// </summary>
public sealed partial class KeySetTests : IDisposable
{
    private static readonly DateTime Noon = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);

    // A landing page's 80 linked articles, and an article it does not link.
    private static readonly IReadOnlyList<string> Linked = Enumerable.Range(1, 80).Select(Article).ToList();
    private static readonly string NotLinked = Article(81);

    private readonly TestDatabase _database = new();
    private readonly ManualTime _time = new(Noon);
    private readonly KeySetStore _store;
    private readonly LedgerStore _ledger;
    private readonly TestOptions _options = new(new CacheTagsOptions { Targets = { ["nextjs"] = new CacheTagsTargetOptions { Type = "test" } } });

    public KeySetTests()
    {
        _store = new KeySetStore(_database.Package);
        _ledger = new LedgerStore(_database.Package);
    }

    public void Dispose() => _database.Dispose();

    private static string Article(int i) => $"content-a0000000-0000-0000-0000-{i:D12}";

    [GeneratedRegex("^set-[0-9a-f]{32}$")]
    private static partial Regex SetKey();

    [Fact]
    public async Task A_page_showing_80_items_carries_one_set_key_in_place_of_their_type()
    {
        var header = await RenderAsync(Linked);

        Assert.Equal("umbraco", header[0]);
        Assert.Matches(SetKey(), Assert.Single(header[1..]));
        Assert.Equal(80, _database.Sql.ExecuteScalar<int>("SELECT COUNT(*) FROM theBuilderCacheTagsKeySetItem WHERE Hash = @0", header[1]["set-".Length..]));
    }

    [Fact]
    public async Task Umbracos_output_cache_keeps_every_item_of_a_page_that_carries_a_set()
    {
        // So this server evicts the page by the changed item's own key, without looking its sets up.
        IReadOnlyList<string>? tags = null;

        await RenderAsync(Linked, rendered: http => tags = (IReadOnlyList<string>)http.Items[CacheKeyResultFilter.OutputCacheTagsItem]!);

        Assert.Equal(["umbraco", .. Linked], tags);
    }

    [Fact]
    public async Task A_listing_of_80_items_carries_their_type_and_stores_no_set()
    {
        // Its selector already reaches it, and every page and filter of it would be a set of its own.
        var header = await RenderAsync(Linked, listing: true);

        Assert.Equal(["umbraco", "type-article"], header);
        Assert.Equal(0, _database.Sql.ExecuteScalar<int>("SELECT COUNT(*) FROM theBuilderCacheTagsKeySet"));
    }

    [Fact]
    public async Task A_page_showing_64_items_lists_them_and_stores_no_set()
    {
        var header = await RenderAsync(Linked.Take(64).ToList());

        Assert.Equal(["umbraco", .. Linked.Take(64)], header);
        Assert.Equal(0, _database.Sql.ExecuteScalar<int>("SELECT COUNT(*) FROM theBuilderCacheTagsKeySet"));
    }

    [Fact]
    public async Task Publishing_an_item_the_page_shows_purges_its_set_and_one_it_does_not_show_does_not()
    {
        var set = (await RenderAsync(Linked))[1];

        Guid linked = Record(Linked[40]);
        Guid notLinked = Record(NotLinked);
        _time.UtcNow = Noon.AddMinutes(2);
        await Releaser().ReleaseAsync(CancellationToken.None);

        Assert.Contains(set, Purged(linked));
        Assert.DoesNotContain(set, Purged(notLinked));
    }

    [Fact]
    public async Task A_purge_by_hand_of_an_item_the_page_shows_purges_its_set()
    {
        var set = (await RenderAsync(Linked))[1];

        var change = new Change(Guid.CreateVersion7(), Noon, ChangeKind.Custom, [], [], null, "purged by hand", [Linked[0]]);
        _ledger.Add(change, new PurgePlan([Linked[0]], Everything: false), "backoffice");
        await Releaser().ReleaseAsync(CancellationToken.None);

        Assert.Contains(set, Purged(change.Id));
    }

    [Fact]
    public async Task With_key_sets_off_the_page_carries_its_items_type()
    {
        var header = await RenderAsync(Linked, new CacheTagsOptions { KeySets = false });

        Assert.Equal(["umbraco", "type-article"], header);
        Assert.Equal(0, _database.Sql.ExecuteScalar<int>("SELECT COUNT(*) FROM theBuilderCacheTagsKeySet"));
    }

    [Fact]
    public async Task When_its_set_cannot_be_stored_the_page_carries_its_items_type()
    {
        _database.Sql.Execute("DROP TABLE theBuilderCacheTagsKeySetItem");

        var header = await RenderAsync(Linked);

        Assert.Equal(["umbraco", "type-article"], header);
    }

    [Fact]
    public async Task When_its_set_is_not_stored_in_time_the_page_carries_its_items_type()
    {
        // The database takes longer than the budget to start the write.
        var release = new ManualResetEventSlim();
        try
        {
            var slow = new KeySetStore(new PackageDatabase(
                Fake.Of<Umbraco.Cms.Infrastructure.Scoping.IScopeProvider>((method, _) =>
                {
                    release.Wait(TimeSpan.FromSeconds(10));
                    return null;
                }),
                Fake.Of<Umbraco.Cms.Core.Services.IRuntimeState>(),
                Fake.Of<Umbraco.Cms.Core.Services.IKeyValueService>()));
            var writer = new KeySetWriter(slow, _database.Package, TimeProvider.System, NullLogger<KeySetWriter>.Instance);

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var header = await RenderAsync(Linked, writer: writer);

            Assert.Equal(["umbraco", "type-article"], header);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"The response waited {watch.Elapsed} for its set.");
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task Responses_whose_sets_miss_the_budget_are_counted_in_a_warning_once_a_minute()
    {
        // A database too slow for sets: every page quietly lists types, so the log has to say so.
        using var release = new ManualResetEventSlim();
        try
        {
            var logger = new Warnings();
            var slow = new KeySetStore(Database(() => release.Wait(TimeSpan.FromSeconds(10))));
            var writer = new KeySetWriter(slow, _database.Package, _time, logger);

            async Task<string[]> RenderLateAsync(int page)
            {
                Task<string[]> render = RenderAsync([.. Linked, Article(1000 + page)], writer: writer);
                while (render.IsCompleted is false)
                {
                    _time.Advance(TimeSpan.FromMilliseconds(250));
                    await Task.Delay(10);
                }

                return await render;
            }

            for (var page = 0; page < 3; page++)
            {
                Assert.Equal(["umbraco", "type-article"], await RenderLateAsync(page));
            }

            Assert.Single(logger.Messages);
            Assert.StartsWith("Cache tags: 1 responses", logger.Messages[0]);

            _time.Advance(TimeSpan.FromMinutes(1));
            await RenderLateAsync(3);

            Assert.Equal(2, logger.Messages.Count);
            Assert.StartsWith("Cache tags: 3 responses", logger.Messages[1]);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task Two_renders_of_the_same_items_in_any_order_store_one_set()
    {
        var first = await RenderAsync(Linked);
        var second = await RenderAsync(Linked.Reverse().ToList());

        Assert.Equal(first, second);
        Assert.Equal(1, _database.Sql.ExecuteScalar<int>("SELECT COUNT(*) FROM theBuilderCacheTagsKeySet"));
        Assert.Equal(80, _database.Sql.ExecuteScalar<int>("SELECT COUNT(*) FROM theBuilderCacheTagsKeySetItem"));
    }

    [Fact]
    public async Task A_server_stores_at_most_100_new_sets_a_minute_and_then_lists_types()
    {
        var writer = Writer();
        for (var page = 0; page < KeySetWriter.MaxNewSetsPerMinute; page++)
        {
            Assert.Matches(SetKey(), (await RenderAsync([.. Linked, Article(1000 + page)], writer: writer))[1]);
        }

        Assert.Equal(["umbraco", "type-article"], await RenderAsync([.. Linked, Article(999)], writer: writer));

        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Matches(SetKey(), (await RenderAsync([.. Linked, Article(999)], writer: writer))[1]);
    }

    [Fact]
    public async Task A_server_marks_a_set_it_keeps_rendering_seen_once_a_day()
    {
        var writer = Writer();
        var set = (await RenderAsync(Linked, writer: writer))[1]["set-".Length..];

        _time.Advance(TimeSpan.FromHours(2));
        await RenderAsync(Linked, writer: writer);
        Assert.Equal(Noon, LastSeen(set));

        _time.Advance(TimeSpan.FromDays(1));
        await RenderAsync(Linked, writer: writer);
        Assert.Equal(_time.UtcNow, LastSeen(set));
    }

    [Fact]
    public async Task A_set_stored_after_the_release_by_a_render_that_read_the_old_item_is_purged_by_the_second_look()
    {
        // Stored long before the change: purged at release, not again.
        var before = (await RenderAsync(Linked))[1];
        Guid change = Record(Linked[0]);
        _time.UtcNow = Noon.AddMinutes(2);
        var releaser = Releaser();
        await releaser.ReleaseAsync(CancellationToken.None);

        // A render that read the item before its server applied the change stores its set just after.
        _time.Advance(TimeSpan.FromSeconds(1));
        var after = (await RenderAsync([.. Linked.Skip(1), Linked[0], Article(500)]))[1];

        _time.Advance(Delivery.Releaser.SetsRecheckDelay - TimeSpan.FromSeconds(2));
        Assert.Equal(0, releaser.RecheckSets());
        Assert.Single(_ledger.PurgesOf(change));

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, releaser.RecheckSets());
        Assert.Equal(0, releaser.RecheckSets());

        IReadOnlyList<Purge> purges = _ledger.PurgesOf(change);
        Assert.Equal(2, purges.Count);
        Assert.Contains(before, purges[0].Keys);
        Assert.Equal([after], purges[1].Keys);
    }

    [Fact]
    public async Task A_set_whose_write_waited_past_the_release_is_purged_by_the_second_look()
    {
        Guid change = Record(Linked[0]);
        DateTime released = Noon.AddMinutes(2);
        _time.UtcNow = released;
        var releaser = Releaser();
        await releaser.ReleaseAsync(CancellationToken.None);

        // A render that read the item before its server applied the change asks for its set before
        // the release, and its write only gets the database 6 s later, after the release looked.
        _time.UtcNow = released - TimeSpan.FromSeconds(5);
        var waited = false;
        using var written = new ManualResetEventSlim();
        PackageDatabase slowLock = Database(
            () =>
            {
                if (waited is false)
                {
                    waited = true;
                    _time.UtcNow += TimeSpan.FromSeconds(6);
                }
            },
            written.Set);
        var writer = new KeySetWriter(new KeySetStore(slowLock), _database.Package, _time, NullLogger<KeySetWriter>.Instance);
        await RenderAsync([.. Linked, Article(500)], writer: writer);
        Assert.True(written.Wait(TimeSpan.FromSeconds(5)));
        var set = (await RenderAsync([.. Linked, Article(500)], writer: writer))[1];
        Assert.Matches(SetKey(), set);

        _time.UtcNow = released + Delivery.Releaser.SetsRecheckDelay;
        Assert.Equal(1, releaser.RecheckSets());

        Assert.Contains(set, Purged(change));
    }

    [Fact]
    public async Task After_a_restart_a_server_marks_its_stored_sets_seen_without_counting_them_as_new()
    {
        IReadOnlyList<string>[] pages = Enumerable.Range(0, KeySetWriter.MaxNewSetsPerMinute).Select(page => (IReadOnlyList<string>)[.. Linked, Article(1000 + page)]).ToArray();
        var before = Writer();
        foreach (var page in pages)
        {
            await RenderAsync(page, writer: before);
        }

        // A new process remembers nothing: every page writes once to mark its set seen.
        _time.Advance(TimeSpan.FromMinutes(1));
        var restarted = Writer();
        foreach (var page in pages)
        {
            Assert.Matches(SetKey(), (await RenderAsync(page, writer: restarted))[1]);
        }

        Assert.Matches(SetKey(), (await RenderAsync([.. Linked, Article(999)], writer: restarted))[1]);
    }

    [Fact]
    public async Task A_header_its_custom_keys_fill_stores_no_set()
    {
        // Nothing standing for the items would fit, so a set would be a wasted write.
        IReadOnlyList<string> custom = Enumerable.Range(0, 200).Select(i => $"x-block-{i:D3}").ToList();

        var header = await RenderAsync(Linked, custom: custom);

        Assert.Equal(128, header.Length);
        Assert.DoesNotContain(header, key => key.StartsWith("set-", StringComparison.Ordinal));
        Assert.Equal(0, _database.Sql.ExecuteScalar<int>("SELECT COUNT(*) FROM theBuilderCacheTagsKeySet"));
    }

    [Fact]
    public async Task Umbracos_output_cache_is_handed_a_pages_keys_once_however_many_items_it_resolves()
    {
        HttpContext? http = null;
        await RenderAsync(Linked, rendered: rendered => http = rendered);
        var tags = new OutputCacheTags(new HttpContextAccessor { HttpContext = http });

        // Umbraco asks once per item it resolved, and adds every answer to the same entry.
        List<string> handed = Enumerable.Range(0, Linked.Count).SelectMany(_ => tags.GetTags(Fake.Of<IPublishedContent>())).ToList();

        Assert.Equal(["umbraco", .. Linked], handed);
    }

    [Fact]
    public async Task With_cache_tags_off_unseen_sets_are_kept()
    {
        // No change can be recorded to purge them, and nothing else reaches the responses that carry them.
        var set = (await RenderAsync(Linked))[1];
        _time.Advance(TimeSpan.FromDays(91));

        Assert.Null(PruneJob(new CacheTagsOptions { Enabled = false }).PruneKeySets(_time.UtcNow));

        Assert.Equal([set], _store.SetsOf([Linked[0]]));
    }

    [Fact]
    public async Task Unseen_sets_whose_purge_cannot_be_recorded_are_kept_for_the_next_run()
    {
        var set = (await RenderAsync(Linked))[1];
        _time.Advance(TimeSpan.FromDays(91));
        _database.Sql.Execute("ALTER TABLE theBuilderCacheTagsEvent RENAME TO broken");

        Assert.ThrowsAny<Exception>(() => PruneJob().PruneKeySets(_time.UtcNow));

        Assert.Equal([set], _store.SetsOf([Linked[0]]));
    }

    [Fact]
    public async Task A_change_to_items_no_set_holds_is_looked_up_again_but_purges_nothing_more()
    {
        await RenderAsync(Linked);
        Guid change = Record(NotLinked);
        _time.UtcNow = Noon.AddMinutes(2);
        var releaser = Releaser();
        await releaser.ReleaseAsync(CancellationToken.None);

        _time.Advance(Delivery.Releaser.SetsRecheckDelay);
        Assert.Equal(1, releaser.RecheckSets());

        Assert.Single(_ledger.PurgesOf(change));
    }

    [Fact]
    public async Task A_set_no_server_has_rendered_for_90_days_is_removed_and_purged()
    {
        var old = (await RenderAsync(Linked))[1];
        _time.Advance(TimeSpan.FromDays(80));
        var recent = (await RenderAsync([.. Linked, NotLinked]))[1];
        _time.Advance(TimeSpan.FromDays(11));

        Guid? change = PruneJob().PruneKeySets(_time.UtcNow);

        LedgerEntry recorded = Assert.Single(_ledger.Latest(10));
        Assert.Equal(change, recorded.Id);
        Assert.Equal([old], recorded.Plan.Keys);
        Assert.Equal([recent], _store.SetsOf([Linked[0]]));
        Assert.Null(PruneJob().PruneKeySets(_time.UtcNow));
    }

    [Fact]
    public async Task The_Info_tab_shows_how_many_sets_hold_an_item()
    {
        await RenderAsync(Linked);
        await RenderAsync([.. Linked.Take(70), NotLinked]);
        var controller = new CacheTagsKeySetsController(new CacheTagsKeySets(_store));

        Assert.Equal(2, Sets(controller.KeySets(Linked[0])));
        Assert.Equal(1, Sets(controller.KeySets(NotLinked)));
        Assert.Equal(0, Sets(controller.KeySets(Article(900))));
        Assert.IsType<BadRequestObjectResult>(controller.KeySets("x-dictionary"));
    }

    private static int Sets(IActionResult result)
        => (int)((OkObjectResult)result).Value!.GetType().GetProperty("sets")!.GetValue(((OkObjectResult)result).Value)!;

    private DateTime LastSeen(string hash)
        => _database.Sql.ExecuteScalar<DateTime>("SELECT LastSeenUtc FROM theBuilderCacheTagsKeySet WHERE Hash = @0", hash);

    private Guid Record(string item)
    {
        var change = new Change(Guid.CreateVersion7(), Noon, ChangeKind.Published, [new ChangedItem(new Guid(item["content-".Length..]), "article", Guid.Empty)], [], null);
        _ledger.Add(change, new PurgePlan([item, "type-article", "children-00000000-0000-0000-0000-000000000000"], Everything: false), "backoffice");
        return change.Id;
    }

    private IReadOnlyList<string> Purged(Guid change) => _ledger.PurgesOf(change).SelectMany(purge => purge.Keys).ToList();

    private KeySetWriter Writer() => new(_store, _database.Package, _time, NullLogger<KeySetWriter>.Instance);

    private Releaser Releaser()
    {
        var identity = new ServerIdentity(_time);
        var targets = new PurgeTargets(_options, null!, new TestDatabase(installed: false).Package, [new NoTarget()], new ConfigurationBuilder().Build(), _time, NullLogger<PurgeTargets>.Instance);
        return new Releaser(_ledger, _store, new DatabaseAckTransport(_database.Package), identity, targets, _options, _time, NullLogger<Releaser>.Instance);
    }

    private LedgerPruneJob PruneJob(CacheTagsOptions? options = null)
    {
        var identity = new ServerIdentity(_time);
        var recorder = new ChangeRecorder(new PurgePlanner(new NoDescendants()), _database.Package, _ledger, identity, new DeliverySignal(), options is null ? _options : new TestOptions(options), NullLogger<ChangeRecorder>.Instance);
        return new LedgerPruneJob(new DatabaseAckTransport(_database.Package), _ledger, _store, recorder, _database.Package, _time);
    }

    // The test database, with a step run each time a write or read starts, before it has the
    // database, and one run once it is done.
    private PackageDatabase Database(Action starting, Action? done = null)
    {
        IUmbracoDatabase database = Fake.Forwarding<IUmbracoDatabase>(_database.Sql);
        IScope scope = Fake.Of<IScope>((method, _) =>
        {
            if (method.Name == nameof(IScope.Dispose))
            {
                done?.Invoke();
            }

            return method.Name == "get_Database" ? database : null;
        });
        return new PackageDatabase(
            Fake.Of<IScopeProvider>((method, _) =>
            {
                if (method.Name == nameof(IScopeProvider.CreateScope))
                {
                    starting();
                    return scope;
                }

                return null;
            }),
            Fake.Of<Umbraco.Cms.Core.Services.IRuntimeState>(),
            Fake.Of<Umbraco.Cms.Core.Services.IKeyValueService>());
    }

    /// <summary>Renders a site's own action that shows these items, every one an article, and returns its header.</summary>
    /// <param name="listing">Render a Delivery API listing of articles that shows the items, instead.</param>
    /// <param name="custom">The site's own keys the response names too.</param>
    private async Task<string[]> RenderAsync(IReadOnlyList<string> items, CacheTagsOptions? options = null, KeySetWriter? writer = null, Action<HttpContext>? rendered = null, bool listing = false, IReadOnlyList<string>? custom = null)
    {
        var named = new CacheTagsResponse();
        foreach (var key in custom ?? [])
        {
            named.Add(key);
        }

        if (listing is false)
        {
            foreach (var item in items)
            {
                named.Add(item);
            }
        }

        IPublishedContentType article = Fake.Of<IPublishedContentType>((method, _) => method.Name == "get_Alias" ? "article" : null);
        IPublishedContent published = Fake.Of<IPublishedContent>((method, _) => method.Name == "get_ContentType" ? article : null);
        IPublishedContentCache cache = Fake.Of<IPublishedContentCache>((method, _) => method.Name == "GetById" ? published : null);
        var filter = new CacheKeyResultFilter(
            new TestOptions(options ?? new CacheTagsOptions()),
            Fake.Of<IRequestPreviewService>(),
            new ResponseCollector(null!, cache, Fake.Of<IPublishedMediaCache>(), Fake.Of<IVariationContextAccessor>(), null!, null!, Names.Of(["article"]), Roots.None),
            named,
            writer ?? Writer());

        var http = new DefaultHttpContext();
        http.Request.Path = listing ? "/umbraco/delivery/api/v2/content" : "/api/landing";
        http.Request.QueryString = new QueryString(listing ? "?filter=contentType:article" : "");
        var action = new ControllerActionDescriptor
        {
            ControllerTypeInfo = (listing ? typeof(Umbraco.Cms.Api.Delivery.Controllers.Content.QueryContentApiController) : typeof(LandingController)).GetTypeInfo(),
            MethodInfo = typeof(LandingController).GetMethod(nameof(LandingController.Get))!,
        };
        object value = listing ? items.Select(Shown).ToArray() : new { title = "Landing" };
        var context = new ResultExecutingContext(new ActionContext(http, new RouteData(), action), [], new ObjectResult(value), new LandingController());
        await filter.OnResultExecutionAsync(context, () => Task.FromResult(new ResultExecutedContext(context, [], context.Result, context.Controller)));
        rendered?.Invoke(http);
        return http.Response.Headers["X-Umbraco-Cache-Keys"].ToString().Split(", ");
    }

    // An article in a listing, as the Delivery API returns it.
    private static IApiContent Shown(string item) => Fake.Of<IApiContent>((method, _) => method.Name switch
    {
        "get_Id" => new Guid(item["content-".Length..]),
        "get_ContentType" => "article",
        "get_Properties" => new Dictionary<string, object?>(),
        _ => null,
    });

    [CacheTagged]
    private sealed class LandingController : ControllerBase
    {
        public IActionResult Get() => Ok();
    }

    // The warnings a writer logs.
    private sealed class Warnings : Microsoft.Extensions.Logging.ILogger<KeySetWriter>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning)
            {
                lock (Messages)
                {
                    Messages.Add(formatter(state, exception));
                }
            }
        }
    }

    private sealed class NoTarget : IPurgeTarget
    {
        public string Type => "test";

        public Task<string?> SendAsync(PurgeRequest request, CacheTagsTargetOptions target, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);
    }

    private sealed class NoDescendants : IDescendants
    {
        public IEnumerable<Guid> Of(Guid key) => [];

        public IEnumerable<string> TypesOf(IReadOnlyCollection<Guid> keys, bool media) => [];

        public IEnumerable<(Guid Key, string Alias, Guid? Root)> TypesAndRootsOf(IReadOnlyCollection<Guid> keys) => [];
    }
}
