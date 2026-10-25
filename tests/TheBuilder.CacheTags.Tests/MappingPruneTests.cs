using Microsoft.Extensions.Logging.Abstractions;
using TheBuilder.CacheTags.Configuration;
using TheBuilder.CacheTags.Delivery;
using TheBuilder.CacheTags.Ledger;
using TheBuilder.CacheTags.Storage;

namespace TheBuilder.CacheTags.Tests;

/// <summary>
///     Pruning and forgetting the mapping through the daily job and the default store, against the
///     package's tables in SQLite.
/// </summary>
public sealed class MappingPruneTests : IDisposable
{
    private static readonly DateTime Monday = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

    private readonly TestDatabase _database = new();
    private readonly UmbracoCacheTagStore _store;
    private readonly LedgerStore _ledger;
    private readonly ManualTime _time = new(Monday);

    public MappingPruneTests()
    {
        _store = new UmbracoCacheTagStore(_database.Package, NullLogger<UmbracoCacheTagStore>.Instance);
        _ledger = new LedgerStore(_database.Package);
    }

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task A_response_last_touched_just_inside_the_writers_hour_survives_a_prune_at_the_cutoff()
    {
        // Touched at Monday noon. A request at 12:59 skips its touch (the writer touches at most once an
        // hour), so a frontend may keep that copy until 12:59 a week later. A prune whose cutoff is
        // 12:30 must still find the response.
        await _store.RecordAsync([Render("response-a", Monday, "content-1")]);

        await _store.PruneAsync(Monday.AddMinutes(30));

        Assert.Equal(["response-a"], await _store.FindResponseKeysAsync(["content-1"], null));
    }

    [Fact]
    public async Task A_response_nobody_asked_for_in_the_retention_and_the_writers_hour_is_pruned()
    {
        await _store.RecordAsync([Render("response-a", Monday, "content-1")]);

        await _store.PruneAsync(Monday.AddHours(1).AddMinutes(1));

        Assert.Empty(await _store.FindResponseKeysAsync(["content-1"], null));
    }

    [Fact]
    public async Task A_response_a_sent_purge_expired_is_forgotten()
    {
        await _store.RecordAsync([Render("response-a", Monday.AddHours(-2), "content-1")]);
        Guid change = Change(Monday, ["response-a", "content-1"], ["webhook"]);
        SendAll(change, Monday);

        await RunJobAsync(Monday.AddDays(1));

        Assert.Empty(await _store.FindResponseKeysAsync(["content-1"], null));
    }

    [Fact]
    public async Task A_response_rendered_again_after_the_change_is_kept()
    {
        await _store.RecordAsync([Render("response-a", Monday.AddHours(-2), "content-1")]);
        Guid change = Change(Monday, ["response-a", "content-1"], ["webhook"]);
        SendAll(change, Monday);
        await _store.RecordAsync([Render("response-a", Monday.AddMinutes(5), "content-1")]);

        await RunJobAsync(Monday.AddDays(1));

        Assert.Equal(["response-a"], await _store.FindResponseKeysAsync(["content-1"], null));
    }

    [Fact]
    public async Task A_response_rendered_within_the_writers_hour_before_the_change_is_kept()
    {
        // Rendered at 11:30, so the writer skips rewriting the same keys until 12:30: a frontend that
        // fetched it again at 12:05, after the purge, has a copy the row does not show.
        await _store.RecordAsync([Render("response-a", Monday.AddMinutes(-30), "content-1")]);
        Guid change = Change(Monday, ["response-a", "content-1"], ["webhook"]);
        SendAll(change, Monday);

        await RunJobAsync(Monday.AddDays(1));

        Assert.Equal(["response-a"], await _store.FindResponseKeysAsync(["content-1"], null));
    }

