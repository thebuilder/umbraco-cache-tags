using TheBuilder.CacheTags.Delivery;
using TheBuilder.CacheTags.Ledger;

namespace TheBuilder.CacheTags.Tests;

public sealed class IndexCheckTests
{
    private static readonly DateTime Applied = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);

    private static IndexedDocument Built(string culture, int secondsAfterApplied) => new(culture, Applied.AddSeconds(secondsAfterApplied));

    [Fact]
    public void A_publish_is_indexed_once_every_culture_was_rebuilt_after_the_change_was_applied()
    {
        Assert.False(IndexCheck.HasIndexed(ChangeKind.Published, [Built("en-us", 2), Built("da-dk", -30)], Applied, indexable: true));
        Assert.True(IndexCheck.HasIndexed(ChangeKind.Published, [Built("en-us", 2), Built("da-dk", 3)], Applied, indexable: true));
    }

    [Fact]
    public void A_document_built_before_the_package_was_installed_has_not_caught_up()
    {
        Assert.False(IndexCheck.HasIndexed(ChangeKind.Unpublished, [new IndexedDocument("none", null)], Applied, indexable: true));
    }

    [Fact]
    public void A_new_item_waits_until_the_index_has_it()
    {
        Assert.False(IndexCheck.HasIndexed(ChangeKind.Published, [], Applied, indexable: true));
    }

    [Theory]
    [InlineData(nameof(ChangeKind.Deleted))]
    [InlineData(nameof(ChangeKind.Trashed))]
    public void Deleting_or_trashing_is_indexed_once_no_document_is_left(string name)
    {
        ChangeKind kind = Enum.Parse<ChangeKind>(name);
        Assert.False(IndexCheck.HasIndexed(kind, [Built("none", -30)], Applied, indexable: true));
        Assert.True(IndexCheck.HasIndexed(kind, [], Applied, indexable: true));
    }

    [Fact]
    public void An_item_of_a_type_the_index_skips_never_waits()
    {
        Assert.True(IndexCheck.HasIndexed(ChangeKind.Published, [], Applied, indexable: false));
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
