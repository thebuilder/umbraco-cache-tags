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

        _database.Execute(TestDatabase.Schema);
        _database.Insert(new EventRow { Id = Change, CreatedUtc = Now, Kind = "Published", Server = "backoffice", Change = "{}", Plan = "{}", Status = nameof(ChangeStage.Pending) });
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
    public void A_change_is_released_and_its_purges_queued_once_by_whichever_server_gets_there_first()
    {
        Assert.True(LedgerSql.Release(_database, Change, ChangeStage.Released, Now, null, [Purge(Guid.NewGuid(), Now)]));
        Assert.False(LedgerSql.Release(_database, Change, ChangeStage.Released, Now, null, [Purge(Guid.NewGuid(), Now)]));

        Assert.Equal(nameof(ChangeStage.Released), Status());
        Assert.Equal(1, _database.ExecuteScalar<int>("SELECT COUNT(*) FROM theBuilderCacheTagsOutbox"));
        Assert.Empty(LedgerSql.InStage(_database, ChangeStage.Pending, 10));
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
        Assert.Equal(1, LedgerSql.Failed(_database, id, "server-1", maxAttempts: 3, error: "503 Service Unavailable", nowUtc: Now));
        OutboxRow retried = _database.Single<OutboxRow>("SELECT * FROM theBuilderCacheTagsOutbox WHERE Id = @0", id);
        Assert.Equal(OutboxStates.Pending, retried.State);
        Assert.Equal(Now.AddSeconds(2), retried.NotBeforeUtc);
        Assert.Empty(LedgerSql.Due(_database, Now.AddSeconds(1), 10));

        // The attempts are counted from the row, so a sender that read the purge before a failure does not lose one.
        LedgerSql.ClaimSend(_database, id, "server-2", Now.AddSeconds(2), Now.AddSeconds(32));
        Assert.Equal(2, LedgerSql.Failed(_database, id, "server-2", maxAttempts: 3, error: "503 Service Unavailable", nowUtc: Now.AddSeconds(2)));
        Assert.Equal(OutboxStates.Pending, _database.ExecuteScalar<string>("SELECT State FROM theBuilderCacheTagsOutbox WHERE Id = @0", id));

        LedgerSql.ClaimSend(_database, id, "server-1", Now.AddSeconds(10), Now.AddSeconds(40));
        Assert.Equal(3, LedgerSql.Failed(_database, id, "server-1", maxAttempts: 3, error: "503 Service Unavailable", nowUtc: Now.AddSeconds(10)));
        Assert.Equal(OutboxStates.Dead, _database.ExecuteScalar<string>("SELECT State FROM theBuilderCacheTagsOutbox WHERE Id = @0", id));
        Assert.Empty(LedgerSql.Due(_database, Now.AddHours(1), 10));
    }

    [Fact]
    public void A_purge_put_off_after_a_failure_cannot_be_claimed_early_by_a_server_that_read_it_before()
    {
        Guid id = Guid.NewGuid();
        _database.Insert(Purge(id, Now));

        // Both servers read the purge as due; server-1 claims it first and its send fails.
        Assert.Single(LedgerSql.Due(_database, Now, 10));
        Assert.True(LedgerSql.ClaimSend(_database, id, "server-1", Now, Now.AddSeconds(30)));
        LedgerSql.Failed(_database, id, "server-1", maxAttempts: 10, error: "503 Service Unavailable", nowUtc: Now);

        Assert.False(LedgerSql.ClaimSend(_database, id, "server-2", Now.AddSeconds(1), Now.AddSeconds(31)));
        Assert.True(LedgerSql.ClaimSend(_database, id, "server-2", Now.AddSeconds(2), Now.AddSeconds(32)));
    }

    [Fact]
    public void A_server_finds_the_recent_changes_released_without_it_and_only_those()
    {
        Guid other = Guid.NewGuid();
        Guid old = Guid.NewGuid();
        _database.Insert(new EventRow { Id = other, CreatedUtc = Now, Kind = "Published", Server = "backoffice", Change = "{}", Plan = "{}", Status = nameof(ChangeStage.Pending) });
        _database.Insert(new EventRow { Id = old, CreatedUtc = Now, Kind = "Published", Server = "backoffice", Change = "{}", Plan = "{}", Status = nameof(ChangeStage.Pending) });

        // Names as JSON, as Release writes them; LIKE's own characters must not widen the match.
        LedgerSql.Release(_database, Change, ChangeStage.Released, Now.AddMinutes(1), "[\"web_1/0a1b2c3d\"]", []);
        LedgerSql.Release(_database, other, ChangeStage.Released, Now.AddMinutes(1), "[\"webX1/0a1b2c3d\"]", []);
        LedgerSql.Release(_database, old, ChangeStage.Released, Now.AddMinutes(-30), "[\"web_1/0a1b2c3d\"]", []);

        Assert.Equal([Change], LedgerSql.ReleasedWithout(_database, "\"web_1/0a1b2c3d\"", Now, 10).Select(row => row.Id));
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

        IReadOnlyDictionary<Guid, IReadOnlyList<ServerAck>> acks = AckSql.Acks(_database, [Change, Guid.NewGuid()]);
        Assert.Equal(["server-1", "server-2"], acks.AcknowledgedBy(Change).Order(StringComparer.Ordinal));
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

    [Fact]
    public void Due_purges_are_read_oldest_first_and_at_most_as_many_as_asked_for()
    {
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();
        _database.Insert(Purge(second, Now.AddSeconds(-1)));
        _database.Insert(Purge(first, Now.AddSeconds(-2)));
        _database.Insert(Purge(Guid.NewGuid(), Now));

        Assert.Equal([first, second], LedgerSql.Due(_database, Now, 2).Select(p => p.Id));
    }

    [Fact]
    public void An_indexing_change_has_its_listings_queued_once_by_whichever_server_gets_there_first()
    {
        LedgerSql.Release(_database, Change, ChangeStage.Indexing, Now, null, [Purge(Guid.NewGuid(), Now)]);

        Assert.Equal([Change], LedgerSql.InStage(_database, ChangeStage.Indexing, 10).Select(row => row.Id));
        Assert.True(LedgerSql.ReleaseListings(_database, Change, [Purge(Guid.NewGuid(), Now.AddSeconds(2))]));
        Assert.False(LedgerSql.ReleaseListings(_database, Change, [Purge(Guid.NewGuid(), Now.AddSeconds(2))]));

        Assert.Empty(LedgerSql.InStage(_database, ChangeStage.Indexing, 10));
        Assert.Equal(nameof(ChangeStage.Released), Status());
        Assert.Equal(2, _database.ExecuteScalar<int>("SELECT COUNT(*) FROM theBuilderCacheTagsOutbox"));
    }

    [Fact]
    public void A_pending_change_has_no_listings_to_release()
    {
        Assert.False(LedgerSql.ReleaseListings(_database, Change, [Purge(Guid.NewGuid(), Now)]));
        Assert.Equal(nameof(ChangeStage.Pending), Status());
    }

    [Fact]
    public void A_server_marks_only_the_changes_it_acknowledged_as_indexed()
    {
        Guid other = Guid.NewGuid();
        AckSql.Ack(_database, "server-1", [Change], Now);
        AckSql.Ack(_database, "server-2", [Change, other], Now);

        AckSql.Indexed(_database, "server-2", [Change, Guid.NewGuid()], Now.AddSeconds(2));

        IReadOnlyDictionary<Guid, IReadOnlyList<ServerAck>> acks = AckSql.Acks(_database, [Change, other]);
        Assert.Equal(["server-2"], acks.IndexedBy(Change));
        Assert.Empty(acks.IndexedBy(other));
        Assert.Equal(["server-1", "server-2"], acks.AcknowledgedBy(Change).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_dead_purge_can_be_sent_again_from_its_first_attempt_and_a_pending_one_set_aside()
    {
        Guid dead = Guid.NewGuid();
        Guid pending = Guid.NewGuid();
        _database.Insert(Purge(dead, Now));
        _database.Insert(Purge(pending, Now));
        _database.Execute("UPDATE theBuilderCacheTagsOutbox SET State = 'Dead', Attempts = 10, LastError = 'down' WHERE Id = @0", dead);

        Assert.Equal([dead], LedgerSql.InState(_database, OutboxStates.Dead, 10).Select(row => row.Id));
        Assert.True(LedgerSql.Retry(_database, dead, Now.AddHours(1)));
        Assert.False(LedgerSql.Retry(_database, pending, Now.AddHours(1)));
        Assert.True(LedgerSql.Discard(_database, pending));
        Assert.False(LedgerSql.Discard(_database, pending));

        OutboxRow retried = LedgerSql.Due(_database, Now.AddHours(1), 10).Single();
        Assert.Equal((dead, 0, (string?)null), (retried.Id, retried.Attempts, retried.LastError));
        Assert.Equal([pending], LedgerSql.InState(_database, OutboxStates.Discarded, 10).Select(row => row.Id));
    }
}
