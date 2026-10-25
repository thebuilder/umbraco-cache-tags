using Microsoft.Extensions.Logging.Abstractions;
using TheBuilder.CacheTags.Storage;

namespace TheBuilder.CacheTags.Tests;

/// <summary>Pruning the mapping through the default store, against the package's tables in SQLite.</summary>
public sealed class MappingPruneTests : IDisposable
{
    private static readonly DateTime Monday = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

    private readonly TestDatabase _database = new();
    private readonly UmbracoCacheTagStore _store;

    public MappingPruneTests() => _store = new UmbracoCacheTagStore(_database.Package, NullLogger<UmbracoCacheTagStore>.Instance);

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
