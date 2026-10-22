using TheBuilder.CacheTags.Delivery;

namespace TheBuilder.CacheTags.Tests;

public sealed class StageBarrierTests
{
    private static readonly DateTime Changed = new(2026, 10, 22, 9, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan IndexTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ServerTimeout = TimeSpan.FromMinutes(2);

    private static ServerPresence Server(string name, int startedSecondsBefore = 600, int seenSecondsAgo = 5)
        => new(name, Changed.AddSeconds(-startedSecondsBefore), Changed.AddSeconds(10 - seenSecondsAgo));

    private static StageBarrier Decide(ServerPresence[] servers, string[] acked, int secondsAfter = 10)
        => StageBarrier.ForAcks(Changed, Changed.AddSeconds(secondsAfter), servers, acked.ToHashSet(), AckTimeout, ServerTimeout);

    [Fact]
    public void Waits_until_every_active_server_has_acknowledged()
    {
        StageBarrier waiting = Decide([Server("backoffice"), Server("delivery-1"), Server("delivery-2")], ["backoffice", "delivery-1"]);
        StageBarrier done = Decide([Server("backoffice"), Server("delivery-1"), Server("delivery-2")], ["backoffice", "delivery-1", "delivery-2"]);

        Assert.False(waiting.Open);
        Assert.Equal(["delivery-2"], waiting.Waiting);
        Assert.True(done.Open);
        Assert.False(done.TimedOut);
    }

    [Fact]
    public void A_single_server_that_acknowledged_releases_at_once()
    {
        Assert.True(Decide([Server("only")], ["only"], secondsAfter: 0).Open);
    }

    [Fact]
    public void A_server_that_stopped_sending_heartbeats_is_not_waited_for()
    {
        Assert.True(Decide([Server("backoffice"), Server("gone", seenSecondsAgo: 300)], ["backoffice"]).Open);
    }

    [Fact]
    public void A_server_that_started_after_the_change_is_not_waited_for()
    {
        Assert.True(Decide([Server("backoffice"), Server("new", startedSecondsBefore: -5)], ["backoffice"]).Open);
    }

    [Fact]
    public void After_the_timeout_it_releases_and_names_the_servers_that_did_not_acknowledge()
    {
        StageBarrier decision = Decide([Server("backoffice"), Server("stuck")], ["backoffice"], secondsAfter: 61);

        Assert.True(decision.Open);
        Assert.True(decision.TimedOut);
        Assert.Equal(["stuck"], decision.Waiting);
    }

    [Fact]
    public void Listings_wait_for_every_server_that_acknowledged_the_change_to_index_it()
    {
        ServerPresence[] servers = [Server("a"), Server("b")];

        StageBarrier waiting = StageBarrier.ForIndex(Changed, Changed.AddSeconds(10), servers, new HashSet<string> { "a", "b" }, new HashSet<string> { "b" }, IndexTimeout, ServerTimeout);
        StageBarrier ready = StageBarrier.ForIndex(Changed, Changed.AddSeconds(10), servers, new HashSet<string> { "a", "b" }, new HashSet<string> { "a", "b" }, IndexTimeout, ServerTimeout);

        Assert.False(waiting.Open);
        Assert.Equal(["a"], waiting.Waiting);
        Assert.True(ready.Open);
        Assert.False(ready.TimedOut);
    }

    [Fact]
    public void Listings_do_not_wait_for_a_server_that_did_not_acknowledge_or_has_stopped()
    {
        ServerPresence[] servers = [Server("a"), Server("stopped", seenSecondsAgo: 300), Server("late")];

        StageBarrier barrier = StageBarrier.ForIndex(Changed, Changed.AddSeconds(10), servers, new HashSet<string> { "a", "stopped" }, new HashSet<string> { "a" }, IndexTimeout, ServerTimeout);

        Assert.True(barrier.Open);
    }

    [Fact]
    public void Listings_are_purged_anyway_once_the_index_timeout_has_passed()
    {
        StageBarrier barrier = StageBarrier.ForIndex(Changed, Changed + IndexTimeout, [Server("a", seenSecondsAgo: 10 - (int)IndexTimeout.TotalSeconds)], new HashSet<string> { "a" }, new HashSet<string>(), IndexTimeout, ServerTimeout);

        Assert.True(barrier.Open);
        Assert.True(barrier.TimedOut);
        Assert.Equal(["a"], barrier.Waiting);
    }
}