    [Fact]
    public async Task A_response_is_kept_while_any_purge_of_the_change_was_not_sent()
    {
        await _store.RecordAsync([Render("response-a", Monday.AddHours(-2), "content-1")]);
        Guid change = Change(Monday, ["response-a", "content-1"], ["webhook", "cloudflare"]);
        Purge cloudflare = _ledger.PurgesOf(change).Single(purge => purge.Target == "cloudflare");
        _ledger.ClaimSend(cloudflare.Id, "server-1", Monday, Monday.AddSeconds(30));
        _ledger.Failed(cloudflare.Id, "server-1", maxAttempts: 1, "503 Service Unavailable", Monday);
        Purge webhook = _ledger.PurgesOf(change).Single(purge => purge.Target == "webhook");
        _ledger.ClaimSend(webhook.Id, "server-1", Monday, Monday.AddSeconds(30));
        _ledger.Sent(webhook.Id, "server-1", Monday);

        await RunJobAsync(Monday.AddDays(1));

        Assert.Equal(["response-a"], await _store.FindResponseKeysAsync(["content-1"], null));
    }

    [Fact]
    public async Task A_response_is_kept_while_its_change_waits_for_the_index()
    {
        await _store.RecordAsync([Render("response-a", Monday.AddHours(-2), "content-1")]);
        Guid change = Change(Monday, ["response-a", "content-1"], ["webhook"], ChangeStage.Indexing);
        SendAll(change, Monday);

        await RunJobAsync(Monday.AddDays(1));

        Assert.Equal(["response-a"], await _store.FindResponseKeysAsync(["content-1"], null));
    }

    [Fact]
    public async Task A_response_is_kept_while_a_server_released_without_the_change_may_still_evict_it_late()
    {
        await _store.RecordAsync([Render("response-a", Monday.AddHours(-2), "content-1")]);
        Guid change = Change(Monday, ["response-a", "content-1"], ["webhook"]);
        SendAll(change, Monday);

        await RunJobAsync(Monday.AddMinutes(14));
        Assert.Equal(["response-a"], await _store.FindResponseKeysAsync(["content-1"], null));

        await RunJobAsync(Monday.AddMinutes(16));
        Assert.Empty(await _store.FindResponseKeysAsync(["content-1"], null));
    }

    [Fact]
    public async Task A_sent_purge_with_the_key_prefix_forgets_the_response_it_names()
    {
        await _store.RecordAsync([Render("response-a", Monday.AddHours(-2), "content-1"), Render("response-b", Monday.AddHours(-2), "content-1")]);
        Guid change = Change(Monday, ["site-response-a", "site-content-1"], ["webhook"]);
        SendAll(change, Monday);

        await RunJobAsync(Monday.AddDays(1), new CacheTagsOptions { KeyPrefix = "site-" });

        Assert.Equal(["response-b"], await _store.FindResponseKeysAsync(["content-1"], null));
    }

    private Guid Change(DateTime at, string[] keys, string[] targets, ChangeStage stage = ChangeStage.Released)
    {
        var change = new Change(Guid.CreateVersion7(), at, ChangeKind.Published, [new ChangedItem(Guid.NewGuid(), "page", Guid.Empty)], [], null);
        _ledger.Add(change, new PurgePlan(["content-1"], Everything: false, Cultures: null), "backoffice");
        _ledger.Release(change.Id, stage, [], at, targets.Select(target => new NewPurge(target, keys, at)));
        return change.Id;
    }

    private void SendAll(Guid change, DateTime at)
    {
        foreach (Purge purge in _ledger.PurgesOf(change))
        {
            _ledger.ClaimSend(purge.Id, "server-1", at, at.AddSeconds(30));
            _ledger.Sent(purge.Id, "server-1", at);
        }
    }

    private Task RunJobAsync(DateTime at, CacheTagsOptions? options = null)
    {
        _time.UtcNow = at;
        return new MappingPruneJob(_store, new DatabaseAckTransport(_database.Package), _ledger, _database.Package, new TestOptions(options ?? new CacheTagsOptions()), _time, NullLogger<MappingPruneJob>.Instance).RunJobAsync();
    }

    private static CachedResponse Render(string responseKey, DateTime at, params string[] keys)
        => new()
        {
            ResponseKey = responseKey,
            Path = "/umbraco/delivery/api/v2/content/item/x/",
            Query = string.Empty,
            Culture = string.Empty,
            Segment = string.Empty,
            StartItem = string.Empty,
            Keys = keys,
            RenderedUtc = at,
            SeenUtc = at,
        };
}
