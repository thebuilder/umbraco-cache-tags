using TheBuilder.CacheTags.Delivery;

namespace TheBuilder.CacheTags.Tests;

public sealed class ReleaseDecisionTests
{
    private static readonly DateTime Changed = new(2026, 10, 22, 9, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ServerTimeout = TimeSpan.FromMinutes(2);

    private static ServerPresence Server(string name, int startedSecondsBefore = 600, int seenSecondsAgo = 5)
        => new(name, Changed.AddSeconds(-startedSecondsBefore), Changed.AddSeconds(10 - seenSecondsAgo));

    private static ReleaseDecision Decide(ServerPresence[] servers, string[] acked, int secondsAfter = 10)
        => ReleaseDecision.Decide(Changed, Changed.AddSeconds(secondsAfter), servers, acked.ToHashSet(), AckTimeout, ServerTimeout);

    [Fact]
    public void Waits_until_every_active_server_has_acknowledged()
    {
        ReleaseDecision waiting = Decide([Server("backoffice"), Server("delivery-1"), Server("delivery-2")], ["backoffice", "delivery-1"]);
        ReleaseDecision done = Decide([Server("backoffice"), Server("delivery-1"), Server("delivery-2")], ["backoffice", "delivery-1", "delivery-2"]);

        Assert.False(waiting.Release);
        Assert.Equal(["delivery-2"], waiting.Missing);
        Assert.True(done.Release);
        Assert.False(done.TimedOut);
    }

    [Fact]
    public void A_single_server_that_acknowledged_releases_at_once()
    {
        Assert.True(Decide([Server("only")], ["only"], secondsAfter: 0).Release);
    }

    [Fact]
    public void A_server_that_stopped_sending_heartbeats_is_not_waited_for()
    {
        Assert.True(Decide([Server("backoffice"), Server("gone", seenSecondsAgo: 300)], ["backoffice"]).Release);
    }

    [Fact]
    public void A_server_that_started_after_the_change_is_not_waited_for()
    {
        Assert.True(Decide([Server("backoffice"), Server("new", startedSecondsBefore: -5)], ["backoffice"]).Release);
    }

    [Fact]
    public void After_the_timeout_it_releases_and_names_the_servers_that_did_not_acknowledge()
    {
        ReleaseDecision decision = Decide([Server("backoffice"), Server("stuck")], ["backoffice"], secondsAfter: 61);

        Assert.True(decision.Release);
        Assert.True(decision.TimedOut);
        Assert.Equal(["stuck"], decision.Missing);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(3, 8)]
    [InlineData(9, 512)]
    [InlineData(10, 600)]
    [InlineData(25, 600)]
    public void Failed_purges_back_off_doubling_up_to_ten_minutes(int failedAttempts, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), Backoff.After(failedAttempts));
    }
}
