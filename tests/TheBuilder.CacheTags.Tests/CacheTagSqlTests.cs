using Microsoft.Data.Sqlite;
using NPoco;
using TheBuilder.CacheTags.Storage;

namespace TheBuilder.CacheTags.Tests;

/// <summary>The mapping store's SQL against a real SQLite database.</summary>
public sealed class CacheTagSqlTests : IDisposable
{
    private static readonly DateTime Monday = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Tuesday = Monday.AddDays(1);
    private static readonly DateTime NextWeek = Monday.AddDays(9);

    private readonly SqliteConnection _connection;
    private readonly Database _database;

    public CacheTagSqlTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _database = new Database(_connection, DatabaseType.SQLite);

        // The schema the package's migration creates, written out for SQLite.
        _database.Execute("""
            CREATE TABLE theBuilderCacheTagsResponse (ResponseKey TEXT PRIMARY KEY, Path TEXT NOT NULL, Query TEXT NOT NULL,
                Culture TEXT NOT NULL, Segment TEXT NOT NULL, StartItem TEXT NOT NULL, RenderedUtc TEXT NOT NULL, SeenUtc TEXT NOT NULL);
            CREATE TABLE theBuilderCacheTagsResponseKey (Id INTEGER PRIMARY KEY AUTOINCREMENT, ResponseKey TEXT NOT NULL,
                CacheKey TEXT NOT NULL, RenderedUtc TEXT NOT NULL);
            CREATE UNIQUE INDEX IX_Pair ON theBuilderCacheTagsResponseKey (ResponseKey, CacheKey);
            """);
    }

    public void Dispose()
    {
        _database.Dispose();
        _connection.Dispose();
    }

    private static CachedResponse Render(string responseKey, DateTime at, string culture, params string[] keys)
        => new()
        {
            ResponseKey = responseKey,
            Path = "/umbraco/delivery/api/v2/content/item/x/",
            Query = string.Empty,
            Culture = culture,
            Segment = string.Empty,
            StartItem = string.Empty,
            Keys = keys,
            RenderedUtc = at,
            SeenUtc = at,
        };

    private static CachedResponse Request(string responseKey, DateTime at, string path, string query)
    {
        CachedResponse response = Render(responseKey, at, "", "content-1");
        return new CachedResponse
        {
            ResponseKey = response.ResponseKey,
            Path = path,
            Query = query,
            Culture = response.Culture,
            Segment = response.Segment,
            StartItem = response.StartItem,
            Keys = response.Keys,
            RenderedUtc = at,
            SeenUtc = at,
        };
    }

    [Fact]
    public void A_response_is_found_by_any_key_it_covers()
    {
        CacheTagSql.Record(_database, Render("response-a", Monday, "", "content-1", "media-2"));
        CacheTagSql.Record(_database, Render("response-b", Monday, "", "content-3"));

        Assert.Equal(["response-a"], CacheTagSql.FindResponseKeys(_database, ["media-2"], null));
        Assert.Equal(["response-a", "response-b"], CacheTagSql.FindResponseKeys(_database, ["content-1", "content-3"], null).Order(StringComparer.Ordinal));
        Assert.Empty(CacheTagSql.FindResponseKeys(_database, ["content-9"], null));
    }

    [Fact]
    public void A_later_render_with_fewer_keys_still_finds_the_dropped_key_until_it_ages_out()
    {
        CacheTagSql.Record(_database, Render("response-a", Monday, "", "content-1", "content-2"));
        CacheTagSql.Record(_database, Render("response-a", Tuesday, "", "content-1"));

        // Another server may still hold Monday's render, so content-2 must keep reaching it.
        Assert.Equal(["response-a"], CacheTagSql.FindResponseKeys(_database, ["content-2"], null));
        Assert.Equal(["content-1"], CacheTagSql.Get(_database, "response-a")!.Keys);

        CacheTagSql.Prune(_database, Monday.AddHours(1));

        Assert.Empty(CacheTagSql.FindResponseKeys(_database, ["content-2"], null));
        Assert.Equal(["response-a"], CacheTagSql.FindResponseKeys(_database, ["content-1"], null));
    }

    [Fact]
    public void A_served_response_is_kept_and_an_unserved_one_is_pruned_with_its_keys()
    {
        CacheTagSql.Record(_database, Render("response-a", Monday, "", "content-1"));
        CacheTagSql.Record(_database, Render("response-b", Monday, "", "content-1"));
        CacheTagSql.Touch(_database, ["response-a"], NextWeek);

        CacheTagSql.Prune(_database, NextWeek.AddDays(-1));

        Assert.Equal(["response-a"], CacheTagSql.FindResponseKeys(_database, ["content-1"], null));
        Assert.Null(CacheTagSql.Get(_database, "response-b"));
        Assert.Equal(0, _database.ExecuteScalar<int>("SELECT COUNT(*) FROM theBuilderCacheTagsResponseKey WHERE ResponseKey = 'response-b'"));
    }

    [Fact]
    public void A_culture_limited_purge_reaches_that_culture_and_responses_without_one()
    {
        CacheTagSql.Record(_database, Render("response-da", Monday, "da-dk", "content-1"));
        CacheTagSql.Record(_database, Render("response-en", Monday, "en-us", "content-1"));
        CacheTagSql.Record(_database, Render("response-none", Monday, "", "content-1"));

        Assert.Equal(["response-da", "response-none"], CacheTagSql.FindResponseKeys(_database, ["content-1"], ["da-DK"]).Order(StringComparer.Ordinal));
        Assert.Equal(3, CacheTagSql.FindResponseKeys(_database, ["content-1"], ["*"]).Count);
    }

    [Fact]
    public void Recording_the_same_render_twice_does_not_duplicate_rows()
    {
        CacheTagSql.Record(_database, Render("response-a", Monday, "", "content-1"));
        CacheTagSql.Record(_database, Render("response-a", Monday, "", "content-1"));

        Assert.Equal(1, _database.ExecuteScalar<int>("SELECT COUNT(*) FROM theBuilderCacheTagsResponseKey"));
        Assert.Equal(1, _database.ExecuteScalar<int>("SELECT COUNT(*) FROM theBuilderCacheTagsResponse"));
    }

    [Fact]
    public void A_later_render_adds_its_new_keys_and_refreshes_the_ones_it_still_has()
    {
        CacheTagSql.Record(_database, Render("response-a", Monday, "", "content-1", "content-2"));
        CacheTagSql.Record(_database, Render("response-a", Tuesday, "", "content-2", "content-3"));

        CacheTagSql.Prune(_database, Monday.AddHours(1));

        Assert.Empty(CacheTagSql.FindResponseKeys(_database, ["content-1"], null));
        Assert.Equal(["response-a"], CacheTagSql.FindResponseKeys(_database, ["content-2"], null));
        Assert.Equal(["response-a"], CacheTagSql.FindResponseKeys(_database, ["content-3"], null));
    }

    [Fact]
    public void A_search_finds_keys_by_part_of_the_key_once_each()
    {
        CacheTagSql.Record(_database, Render("response-a", Monday, "", "content-e0000000-0000-0000-0000-000000000003", "type-article"));
        CacheTagSql.Record(_database, Render("response-b", Monday, "", "content-e0000000-0000-0000-0000-000000000003", "content-e0000000-0000-0000-0000-000000000004"));

        CacheSearchResult found = CacheTagSql.Search(_database, "0000003", 10);

        Assert.Equal(["content-e0000000-0000-0000-0000-000000000003"], found.Keys);
        Assert.Empty(found.Responses);
    }

    [Fact]
    public void A_search_finds_responses_by_path_or_by_a_query_value_as_typed_newest_served_first()
    {
        CacheTagSql.Record(_database, Request("response-a", Monday, "/umbraco/delivery/api/v2/content/item/articles/forest-walks/", ""));
        CacheTagSql.Record(_database, Request("response-b", Tuesday, "/umbraco/delivery/api/v2/content", "fetch=descendants%3A%2Fguides%2F&sort=name%3Aasc"));
        CacheTagSql.Record(_database, Request("response-c", Tuesday, "/umbraco/delivery/api/v2/media", ""));

        Assert.Equal(["response-a"], CacheTagSql.Search(_database, "Forest-Walks", 10).Responses.Select(response => response.ResponseKey));
        Assert.Equal(["response-b"], CacheTagSql.Search(_database, "descendants:/guides/", 10).Responses.Select(response => response.ResponseKey));
        Assert.Equal(["response-c"], CacheTagSql.Search(_database, "nse-c", 10).Responses.Select(response => response.ResponseKey));
        CachedResponse[] content = [.. CacheTagSql.Search(_database, "v2/content", 10).Responses];
        Assert.Equal(["response-b", "response-a"], content.Select(response => response.ResponseKey));
        Assert.Equal("fetch=descendants%3A%2Fguides%2F&sort=name%3Aasc", content[0].Query);
    }

    [Fact]
    public void A_search_takes_its_wildcards_literally_and_returns_at_most_take()
    {
        CacheTagSql.Record(_database, Render("response-a", Monday, "", "x-dictionary-en-us", "x-dictionary-da-dk", "x-dictionary-de-de"));

        Assert.Empty(CacheTagSql.Search(_database, "%", 10).Keys);
        Assert.Empty(CacheTagSql.Search(_database, "x_dictionary", 10).Keys);
        Assert.Equal(["x-dictionary-da-dk", "x-dictionary-de-de"], CacheTagSql.Search(_database, "dictionary-d", 2).Keys);
        Assert.Equal(["x-dictionary-da-dk", "x-dictionary-de-de"], CacheTagSql.Search(_database, "x-dictionary", 2).Keys);
    }
}
