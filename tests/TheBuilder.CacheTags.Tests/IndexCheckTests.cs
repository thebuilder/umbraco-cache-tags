using TheBuilder.CacheTags.Delivery;
using TheBuilder.CacheTags.Ledger;

namespace TheBuilder.CacheTags.Tests;

public sealed class IndexCheckTests
{
    private const string Now = "12|11|da-DK,en-US|-1,1060,1061|2";
    private const string Before = "10|9|da-DK,en-US|-1,1060,1061|2";

    [Fact]
    public void An_item_is_indexed_once_every_culture_was_built_from_its_state_now()
    {
        Assert.False(IndexCheck.HasIndexed(Now, [new IndexedDocument("en-us", Now), new IndexedDocument("da-dk", Before)]));
        Assert.True(IndexCheck.HasIndexed(Now, [new IndexedDocument("en-us", Now), new IndexedDocument("da-dk", Now)]));
    }

    [Fact]
    public void A_document_built_before_the_package_was_installed_has_not_caught_up()
    {
        Assert.False(IndexCheck.HasIndexed(Now, [new IndexedDocument("none", null)]));
    }

    [Fact]
    public void A_new_item_waits_until_the_index_has_it()
    {
        Assert.False(IndexCheck.HasIndexed(Now, []));
    }

    [Fact]
    public void An_item_that_should_have_no_documents_is_indexed_once_none_is_left()
    {
        Assert.False(IndexCheck.HasIndexed(null, [new IndexedDocument("none", Before)]));
        Assert.True(IndexCheck.HasIndexed(null, []));
    }

    [Theory]
    [InlineData(nameof(ChangeKind.Published), true)]
    [InlineData(nameof(ChangeKind.Sorted), true)]
    [InlineData(nameof(ChangeKind.Moved), true)]
    [InlineData(nameof(ChangeKind.MediaSaved), false)]
    [InlineData(nameof(ChangeKind.TypesChanged), false)]
    [InlineData(nameof(ChangeKind.Everything), false)]
    public void Only_content_item_changes_wait_for_the_index(string kind, bool waits)
    {
        Assert.Equal(waits, IndexCheck.Waits(Enum.Parse<ChangeKind>(kind)));
    }
}

public sealed class ListingDecisionTests
{
    private static readonly DateTime Released = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan IndexTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ServerTimeout = TimeSpan.FromMinutes(2);

    private static ServerPresence Server(string name, DateTime lastSeen) => new(name, Released.AddHours(-1), lastSeen);

    [Fact]
    public void Listings_wait_for_every_server_that_acknowledged_the_change_to_index_it()
    {
        DateTime now = Released.AddSeconds(10);
        ServerPresence[] servers = [Server("a", now), Server("b", now)];

        ListingDecision waiting = ListingDecision.Decide(Released, now, servers, new HashSet<string> { "a", "b" }, new HashSet<string> { "b" }, IndexTimeout, ServerTimeout);
        ListingDecision ready = ListingDecision.Decide(Released, now, servers, new HashSet<string> { "a", "b" }, new HashSet<string> { "a", "b" }, IndexTimeout, ServerTimeout);

        Assert.False(waiting.Release);
        Assert.Equal(["a"], waiting.Waiting);
        Assert.True(ready.Release);
        Assert.False(ready.TimedOut);
    }

    [Fact]
    public void A_server_that_did_not_acknowledge_or_has_stopped_is_not_waited_for()
    {
        DateTime now = Released.AddSeconds(10);
        ServerPresence[] servers = [Server("a", now), Server("stopped", now.AddMinutes(-3)), Server("late", now)];

        ListingDecision decision = ListingDecision.Decide(Released, now, servers, new HashSet<string> { "a", "stopped" }, new HashSet<string> { "a" }, IndexTimeout, ServerTimeout);

        Assert.True(decision.Release);
    }

    [Fact]
    public void Listings_are_purged_anyway_once_the_index_timeout_has_passed()
    {
        DateTime now = Released + IndexTimeout;

        ListingDecision decision = ListingDecision.Decide(Released, now, [Server("a", now)], new HashSet<string> { "a" }, new HashSet<string>(), IndexTimeout, ServerTimeout);

        Assert.True(decision.Release);
        Assert.True(decision.TimedOut);
        Assert.Equal(["a"], decision.Waiting);
    }
}
