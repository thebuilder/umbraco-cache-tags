using Microsoft.Data.Sqlite;
using NPoco;
using TheBuilder.CacheTags.Delivery;
using TheBuilder.CacheTags.Ledger;
using TheBuilder.CacheTags.Storage;

namespace TheBuilder.CacheTags.Tests;

/// <summary>Leases, the outbox and acks against a real SQLite database.</summary>
public sealed class DeliverySqlTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 22, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Change = new("01a1606c-318c-77da-8780-ba1b54b6add8");

    private readonly SqliteConnection _connection;
    private readonly Database _database;

    public DeliverySqlTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _database = new Database(_connection, DatabaseType.SQLite);

        // The schema the package's migrations create, written out for SQLite.
        _database.Execute("""
            CREATE TABLE theBuilderCacheTagsEvent (Id TEXT PRIMARY KEY, CreatedUtc TEXT NOT NULL, Kind TEXT NOT NULL, Server TEXT NOT NULL,
                Change TEXT NOT NULL, Plan TEXT NOT NULL, Signal TEXT NULL, Status TEXT NULL, ReleasedUtc TEXT NULL, Missing TEXT NULL,
                LeaseOwner TEXT NULL, LeaseUntilUtc TEXT NULL);
            CREATE TABLE theBuilderCacheTagsOutbox (Id TEXT PRIMARY KEY, EventId TEXT NOT NULL, Target TEXT NOT NULL, Keys TEXT NOT NULL,
                State TEXT NOT NULL, Attempts INTEGER NOT NULL, CreatedUtc TEXT NOT NULL, NotBeforeUtc TEXT NOT NULL, SentUtc TEXT NULL,
                LastError TEXT NULL, LeaseOwner TEXT NULL, LeaseUntilUtc TEXT NULL);
            CREATE TABLE theBuilderCacheTagsServer (Server TEXT PRIMARY KEY, FirstSeenUtc TEXT NOT NULL, LastSeenUtc TEXT NOT NULL);
            CREATE TABLE theBuilderCacheTagsAck (Id INTEGER PRIMARY KEY AUTOINCREMENT, EventId TEXT NOT NULL, Server TEXT NOT NULL, AckedUtc TEXT NOT NULL);
            CREATE UNIQUE INDEX IX_AckPair ON theBuilderCacheTagsAck (EventId, Server);
            """);
        _database.Insert(new EventRow { Id = Change, CreatedUtc = Now, Kind = "Published", Server = "backoffice", Change = "{}", Plan = "{}", Status = EventStatuses.Pending });
    }

    public void Dispose()
    {
        _database.Dispose();
        _connection.Dispose();
    }

    private static OutboxRow Purge(Guid id, DateTime notBefore) => new()
    {
        Id = id, EventId = Change, Target = "nextjs", Keys = "[\"content-a\"]", State = OutboxStates.Pending, CreatedUtc = Now, NotBeforeUtc = notBefore,
    };

    private string Status() => _database.ExecuteScalar<string>("SELECT Status FROM theBuilderCacheTagsEvent WHERE Id = @0", Change);

    [Fact]
    public void Only_one_server_can_hold_the_release_lease_until_it_expires()
    {
        Assert.True(LedgerSql.ClaimRelease(_database, Change, "server-1", Now, Now.AddSeconds(30)));
        Assert.False(LedgerSql.ClaimRelease(_database, Change, "server-2", Now.AddSeconds(5), Now.AddSeconds(35)));

        // server-1 stopped without finishing; once its lease runs out, server-2 takes over.
        Assert.True(LedgerSql.ClaimRelease(_database, Change, "server-2", Now.AddSeconds(31), Now.AddSeconds(61)));
    }

    [Fact]
    public void Releasing_queues_the_purges_only_for_the_lease_holder_and_only_once()
    {
        LedgerSql.ClaimRelease(_database, Change, "server-1", Now, Now.AddSeconds(30));

        Assert.False(LedgerSql.CompleteRelease(_database, Change, "server-2", EventStatuses.Released, Now, null, [Purge(Guid.NewGuid(), Now)]));
        Assert.True(LedgerSql.CompleteRelease(_database, Change, "server-1", EventStatuses.Released, Now, null, [Purge(Guid.NewGuid(), Now)]));
        Assert.False(LedgerSql.ClaimRelease(_database, Change, "server-2", Now.AddMinutes(5), Now.AddMinutes(6)));

        Assert.Equal(EventStatuses.Released, Status());
        Assert.Equal(1, _database.ExecuteScalar<int>("SELECT COUNT(*) FROM theBuilderCacheTagsOutbox"));
        Assert.Empty(LedgerSql.Unreleased(_database, 10));
    }

    [Fact]
    public void A_purge_is_due_once_its_time_comes_and_not_while_another_server_is_sending_it()
    {
        Guid now = Guid.NewGuid();
        Guid later = Guid.NewGuid();
        _database.Insert(Purge(now, Now));
        _database.Insert(Purge(later, Now.AddMinutes(1)));

        Assert.Equal([now], LedgerSql.Due(_database, Now, 10).Select(p => p.Id));
        Assert.True(LedgerSql.ClaimSend(_database, now, "server-1", Now, Now.AddSeconds(30)));
        Assert.False(LedgerSql.ClaimSend(_database, now, "server-2", Now, Now.AddSeconds(30)));
        Assert.Empty(LedgerSql.Due(_database, Now.AddSeconds(1), 10));
    }

    [Fact]
    public void A_failed_purge_is_tried_again_after_a_backoff_and_given_up_after_the_last_attempt()
    {
        Guid id = Guid.NewGuid();
        _database.Insert(Purge(id, Now));

        LedgerSql.ClaimSend(_database, id, "server-1", Now, Now.AddSeconds(30));
        LedgerSql.Failed(_database, id, "server-1", attempts: 1, maxAttempts: 3, "503 Service Unavailable", Now);
        OutboxRow retried = _database.Single<OutboxRow>("SELECT * FROM theBuilderCacheTagsOutbox WHERE Id = @0", id);
        Assert.Equal(OutboxStates.Pending, retried.State);
        Assert.Equal(Now.AddSeconds(2), retried.NotBeforeUtc);
        Assert.Empty(LedgerSql.Due(_database, Now.AddSeconds(1), 10));

        LedgerSql.ClaimSend(_database, id, "server-1", Now.AddSeconds(2), Now.AddSeconds(32));
        LedgerSql.Failed(_database, id, "server-1", attempts: 3, maxAttempts: 3, "503 Service Unavailable", Now.AddSeconds(2));
        Assert.Equal(OutboxStates.Dead, _database.ExecuteScalar<string>("SELECT State FROM theBuilderCacheTagsOutbox WHERE Id = @0", id));
        Assert.Empty(LedgerSql.Due(_database, Now.AddHours(1), 10));
    }

    [Fact]
    public void A_sent_purge_is_not_due_again()
    {
        Guid id = Guid.NewGuid();
        _database.Insert(Purge(id, Now));
        LedgerSql.ClaimSend(_database, id, "server-1", Now, Now.AddSeconds(30));
        LedgerSql.Sent(_database, id, "server-1", Now);

        Assert.Empty(LedgerSql.Due(_database, Now.AddHours(1), 10));
        Assert.Equal(OutboxStates.Sent, _database.ExecuteScalar<string>("SELECT State FROM theBuilderCacheTagsOutbox WHERE Id = @0", id));
    }

    [Fact]
    public void Acks_are_recorded_once_per_server_and_read_back_per_change()
    {
        AckSql.Ack(_database, "server-1", [Change], Now);
        AckSql.Ack(_database, "server-1", [Change], Now.AddSeconds(1));
        AckSql.Ack(_database, "server-2", [Change], Now.AddSeconds(2));

        IReadOnlyDictionary<Guid, IReadOnlySet<string>> acks = AckSql.Acks(_database, [Change, Guid.NewGuid()]);
        Assert.Equal(["server-1", "server-2"], acks[Change].Order(StringComparer.Ordinal));
        Assert.Single(acks);
    }

    [Fact]
    public void A_heartbeat_keeps_the_first_seen_time_and_moves_the_last()
    {
        AckSql.Heartbeat(_database, "server-1", Now, Now);
        AckSql.Heartbeat(_database, "server-1", Now, Now.AddSeconds(30));

        ServerPresence presence = Assert.Single(AckSql.Servers(_database));
        Assert.Equal(new ServerPresence("server-1", Now, Now.AddSeconds(30)), presence);
    }
}
