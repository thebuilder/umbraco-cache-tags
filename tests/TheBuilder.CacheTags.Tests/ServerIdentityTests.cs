using TheBuilder.CacheTags.Delivery;

namespace TheBuilder.CacheTags.Tests;

public sealed class ServerIdentityTests
{
    private static readonly DateTime Now = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_server_counts_itself_as_running_before_its_first_heartbeat_is_recorded()
    {
        var identity = new ServerIdentity(TimeProvider.System);

        IReadOnlyCollection<ServerPresence> servers = identity.Among([new ServerPresence("other", Now.AddHours(-1), Now)], Now);

        Assert.Equal(["other", identity.Name], servers.Select(server => server.Server));
        Assert.Equal(identity.StartedUtc, servers.Single(server => server.Server == identity.Name).FirstSeenUtc);
    }

    [Fact]
    public void A_server_whose_heartbeat_is_recorded_is_listed_once()
    {
        var identity = new ServerIdentity(TimeProvider.System);

        IReadOnlyCollection<ServerPresence> servers = identity.Among([new ServerPresence(identity.Name, identity.StartedUtc, Now.AddMinutes(-10))], Now);

        Assert.Equal(Now, servers.Single().LastSeenUtc);
    }
}
